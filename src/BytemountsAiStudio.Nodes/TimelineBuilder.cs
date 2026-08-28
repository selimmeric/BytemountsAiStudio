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
        => Build(runContext, null);

    /// `fontStack` kanaldan geliyor (P3-01). `null` ise varsayılan
    /// zincir kullanılıyor — kanal ayarı yoksa altyazısız video değil,
    /// makul bir yazı tipi doğru davranış.
    public static Result<TimelineDocument> Build(
        JsonElement runContext, IReadOnlyList<string>? fontStack)
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

        var canvas = Canvas.Shorts1080;

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
            var isLast = index == planned.Count - 1;

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
                TransitionOut = isLast ? null : new Transition(TransitionKind.Fade, new Ms(300)),
            });

            total = start + duration;
        }

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
            Audio = new AudioTrack { VoiceSegments = segments, Music = MusicFrom(runContext) },
            Scenes = scenes,
            Captions = cues.Count > 0
                ? new CaptionTrack { StyleRef = "caption", Cues = cues }
                : null,
            Styles = new Dictionary<string, TextStyle>(StringComparer.Ordinal)
            {
                ["caption"] = new()
                {
                    FontFamily = "Inter",
                    SizePercent = 5.5,
                    Bold = true,
                    Color = "#FFFFFF",
                    HighlightColor = "#FFD400",
                    StrokeColor = "#000000",
                    StrokeWidth = 8,
                    BoxColor = "#000000",
                    BoxOpacity = 0.35,
                    Position = Anchor.BottomCenter,
                    OffsetPercent = 22,
                    MaxLines = 2,
                },
            },
            Output = new OutputSpec { Preset = "shorts-1080x1920" },
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
    internal static MusicBed? MusicFrom(JsonElement runContext)
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
            // DUCKING VARSAYILAN OLARAK AÇIK.
            //
            // Kapalı olsaydı müzik konuşmanın üstüne biner ve bunu
            // ancak videoyu dinleyen biri fark ederdi — mekanik QC
            // ses seviyesine bakıyor ama "hangi ses" sorusuna cevap
            // veremiyor.
            Ducking = new DuckingSpec(),
        };
    }
}
