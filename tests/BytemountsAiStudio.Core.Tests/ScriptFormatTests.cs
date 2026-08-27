using BytemountsAiStudio.Core.Content;

namespace BytemountsAiStudio.Core.Tests;

/// Senaryo biçim şablonlarının testleri (P1-12).
public sealed class ScriptFormatTests
{
    [Fact]
    public void BilinenBicim_AdiylaBulunur()
    {
        Assert.Equal(ScriptFormat.HookListPayoff, ScriptFormat.Get("hook-list-payoff"));
        Assert.Equal(ScriptFormat.Explainer, ScriptFormat.Get("explainer"));
    }

    [Fact]
    public void BuyukKucukHarf_Onemsiz()
    {
        Assert.Equal(ScriptFormat.Explainer, ScriptFormat.Get("EXPLAINER"));
    }

    /// Kanal ayarındaki bir yazım hatası içerik üretimini durdurmamalı;
    /// biçim bir tercih, zorunluluk değil.
    [Theory]
    [InlineData("boyle-bir-bicim-yok")]
    [InlineData("")]
    [InlineData(null)]
    public void BilinmeyenBicim_VarsayilanaDuser(string? name)
    {
        Assert.Equal(ScriptFormat.HookPayoff, ScriptFormat.Get(name));
    }

    /// Liste biçimi üç cümleye sığmıyor: her madde kendi cümlesini
    /// istiyor, yoksa maddeler birbirine karışıyor ve sahne planlayıcı
    /// da onları ayıramıyor.
    [Fact]
    public void ListeBicimi_UcCumleyeSigmaz()
    {
        Assert.False(ScriptFormat.HookListPayoff.Accepts(3));
        Assert.True(ScriptFormat.HookListPayoff.Accepts(7));
    }

    [Fact]
    public void Sinirlar_Kapsayici()
    {
        var format = ScriptFormat.HookPayoff;

        Assert.True(format.Accepts(format.MinSentences));
        Assert.True(format.Accepts(format.MaxSentences));
        Assert.False(format.Accepts(format.MinSentences - 1));
        Assert.False(format.Accepts(format.MaxSentences + 1));
    }

    /// Hedef sayı sınırların içinde olmalı; olmasaydı istem, kendi
    /// denetimini geçemeyecek bir sayı isterdi.
    [Fact]
    public void HedefSayi_SinirlarIcinde()
    {
        Assert.All(ScriptFormat.All, f => Assert.True(f.Accepts(f.TargetSentences),
            $"'{f.Name}' hedefi {f.TargetSentences}, sinirlar {f.MinSentences}-{f.MaxSentences}"));
    }

    [Fact]
    public void HerBicimin_YapiTarifiVar()
    {
        Assert.All(ScriptFormat.All, f =>
        {
            Assert.False(string.IsNullOrWhiteSpace(f.Structure));
            Assert.False(string.IsNullOrWhiteSpace(f.Name));
        });
    }

    [Fact]
    public void BicimAdlari_Tekil()
    {
        Assert.Equal(
            ScriptFormat.All.Count,
            ScriptFormat.All.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Sinirlar_Tutarli()
    {
        Assert.All(ScriptFormat.All, f => Assert.True(f.MinSentences <= f.MaxSentences));
        Assert.All(ScriptFormat.All, f => Assert.True(f.MinSentences >= 1));
    }
}
