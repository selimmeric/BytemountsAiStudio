using BytemountsAiStudio.Core.Content;

namespace BytemountsAiStudio.Core.Tests;

/// Bölüm sınırlarının sahne sınırlarına eşlenmesi (P3-04).
///
/// SORUN ŞU: bölüm planı bir HEDEF veriyor (`start_ms`), sahneler ise
/// gerçek seslendirme sürelerinden doğuyor. İkisi asla tam tutmuyor.
/// Eşitlik araması hiçbir sınır bulamazdı ve "bölüm geçişleri var"
/// iddiası sessizce boş kalırdı — video yine üretilir, kimse bir şey
/// fark etmezdi.
public sealed class ChapterBoundaryTests
{
    /// HEDEF TUTMUYOR AMA SINIR BULUNUYOR.
    ///
    /// Plan 144.000 diyor, en yakın sahne sınırı 141.320. Tolerans
    /// aramak yerine en yakını seçmek, sapmanın büyüklüğünden
    /// bağımsız çalışıyor.
    [Fact]
    public void PlanTutmasaBile_EnYakinSinirSeciliyor()
    {
        var ends = new[] { 36_100, 141_320, 250_000, 361_400, 470_000, 603_000 };
        var starts = new[] { 36_000, 144_000, 252_000, 360_000, 468_000 };

        var marked = ChapterBoundaries.Match(ends, starts);

        Assert.Equal([0, 1, 2, 3, 4], marked.Order());
    }

    /// SON SAHNE HİÇ İŞARETLENMİYOR.
    ///
    /// Onun geçişi videonun KAPANIŞI, bir bölüm geçişi değil. İkisini
    /// aynı yere yazmak kapanışı bölüm geçişi uzunluğuna kısaltırdı.
    [Fact]
    public void SonSahne_Isaretlenmiyor()
    {
        var ends = new[] { 1_000, 2_000, 3_000 };

        // Son sahnenin sonuna denk gelen bir bölüm başlangıcı.
        var marked = ChapterBoundaries.Match(ends, [3_000]);

        Assert.DoesNotContain(2, marked);
    }

    /// SIFIRDAN BAŞLAYAN BÖLÜM SINIR DEĞİL: videonun başında "önceki
    /// bölüm" yok ve orası zaten açılma geçişi.
    [Theory]
    [InlineData(0)]
    [InlineData(-500)]
    public void SifirVeyaOncesi_SinirSayilmiyor(int start)
        => Assert.Empty(ChapterBoundaries.Match([1_000, 2_000, 3_000], [start]));

    /// İKİ SAHNEDEN AZSA HİÇ SINIR OLAMAZ.
    ///
    /// Tek sahnelik bir videoda "bölüm geçişi" yeri yok; kod bunu
    /// aramaya kalksaydı son sahneyi işaretlerdi ve kapanışı bozardı.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void BirVeyaSifirSahne_SinirYok(int count)
    {
        var ends = Enumerable.Range(1, count).Select(i => i * 1_000).ToArray();

        Assert.Empty(ChapterBoundaries.Match(ends, [500]));
    }

    /// İKİ BÖLÜM AYNI SINIRA DÜŞEBİLİR ve bu bir hata değil.
    ///
    /// Çok kısa bir bölüm, birkaç uzun sahnenin arasına sıkışabiliyor.
    /// Sınır ya bölüm sınırı ya değil; "iki kere bölüm sınırı" diye
    /// bir şey yok.
    [Fact]
    public void IkiBolumAyniSinir_TekIsaret()
    {
        var marked = ChapterBoundaries.Match([1_000, 10_000, 20_000], [1_100, 1_200]);

        Assert.Single(marked);
        Assert.Contains(0, marked);
    }

    /// AYNI GİRDİ HER ZAMAN AYNI SONUÇ.
    ///
    /// İki sınır tam eşit uzaklıktaysa seçim belirli olmalı, yoksa
    /// aynı plan iki farklı video üretirdi ve fark ancak iki koşuyu
    /// yan yana koyan biri tarafından görülürdü.
    [Fact]
    public void EsitUzaklik_BelirliSecim()
    {
        // 1500, hem 1000'e hem 2000'e 500 uzaklıkta.
        var first = ChapterBoundaries.Match([1_000, 2_000, 3_000], [1_500]);
        var second = ChapterBoundaries.Match([1_000, 2_000, 3_000], [1_500]);

        Assert.Equal(first.Order(), second.Order());
        Assert.Contains(0, first);
    }

    /// BÖLÜM YOKSA SINIR DA YOK: kısa video tamamen geçerli.
    [Fact]
    public void BolumYok_SinirYok()
        => Assert.Empty(ChapterBoundaries.Match([1_000, 2_000, 3_000], []));
}
