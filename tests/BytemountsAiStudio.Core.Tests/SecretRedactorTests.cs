using BytemountsAiStudio.Core.Observability;

namespace BytemountsAiStudio.Core.Tests;

/// Log süzgecinin testleri (P1-01).
///
/// Süzgeç veritabanı gerektirmiyor, bu yüzden burada — kimlik deposunun
/// testlerinden ayrı koşabilsin.
[Collection("SecretRedactor")]
public sealed class SecretRedactorTests : IDisposable
{
    public SecretRedactorTests() => SecretRedactor.Clear();

    public void Dispose() => SecretRedactor.Clear();

    [Fact]
    public void KayitliDeger_Maskelenir()
    {
        SecretRedactor.Register("sk-cok-gizli-bir-anahtar");

        var line = SecretRedactor.Redact("Authorization: Bearer sk-cok-gizli-bir-anahtar oldu");

        Assert.DoesNotContain("sk-cok-gizli", line, StringComparison.Ordinal);
        Assert.Equal("Authorization: Bearer *** oldu", line);
    }

    [Fact]
    public void AyniSatirdaCokKez_HepsiMaskelenir()
    {
        SecretRedactor.Register("anahtar-degeri-12345");

        var line = SecretRedactor.Redact("anahtar-degeri-12345 ve yine anahtar-degeri-12345");

        Assert.Equal("*** ve yine ***", line);
    }

    [Fact]
    public void BirdenCokAnahtar_HepsiSuzulur()
    {
        SecretRedactor.Register("birinci-anahtar-123");
        SecretRedactor.Register("ikinci-anahtar-4567");

        var line = SecretRedactor.Redact("a=birinci-anahtar-123 b=ikinci-anahtar-4567");

        Assert.Equal("a=*** b=***", line);
    }

    /// Kısa değerler KAYDEDİLMİYOR: "test" gibi bir dize normal log
    /// metinlerinde tesadüfen geçer ve süzgeç bütün satırları hurdaya
    /// çevirirdi.
    [Theory]
    [InlineData("test")]
    [InlineData("abc")]
    [InlineData("11-karakter")]
    public void KisaDeger_KaydedilmezVeSuzulmez(string value)
    {
        SecretRedactor.Register(value);

        var line = $"bu satirda {value} geciyor";

        Assert.Equal(line, SecretRedactor.Redact(line));
    }

    /// Esik tam olarak 12 karakter. Sinirin nerede oldugunu testin
    /// soylemesi gerekiyor: bir gun biri esigi degistirirse burasi kirilsin.
    [Fact]
    public void OnIkiKarakter_KaydedilirVeSuzulur()
    {
        SecretRedactor.Register("kisa-anahtar");

        Assert.Equal("*** geciyor", SecretRedactor.Redact("kisa-anahtar geciyor"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BosDeger_Kaydedilmez(string? value)
    {
        SecretRedactor.Register(value);

        Assert.Equal("degismedi", SecretRedactor.Redact("degismedi"));
    }

    [Fact]
    public void Maskeleme_SonDortKarakteriBirakir()
    {
        Assert.Equal("***3456", SecretRedactor.Mask4("sk-proj-abcdef123456"));
    }

    /// Dört karakterden kısa bir değerin son dördünü göstermek, değerin
    /// tamamını göstermek demek olurdu.
    [Theory]
    [InlineData("abcd")]
    [InlineData("ab")]
    [InlineData("")]
    [InlineData(null)]
    public void CokKisaDeger_TamamenMaskelenir(string? value)
    {
        Assert.Equal("***", SecretRedactor.Mask4(value));
    }

    [Fact]
    public void KayitYokken_MetinDegismez()
    {
        Assert.Equal("hicbir sey yok", SecretRedactor.Redact("hicbir sey yok"));
    }

    [Fact]
    public void NullMetin_BosDizeDoner()
    {
        Assert.Equal(string.Empty, SecretRedactor.Redact(null));
    }
}
