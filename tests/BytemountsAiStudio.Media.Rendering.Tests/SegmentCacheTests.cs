using BytemountsAiStudio.Core.Assets;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Time;
using BytemountsAiStudio.Media.Rendering;
using BytemountsAiStudio.Media.Timeline;

namespace BytemountsAiStudio.Media.Rendering.Tests;

/// Segment önbelleği anahtarlarının testleri (P2-11).
///
/// Kabul kriteri: **tek sahne değişince yalnız o segment yeniden
/// render ediliyor.** Render bu hattın en yavaş adımı ve yanlış bir
/// anahtar ya bayat kare gösteriyor ya da önbelleği tamamen işe
/// yaramaz kılıyor.
public sealed class SegmentCacheTests
{
    private static AssetRef Asset(string hex)
        => AssetRef.TryCreate(hex.PadRight(64, '0')).Value;

    private static Scene Scene(int index, int startMs, int durationMs, string asset = "a1")
        => new()
        {
            Index = index,
            Range = TimeRange.FromDuration(new Ms(startMs), new Ms(durationMs)),
            VoiceSegmentIds = ["s0"],
            Visual = new SceneVisual { Asset = Asset(asset) },
        };

    private static readonly Canvas Shorts = Canvas.Shorts1080;

    [Fact]
    public void AyniSahne_AyniAnahtar()
    {
        var first = SegmentCache.KeyFor(Scene(0, 0, 3000), Shorts);
        var second = SegmentCache.KeyFor(Scene(0, 0, 3000), Shorts);

        Assert.Equal(first.Value, second.Value);
    }

    /// MUTLAK ZAMAN ANAHTARA GİRMİYOR.
    ///
    /// Girseydi, önündeki bir sahne uzayınca sonraki BÜTÜN segmentler
    /// geçersiz olurdu — yani önbellek hiç yokmuş gibi davranırdı.
    [Fact]
    public void SahneKaymasi_AnahtariDegistirmiyor()
    {
        var early = SegmentCache.KeyFor(Scene(2, 0, 3000), Shorts);
        var late = SegmentCache.KeyFor(Scene(2, 9000, 3000), Shorts);

        Assert.Equal(early.Value, late.Value);
    }

    /// SIRA NUMARASI da anahtara girmiyor: sahne sırası değişince
    /// görüntüsü hiç değişmemiş segmentler yeniden render edilmemeli.
    [Fact]
    public void SiraNumarasi_AnahtariDegistirmiyor()
    {
        Assert.Equal(
            SegmentCache.KeyFor(Scene(0, 0, 3000), Shorts).Value,
            SegmentCache.KeyFor(Scene(7, 0, 3000), Shorts).Value);
    }

    /// Görüntüyü belirleyen HER ŞEY anahtarı değiştirmeli: eksik
    /// bırakılan tek bir alan, o alan değiştiğinde BAYAT bir segmentin
    /// kullanılması demek — ve bayat kare sessiz olduğu için hiç
    /// önbellek olmamasından kötü.
    [Fact]
    public void Sure_AnahtariDegistiriyor()
    {
        Assert.NotEqual(
            SegmentCache.KeyFor(Scene(0, 0, 3000), Shorts).Value,
            SegmentCache.KeyFor(Scene(0, 0, 5000), Shorts).Value);
    }

    [Fact]
    public void Gorsel_AnahtariDegistiriyor()
    {
        Assert.NotEqual(
            SegmentCache.KeyFor(Scene(0, 0, 3000, "a1"), Shorts).Value,
            SegmentCache.KeyFor(Scene(0, 0, 3000, "b2"), Shorts).Value);
    }

    [Fact]
    public void Tuval_AnahtariDegistiriyor()
    {
        Assert.NotEqual(
            SegmentCache.KeyFor(Scene(0, 0, 3000), Shorts).Value,
            SegmentCache.KeyFor(Scene(0, 0, 3000), new Canvas(1920, 1080, 30)).Value);
    }

    [Fact]
    public void KenBurns_AnahtariDegistiriyor()
    {
        var still = Scene(0, 0, 3000);

        var moving = still with
        {
            Visual = still.Visual with
            {
                Motion = new KenBurns { FromScale = 1.0, ToScale = 1.15 },
            },
        };

        Assert.NotEqual(
            SegmentCache.KeyFor(still, Shorts).Value,
            SegmentCache.KeyFor(moving, Shorts).Value);
    }

    [Fact]
    public void UstYazi_AnahtariDegistiriyor()
    {
        var plain = Scene(0, 0, 3000);

        var titled = plain with
        {
            Overlays =
            [
                new TextOverlay
                {
                    Text = "Göbeklitepe",
                    StyleRef = "title",
                    Range = TimeRange.FromDuration(Ms.Zero, new Ms(2000)),
                },
            ],
        };

        Assert.NotEqual(
            SegmentCache.KeyFor(plain, Shorts).Value,
            SegmentCache.KeyFor(titled, Shorts).Value);
    }

    /// Üst yazının zamanı SAHNEYE GÖRE ölçülüyor: sahne kayınca yazı
    /// da kayıyor ama görüntü aynı kalıyor.
    [Fact]
    public void UstYaziZamani_SahneyeGoreOlculuyor()
    {
        var early = Scene(0, 0, 3000) with
        {
            Overlays =
            [
                new TextOverlay
                {
                    Text = "x", StyleRef = "title",
                    Range = TimeRange.FromDuration(Ms.Zero, new Ms(1000)),
                },
            ],
        };

        var late = Scene(0, 9000, 3000) with
        {
            Overlays =
            [
                new TextOverlay
                {
                    Text = "x", StyleRef = "title",
                    Range = TimeRange.FromDuration(new Ms(9000), new Ms(1000)),
                },
            ],
        };

        Assert.Equal(SegmentCache.KeyFor(early, Shorts).Value, SegmentCache.KeyFor(late, Shorts).Value);
    }

    /// Font zinciri değişince çizilen yazı da değişebiliyor (§20.4).
    [Fact]
    public void FontZinciri_AnahtariDegistiriyor()
    {
        Assert.NotEqual(
            SegmentCache.KeyFor(Scene(0, 0, 3000), Shorts, ["Inter"]).Value,
            SegmentCache.KeyFor(Scene(0, 0, 3000), Shorts, ["Noto Sans"]).Value);
    }

    /// KABUL KRİTERİ, sayı olarak: tek sahne değişince yalnız o
    /// segment yeniden render ediliyor.
    [Fact]
    public void TekSahneDegisti_YalnizOSegmentYenileniyor()
    {
        var before = new[]
        {
            SegmentCache.KeyFor(Scene(0, 0, 3000, "a1"), Shorts),
            SegmentCache.KeyFor(Scene(1, 3000, 3000, "b2"), Shorts),
            SegmentCache.KeyFor(Scene(2, 6000, 3000, "c3"), Shorts),
        };

        // Ortadaki sahnenin görseli değişti; süreler aynı kaldı.
        var after = new[]
        {
            SegmentCache.KeyFor(Scene(0, 0, 3000, "a1"), Shorts),
            SegmentCache.KeyFor(Scene(1, 3000, 3000, "d4"), Shorts),
            SegmentCache.KeyFor(Scene(2, 6000, 3000, "c3"), Shorts),
        };

        var stale = SegmentCache.Stale(after, [.. before.Select(k => k.Value)]);

        Assert.Single(stale);
        Assert.Equal(1, stale[0].Index);
        Assert.Equal(2, SegmentCache.Reused(after, stale));
    }

    /// Önbellek boşsa her şey yeniden render ediliyor — ilk koşunun
    /// hâli bu.
    [Fact]
    public void BosOnbellek_HerSeyYeniden()
    {
        var keys = new[]
        {
            SegmentCache.KeyFor(Scene(0, 0, 3000), Shorts),
            SegmentCache.KeyFor(Scene(1, 3000, 3000, "b2"), Shorts),
        };

        Assert.Equal(2, SegmentCache.Stale(keys, []).Count);
        Assert.Equal(0, SegmentCache.Reused(keys, SegmentCache.Stale(keys, [])));
    }

    /// Sürüm değişince BÜTÜN önbellek geçersiz: düzeltilen bir çizim
    /// hatası eski segmentlerde yaşamaya devam ederse, o hata artık
    /// kodda görünmediği için teşhis edilemez.
    [Fact]
    public void AnahtarSurumu_IceriyOr()
    {
        Assert.StartsWith(
            SegmentCache.KeyFor(Scene(0, 0, 3000), Shorts).Value[..1],
            SegmentCache.KeyFor(Scene(0, 0, 3000), Shorts).Value,
            StringComparison.Ordinal);

        Assert.Equal(1, SegmentCache.Version);
    }
}
