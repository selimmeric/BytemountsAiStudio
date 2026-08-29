using System.Text.Json;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Time;

namespace BytemountsAiStudio.Nodes.Tests;

/// Bölüm işaretlerinin AÇIKLAMAYA ULAŞTIĞININ sınanması (P3-04).
///
/// ***BU DOSYANIN VAR OLMA SEBEBİ:*** `ChapterMarkers` yazılmış,
/// testlenmiş ve **hiçbir node çağırmıyordu**. Uzun videolarda bölüm
/// planı hesaplanıyor, timeline'da kesim olarak kullanılıyor, ama
/// "00:00 Giriş" satırları video **açıklamasına hiç yazılmıyordu**.
/// YouTube bölüm işaretlerini yalnızca açıklamadan okuduğu için
/// oynatıcıda hiçbir bölüm görünmüyordu — ve `ChapterMarkers`'ın kendi
/// yorumunun uyardığı gibi bu hata vermiyor, yalnızca hiç çıkmıyor.
public sealed class ChapterMarkerWiringTests
{
    private static Chapter Chapter(int index, string title, int startMs)
        => new()
        {
            Index = index,
            Title = title,
            Start = new Ms(startMs),
            TargetDuration = new Ms(120_000),
        };

    /* ---- gerçek sınırlara hizalama ---- */

    /// ***İŞARETLER GERÇEK SAHNE SINIRLARINA HİZALANIYOR.***
    ///
    /// BU DOSYANIN EN ÖNEMLİ TESTİ. Plan bir HEDEF veriyor; sahneler
    /// gerçek seslendirme sürelerinden doğuyor ve ikisi asla tam
    /// tutmuyor. Planı yazsaydık işaretler videonun içindeki gerçek
    /// geçişlerin saniyeler ötesine düşerdi: izleyici bir bölüme
    /// atlıyor ve önceki bölümün son cümlesini dinliyor.
    [Fact]
    public void Isaretler_GercekSinirlaraHizalaniyor()
    {
        // Plan: 0, 120.000, 240.000. Gerçek sahne sınırları biraz
        // kaymış: 118.400 ve 243.100.
        List<Chapter> chapters =
        [
            Chapter(0, "Açılış", 0),
            Chapter(1, "Keşif", 120_000),
            Chapter(2, "Sonuç", 240_000),
        ];

        int[] starts = [0, 118_400, 243_100];
        int[] ends = [118_400, 243_100, 360_000];

        var markers = ChapterMarkers.Align(chapters, starts, ends);

        Assert.Equal(3, markers.Count);
        Assert.Equal(0, markers[0].Start.Value);

        // PLANDAKİ 120.000 DEĞİL, GERÇEK SINIR 118.400.
        Assert.Equal(118_400, markers[1].Start.Value);
        Assert.Equal(243_100, markers[2].Start.Value);
        Assert.Equal("Keşif", markers[1].Title);
    }

    /// GİRİŞ İŞARETİ SIFIRDA ÜRETİLİYOR.
    ///
    /// YouTube ilk zaman damgası `0:00` değilse bölüm listesini HİÇ
    /// göstermiyor.
    [Fact]
    public void IlkIsaret_SifirdaBasliyor()
    {
        List<Chapter> chapters =
        [
            Chapter(0, "Keşif", 60_000),
            Chapter(1, "Sonuç", 180_000),
        ];

        var markers = ChapterMarkers.Align(chapters, [0, 58_000, 176_000], [58_000, 176_000, 300_000]);

        Assert.Equal(0, markers[0].Start.Value);
        Assert.Equal(ChapterMarkers.IntroTitle, markers[0].Title);
    }

    /* ---- açıklamaya yazılması ---- */

    /// ***İŞARETLER SEO AÇIKLAMASINA EKLENİYOR.***
    [Fact]
    public void Isaretler_AciklamayaEkleniyor()
    {
        var runContext = JsonSerializer.SerializeToElement(new
        {
            timeline = new
            {
                chapter_markers = new object[]
                {
                    new { start_ms = 0, title = "Giriş" },
                    new { start_ms = 118_400, title = "Keşif" },
                    new { start_ms = 243_100, title = "Sonuç" },
                },
            },
        });

        var built = SeoGenerateHandler.Build(
            """{"title":"Bir başlık","description":"Kısa açıklama.","tags":["a","b"]}""",
            "seo.generate@2#abcdef",
            runContext: runContext);

        Assert.True(built.IsSuccess, built.IsFailure ? built.Error.Message : string.Empty);

        var description = built.Value.GetProperty("description").GetString();

        Assert.NotNull(description);
        Assert.Contains("Kısa açıklama.", description, StringComparison.Ordinal);
        Assert.Contains("0:00 Giriş", description, StringComparison.Ordinal);
        Assert.Contains("1:58 Keşif", description, StringComparison.Ordinal);
        Assert.Contains("4:03 Sonuç", description, StringComparison.Ordinal);

        // KAYDA GEÇİYOR: bölüm listesi olmayan bir uzun videonun
        // sebebi (plan yok mu, kurallar mı tutmadı) sorulabilir olmalı.
        Assert.True(built.Value.GetProperty("chapter_markers").GetBoolean());
    }

    /// ***KISA VİDEODA AÇIKLAMA DEĞİŞMİYOR.***
    ///
    /// 48 saniyelik bir Shorts'ta bölüm diye bir şey yok; açıklamaya
    /// boş bir blok eklemek anlamsız olurdu.
    [Fact]
    public void BolumYok_AciklamaDegismiyor()
    {
        var built = SeoGenerateHandler.Build(
            """{"title":"Bir başlık","description":"Kısa açıklama.","tags":["a"]}""",
            "seo.generate@2#abcdef",
            runContext: JsonSerializer.SerializeToElement(new { }));

        Assert.True(built.IsSuccess);
        Assert.Equal("Kısa açıklama.", built.Value.GetProperty("description").GetString());
        Assert.False(built.Value.GetProperty("chapter_markers").GetBoolean());
    }

    /// ***ÜÇTEN AZ İŞARET YAZILMIYOR.***
    ///
    /// YouTube'un kuralı: iki bölümlü bir video için liste hiç
    /// görünmüyor. Yarım bir liste açıklamayı kirletip hiçbir şey
    /// kazandırmazdı.
    [Fact]
    public void IkiIsaret_Yazilmiyor()
    {
        var runContext = JsonSerializer.SerializeToElement(new
        {
            timeline = new
            {
                chapter_markers = new object[]
                {
                    new { start_ms = 0, title = "Giriş" },
                    new { start_ms = 60_000, title = "Sonuç" },
                },
            },
        });

        Assert.Null(SeoGenerateHandler.MarkerText(runContext));
    }

    /// ZAMAN DAMGASI BİÇİMİ PLATFORMUN İSTEDİĞİ GİBİ.
    ///
    /// `4:2` biçimi tanınmıyor; dakika ve saniye her zaman iki hane
    /// ve saat yalnızca gerekiyorsa yazılıyor.
    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(62_000, "1:02")]
    [InlineData(3_723_000, "1:02:03")]
    public void ZamanDamgasi_Bicimi(int ms, string expected)
        => Assert.Equal(expected, ChapterMarkers.Timestamp(new Ms(ms)));
}
