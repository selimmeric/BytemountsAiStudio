using System.Text.Json;
using BytemountsAiStudio.Core.Learning;

namespace BytemountsAiStudio.Core.Tests;

/// İstem sürümü varyantı ve seçim köprüsü (P5-05).
public sealed class PromptVariantTests
{
    private static JsonElement Context(string? promptConfig)
    {
        var json = promptConfig is null
            ? """{"topic":{"topic":"x"}}"""
            : """{"topic":{"topic":"x"},"experiments":{"prompt":{"name":"kol","config":"""
                + promptConfig + "}}}";

        using var document = JsonDocument.Parse(json);

        return document.RootElement.Clone();
    }

    /* ---- ayar ---- */

    [Fact]
    public void GecerliAyar_Okunuyor()
    {
        var parsed = PromptVariant.Parse("""{"istem":"script.generate","surum":"3"}""");

        Assert.True(parsed.IsSuccess, parsed.IsFailure ? parsed.Error.Message : string.Empty);
        Assert.Equal("script.generate", parsed.Value.Key);
        Assert.Equal(3, parsed.Value.Version);
    }

    /// SAYI OLARAK YAZILAN SÜRÜM DE KABUL EDİLİYOR.
    ///
    /// `"surum": 3` yazan biri haklı; metne çevirip okumak, o kişinin
    /// deneyini çalıştırılamaz kılmaktan iyi.
    [Fact]
    public void SayiOlarakSurum_Okunuyor()
        => Assert.Equal(3, PromptVariant.Parse("""{"istem":"a.b","surum":3}""").Value.Version);

    /// HANGİ İSTEM OLDUĞU YAZILMAMIŞSA REDDEDİLİYOR.
    ///
    /// Yalnızca sürüm veren bir kol, hangi isteme dokunduğunu
    /// söylemiyor — ve tahmin etmek, yanlış node'u deneye sokmak
    /// olurdu.
    [Fact]
    public void IstemAnahtariYok_Reddediliyor()
    {
        var parsed = PromptVariant.Parse("""{"surum":"2"}""");

        Assert.True(parsed.IsFailure);
        Assert.Equal("variant.no_prompt_key", parsed.Error.Code);
    }

    [Theory]
    [InlineData("""{"istem":"a.b","surum":"sifir"}""")]
    [InlineData("""{"istem":"a.b","surum":"0"}""")]
    [InlineData("""{"istem":"a.b","surum":"-1"}""")]
    public void BozukSurum_Reddediliyor(string config)
    {
        var parsed = PromptVariant.Parse(config);

        Assert.True(parsed.IsFailure);
        Assert.Equal("variant.bad_prompt_version", parsed.Error.Code);
    }

    [Fact]
    public void TaninmayanAyar_Reddediliyor()
    {
        var parsed = PromptVariant.Parse("""{"istem":"a.b","surum":"2","sicaklik":"0.9"}""");

        Assert.True(parsed.IsFailure);
        Assert.Equal("variant.unknown_key", parsed.Error.Code);
    }

    /* ---- seçim köprüsü ---- */

    /// DENEY YOKSA SÜRÜM SEÇİMİ DE YOK.
    ///
    /// `null` "en yeni sürüm" demek ve videoların ezici çoğunluğu
    /// böyle koşuyor.
    [Fact]
    public void DeneyYok_SurumYok()
        => Assert.Null(PromptSelection.Version(Context(null), "seo.generate"));

    /// ATANAN SÜRÜM DOĞRU İSTEME ULAŞIYOR.
    [Fact]
    public void AtananSurum_DogruIsteme()
        => Assert.Equal(1, PromptSelection.Version(
            Context("""{"istem":"seo.generate","surum":"1"}"""), "seo.generate"));

    /// BAŞKA BİR İSTEMİN DENEYİ BU NODE'U ETKİLEMİYOR.
    ///
    /// Anahtar kontrolü olmasaydı `script.generate` üzerinde açılan bir
    /// deney `seo.generate` node'unu da o sürüme zorlar ve muhtemelen
    /// "sürüm yok" hatasıyla run'ı düşürürdü — deneyle hiç ilgisi
    /// olmayan bir yerde.
    [Fact]
    public void BaskaIstemDeneyi_BuNodeuEtkilemiyor()
        => Assert.Null(PromptSelection.Version(
            Context("""{"istem":"script.generate","surum":"3"}"""), "seo.generate"));

    /// BOZUK AYAR NODE'U DÜŞÜRMÜYOR.
    ///
    /// Doğrulama kayıt anında yapılıyor ve bozuk deney orada
    /// kapatılıyor. Burada da hata döndürmek, aynı doğrulamayı her
    /// node'a tekrar ettirmek ve bir kez kaçırılmış bir hatanın bütün
    /// üretimi durdurması olurdu.
    [Fact]
    public void BozukAyar_VarsayilanaDusuyor()
        => Assert.Null(PromptSelection.Version(
            Context("""{"istem":"seo.generate","surum":"sifir"}"""), "seo.generate"));
}
