using BytemountsAiStudio.Contracts.Prompts;

namespace BytemountsAiStudio.Contracts.Tests;

/// İstem damgasının okunması (P5-05).
///
/// Damgayı ÜRETEN ve OKUYAN aynı dosyada; bu testler ikisinin
/// birbirinden ayrılmadığını sınıyor. Ayrılsalardı biçim bir gün
/// değişir, okuyan taraf sessizce eşleşmeyi kaybeder ve istem
/// performans raporu "hiç veri yok" derdi — hata olmadan.
public sealed class PromptStampTests
{
    [Fact]
    public void UretilenDamga_GeriOkunuyor()
    {
        var stamp = new PromptStamp("seo.generate", 2, "abc123");
        var parsed = PromptStamp.TryParse(stamp.ToString());

        Assert.Equal(stamp, parsed);
    }

    /// GERÇEK BİR KAYITTAN OKUNUYOR.
    ///
    /// Elle kurulmuş bir dizgeyi ayrıştırmak, biçimin gerçekten böyle
    /// olduğunu KANITLAMIYOR. Kayıttaki damga şablonun kendisinden
    /// geliyor.
    [Fact]
    public void KayittakiDamga_Okunuyor()
    {
        var registry = PromptRegistry.Embedded;
        Assert.True(registry.IsSuccess, registry.IsFailure ? registry.Error.Message : string.Empty);

        var template = registry.Value.Get("seo.generate", null);
        Assert.True(template.IsSuccess);

        var parsed = PromptStamp.TryParse(template.Value.Stamp);

        Assert.NotNull(parsed);
        Assert.Equal("seo.generate", parsed.Value.Key);
        Assert.Equal(template.Value.Version, parsed.Value.Version);
    }

    /// NOKTALI ANAHTAR DAMGAYI BOZMUYOR.
    [Fact]
    public void NoktaliAnahtar_Okunuyor()
        => Assert.Equal("script.chapter", PromptStamp.TryParse("script.chapter@11#ff00")!.Value.Key);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("anahtarsiz")]
    [InlineData("@2#abc")]
    [InlineData("a.b@iki#abc")]
    [InlineData("a.b@2")]
    public void BozukDamga_Null(string? stamp)
        => Assert.Null(PromptStamp.TryParse(stamp));
}
