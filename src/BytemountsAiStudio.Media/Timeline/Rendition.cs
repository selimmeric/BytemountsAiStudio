using System.Globalization;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Core.Time;

namespace BytemountsAiStudio.Media.Timeline;

/// Bir rendition'ın hedefi (P6-03).
public sealed record RenditionSpec
{
    /// Hedef tuval — 9:16, 1:1, 16:9.
    public required Canvas Canvas { get; init; }

    /// Üst süre sınırı. `null` ise süre değişmiyor.
    public Ms? MaxDuration { get; init; }
}

/// Aynı içerikten başka bir en-boy oranı ve süre için türev (P6-03).
///
/// TÜREV TIMELINE'DAN, BİTMİŞ VİDEODAN DEĞİL.
///
/// Hazır mp4'ü kırpmak ucuz görünüyor ve yanlış: 9:16'lık bir videodan
/// 16:9 kesmek karenin dörtte üçünü atıyor ve altyazının tam ortasından
/// geçiyor. Doldurmak (letterbox) ise ekranın yarısını siyah bant
/// yapıyor. İkisi de "aynı içerik başka orana uyarlandı" değil,
/// "aynı içerik bozuldu".
///
/// Timeline'dan türetmek bunun olmadığı tek yol: metin boyutları
/// zaten tuval yüzdesi (`TextStyle.SizePercent`), görseller kadraja
/// yeniden yerleşiyor, altyazı yeniden konumlanıyor. Bedeli yeniden
/// render — ve o bedel, kırpılmış bir kapak metninden ucuz.
///
/// SÜRE KIRPMA CÜMLE SINIRINDA. Onuncu dakikadaki bir videodan 60
/// saniyelik Short çıkarmak, 60. saniyede sesi ortadan kesmek demek
/// değil: kırpma yalnızca bir SES PARÇASININ bittiği yerde yapılıyor.
/// Kelimenin ortasından kesilen bir video, kırpılmamış olmasından
/// kötü.
public static class Rendition
{
    /// Türev belgeyi üretir.
    ///
    /// SONUÇ DOĞRULANIYOR. Türetme sırasında bir sahne boşluğu ya da
    /// taşan bir katman bırakmak kolay; bunu render sırasında
    /// "Invalid argument" olarak görmek, saatler sonra ve nerede
    /// olduğunu söylemeden görmek demek.
    public static Result<TimelineDocument> Derive(TimelineDocument source, RenditionSpec spec)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(spec);

        var window = Window(source, spec.MaxDuration);

        if (window.IsFailure)
        {
            return Result.Failure<TimelineDocument>(window.Error);
        }

        var end = window.Value;
        var trimmed = end < source.Duration;

        var scenes = Scenes(source, end);

        if (scenes.Count == 0)
        {
            return Error.Permanent("rendition.no_scenes",
                "Kırpma sonrası hiç sahne kalmadı.");
        }

        var derived = source with
        {
            Canvas = spec.Canvas,
            Output = RenderPreset.ForCanvas(spec.Canvas),
            Duration = end,
            Scenes = scenes,
            Audio = Audio(source.Audio, end),
            Captions = Captions(source.Captions, end),
            PersistentLayers = Layers(source.PersistentLayers, source.Canvas, spec.Canvas),
            Provenance = Stamp(source, end, trimmed),
        };

        var issues = TimelineValidator.Validate(derived);

        if (issues.Count > 0)
        {
            return Error.Permanent("rendition.invalid",
                "Türetilen timeline geçersiz: " + string.Join(" | ", issues.Select(i => i.Message)));
        }

        return Result.Success(derived);
    }

    /// Kırpma noktası — SES PARÇASI SINIRINDA.
    ///
    /// Sınıra oturmayan bir kırpma cümlenin ortasından kesiyor. Sınır
    /// bulunamıyorsa HATA: ilk cümle bile sınırdan uzunsa, yapılacak
    /// doğru şey kelimenin ortasından kesmek değil, "bu içerikten bu
    /// sürede rendition çıkmıyor" demek.
    internal static Result<Ms> Window(TimelineDocument source, Ms? maxDuration)
    {
        if (maxDuration is not { } limit || source.Duration <= limit)
        {
            return Result.Success(source.Duration);
        }

        if (limit.Value <= 0)
        {
            return Error.Permanent("rendition.bad_limit",
                $"Süre sınırı pozitif olmalı: {limit}");
        }

        var boundary = source.Audio.VoiceSegments
            .Select(s => s.End)
            .Where(e => e <= limit)
            .DefaultIfEmpty(Ms.Zero)
            .Max();

        if (boundary.Value <= 0)
        {
            return Error.Permanent("rendition.no_boundary",
                string.Create(CultureInfo.InvariantCulture,
                    $"{limit.TotalSeconds:0.#} sn sınırına sığan bir cümle sınırı yok; ")
                + "ilk ses parçası bile daha uzun. Kelimenin ortasından kesmektense "
                + "rendition üretilmiyor.");
        }

        return Result.Success(boundary);
    }

    /// Pencereye giren sahneler — sınırdakiler KIRPILIYOR.
    ///
    /// Sahne kırpmadan atılsaydı son sahne ile video sonu arasında
    /// boşluk kalırdı ve ffmpeg orada siyah kare üretirdi.
    private static List<Scene> Scenes(TimelineDocument source, Ms end)
    {
        var kept = new List<Scene>();
        var index = 0;

        foreach (var scene in source.Scenes.OrderBy(s => s.Range.Start.Value))
        {
            if (scene.Range.Start >= end)
            {
                continue;
            }

            var range = scene.Range.End <= end
                ? scene.Range
                : new TimeRange(scene.Range.Start, end);

            if (range.Duration.Value <= 0)
            {
                continue;
            }

            kept.Add(scene with
            {
                // SAHNE NUMARASI YENİDEN VERİLİYOR: planner girdi
                // kimliklerini (`scene0`, `scene1`) bu numaradan
                // türetiyor ve türev belge kendi başına tutarlı olmalı.
                Index = index++,
                Range = range,

                // KATMANLAR SAHNENİN İÇİNDE KALMAK ZORUNDA (doğrulayıcı
                // kuralı). Kırpılan sahnede dışarı taşan katman, türev
                // belgeyi render edilemez yapardı.
                Overlays =
                [
                    .. scene.Overlays
                        .Where(o => o.Range.Start < range.End)
                        .Select(o => o.Range.End <= range.End
                            ? o
                            : o with { Range = new TimeRange(o.Range.Start, range.End) })
                        .Where(o => o.Range.Duration.Value > 0),
                ],
            });
        }

        if (kept.Count == 0)
        {
            return kept;
        }

        // SON SAHNENİN ÇIKIŞ GEÇİŞİ: kırpılan video aniden kesilirse
        // izleyici "yüklenmedi mi" diye düşünüyor. Geçiş yalnızca
        // sahne ona sığacak kadar uzunsa ekleniyor.
        var last = kept[^1];

        if (last.TransitionOut is null && last.Range.Duration >= FadeOut + FadeOut)
        {
            kept[^1] = last with { TransitionOut = new Transition(TransitionKind.Fade, FadeOut) };
        }

        return kept;
    }

    /// Kırpılan videonun kapanış geçişi.
    private static readonly Ms FadeOut = new(500);

    private static AudioTrack Audio(AudioTrack source, Ms end)
        => source with
        {
            // SINIRI AŞAN PARÇA ATILIYOR, KIRPILMIYOR: pencere zaten
            // bir parça sınırında bittiği için aşan parça olmamalı —
            // ama olursa yarım cümle bırakmaktansa düşürmek doğru.
            VoiceSegments = [.. source.VoiceSegments.Where(s => s.End <= end)],

            Music = source.Music is { } music
                ? music with
                {
                    // KAPANIŞ SÖNÜMÜ SAHNEYE SIĞDIRILIYOR: 2 saniyelik
                    // sönüm 1 saniyelik bir rendition'da videodan uzun
                    // olurdu.
                    FadeOut = music.FadeOut > end ? end : music.FadeOut,
                }
                : null,
        };

    private static CaptionTrack? Captions(CaptionTrack? source, Ms end)
    {
        if (source is null)
        {
            return null;
        }

        // KELİME KIRPILMIYOR, DÜŞÜRÜLÜYOR. Bir altyazı işareti tek
        // kelime; ortasından kesmek yarım kelime göstermek demek.
        return source with { Cues = [.. source.Cues.Where(c => c.Range.End <= end)] };
    }

    /// Kalıcı katmanların kenar boşlukları ORANSAL taşınıyor.
    ///
    /// `TextStyle.SizePercent` tuval yüzdesi olduğu için metin kendi
    /// kendine ölçekleniyor; `PersistentLayer` marjları ise PİKSEL —
    /// belgedeki tek tuvale bağımlı alan. 1080 genişlikte 40 piksel
    /// olan boşluk, 1920 genişlikte yarı yarıya daralmış görünürdü.
    private static IReadOnlyList<PersistentLayer> Layers(
        IReadOnlyList<PersistentLayer> layers, Canvas from, Canvas to)
    {
        if (layers.Count == 0 || (from.Width == to.Width && from.Height == to.Height))
        {
            return layers;
        }

        var scaleX = (double)to.Width / from.Width;
        var scaleY = (double)to.Height / from.Height;

        return
        [
            .. layers.Select(l => l with
            {
                // `AwayFromZero`: .NET'in varsayılanı yarımları ÇİFTE
                // yuvarlıyor (22,5 -> 22) ve bir kenar boşluğunda bu
                // sürpriz, faydasından çok kafa karıştırıyor.
                MarginX = (int)Math.Round(l.MarginX * scaleX, MidpointRounding.AwayFromZero),
                MarginY = (int)Math.Round(l.MarginY * scaleY, MidpointRounding.AwayFromZero),
            }),
        ];
    }

    /// Türevin NEREDEN geldiği kayda giriyor.
    ///
    /// Kırpılmış bir rendition, videonun tamamı sanılırsa yanlış
    /// okunur: "izlenme oranı düşük" diye rapor edilen şey aslında
    /// videonun ilk dakikası olabilir.
    private static Provenance Stamp(TimelineDocument source, Ms end, bool trimmed)
    {
        var versions = new Dictionary<string, string>(
            source.Provenance?.PromptVersions ?? new Dictionary<string, string>(StringComparer.Ordinal),
            StringComparer.Ordinal)
        {
            ["rendition.source"] = RenderPreset.Name(source.Canvas),
        };

        if (trimmed)
        {
            versions["rendition.excerpt"] = string.Create(CultureInfo.InvariantCulture,
                $"0-{end.TotalSeconds:0.#}s / {source.Duration.TotalSeconds:0.#}s");
        }

        return (source.Provenance ?? new Provenance()) with { PromptVersions = versions };
    }
}
