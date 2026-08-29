using System.Text.Json;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Assets;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Core.Time;
using BytemountsAiStudio.Media.Timeline;

namespace BytemountsAiStudio.Nodes;

/// Run bağlamındaki ses ve görsel çıktılarından timeline derler.
///
/// ADR-006'nın somutlaştığı yer: sahne SINIRLARI senaryodan, sahne SÜRELERİ
/// ölçülen sesten geliyor. Ters kurulsaydı ses–görsel kayması sahte veriyle
/// görünmez, gerçek veriyle ortaya çıkardı.
public static class TimelineBuilder
{
    public static Result<TimelineDocument> Build(JsonElement runContext)
        => Build(runContext, null, null);

    public static Result<TimelineDocument> Build(
        JsonElement runContext, IReadOnlyList<string>? fontStack)
        => Build(runContext, fontStack, null);

    /// `fontStack` kanaldan geliyor (P3-01). `null` ise varsayılan
    /// zincir kullanılıyor — kanal ayarı yoksa altyazısız video değil,
    /// makul bir yazı tipi doğru davranış.
    /// `canvas` graftan geliyor (P3-03): kısa video dikey, uzun video
    /// yatay. `null` ise dikey — bu sistem ağırlıklı olarak Shorts
    /// üretiyor.
    public static Result<TimelineDocument> Build(
        JsonElement runContext, IReadOnlyList<string>? fontStack, Canvas? canvasOverride)
        => Build(runContext, fontStack, canvasOverride, null, null);

    /// `captions` ve `music` kanaldan geliyor (P3-01).
    ///
    /// ***ÖNCEDEN İKİSİ DE `TimelineBuilder` İÇİNDE SABİTTİ:*** iki
    /// kanal aynı graftan koşunca altyazılar piksel piksel aynı
    /// çıkıyordu ve müziği biraz öne çıkarmak isteyen bir kanalın tek
    /// seçeneği müziği tamamen kapatmaktı. `TextStyle`'ın kendi yorumu
    /// "bir kanalın altyazı stilini değiştirmek tek satır olsun" diyordu
    /// ve o satır hiçbir yerde yoktu.
    public static Result<TimelineDocument> Build(
        JsonElement runContext,
        IReadOnlyList<string>? fontStack,
        Canvas? canvasOverride,
        CaptionStyle? captions,
        MusicLevels? music)
    {
        if (!runContext.TryGetProperty("tts", out var tts)
            || !tts.TryGetProperty("segments", out var segmentsJson))
        {
            return Error.Permanent("timeline.no_audio", "Ses parçaları bulunamadı.");
        }

        if (!runContext.TryGetProperty("visuals", out var visuals)
            || !visuals.TryGetProperty("images", out var imagesJson))
        {
            return Error.Permanent("timeline.no_visuals", "Sahne görselleri bulunamadı.");
        }

        var language = LanguageTag.Create(
            NodeJson.Text(runContext, "topic.language") ?? "tr-TR");

        var canvas = canvasOverride ?? Canvas.Shorts1080;

        // SES ve SAHNE ayrı listeler.
        //
        // Eskiden birebir varsayılıyordu: her ses parçası bir sahne.
        // Sahne planlayıcı (P1-16) kısa cümleleri birleştirmeye başlayınca
        // bu varsayım kırıldı — ve kırılması yalnızca kısa cümle içeren
        // senaryolarda görülecekti, yani seyrek ve zor fark edilen bir
        // ses–görsel kayması olarak.
        var segments = new List<VoiceSegment>();

        foreach (var element in segmentsJson.EnumerateArray())
        {
            segments.Add(new VoiceSegment
            {
                Id = element.GetProperty("id").GetString()!,
                Asset = AssetRef.Create(element.GetProperty("asset").GetString()!),
                Start = new Ms(element.GetProperty("start_ms").GetInt32()),
                Duration = new Ms(element.GetProperty("duration_ms").GetInt32()),
                SpeechText = element.TryGetProperty("speech_text", out var speech)
                    ? speech.GetString()
                    : null,
            });
        }

        if (segments.Count == 0)
        {
            return Error.Permanent("timeline.no_audio", "Ses parçaları bulunamadı.");
        }

        var planned = imagesJson.EnumerateArray().OrderBy(e => e.GetProperty("scene").GetInt32()).ToList();

        if (planned.Count == 0)
        {
            return Error.Permanent("timeline.no_visuals", "Sahne görselleri bulunamadı.");
        }

        var scenes = new List<Scene>(planned.Count);
        var total = Ms.Zero;

        for (var index = 0; index < planned.Count; index++)
        {
            var element = planned[index];
            var scene = ReadScene(element, index, segments);

            if (scene.IsFailure)
            {
                return Result.Failure<TimelineDocument>(scene.Error);
            }

            var (start, duration, ids, asset) = scene.Value;

            scenes.Add(new Scene
            {
                Index = index,
                Range = TimeRange.FromDuration(start, duration),
                VoiceSegmentIds = ids,
                Visual = new SceneVisual
                {
                    Asset = asset,
                    // Dönüşümlü yakınlaşma/uzaklaşma: hepsi aynı yönde
                    // olsaydı video tekdüze görünürdü.
                    Motion = index % 2 == 0
                        ? new KenBurns { FromScale = 1.0, ToScale = 1.12, ToX = 0.04 }
                        : new KenBurns { FromScale = 1.12, ToScale = 1.0, FromX = -0.04 },
                },
            });

            total = start + duration;
        }

        // GEÇİŞLER İKİNCİ GEÇİŞTE: bölüm sınırının hangi sahneye
        // düştüğü BÜTÜN sahne sonlarına bakmadan bilinemiyor, tek
        // döngüde karar verilemezdi.
        scenes = ApplyTransitions(scenes, ChapterStartsFrom(runContext));

        var cues = new List<CaptionCue>();

        if (tts.TryGetProperty("cues", out var cuesJson))
        {
            foreach (var element in cuesJson.EnumerateArray())
            {
                cues.Add(new CaptionCue
                {
                    Text = element.GetProperty("text").GetString() ?? string.Empty,
                    Range = new TimeRange(
                        new Ms(element.GetProperty("start_ms").GetInt32()),
                        new Ms(element.GetProperty("end_ms").GetInt32())),
                    SegmentId = element.TryGetProperty("segment", out var segment)
                        ? segment.GetString()
                        : null,
                });
            }
        }

        return new TimelineDocument
        {
            Canvas = canvas,
            Language = language,
            RightToLeft = language.IsRightToLeft,
            Duration = total,
            FontStack = fontStack ?? ["Inter", "Noto Sans", "Segoe UI", "Arial"],
            Audio = new AudioTrack { VoiceSegments = segments, Music = MusicFrom(runContext, music ?? MusicLevels.Default) },
            Scenes = scenes,
            Captions = cues.Count > 0
                ? new CaptionTrack { StyleRef = "caption", Cues = cues }
                : null,
            Styles = new Dictionary<string, TextStyle>(StringComparer.Ordinal)
            {
                ["caption"] = StyleFrom(captions ?? CaptionStyle.Default),
            },
            // ÖN AYAR TUVALDEN (P3-02): burada sabit `"shorts-1080x1920"`
            // yazıyordu ve 1920×1080 çıkan uzun videoda da öyle
            // kalıyordu — çıktının yanında duran, çıktıyı yanlış
            // anlatan bir kayıt.
            Output = RenderPreset.ForCanvas(canvas),
            Provenance = new Provenance
            {
                // İstem damgası artık gerçek (P1-07): senaryo node'u
                // hangi istem sürümüyle üretildiğini çıktısına yazıyor
                // ve buraya olduğu gibi taşınıyor.
                PromptVersions = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["script.generate"] = NodeJson.Text(runContext, "script.prompt") ?? "bilinmiyor",
                },
                EngineMinVersion = "0.1.0",
            },
        };
    }

    /// Bir sahne kaydını okur.
    ///
    /// Zamanlama görsel node'unun yazdığı plandan geliyor; yoksa ses
    /// parçasından türetiliyor. Yedek yol GEÇİŞ İÇİN: eski bir run
    /// bağlamı yeniden derlendiğinde kırılmasın.
    private static Result<(Ms Start, Ms Duration, IReadOnlyList<string> Ids, AssetRef Asset)> ReadScene(
        JsonElement element, int index, List<VoiceSegment> segments)
    {
        if (!element.TryGetProperty("asset", out var assetJson) || assetJson.GetString() is not { } assetText)
        {
            return Error.Permanent("timeline.missing_visual", $"{index}. sahne için görsel yok.");
        }

        var ids = element.TryGetProperty("segments", out var segmentsJson)
            ? segmentsJson.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList()
            : [];

        if (ids.Count == 0)
        {
            // Plan bilgisi yoksa birebir eşleşmeye düşülüyor.
            if (index >= segments.Count)
            {
                return Error.Permanent("timeline.scene_without_audio",
                    $"{index}. sahnenin sesi yok.");
            }

            var fallback = segments[index];

            return Result.Success<(Ms, Ms, IReadOnlyList<string>, AssetRef)>(
                (fallback.Start, fallback.Duration, [fallback.Id], AssetRef.Create(assetText)));
        }

        var start = element.TryGetProperty("start_ms", out var startJson)
            ? new Ms(startJson.GetInt32())
            : segments.First(s => s.Id == ids[0]).Start;

        var duration = element.TryGetProperty("duration_ms", out var durationJson)
            ? new Ms(durationJson.GetInt32())
            : segments.Where(s => ids.Contains(s.Id)).Aggregate(Ms.Zero, (a, s) => a + s.Duration);

        return Result.Success<(Ms, Ms, IReadOnlyList<string>, AssetRef)>(
            (start, duration, ids, AssetRef.Create(assetText)));
    }

    /// Müzik yatağını run bağlamından okur (P2-09).
    ///
    /// MÜZİK YOKSA `null` VE BU NORMAL: müziksiz video tamamen
    /// geçerli. Boş bir `MusicBed` uydurmak, render'ı olmayan bir
    /// dosyayı aramaya göndermek olurdu.
    ///
    /// LİSANS KANITI TAŞINMAZSA MÜZİK DE TAŞINMIYOR. Kanıtsız bir
    /// parçayı timeline'a koyup QC'nin yakalamasını beklemek, çalışan
    /// bir kontrol varken riski üretim hattının içine sokmak olurdu —
    /// ve bir Content ID talebi kanalın o videodan gelen gelirinin
    /// tamamını götürüyor.
    /* ---- geçişler (P3-04) ---- */

    /// Videonun başındaki açılma.
    internal static readonly Ms Opening = new(500);

    /// Videonun sonundaki kapanma.
    ///
    /// AÇILMADAN UZUN ve bu bilinçli: izleyici içeriğe hızlı girmek
    /// istiyor, sonda ise nefes alacak yer iyi geliyor.
    internal static readonly Ms Closing = new(900);

    /// Bölüm değişimi.
    internal static readonly Ms ChapterCut = new(700);

    /// Aynı bölüm içindeki sahne geçişi.
    internal static readonly Ms SceneCut = new(300);

    /// Sahnelere açılma/kararma yazar (P3-04).
    ///
    /// ÜÇ FARKLI UZUNLUK, ÇÜNKÜ ÜÇ FARKLI ŞEY OLUYOR:
    ///   - videonun başı ve sonu (siyahtan açılma, siyaha kapanma)
    ///   - bölüm değişimi
    ///   - aynı bölüm içinde sahne değişimi
    ///
    /// Hepsi 300 ms olduğunda on dakikalık bir videoda YAPI
    /// GÖRÜNMÜYORDU: beş bölümlük bir belgesel, kırk sahnelik tek bir
    /// akış gibi izleniyordu. Bölüm sınırının daha uzun sürmesi,
    /// izleyiciye "burada konu değişti" diyen tek görsel işaret.
    ///
    /// Videonun kendi başı ve sonu hiç yoktu: ilk kare tam
    /// parlaklıkta patlıyor, son kare aniden kesiliyordu. Son
    /// sahnenin geçişi kasten `null`'dı çünkü geçiş "sahneler arası"
    /// diye düşünülmüştü — oysa videonun sonu da bir geçiş.
    internal static List<Scene> ApplyTransitions(
        List<Scene> scenes, IReadOnlyList<int> chapterStartsMs)
    {
        ArgumentNullException.ThrowIfNull(scenes);

        if (scenes.Count == 0)
        {
            return scenes;
        }

        var ends = scenes.Select(s => s.Range.End.Value).ToList();
        var boundaries = ChapterBoundaries.Match(ends, chapterStartsMs);

        var result = new List<Scene>(scenes.Count);

        for (var i = 0; i < scenes.Count; i++)
        {
            var scene = scenes[i];
            var isFirst = i == 0;
            var isLast = i == scenes.Count - 1;

            var opening = isFirst ? Opening : Ms.Zero;

            var closing = isLast
                ? Closing
                : boundaries.Contains(i) ? ChapterCut : SceneCut;

            // KISA SAHNEDE GEÇİŞ KISALTILIYOR, ATILMIYOR.
            //
            // İkisinin toplamı sahneyi aşarsa görüntü açılırken
            // kararmaya başlar ve sahne hiç tam parlaklığa çıkmaz.
            // Doğrulayıcı bunu hata sayıyor; burada kırpmak, geçerli
            // bir belge üretmenin ve kısa sahneyi cezalandırmamanın
            // yolu. Atmak da olurdu ama o zaman bir sahne sebepsizce
            // sert kesilirdi.
            var span = scene.Range.Duration.Value;
            (opening, closing) = Fit(opening, closing, span);

            result.Add(scene with
            {
                TransitionIn = opening.Value > 0
                    ? new Transition(TransitionKind.Fade, opening)
                    : null,
                TransitionOut = closing.Value > 0
                    ? new Transition(TransitionKind.Fade, closing)
                    : null,
            });
        }

        return result;
    }

    /// İki geçişi sahneye sığdırır — oranlarını koruyarak.
    private static (Ms Opening, Ms Closing) Fit(Ms opening, Ms closing, int spanMs)
    {
        var total = opening.Value + closing.Value;

        if (total <= spanMs || total == 0)
        {
            return (opening, closing);
        }

        // Sahnenin tamamını geçişe ayırmak da yanlış olurdu: hiç tam
        // parlaklıkta kare kalmazdı. Yarısı geçişe, yarısı görüntüye.
        var budget = spanMs / 2;

        return (new Ms(opening.Value * budget / total), new Ms(closing.Value * budget / total));
    }

    /// Bölüm başlangıçları — bölüm planı yoksa boş liste.
    ///
    /// KISA VİDEODA BÖLÜM YOK ve bu bir eksiklik değil: 48 saniyelik
    /// bir Shorts'ta bölüm sınırı diye bir şey olmuyor. Liste boş
    /// olunca her sahne normal geçişini alıyor, videonun başı ve
    /// sonu yine açılıp kapanıyor.
    internal static IReadOnlyList<int> ChapterStartsFrom(JsonElement runContext)
    {
        if (!runContext.TryGetProperty("chapters", out var node)
            || node.ValueKind != JsonValueKind.Object
            || !node.TryGetProperty("chapters", out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var starts = new List<int>();

        foreach (var chapter in array.EnumerateArray())
        {
            if (chapter.ValueKind == JsonValueKind.Object
                && chapter.TryGetProperty("start_ms", out var start)
                && start.TryGetInt32(out var value))
            {
                starts.Add(value);
            }
        }

        return starts;
    }

    /// Kanal ayarını çizim katmanının stiline çevirir.
    ///
    /// ÇEVİRİ BURADA, `Core` İÇİNDE DEĞİL: `Anchor` çizim katmanının
    /// tipi ve `Core` oraya bakmıyor. Ayar tarafında konum bir DİZGE ve
    /// geçerli değerler `CaptionStyle.Positions` içinde doğrulanıyor —
    /// yani buraya tanınmayan bir dizge gelmiyor.
    internal static TextStyle StyleFrom(CaptionStyle style)
        => new()
        {
            // ***`FontFamily` ARTIK OKUNUYOR.***
            //
            // Alan yazılıyor ve hiçbir yerde okunmuyordu: çizim
            // `timeline.FontStack` kullanıyor. Boş bırakıldığında
            // zincirin ilk yazı tipi geçerli; dolduğunda çizim onu
            // zincirin BAŞINA alıyor (`CaptionRenderer`).
            FontFamily = style.FontFamily ?? string.Empty,
            SizePercent = style.SizePercent,
            Bold = style.Bold,
            Color = style.Color,
            HighlightColor = style.HighlightColor,
            StrokeColor = style.StrokeColor,
            StrokeWidth = style.StrokeWidth,
            BoxColor = style.BoxColor,
            BoxOpacity = style.BoxOpacity,
            Position = style.Position switch
            {
                "top_left" => Anchor.TopLeft,
                "top_right" => Anchor.TopRight,
                "bottom_left" => Anchor.BottomLeft,
                "bottom_right" => Anchor.BottomRight,
                "center" => Anchor.Center,
                _ => Anchor.BottomCenter,
            },
            OffsetPercent = style.OffsetPercent,
            MaxLines = style.MaxLines,
        };

    internal static MusicBed? MusicFrom(JsonElement runContext)
        => MusicFrom(runContext, MusicLevels.Default);

    internal static MusicBed? MusicFrom(JsonElement runContext, MusicLevels levels)
    {
        if (!runContext.TryGetProperty("music", out var music)
            || music.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var asset = NodeJson.Text(music, "asset");

        if (string.IsNullOrWhiteSpace(asset))
        {
            return null;
        }

        if (!music.TryGetProperty("license", out var licenseJson)
            || licenseJson.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var license = new MusicLicense
        {
            Name = NodeJson.Text(licenseJson, "name") ?? string.Empty,
            Author = NodeJson.Text(licenseJson, "author"),
            Url = Uri.TryCreate(NodeJson.Text(licenseJson, "url"), UriKind.Absolute, out var url) ? url : null,
            RequiresAttribution = licenseJson.TryGetProperty("requires_attribution", out var requires)
                                  && requires.ValueKind == JsonValueKind.True,
            CapturedAt = licenseJson.TryGetProperty("captured_at", out var captured)
                         && captured.TryGetDateTimeOffset(out var at)
                ? at
                : DateTimeOffset.UtcNow,
        };

        if (!license.IsComplete)
        {
            return null;
        }

        var reference = AssetRef.TryCreate(asset);

        if (reference.IsFailure)
        {
            return null;
        }

        return new MusicBed
        {
            Asset = reference.Value,
            License = license,
            // SEVİYELER KANALDAN (P3-01): önceden yalnızca kayıt
            // varsayılanıydı ve müziği biraz öne çıkarmak isteyen bir
            // kanalın tek seçeneği müziği tamamen KAPATMAKTI.
            GainDb = levels.GainDb,
            FadeIn = new Ms(levels.FadeInMs),
            FadeOut = new Ms(levels.FadeOutMs),

            // DUCKING VARSAYILAN OLARAK AÇIK.
            //
            // Kapalı olsaydı müzik konuşmanın üstüne biner ve bunu
            // ancak videoyu dinleyen biri fark ederdi — mekanik QC
            // ses seviyesine bakıyor ama "hangi ses" sorusuna cevap
            // veremiyor. Kapatmak artık MÜMKÜN ama açık bir karar:
            // `music.ducking: false`.
            Ducking = levels.Ducking ? new DuckingSpec { TargetGainDb = levels.DuckingDb } : null,
        };
    }
}
