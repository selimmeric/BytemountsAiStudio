using BytemountsAiStudio.Core.Learning;

namespace BytemountsAiStudio.Core.Tests;

/// Deney çerçevesi (P5-02).
///
/// NEDEN İSTATİSTİK: "A %4,2 aldı, B %4,8 aldı, B kazandı" cümlesi
/// ölçüme değil GÜRÜLTÜYE dayanabiliyor. Kararı veriye dayandırmayan
/// bir öğrenme döngüsü, öğrendiğini SANIYOR: her hafta bir "kazanan"
/// ilan ediyor, stratejiyi ona göre değiştiriyor ve aslında rastgele
/// yürüyor.
public sealed class ExperimentTests
{
    /* ---- istatistik doğru mu ---- */

    /// NORMAL DAĞILIM BİLİNEN DEĞERLERİ VERİYOR.
    ///
    /// Yaklaşım fonksiyonları sessizce yanlış olabiliyor ve yanlış
    /// bir p-değeri, yanlış bir strateji kararı demek. Bilinen
    /// noktalarla sınanıyor.
    [Theory]
    [InlineData(0.0, 0.5)]
    [InlineData(1.0, 0.8413)]
    [InlineData(1.96, 0.9750)]
    [InlineData(-1.96, 0.0250)]
    [InlineData(2.576, 0.9950)]
    public void NormalDagilim_BilinenDegerler(double z, double expected)
        => Assert.Equal(expected, Significance.NormalCdf(z), 3);

    /// TERS NORMAL DE BİLİNEN DEĞERLERİ VERİYOR.
    [Theory]
    [InlineData(0.975, 1.960)]
    [InlineData(0.80, 0.8416)]
    [InlineData(0.95, 1.6449)]
    public void TersNormal_BilinenDegerler(double p, double expected)
        => Assert.Equal(expected, Significance.InverseNormalCdf(p), 3);

    /// AYNI ORAN, FARK YOK.
    [Fact]
    public void AyniOran_PDegeriYuksek()
        => Assert.True(Significance.PValue(50, 1000, 50, 1000) > 0.9);

    /// BÜYÜK VE GERÇEK FARK YAKALANIYOR.
    ///
    /// Onbin gösterimde %4 ile %6 arasındaki fark tesadüf olamaz.
    [Fact]
    public void BuyukFark_Yakalaniyor()
        => Assert.True(Significance.PValue(400, 10_000, 600, 10_000) < 0.001);

    /// KÜÇÜK ÖRNEKLEMDE AYNI ORAN FARKI ANLAMLI DEĞİL.
    ///
    /// ASIL KORUNAN ŞEY BU: yüz gösterimde %4 ile %6, madenî para
    /// atışıyla üretilebilecek bir fark. Testin bunu "anlamlı"
    /// saymaması gerekiyor — yoksa çerçeve gürültüyü öğreniyor.
    [Fact]
    public void KucukOrneklem_AyniFarkAnlamliDegil()
        => Assert.True(Significance.PValue(4, 100, 6, 100) > 0.05);

    /// DENENMEMİŞ VARYANT HAKKINDA KONUŞULMUYOR.
    ///
    /// Sıfır döndürmek "kesin fark var" olurdu ve bu, veri yokken en
    /// tehlikeli cevap.
    [Fact]
    public void DenenmemisVaryant_FarkIddiaEtmiyor()
        => Assert.Equal(1.0, Significance.PValue(10, 100, 0, 0));

    /* ---- örneklem hesabı ---- */

    /// KÜÇÜK FARKI GÖRMEK ÇOK DAHA BÜYÜK ÖRNEKLEM İSTİYOR.
    ///
    /// Bu takas açıkta durmalı: "otuz video yeter" demek, ne kadar
    /// küçük bir farkı görebileceğini bilmemek ve göremediği bir
    /// farkı "fark yok" diye raporlamak demek.
    [Fact]
    public void KucukFark_DahaBuyukOrneklem()
    {
        var buyukFark = Significance.RequiredSamplePerVariant(0.04, 0.02);
        var kucukFark = Significance.RequiredSamplePerVariant(0.04, 0.005);

        Assert.True(kucukFark > buyukFark * 5,
            $"Dört kat küçük fark, beş kattan az örneklem istiyor: {buyukFark} → {kucukFark}");
    }

    /// HESAP BİLİNEN BİR SONUCA YAKIN.
    ///
    /// %4 tabandan %6'ya (iki puan) çıkışı α=0,05 ve %80 güçle
    /// görmek, varyant başına yaklaşık 1.500 deneme istiyor. Standart
    /// örneklem tablolarının verdiği sayı bu.
    [Fact]
    public void OrneklemHesabi_BilinenSonucaYakin()
    {
        var n = Significance.RequiredSamplePerVariant(0.04, 0.02);

        Assert.InRange(n, 1200, 1900);
    }

    /* ---- karar ---- */

    /// YETERSİZ VERİ "FARK YOK" DEĞİL.
    ///
    /// Bu çerçevenin var olma sebebi. Üç videodan sonra "fark yok"
    /// demek denemeyi bırakmak demek; oysa henüz hiçbir şey
    /// ölçülmemiş.
    [Fact]
    public void YetersizVeri_FarkYokDemiyor()
    {
        var verdict = ExperimentEvaluator.Evaluate(
            new VariantResult("kontrol", 4, 100),
            new VariantResult("varyant", 8, 100),
            minimumDetectableEffect: 0.02);

        Assert.Equal(ExperimentOutcome.NotEnoughData, verdict.Outcome);
        Assert.False(verdict.IsDecided);

        // Ve gerekçe bunu AÇIKÇA söylüyor.
        Assert.Contains("henüz bilinmiyor", verdict.Reason, StringComparison.Ordinal);
        Assert.Contains("eksik", verdict.Reason, StringComparison.Ordinal);
    }

    /// YETERLİ VERİ VE GERÇEK FARK: VARYANT KAZANIYOR.
    [Fact]
    public void YeterliVeriVeFark_VaryantKazaniyor()
    {
        var verdict = ExperimentEvaluator.Evaluate(
            new VariantResult("kontrol", 400, 10_000),
            new VariantResult("varyant", 600, 10_000),
            minimumDetectableEffect: 0.02);

        Assert.Equal(ExperimentOutcome.VariantWins, verdict.Outcome);
        Assert.True(verdict.IsDecided);
        Assert.True(verdict.PValue < 0.05);
    }

    /// VARYANT DAHA KÖTÜYSE AYRI BİR SONUÇ.
    ///
    /// "Fark yok" denemeye devam etmeyi düşündürür; "daha kötü" o
    /// varyantı kapatmayı gerektirir. İkisini birleştirmek, zarar
    /// veren bir değişikliği denemeye devam etmek demekti.
    [Fact]
    public void VaryantDahaKotu_KontrolKazaniyor()
    {
        var verdict = ExperimentEvaluator.Evaluate(
            new VariantResult("kontrol", 600, 10_000),
            new VariantResult("varyant", 400, 10_000),
            minimumDetectableEffect: 0.02);

        Assert.Equal(ExperimentOutcome.ControlWins, verdict.Outcome);
        Assert.True(verdict.IsDecided);
    }

    /// YETERLİ VERİ, GERÇEK FARK YOK.
    [Fact]
    public void YeterliVeriFarkYok_TasinmiyorDiyor()
    {
        var verdict = ExperimentEvaluator.Evaluate(
            new VariantResult("kontrol", 400, 10_000),
            new VariantResult("varyant", 405, 10_000),
            minimumDetectableEffect: 0.02);

        Assert.Equal(ExperimentOutcome.NoDifference, verdict.Outcome);
        Assert.Contains("taşınmıyor", verdict.Reason, StringComparison.Ordinal);
    }

    /* ---- tek değişken ---- */

    /// TEK BOYUT DEĞİŞMİŞSE DENEY GEÇERLİ.
    [Fact]
    public void TekBoyutDegismis_Geciyor()
    {
        var result = ExperimentEvaluator.SingleChangedDimension(
            new Dictionary<string, string> { ["kapak"] = "a", ["baslik"] = "x" },
            new Dictionary<string, string> { ["kapak"] = "b", ["baslik"] = "x" });

        Assert.True(result.IsSuccess);
        Assert.Equal("kapak", result.Value);
    }

    /// İKİ BOYUT DEĞİŞMİŞSE DENEY GEÇERSİZ.
    ///
    /// Kazandığında hangisinin kazandırdığı bilinemez — ve bir
    /// sonraki videoda yanlış olanı taşımak mümkün.
    [Fact]
    public void IkiBoyutDegismis_Reddediliyor()
    {
        var result = ExperimentEvaluator.SingleChangedDimension(
            new Dictionary<string, string> { ["kapak"] = "a", ["baslik"] = "x" },
            new Dictionary<string, string> { ["kapak"] = "b", ["baslik"] = "y" });

        Assert.True(result.IsFailure);
        Assert.Equal("experiment.multiple_changes", result.Error.Code);

        // HANGİ boyutlar olduğu da yazılı: "iki boyut değişti" tek
        // başına hangisini geri alacağını söylemiyor.
        Assert.Contains("baslik", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("kapak", result.Error.Message, StringComparison.Ordinal);
    }

    /// HİÇ DEĞİŞMEMİŞSE ÖLÇÜLECEK BİR ŞEY YOK.
    ///
    /// Sessizce geçirmek, hiçbir şey ölçmeyen bir deneyin haftalarca
    /// koşup "fark yok" demesi demekti.
    [Fact]
    public void HicDegismemis_Reddediliyor()
    {
        var result = ExperimentEvaluator.SingleChangedDimension(
            new Dictionary<string, string> { ["kapak"] = "a" },
            new Dictionary<string, string> { ["kapak"] = "a" });

        Assert.True(result.IsFailure);
        Assert.Equal("experiment.no_change", result.Error.Code);
    }

    /// EKSİK ANAHTAR DA BİR DEĞİŞİKLİK.
    ///
    /// Varyantta olmayan bir alan, "boş" ile "farklı" arasındaki
    /// farkı gizlerdi.
    [Fact]
    public void EksikAnahtar_DegisiklikSayiliyor()
    {
        var result = ExperimentEvaluator.SingleChangedDimension(
            new Dictionary<string, string> { ["kapak"] = "a", ["muzik"] = "var" },
            new Dictionary<string, string> { ["kapak"] = "a" });

        Assert.True(result.IsSuccess);
        Assert.Equal("muzik", result.Value);
    }
}
