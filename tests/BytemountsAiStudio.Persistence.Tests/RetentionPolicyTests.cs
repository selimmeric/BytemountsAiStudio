using BytemountsAiStudio.Core.Assets;
using BytemountsAiStudio.Persistence.Storage;

namespace BytemountsAiStudio.Persistence.Tests;

/// Saklama kuralı (P4-02).
///
/// İKİ YÖNLÜ BİR RİSK. Hiçbir şey silinmezse maliyet üretimle değil
/// GEÇMİŞLE orantılı hale geliyor: bir yıl önce üretilmiş bir
/// videonun ara dosyaları için her ay para ödemek.
///
/// Ama körü körüne silmek daha kötü, çünkü silinen bazı şeyler geri
/// GELEMEZ. Testlerin çoğu bu ikinci riski koruyor.
public sealed class RetentionPolicyTests
{
    private static readonly TimeSpan Old = RetentionPolicy.IntermediateAge + TimeSpan.FromDays(1);

    /// YAYINLANMIŞ İÇERİK HİÇ SİLİNMİYOR — yaşı ne olursa olsun.
    ///
    /// Platformdaki kopya bizim değil: kaldırılabiliyor, yeniden
    /// kodlanıyor ve indirilemiyor. Bir telif itirazında elimizde
    /// kalan tek şey bu.
    [Theory]
    [InlineData(AssetKind.Video)]
    [InlineData(AssetKind.Image)]
    [InlineData(AssetKind.Audio)]
    public void YayinlanmisIcerik_Silinmiyor(AssetKind kind)
    {
        var decision = RetentionPolicy.Decide(
            kind, TimeSpan.FromDays(3650), published: true, externallyLicensed: false);

        Assert.False(decision.CanDelete);
        Assert.Contains("yayınlanmış", decision.Reason, StringComparison.Ordinal);
    }

    /// LİSANSLI DIŞ VARLIK HİÇ SİLİNMİYOR.
    ///
    /// Lisans kaydı hangi dosyaya ait olduğunu söylüyor; dosya gidince
    /// kayıt bir şeyi ispatlamıyor ve uyum kaydı, kanıtı olmayan bir
    /// beyana dönüşür (§2.3/14).
    [Fact]
    public void LisansliDisVarlik_Silinmiyor()
    {
        var decision = RetentionPolicy.Decide(
            AssetKind.Music, Old, published: false, externallyLicensed: true);

        Assert.False(decision.CanDelete);
        Assert.Contains("lisans", decision.Reason, StringComparison.Ordinal);
    }

    /// NİHAİ VİDEO YAYINLANMAMIŞ OLSA DA SAKLANIYOR.
    ///
    /// Onay bekleyen ya da reddedilmiş bir video, insanın hâlâ
    /// bakabileceği bir şey. Ara ürünlerden ayıran fark: bu yeniden
    /// üretilemez — üreten model, istem ve rastgelelik aynı çıktıyı
    /// vermiyor.
    [Fact]
    public void YayinlanmamisVideo_Silinmiyor()
    {
        var decision = RetentionPolicy.Decide(
            AssetKind.Video, Old, published: false, externallyLicensed: false);

        Assert.False(decision.CanDelete);
        Assert.Contains("nihai video", decision.Reason, StringComparison.Ordinal);
    }

    /// ESKİ ARA ÜRÜN SİLİNEBİLİR.
    ///
    /// İçerik-adresli olduğu için tekrar üretilebiliyor ve aynı
    /// sha256'ya düşüyor: bu, geri alınabilir bir karar.
    [Theory]
    [InlineData(AssetKind.Image)]
    [InlineData(AssetKind.Audio)]
    [InlineData(AssetKind.Subtitle)]
    public void EskiAraUrun_Silinebilir(AssetKind kind)
    {
        var decision = RetentionPolicy.Decide(kind, Old, published: false, externallyLicensed: false);

        Assert.True(decision.CanDelete);
    }

    /// YENİ ARA ÜRÜN SİLİNMİYOR.
    ///
    /// Bir videonun performansı ilk haftalarda belli oluyor ve "bunu
    /// yeniden render edelim" kararı o pencerede veriliyor. Daha kısa
    /// bir süre, düzeltilebilir bir videoyu sıfırdan üretmeye
    /// zorlardı.
    [Fact]
    public void YeniAraUrun_Silinmiyor()
    {
        var decision = RetentionPolicy.Decide(
            AssetKind.Image, RetentionPolicy.IntermediateAge - TimeSpan.FromDays(1),
            published: false, externallyLicensed: false);

        Assert.False(decision.CanDelete);
        Assert.Contains("eski değil", decision.Reason, StringComparison.Ordinal);
    }

    /// SINIRDA OLAN SİLİNMİYOR.
    ///
    /// Tam otuz günlük bir varlık "otuz günden eski" değil. Sınırı
    /// içeri almak, kuralın adını yalan çıkarırdı.
    [Fact]
    public void TamSinirdaki_Silinmiyor()
        => Assert.False(RetentionPolicy.Decide(
            AssetKind.Image, RetentionPolicy.IntermediateAge,
            published: false, externallyLicensed: false).CanDelete);

    /// HER KARAR GEREKÇE TAŞIYOR.
    ///
    /// Yalnızca `bool` dönseydi, bir varlığın neden silindiği ya da
    /// neden silinmediği hiçbir yerde yazılı olmazdı — ve depo
    /// beklenmedik şekilde büyüdüğünde cevap aranacak yer kalmazdı.
    [Fact]
    public void HerKarar_GerekceTasiyor()
    {
        foreach (var kind in Enum.GetValues<AssetKind>())
        {
            foreach (var published in new[] { true, false })
            {
                foreach (var licensed in new[] { true, false })
                {
                    var decision = RetentionPolicy.Decide(kind, Old, published, licensed);

                    Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
                }
            }
        }
    }
}
