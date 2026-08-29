namespace BytemountsAiStudio.Nodes.Tests;

/// Dışarıdan erişilebilir adres (P6-02).
///
/// ***BU DOSYANIN VAR OLMA SEBEBİ:*** `render.public_url` alanı
/// `PublishHandler` içinde **okunuyordu** ve hiçbir yerde
/// **yazılmıyordu**. Instagram anahtarı gelse bile her yayın
/// `instagram.no_public_url` kalıcı hatasıyla düşerdi — Instagram
/// videoyu **çekiyor**, yükleme kabul etmiyor.
///
/// Planda bu hiç geçmiyordu: P6-02 "anahtar bekliyor" diye
/// işaretliydi ve anahtarın gelmesi tek başına hiçbir şeyi
/// çözmezdi.
public sealed class PublicUrlTests
{
    private const string Variable = "BMAI_PUBLIC_BASE_URL";

    /// Süreç ortamını değiştirip GERİ ALAN yardımcı.
    ///
    /// `Environment.SetEnvironmentVariable` çağırmak, aynı süreçte
    /// koşan komşu testleri kırmanın sessiz bir yolu — bu depoda iki
    /// kez yaşandı. `finally` olmadan yazılmıyor.
    private static T With<T>(string? value, Func<T> body)
    {
        var previous = Environment.GetEnvironmentVariable(Variable);

        Environment.SetEnvironmentVariable(Variable, value);

        try
        {
            return body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(Variable, previous);
        }
    }

    /// ***AYAR YOKSA ADRES DE YOK — VE BU DOĞRU.***
    ///
    /// BU DOSYANIN EN ÖNEMLİ TESTİ. Kod bunu üretemez: çıktı dosyası
    /// bir kabın içinde ya da bir diskte duruyor ve internetten
    /// erişilebilir olup olmadığını yalnızca kurulum bilir. Uydurulmuş
    /// bir adres göndermek, hatayı Meta tarafında "medya
    /// indirilemedi" diye görmek demekti.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AyarYok_AdresYok(string? value)
        => Assert.Null(With(value, () => MediaRenderHandler.PublicUrl("/veri/output/abc.mp4")));

    /// ADRES ÜRETİLİYOR.
    [Fact]
    public void AyarVar_AdresUretiliyor()
        => Assert.Equal(
            "https://cdn.ornek.com/abc.mp4",
            With("https://cdn.ornek.com", () => MediaRenderHandler.PublicUrl("/veri/output/abc.mp4")));

    /// SONDAKİ EĞİK ÇİZGİ İKİ KEZ YAZILMIYOR.
    ///
    /// `https://cdn.ornek.com//abc.mp4` çoğu sunucuda çalışıyor ama
    /// bazı CDN'lerde ayrı bir yol sayılıyor ve 404 dönüyor.
    [Fact]
    public void SondakiEgikCizgi_TeklestiriliyOr()
        => Assert.Equal(
            "https://cdn.ornek.com/abc.mp4",
            With("https://cdn.ornek.com/", () => MediaRenderHandler.PublicUrl("/veri/output/abc.mp4")));

    /// ***YALNIZCA DOSYA ADI EKLENİYOR, TAM YOL DEĞİL.***
    ///
    /// Tam yol kabın iç dizin yapısını dışarı sızdırırdı ve o yapı
    /// adreste hiçbir işe yaramıyor.
    [Fact]
    public void TamYol_Sizmiyor()
    {
        var url = With("https://cdn.ornek.com", () =>
            MediaRenderHandler.PublicUrl("/veri/output/gizli-dizin/abc.mp4"));

        Assert.NotNull(url);
        Assert.DoesNotContain("gizli-dizin", url, StringComparison.Ordinal);
        Assert.EndsWith("/abc.mp4", url, StringComparison.Ordinal);
    }

    /// ÇIKTI YOLU BOŞSA ADRES DE YOK.
    [Fact]
    public void CiktiYoluBos_AdresYok()
        => Assert.Null(With("https://cdn.ornek.com", () => MediaRenderHandler.PublicUrl("")));
}
