using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Media.Ir;
using BytemountsAiStudio.Media.Planning;
using BytemountsAiStudio.Media.Timeline;

namespace BytemountsAiStudio.Media.Tests;

/// Tuvale göre render ön ayarı (P3-02).
///
/// GERÇEK HATA: `OutputSpec.Preset` HER videoda `"shorts-1080x1920"`
/// yazıyordu — 1920×1080 çıkan on dakikalık uzun videoda da. Ad
/// hiçbir yerde okunmuyordu, o yüzden kimse fark etmiyordu; ama
/// çıktının yanında duran ve çıktıyı YANLIŞ ANLATAN bir kayıt, hiç
/// kayıt olmamasından kötü.
public sealed class RenderPresetTests
{
    private static Dictionary<string, string> Paths(TimelineDocument t)
        => t.Scenes.Select(s => s.Visual.Asset.Sha256)
            .Concat(t.Audio.VoiceSegments.Select(s => s.Asset.Sha256))
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(sha => sha, sha => $"/tmp/{sha[..8]}.bin", StringComparer.Ordinal);

    /// AD TUVALDEN TÜRÜYOR.
    [Theory]
    [InlineData(1080, 1920, "shorts-1080x1920")]
    [InlineData(1920, 1080, "video-1920x1080")]
    public void OnAyarAdi_TuvaldenTuruyor(int width, int height, string expected)
        => Assert.Equal(expected, RenderPreset.ForCanvas(Canvas.ForAspect(width < height ? "9:16" : "16:9")).Preset);

    /// YATAY VİDEODA ANAHTAR KARE SINIRI VAR, DİKEYDE YOK.
    ///
    /// Oynatıcı yalnızca anahtar kareye atlayabiliyor. Uzun videoda
    /// bölüm işaretleri üretiyoruz ve atlamanın nereye düşeceğini
    /// şansa bırakmak, işaretlerin yarısını vermekti.
    ///
    /// 48 saniyelik bir Shorts'ta kimse atlamıyor ve daha uzun GOP
    /// daha iyi sıkıştırma demek: sınırı her yere koymak, hiçbir
    /// faydası olmayan bir yerde dosyayı büyütürdü.
    [Fact]
    public void YatayVideo_AnahtarKareSiniriVar()
    {
        Assert.Equal(
            RenderPreset.LandscapeKeyframeSeconds,
            RenderPreset.ForCanvas(Canvas.ForAspect("16:9")).KeyframeIntervalSeconds);

        Assert.Null(RenderPreset.ForCanvas(Canvas.ForAspect("9:16")).KeyframeIntervalSeconds);
    }

    /// SINIR FFMPEG KOMUTUNA ULAŞIYOR.
    ///
    /// Bu depoda tekrar eden hata sınıfı: bir ayar yazılıyor ve
    /// hiçbir şey okumuyor. `KeyframeIntervalSeconds` timeline'da
    /// dursa da `-g` argümanına dönüşmezse hiçbir kare değişmez.
    [Fact]
    public void AnahtarKareSiniri_KomutaUlasiyor()
    {
        var timeline = Landscape();
        var plan = RenderPlanner.Plan(timeline, Paths(timeline));

        Assert.True(plan.IsSuccess, string.Join(" | ", plan.Issues));

        var command = FilterGraphEmitter.Emit(plan.Plan!.Graph, "/tmp/filtre.txt", "/tmp/cikti.mp4", plan.Plan.Output);
        var args = command.Arguments.ToList();
        var index = args.IndexOf("-g");

        Assert.True(index >= 0, "Anahtar kare aralığı komuta hiç girmemiş.");

        // 2 saniye × 30 kare = 60.
        Assert.Equal("60", args[index + 1]);
    }

    /// SINIR YOKSA ARGÜMAN DA YOK.
    ///
    /// Boş bir `-g` ya da `-g 0` göndermek, ffmpeg'e her kareyi
    /// anahtar kare yaptırırdı ve dosya birkaç kat büyürdü.
    [Fact]
    public void SinirYok_ArgumanYok()
    {
        var timeline = TimelineFactory.Valid();
        var plan = RenderPlanner.Plan(timeline, Paths(timeline));

        Assert.True(plan.IsSuccess, string.Join(" | ", plan.Issues));

        var command = FilterGraphEmitter.Emit(plan.Plan!.Graph, "/tmp/filtre.txt", "/tmp/cikti.mp4", plan.Plan.Output);

        Assert.DoesNotContain("-g", command.Arguments);
    }

    /// SIFIR VE NEGATİF SANİYE YOK SAYILIYOR.
    ///
    /// Yapılandırmaya sıfır yazan biri, "anahtar kare yok" demek
    /// istiyor — "her kare anahtar kare" değil. İkisini karıştırmak
    /// dosyayı birkaç kat büyütürdü.
    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void SifirVeyaNegatifAralik_YokSayiliyor(double seconds)
    {
        var timeline = Landscape() with
        {
            Output = RenderPreset.ForCanvas(Canvas.ForAspect("16:9")) with
            {
                KeyframeIntervalSeconds = seconds,
            },
        };

        var plan = RenderPlanner.Plan(timeline, Paths(timeline));
        var command = FilterGraphEmitter.Emit(plan.Plan!.Graph, "/tmp/filtre.txt", "/tmp/cikti.mp4", plan.Plan.Output);

        Assert.DoesNotContain("-g", command.Arguments);
    }

    private static TimelineDocument Landscape()
    {
        var timeline = TimelineFactory.Valid();
        var canvas = Canvas.ForAspect("16:9");

        return timeline with { Canvas = canvas, Output = RenderPreset.ForCanvas(canvas) };
    }
}
