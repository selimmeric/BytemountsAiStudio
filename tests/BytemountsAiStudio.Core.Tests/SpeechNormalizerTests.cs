using BytemountsAiStudio.Core.Content;

namespace BytemountsAiStudio.Core.Tests;

/// Konuşma normalizasyonunun altın kümesi (§20.3).
///
/// Bu testler bir "kalite kapısı": TTS'e giden metin yanlış okunursa video
/// kulağa yapay gelir ve bunu ancak izleyici fark eder. Kural tabanlı
/// olmasının sebebi de bu — aynı sayı her videoda aynı okunmalı.
public sealed class TurkishSpeechNormalizerTests
{
    private readonly TurkishSpeechNormalizer _normalizer = new();

    [Theory]
    [InlineData("0", "sıfır")]
    [InlineData("7", "yedi")]
    [InlineData("12", "on iki")]
    [InlineData("40", "kırk")]
    [InlineData("99", "doksan dokuz")]
    [InlineData("100", "yüz")]
    [InlineData("253", "iki yüz elli üç")]
    [InlineData("1000", "bin")]
    [InlineData("1453", "bin dört yüz elli üç")]
    [InlineData("2026", "iki bin yirmi altı")]
    [InlineData("15000", "on beş bin")]
    [InlineData("1000000", "bir milyon")]
    public void SayiOkunusu(string input, string expected)
        => Assert.Equal(expected, TurkishSpeechNormalizer.SpellNumber(input));

    [Fact]
    public void BininOnundekiBir_Dusar()
    {
        // Türkçe'nin en sık atlanan kuralı. "bir bin" diye okuyan bir video
        // ilk cümlede yapay olduğunu belli eder.
        Assert.Equal("bin", TurkishSpeechNormalizer.Spell(1000));
        Assert.Equal("bir milyon", TurkishSpeechNormalizer.Spell(1_000_000));
    }

    [Fact]
    public void YuzunOnundekiBir_Dusar()
    {
        Assert.Equal("yüz", TurkishSpeechNormalizer.Spell(100));
        Assert.Equal("iki yüz", TurkishSpeechNormalizer.Spell(200));
    }

    [Theory]
    [InlineData("%12 arttı", "yüzde on iki arttı")]
    [InlineData("$45 değerinde", "kırk beş dolar değerinde")]
    [InlineData("250 TL", "iki yüz elli lira")]
    public void ParaVeYuzde(string input, string expected)
        => Assert.Equal(expected, _normalizer.Normalize(input));

    [Fact]
    public void YuzdeIsareti_SayidanOnceIslenir()
    {
        // Sıra yanlış olsaydı "%" ortada kalır ve TTS onu okumaya çalışırdı.
        Assert.DoesNotContain("%", _normalizer.Normalize("%12"), StringComparison.Ordinal);
    }

    [Fact]
    public void BinlikAyirici_TekSayiSayilir()
    {
        // "1.453" iki sayı değil, bir sayı. Ayırıcı temizlenmezse
        // "bir nokta dört yüz elli üç" gibi bir şey çıkardı.
        Assert.Equal("bin dört yüz elli üç", _normalizer.Normalize("1.453"));
    }

    [Theory]
    [InlineData("M.Ö. 300", "milattan önce üç yüz")]
    [InlineData("15 km uzakta", "on beş kilometre uzakta")]
    [InlineData("20 yy.", "yirmi yüzyıl")]
    public void Kisaltmalar(string input, string expected)
        => Assert.Equal(expected, _normalizer.Normalize(input));

    [Fact]
    public void CumleIcindeKarisik()
    {
        var result = _normalizer.Normalize("1453'te İstanbul fethedildi ve %90 değişti.");

        Assert.Contains("bin dört yüz elli üç", result, StringComparison.Ordinal);
        Assert.Contains("yüzde doksan", result, StringComparison.Ordinal);
    }

    [Fact]
    public void BosMetin_BosDoner()
        => Assert.Equal(string.Empty, _normalizer.Normalize("   "));
}

public sealed class EnglishSpeechNormalizerTests
{
    private readonly EnglishSpeechNormalizer _normalizer = new();

    [Theory]
    [InlineData(0, "zero")]
    [InlineData(15, "fifteen")]
    [InlineData(42, "forty-two")]
    [InlineData(253, "two hundred fifty-three")]
    [InlineData(1000, "one thousand")]
    [InlineData(2_500_000, "two million five hundred thousand")]
    public void SayiOkunusu(long value, string expected)
        => Assert.Equal(expected, EnglishSpeechNormalizer.Spell(value));

    [Theory]
    [InlineData(1453, "fourteen fifty-three")]
    [InlineData(1969, "nineteen sixty-nine")]
    [InlineData(1905, "nineteen oh five")]
    [InlineData(1900, "nineteen hundred")]
    [InlineData(2026, "twenty twenty-six")]
    public void YilOkunusu_OzelKalibiIzler(int year, string expected)
    {
        // 1453 "one thousand four hundred fifty-three" diye okunmaz. Bu
        // istisna olmadan tarih anlatan video kulağa yapay gelir.
        Assert.Equal(expected, EnglishSpeechNormalizer.SpellYear(year));
    }

    [Fact]
    public void YilAraligininDisi_NormalOkunur()
    {
        // 800 ya da 3000 yıl kalıbına girmiyor.
        Assert.Equal("eight hundred", EnglishSpeechNormalizer.SpellYear(800));
    }

    [Theory]
    [InlineData("12% growth", "twelve percent growth")]
    [InlineData("$45 each", "forty-five dollars each")]
    public void ParaVeYuzde(string input, string expected)
        => Assert.Equal(expected, _normalizer.Normalize(input));

    [Fact]
    public void CumleIcindeYil()
    {
        var result = _normalizer.Normalize("In 1453 the city fell.");

        Assert.Contains("fourteen fifty-three", result, StringComparison.Ordinal);
    }
}

public sealed class SpeechNormalizerRegistryTests
{
    [Fact]
    public void DileGoreSecer()
    {
        var registry = SpeechNormalizerRegistry.Default();

        Assert.Contains("bin dört yüz",
            registry.Normalize(LanguageTag.Create("tr-TR"), "1453"), StringComparison.Ordinal);

        Assert.Contains("fourteen fifty-three",
            registry.Normalize(LanguageTag.Create("en-US"), "1453"), StringComparison.Ordinal);
    }

    [Fact]
    public void DesteklenmeyenDil_MetniOlduguGibiDondurur()
    {
        // Yeni bir dil eklemek bir normalizer yazmak demek; o gelene kadar
        // içerik üretilebilmeli. Hata döndürmek üçüncü dili engellerdi.
        var registry = SpeechNormalizerRegistry.Default();
        var german = LanguageTag.Create("de-DE");

        Assert.False(registry.Supports(german));
        Assert.Equal("1453", registry.Normalize(german, "1453"));
    }
}
