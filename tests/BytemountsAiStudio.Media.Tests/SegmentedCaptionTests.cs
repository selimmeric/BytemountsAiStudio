using BytemountsAiStudio.Core.Time;
using BytemountsAiStudio.Media.Planning;

namespace BytemountsAiStudio.Media.Tests;

/// Birleştirme sonrası plana altyazı biniyor mu (P2-11, P3-02).
///
/// ***BU DOSYA GERÇEK BİR İÇERİK HATASINI KAPATIYOR.***
///
/// `PlanOverVideo` birleştirilmiş videoya sesi biniyor ve **altyazıyı
/// hiç binmiyordu**. Uzun video tohum grafı `"segmented": true`
/// kullanıyor — yani bu yol **onun varsayılanı**. Sonuç: **her uzun
/// video altyazısız çıkıyordu.**
///
/// Hiçbir kontrol bunu yakalayamazdı:
///   - Timeline'da altyazı VARDI ve `caption_count` doğru sayıyı
///     yazıyordu.
///   - Süre, çözünürlük, ses seviyesi doğruydu.
///   - Mekanik QC piksele bakmıyor.
///
/// Eksik olan tek şey görüntünün üzerindeki yazıydı. Üstelik tek
/// geçişli yoldaki yorum *"birleştirmeden sonra bindirmek bu sorunu
/// tamamen ortadan kaldırıyor"* diyerek tam da bu noktayı tarif
/// ediyordu; katman orada uygulanmıyordu.
public sealed class SegmentedCaptionTests
{
    private static IReadOnlyList<RenderPlanner.TimedLayer> Overlays(int count)
        => [.. Enumerable.Range(0, count).Select(i =>
            new RenderPlanner.TimedLayer(
                Path.Combine(Path.GetTempPath(), $"altyazi_{i}.png"),
                new TimeRange(new Ms(i * 1000), new Ms((i + 1) * 1000))))];

    private static RenderPlanner.Result PlanOver(int overlayCount)
    {
        var timeline = TimelineFactory.Valid();

        var paths = timeline.Scenes
            .Select(s => s.Visual.Asset.Sha256)
            .Concat(timeline.Audio.VoiceSegments.Select(v => v.Asset.Sha256))
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(
                sha => sha,
                _ => Path.Combine(Path.GetTempPath(), "girdi.png"),
                StringComparer.Ordinal);

        return RenderPlanner.PlanOverVideo(
            timeline, paths,
            Path.Combine(Path.GetTempPath(), "birlesik.mp4"),
            overlayCount == 0 ? null : Overlays(overlayCount));
    }

    /// ***ALTYAZI KATMANLARI GRAFA GİRİYOR.***
    ///
    /// BU DOSYANIN EN ÖNEMLİ TESTİ. Girmediğinde uzun videolar
    /// altyazısız çıkıyor ve hiçbir kontrol bunu görmüyor.
    [Fact]
    public void Katmanlar_GrafaGiriyor()
    {
        var plan = PlanOver(3);

        Assert.True(plan.IsSuccess, string.Join(" | ", plan.Issues));

        // Her katman bir GİRDİ olarak ekleniyor: birleştirilmiş video
        // bir girdi, üç altyazı karesi üç girdi daha.
        Assert.Equal(3, plan.Plan!.Graph.Inputs.Count(i => i.Path.Contains("altyazi_", StringComparison.Ordinal)));

        // Ve her biri bir `overlay` düğümü üretiyor.
        Assert.Equal(3, plan.Plan.Graph.Nodes.Count(n => string.Equals(n.Filter, "overlay", StringComparison.Ordinal)));
    }

    /// KATMAN YOKSA GRAF DA SADE KALIYOR.
    ///
    /// Kısa videoda `segmented` kapalı ve bu yol altyazısız da
    /// çağrılabiliyor; boş liste fazladan düğüm üretmemeli.
    [Fact]
    public void KatmanYok_FazladanDugumYok()
    {
        var plan = PlanOver(0);

        Assert.True(plan.IsSuccess, string.Join(" | ", plan.Issues));
        Assert.DoesNotContain(plan.Plan!.Graph.Nodes, n => string.Equals(n.Filter, "overlay", StringComparison.Ordinal));
    }

    /// ***KATMAN YALNIZCA KENDİ PENCERESİ KADAR ÜRETİLİYOR.***
    ///
    /// Tek geçişli yolda ölçülmüş bir hata: her katman videonun
    /// tamamı boyunca döngüye alınınca 48 saniyelik bir videoda 97
    /// altyazı için 140.000 kare üretiliyordu — tek render 31,5 GB
    /// bellek. Birleştirme sonrası yol aynı kuralı kullanmak zorunda,
    /// yoksa aynı bedel uzun videoda ödenir.
    [Fact]
    public void Katman_YalnizcaKendiPenceresi()
    {
        var plan = PlanOver(2);

        Assert.True(plan.IsSuccess);

        var layers = plan.Plan!.Graph.Inputs
            .Where(i => i.Path.Contains("altyazi_", StringComparison.Ordinal))
            .ToList();

        Assert.All(layers, i =>
        {
            Assert.True(i.Loop);
            Assert.NotNull(i.DurationSeconds);

            // Bir saniyelik pencere: videonun tamamı değil.
            Assert.True(i.DurationSeconds < 2.0, $"katman {i.DurationSeconds} sn üretiyor");
        });

        // İKİNCİ KATMAN KENDİ BAŞLANGICINDAN KAYDIRILIYOR: `-itsoffset`
        // olmadan girdiyi kırpmak katmanın zaman eksenini kaydırırdı.
        Assert.Contains(layers, i => i.OffsetSeconds is > 0);
    }
}
