using BytemountsAiStudio.Media.Ir;
using BytemountsAiStudio.Media.Planning;
using BytemountsAiStudio.Media.Timeline;

namespace BytemountsAiStudio.Media.Tests;

/// Kalıcı katman (filigran) testleri (P1-20).
///
/// Müzik yatağıyla aynı hikâye: `PersistentLayers` modelde vardı, render
/// onu hiç görmüyordu. Kanal ayarında filigran açık görünüyor, videoda
/// filigran yok, ve hiçbir şey hata vermiyordu.
public sealed class PersistentLayerTests
{
    private static TimelineDocument WithLayers(params PersistentLayer[] layers)
    {
        var timeline = TimelineFactory.Valid();

        return timeline with { PersistentLayers = layers };
    }

    private static Dictionary<string, string> Paths(TimelineDocument t)
    {
        var paths = t.Scenes.Select(s => s.Visual.Asset.Sha256)
            .Concat(t.Audio.VoiceSegments.Select(s => s.Asset.Sha256))
            .Concat(t.PersistentLayers.Select(l => l.Asset.Sha256))
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(sha => sha, sha => $"/tmp/{sha[..8]}.png", StringComparer.Ordinal);

        return paths;
    }

    private static RenderPlanner.Result Plan(params PersistentLayer[] layers)
    {
        var timeline = WithLayers(layers);

        return RenderPlanner.Plan(timeline, Paths(timeline));
    }

    private static PersistentLayer Watermark(Anchor anchor = Anchor.TopRight, double opacity = 0.55) => new()
    {
        Asset = TimelineFactory.Asset('c'),
        Role = "watermark",
        Anchor = anchor,
        Opacity = opacity,
    };

    private static IReadOnlyList<string> Filters(RenderPlanner.Result plan)
        => [.. plan.Plan!.Graph.Nodes.Select(n => n.Filter)];

    /// Katmansız timeline eskisi gibi çalışmalı.
    [Fact]
    public void KatmanYok_GrafDegismez()
    {
        var plan = Plan();

        Assert.True(plan.IsSuccess, string.Join(" | ", plan.Issues));
        Assert.DoesNotContain("colorchannelmixer", Filters(plan), StringComparer.Ordinal);
    }

    /// ASIL TEST: filigran artık grafiğe GİRİYOR.
    [Fact]
    public void Filigran_GrafigeGirer()
    {
        var plan = Plan(Watermark());

        Assert.True(plan.IsSuccess, string.Join(" | ", plan.Issues));
        Assert.Contains(plan.Plan!.Graph.Inputs, i => i.Id == "layer0");
        Assert.Contains("overlay", Filters(plan), StringComparer.Ordinal);
    }

    [Fact]
    public void Filigran_UretilenGrafGecerli()
    {
        var plan = Plan(Watermark());

        var issues = GraphValidator.Validate(plan.Plan!.Graph);

        Assert.True(issues.Count == 0, string.Join(" | ", issues));
    }

    /// Alfa kanalı olmayan bir görselde `colorchannelmixer` saydamlık
    /// üretemiyor ve filigran tam opak çıkıyor. PNG'de alfa var, JPEG'de
    /// yok, ve filigranın hangi biçimde geleceğini önceden bilmiyoruz.
    [Fact]
    public void Saydamlik_OncesindeRgbaZorlanir()
    {
        var nodes = Plan(Watermark()).Plan!.Graph.Nodes.ToList();

        var formatIndex = nodes.FindIndex(n => n.Filter == "format" && n.Inputs[0].Id == "layer0");
        var mixIndex = nodes.FindIndex(n => n.Filter == "colorchannelmixer");

        Assert.True(formatIndex >= 0, "rgba format dugumu yok");
        Assert.True(mixIndex > formatIndex, "saydamlik rgba'dan once uygulanmis");
    }

    [Fact]
    public void Saydamlik_FiltreyeGecer()
    {
        var node = Plan(Watermark(opacity: 0.4)).Plan!.Graph.Nodes
            .Single(n => n.Filter == "colorchannelmixer");

        Assert.Contains(node.Args, a => a.Value.Contains("0.4", StringComparison.Ordinal));
    }

    /// Filigran ALTYAZIDAN SONRA biniyor: altında kalması onu kısmen
    /// görünmez yapardı ve filigranın tek işi görünmek.
    [Fact]
    public void Filigran_EnUstteKalir()
    {
        var timeline = WithLayers(Watermark());

        var overlay = new RenderPlanner.TimedLayer(
            "/tmp/caption.png",
            new Core.Time.TimeRange(Core.Time.Ms.Zero, new Core.Time.Ms(2000)));

        var plan = RenderPlanner.Plan(timeline, Paths(timeline), [overlay]);

        Assert.True(plan.IsSuccess, string.Join(" | ", plan.Issues));

        var nodes = plan.Plan!.Graph.Nodes.ToList();

        var captionIndex = nodes.FindIndex(n => n.Filter == "overlay" && n.Inputs[1].Id == "ovl0");
        var layerIndex = nodes.FindIndex(n => n.Filter == "overlay" && n.Inputs[1].Id.StartsWith("layer0", StringComparison.Ordinal));

        Assert.True(captionIndex >= 0, "altyazi katmani yok");
        Assert.True(layerIndex > captionIndex, "filigran altyazinin altinda kalmis");
    }

    // ---- Bağlantı noktaları ----

    /// Konum İFADE olarak veriliyor: `W`/`w` FFmpeg'in ana ve katman
    /// genişlikleri. Sabit piksel yazsaydık filigranın kendi boyutunu
    /// bilmemiz gerekirdi ve o bilgi planlama anında yok.
    [Theory]
    [InlineData(Anchor.TopLeft, "40", "40")]
    [InlineData(Anchor.TopRight, "W-w-40", "40")]
    [InlineData(Anchor.BottomLeft, "40", "H-h-40")]
    [InlineData(Anchor.BottomRight, "W-w-40", "H-h-40")]
    [InlineData(Anchor.BottomCenter, "(W-w)/2", "H-h-40")]
    [InlineData(Anchor.Center, "(W-w)/2", "(H-h)/2")]
    public void BaglantiNoktasi_DogruIfadeUretir(Anchor anchor, string x, string y)
    {
        var (actualX, actualY) = RenderPlanner.AnchorExpression(Watermark(anchor));

        Assert.Equal(x, actualX);
        Assert.Equal(y, actualY);
    }

    [Fact]
    public void OzelKenarBoslugu_IfadeyeGecer()
    {
        var layer = Watermark(Anchor.BottomRight) with { MarginX = 12, MarginY = 90 };

        var (x, y) = RenderPlanner.AnchorExpression(layer);

        Assert.Equal("W-w-12", x);
        Assert.Equal("H-h-90", y);
    }

    // ---- Çoklu katman ----

    [Fact]
    public void IkiKatman_IkisiDeBinuyor()
    {
        var plan = Plan(
            Watermark(Anchor.TopRight),
            new PersistentLayer
            {
                Asset = TimelineFactory.Asset('d'),
                Role = "logo",
                Anchor = Anchor.BottomLeft,
            });

        Assert.True(plan.IsSuccess, string.Join(" | ", plan.Issues));
        Assert.Contains(plan.Plan!.Graph.Inputs, i => i.Id == "layer0");
        Assert.Contains(plan.Plan.Graph.Inputs, i => i.Id == "layer1");
    }

    // ---- Eksik varlık ----

    [Fact]
    public void KatmanVarligiCozumlenemezse_SorunKaydedilir()
    {
        var timeline = WithLayers(Watermark());

        // Filigran yolu bilerek dışarıda bırakılıyor.
        var paths = timeline.Scenes.Select(s => s.Visual.Asset.Sha256)
            .Concat(timeline.Audio.VoiceSegments.Select(s => s.Asset.Sha256))
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(sha => sha, sha => $"/tmp/{sha[..8]}.bin", StringComparer.Ordinal);

        var plan = RenderPlanner.Plan(timeline, paths);

        Assert.False(plan.IsSuccess);
        Assert.Contains(plan.Issues, i => i.Code == "plan.unresolved_asset");
    }
}
