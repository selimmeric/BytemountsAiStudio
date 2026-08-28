namespace BytemountsAiStudio.Api.Tests;

/// Varlık gezgini ve lisans raporu (P3-08).
///
/// Lisans bir metadata değil, bir UYUM KAYDI (§2.3/14). Rapor bir
/// envanter değil, bir RİSK LİSTESİ — ve bir risk listesinin en büyük
/// düşmanı yanlış alarm: gürültünün içinde gerçek bir risk
/// kaybolur.
public sealed class AssetQueriesTests
{
    private static AssetQueries.LicenseFacts License(
        string? name = null, string? author = null, bool attribution = false)
        => new(name, author, attribution);

    /// KENDİ ÜRETTİĞİMİZ VARLIK RİSK DEĞİL.
    ///
    /// İlk yazımda türe bakıyordum ("ses ve müzikte lisans zorunlu")
    /// ve gerçek veriye bakınca yanlış olduğu hemen görüldü: kendi
    /// ürettiğimiz 38 seslendirme dosyası "uyum riski" olarak
    /// işaretlenmişti. Yüzlerce yanlış uyarı raporu okunmaz yapardı.
    ///
    /// Ayıran şey tür değil KAYNAK: `SourceUrl` boşsa üreten biziz.
    [Theory]
    [InlineData("Audio")]
    [InlineData("Music")]
    [InlineData("Image")]
    public void KendiUrettigimiz_RiskDegil(string kind)
        => Assert.Null(AssetQueries.Risk(kind, License(), sourceUrl: null));

    /// DIŞ KAYNAKLI VE LİSANSSIZ: risk.
    [Fact]
    public void DisKaynakliLisanssizGorsel_Risk()
    {
        var risk = AssetQueries.Risk("Image", License(), "https://ornek.invalid/a.jpg");

        Assert.NotNull(risk);
        Assert.Contains("lisans kaydı yok", risk, StringComparison.Ordinal);
    }

    /// MÜZİKTE DAHA AĞIR: Content ID müziği otomatik tanıyor ve bir
    /// talep kanalın o videodan gelen gelirinin tamamını götürüyor.
    [Theory]
    [InlineData("Audio")]
    [InlineData("Music")]
    public void DisKaynakliLisanssizMuzik_Bloklayici(string kind)
    {
        var risk = AssetQueries.Risk(kind, License(), "https://ornek.invalid/a.mp3");

        Assert.NotNull(risk);
        Assert.Contains("bloklayıcı", risk, StringComparison.Ordinal);
    }

    /// ATIF GEREKİYORSA YAZAR ADI ŞART: "CC BY" deyip yazarı bilmemek,
    /// atfı yapılamaz kılıyor ve lisansı ihlal ediyor.
    [Fact]
    public void AtifGerekliYazarYok_Risk()
    {
        var risk = AssetQueries.Risk(
            "Image", License("CC BY 4.0", author: null, attribution: true),
            "https://ornek.invalid/a.jpg");

        Assert.NotNull(risk);
        Assert.Contains("atıf", risk, StringComparison.Ordinal);
    }

    /// Atıf gerekmiyorsa yazar da gerekmiyor: CC0 tam da bu.
    [Fact]
    public void AtifGerekmiyor_YazarsizGeciyor()
        => Assert.Null(AssetQueries.Risk(
            "Music", License("CC0 1.0", author: null, attribution: false),
            "https://ornek.invalid/a.mp3"));

    /// Atıf gerekiyor ve yazar var: geçiyor.
    [Fact]
    public void AtifVeYazarVar_Geciyor()
        => Assert.Null(AssetQueries.Risk(
            "Music", License("CC BY 4.0", "besteci", attribution: true),
            "https://ornek.invalid/a.mp3"));

    /* ---- Lisans kaydını okuma ---- */

    /// İKİ YAZIM BİÇİMİ DE OKUNUYOR.
    ///
    /// Kayıtlar farklı zamanlarda farklı serileştiricilerden geçti:
    /// bazıları PascalCase, bazıları snake_case. Yalnız birini
    /// desteklemek, eski kayıtların lisansını sessizce kaybetmekti —
    /// ve uyum kaydında "kaybettim" kabul edilebilir bir cevap değil.
    [Theory]
    [InlineData("""{"Name":"CC0","Author":"a","RequiresAttribution":false}""")]
    [InlineData("""{"name":"CC0","author":"a","requires_attribution":false}""")]
    public void IkiYazimBicimi_Okunuyor(string json)
    {
        var facts = AssetQueries.LicenseOf(json);

        Assert.Equal("CC0", facts.Name);
        Assert.Equal("a", facts.Author);
        Assert.False(facts.RequiresAttribution);
    }

    /// OKUNAMAYAN KAYIT "LİSANS YOK" SAYILIYOR, "sorun yok" değil.
    ///
    /// Bozuk bir JSON'u geçerli saymak, uyum kaydını olmadığı hâlde
    /// varmış gibi göstermekti — ve bir talep geldiğinde elimizde
    /// hiçbir şey olmazdı.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("json degil")]
    [InlineData("[]")]
    [InlineData("42")]
    public void BozukLisansKaydi_LisansYokSayiliyor(string? json)
    {
        var facts = AssetQueries.LicenseOf(json);

        Assert.Null(facts.Name);

        // Ve bu, dış kaynaklı bir varlıkta risk üretiyor.
        Assert.NotNull(AssetQueries.Risk("Image", facts, "https://ornek.invalid/a.jpg"));
    }

    [Fact]
    public void AtifBayragi_Okunuyor()
        => Assert.True(AssetQueries.LicenseOf(
            """{"Name":"CC BY","RequiresAttribution":true}""").RequiresAttribution);
}
