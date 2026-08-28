using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Time;

namespace BytemountsAiStudio.Core.Tests;

/// Bölüm işaretleri (P3-04).
///
/// YouTube'un kuralları katı ve bir tanesi bile tutmazsa bölümler HİÇ
/// görünmüyor — üstelik hata da vermiyor. Açıklamaya yazdığınız
/// satırlar orada duruyor ama oynatıcıda hiçbir şey çıkmıyor: sessiz
/// başarısızlığın ders kitabı örneği. Bu yüzden kurallar burada
/// SINANIYOR, umut edilmiyor.
public sealed class ChapterMarkersTests
{
    private static Chapter Chapter(int index, string title, int startMs, int durationMs)
        => new()
        {
            Index = index,
            Title = title,
            Start = new Ms(startMs),
            TargetDuration = new Ms(durationMs),
        };

    private static IReadOnlyList<Chapter> Valid()
        =>
        [
            Chapter(0, "Kesif", 30_000, 120_000),
            Chapter(1, "Yapim", 150_000, 120_000),
            Chapter(2, "Miras", 270_000, 120_000),
        ];

    /// ZAMAN DAMGASI BİÇİMİ: dakika ve saniye her zaman iki hane,
    /// saat yalnızca gerekiyorsa. `4:2` biçimi platformda tanınmıyor.
    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(9_000, "0:09")]
    [InlineData(65_000, "1:05")]
    [InlineData(252_000, "4:12")]
    [InlineData(600_000, "10:00")]
    [InlineData(3_903_000, "1:05:03")]
    public void ZamanDamgasi_DogruBicimde(int ms, string expected)
        => Assert.Equal(expected, ChapterMarkers.Timestamp(new Ms(ms)));

    /// İLK İŞARET SIFIRDAN BAŞLIYOR.
    ///
    /// Plan girişe yer ayırdığı için ilk bölüm sıfırda başlamıyor.
    /// Bunu olduğu gibi yazmak, listeyi TAMAMEN görünmez kılardı —
    /// YouTube ilk damga `0:00` değilse hiçbir şey göstermiyor.
    [Fact]
    public void GirisIcin_SifirdanIsaretUretiliyor()
    {
        var markers = ChapterMarkers.Build(Valid(), new Ms(400_000));

        Assert.Equal(0, markers[0].Start.Value);
        Assert.Equal(ChapterMarkers.IntroTitle, markers[0].Title);
        Assert.Equal(4, markers.Count);
    }

    /// Bölüm zaten sıfırda başlıyorsa fazladan giriş eklenmiyor:
    /// aynı ana iki işaret koymak, aralık kuralını da çiğnerdi.
    [Fact]
    public void SifirdanBaslayanBolum_FazladanGirisEklenmiyor()
    {
        var markers = ChapterMarkers.Build(
            [Chapter(0, "Basla", 0, 120_000), Chapter(1, "Devam", 120_000, 120_000),
             Chapter(2, "Bitir", 240_000, 120_000)],
            new Ms(400_000));

        Assert.Equal(3, markers.Count);
        Assert.Equal("Basla", markers[0].Title);
    }

    [Fact]
    public void GecerliPlan_MetinUretiyor()
    {
        var text = ChapterMarkers.Render(Valid(), new Ms(400_000));

        Assert.NotNull(text);

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .ToList();

        Assert.Equal(4, lines.Count);
        Assert.StartsWith("0:00 ", lines[0], StringComparison.Ordinal);
        Assert.Equal("0:30 Kesif", lines[1]);
        Assert.Equal("2:30 Yapim", lines[2]);
        Assert.Equal("4:30 Miras", lines[3]);
    }

    /// ÜÇ İŞARETTEN AZ: liste hiç görünmüyor.
    [Fact]
    public void CokAzIsaret_Reddediliyor()
    {
        var chapters = new[] { Chapter(0, "Tek", 30_000, 120_000) };

        var problems = ChapterMarkers.Validate(
            ChapterMarkers.Build(chapters, new Ms(200_000)), new Ms(200_000));

        Assert.NotEmpty(problems);
        Assert.Contains(problems, p => p.Contains("en az", StringComparison.Ordinal));

        // Render `null` dönüyor: yarım bir liste hiç görünmüyor ve
        // "yazdım ama çıkmıyor" sorusunun cevabı hiçbir yerde olmazdı.
        Assert.Null(ChapterMarkers.Render(chapters, new Ms(200_000)));
    }

    /// ON SANİYEDEN KISA ARALIK: liste tamamen geçersiz oluyor.
    [Fact]
    public void CokYakinIsaretler_Reddediliyor()
    {
        var chapters = new[]
        {
            Chapter(0, "Bir", 1_000, 3_000),
            Chapter(1, "Iki", 4_000, 3_000),
            Chapter(2, "Uc", 7_000, 3_000),
        };

        var problems = ChapterMarkers.Validate(
            ChapterMarkers.Build(chapters, new Ms(20_000)), new Ms(20_000));

        Assert.NotEmpty(problems);
        Assert.Contains(problems, p => p.Contains("sn sonra", StringComparison.Ordinal));
    }

    /// SON İŞARET VİDEONUN İÇİNDE OLMALI.
    ///
    /// Plan bir HEDEF; gerçek süre seslendirmeden sonra ölçülüyor
    /// (ADR-006). İkisi ayrıştığında son işaret videonun dışına
    /// düşebiliyor ve o hâlde liste geçersiz.
    [Fact]
    public void SureninDisindaIsaret_Reddediliyor()
    {
        var problems = ChapterMarkers.Validate(
            ChapterMarkers.Build(Valid(), new Ms(200_000)), new Ms(200_000));

        Assert.Contains(problems, p => p.Contains("ama video", StringComparison.Ordinal));
    }

    /// Geçerli bir plan hiçbir uyarı üretmiyor.
    [Fact]
    public void GecerliPlan_UyariYok()
        => Assert.Empty(ChapterMarkers.Validate(
            ChapterMarkers.Build(Valid(), new Ms(400_000)), new Ms(400_000)));

    /// PLANLAYICININ ÇIKTISI İŞARET KURALLARINA UYUYOR.
    ///
    /// İki parça ayrı ayrı doğru olabilir ve birlikte yanlış: bölüm
    /// planı 90 saniyelik bölümlere izin veriyor, işaret kuralı 10
    /// saniyelik aralık istiyor. Bu test ikisinin aynı dünyada
    /// yaşadığını doğruluyor.
    [Theory]
    [InlineData(3, 8)]
    [InlineData(5, 12)]
    [InlineData(7, 15)]
    public void PlanlayiciCiktisi_IsaretKurallarinaUyuyor(int sections, int minutes)
    {
        var plan = ChapterPlanner.Plan(
            [.. Enumerable.Range(1, sections).Select(i => ($"Bolum {i}", (string?)null))],
            new Ms(minutes * 60 * 1000)).Value;

        var markers = ChapterMarkers.Build(plan.Chapters, plan.TotalDuration);

        Assert.Empty(ChapterMarkers.Validate(markers, plan.TotalDuration));
        Assert.NotNull(ChapterMarkers.Render(plan.Chapters, plan.TotalDuration));
    }
}
