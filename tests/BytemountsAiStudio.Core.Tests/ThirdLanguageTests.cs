using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Execution;

namespace BytemountsAiStudio.Core.Tests;

/// Üçüncü dil — soyutlamanın sınavı (P3-09).
///
/// SORU ŞU: yeni bir dil eklemek KOD mu yazdırıyor, YAPILANDIRMA mı
/// istiyor? İkincisiyse soyutlama işini yapmış demektir; birincisiyse
/// "dil desteği" iddiası her yeni dilde yeniden sınanmak zorunda.
///
/// Arapça bilinçli seçildi: Türkçe ve İngilizce'nin ikisi de soldan
/// sağa yazılıyor ve ikisi de Latin alfabesi kullanıyor. İki soldan
/// sağa dil desteklemek "çok dilli" olmayı kanıtlamıyor — ilk gerçek
/// sınav sağdan sola.
public sealed class ThirdLanguageTests
{
    /// SAĞDAN SOLA DİL TANINIYOR ve bu bilgi dilin KENDİSİNDEN
    /// geliyor, bir listeden değil.
    ///
    /// Elle tutulan bir "RTL dilleri" listesi, dördüncü dilde yine
    /// kod değişikliği isterdi — tam da kaçınmaya çalıştığımız şey.
    [Theory]
    [InlineData("ar-SA", true)]
    [InlineData("he-IL", true)]
    [InlineData("fa-IR", true)]
    [InlineData("tr-TR", false)]
    [InlineData("en-US", false)]
    [InlineData("ja-JP", false)]
    public void YonBilgisi_DildenTuruyor(string tag, bool rightToLeft)
        => Assert.Equal(rightToLeft, LanguageTag.Create(tag).IsRightToLeft);

    /// TÜRKÇE i/I DÖNÜŞÜMÜ dilin kültüründen geliyor.
    ///
    /// `InvariantCulture` kullanmak "İSTANBUL" ile "istanbul"u farklı
    /// gösterirdi ve tekillik kontrolü sessizce yanlış çalışırdı.
    [Fact]
    public void KulturDuyarliDonusum_DildenGeliyor()
    {
        var turkish = LanguageTag.Create("tr-TR");
        var english = LanguageTag.Create("en-US");

        Assert.Equal("ı", "I".ToLower(turkish.Culture));
        Assert.Equal("i", "I".ToLower(english.Culture));
    }

    /// Ana dil alt etiketi: ses ve yazı tipi seçimi buna bakıyor.
    [Theory]
    [InlineData("ar-SA", "ar")]
    [InlineData("tr-TR", "tr")]
    [InlineData("en-US", "en")]
    public void AnaDilEtiketi_Ayrisiyor(string tag, string primary)
        => Assert.Equal(primary, LanguageTag.Create(tag).Primary);

    /// TANINMAYAN DİL REDDEDİLİYOR, sessizce kabul edilmiyor.
    [Theory]
    [InlineData("bilinmeyen-dil")]
    [InlineData("")]
    [InlineData(null)]
    public void TaninmayanDil_Reddediliyor(string? tag)
        => Assert.True(LanguageTag.TryCreate(tag).IsFailure);

    /// AYNI DİL HER YAZIMDA AYNI NESNEYE DÖNÜŞÜYOR.
    ///
    /// GERÇEK BİR HATA BURADAN ÇIKTI: .NET `tr_TR` etiketini kabul
    /// ediyor ve adını `tr_tr` yapıyor — yani `tr-TR` ile **eşit
    /// olmayan** ikinci bir dil nesnesi. Sonuçları sessiz ve ağırdı:
    ///
    ///   - `Primary` değeri "tr" değil "tr_tr" çıkıyor, yani ses ve
    ///     yazı tipi seçimi hiçbir şeyle eşleşmiyor
    ///   - tekillik sorgusu dile göre filtreliyor; `tr_tr` konuları
    ///     `tr-TR` konularını hiç görmüyor ve aynı video ikinci kez
    ///     üretiliyor
    ///
    /// Sınıfın belge yorumu tam olarak bu senaryoyu "önlendi" diye
    /// anlatıyordu; önlenmemişti.
    [Theory]
    [InlineData("tr_TR")]
    [InlineData("tr-tr")]
    [InlineData("TR-tr")]
    [InlineData(" tr-TR ")]
    public void FarkliYazimlar_AyniDileDonusuyor(string tag)
    {
        var parsed = LanguageTag.Create(tag);

        Assert.Equal("tr-TR", parsed.Value);
        Assert.Equal("tr", parsed.Primary);
        Assert.Equal(LanguageTag.Create("tr-TR"), parsed);
    }

    /// Arapça da aynı: alt çizgi ya da küçük harf yazımı dili
    /// bölmüyor.
    [Theory]
    [InlineData("ar_SA")]
    [InlineData("ar-sa")]
    public void ArapcaFarkliYazimlar_AyniDil(string tag)
    {
        var parsed = LanguageTag.Create(tag);

        Assert.Equal("ar", parsed.Primary);
        Assert.True(parsed.IsRightToLeft);
        Assert.Equal(LanguageTag.Create("ar-SA"), parsed);
    }

    /// ÜÇÜNCÜ DİL YAPILANDIRMAYLA GELİYOR.
    ///
    /// Bu test soyutlamanın sınavı: Arapça bir kanalın ayar belgesi,
    /// Türkçe olanla AYNI ayrıştırıcıdan geçiyor ve kod hiçbir yerde
    /// dili özel olarak tanımıyor. Yeni dil = yeni satır, yeni kod
    /// değil.
    [Fact]
    public void ArapcaKanal_YapilandirmaylaTanimlaniyor()
    {
        var settings = ChannelSettings.Parse(
            """
            {
              "voice": { "voice_id": "ar-m1" },
              "font_stack": ["Noto Naskh Arabic", "Arial"],
              "daily_target": 2,
              "pacing": { "time_zone": "Asia/Riyadh", "publish_windows": ["20:00"] }
            }
            """);

        Assert.Equal("ar-m1", settings.VoiceId);
        Assert.Equal(["Noto Naskh Arabic", "Arial"], settings.FontStack);
        Assert.Equal("Asia/Riyadh", settings.Pacing.TimeZoneId);
        Assert.Equal([new TimeOnly(20, 0)], settings.Pacing.PublishWindows);
        Assert.Empty(settings.Warnings);
    }

    /// ÜÇ DİL, ÜÇ KİMLİK, TEK AYRIŞTIRICI.
    ///
    /// Testin asıl söylediği bu: aynı kod üç farklı dili taşıyor ve
    /// hiçbirini özel olarak tanımıyor.
    [Fact]
    public void UcDil_AyniKodlaTasiniyor()
    {
        var channels = new[]
        {
            ("tr-TR", """{"voice":{"voice_id":"tr-f1"},"font_stack":["Inter"]}"""),
            ("en-US", """{"voice":{"voice_id":"en-m1"},"font_stack":["Verdana"]}"""),
            ("ar-SA", """{"voice":{"voice_id":"ar-m1"},"font_stack":["Noto Naskh Arabic"]}"""),
        };

        var parsed = channels
            .Select(c => (Language: LanguageTag.Create(c.Item1), Settings: ChannelSettings.Parse(c.Item2)))
            .ToList();

        // Üç farklı ses, üç farklı yazı tipi.
        Assert.Equal(3, parsed.Select(p => p.Settings.VoiceId).Distinct().Count());
        Assert.Equal(3, parsed.Select(p => p.Settings.FontStack![0]).Distinct().Count());

        // Ve yalnızca biri sağdan sola — yön dilden türüyor, ayardan
        // değil. Ayardan gelseydi yanlış yazılabilirdi.
        Assert.Single(parsed, p => p.Language.IsRightToLeft);
    }
}
