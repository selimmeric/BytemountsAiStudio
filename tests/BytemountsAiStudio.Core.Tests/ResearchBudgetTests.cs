using BytemountsAiStudio.Core.Content;

namespace BytemountsAiStudio.Core.Tests;

/// Araştırma bütçesi testleri (P1-09).
///
/// Saf ve ayrı: döngünün ne zaman duracağı kararı ağa çıkmadan
/// sınanabilsin. Bütçe mantığındaki bir hata, gerçek koşuda para
/// harcayarak öğrenilecek bir şey olmamalı.
public sealed class ResearchBudgetTests
{
    [Fact]
    public void YeniButce_Calisiyor()
    {
        var budget = new ResearchBudget(6, 3);

        Assert.True(budget.CanContinue);
        Assert.Equal(ResearchStop.Running, budget.Stop);
        Assert.Equal(0, budget.Steps);
    }

    [Fact]
    public void AdimBitince_Durur()
    {
        var budget = new ResearchBudget(2, 10);

        budget.StepTaken();
        Assert.True(budget.CanContinue);

        budget.StepTaken();
        Assert.False(budget.CanContinue);
        Assert.Equal(ResearchStop.StepsExhausted, budget.Stop);
    }

    [Fact]
    public void HedefeUlasinca_Durur()
    {
        var budget = new ResearchBudget(10, 2);

        budget.SourceFound();
        Assert.True(budget.CanContinue);

        budget.SourceFound();
        Assert.False(budget.CanContinue);
        Assert.Equal(ResearchStop.TargetReached, budget.Stop);
    }

    /// "Durdu" yetmez: hedefe ulaşarak mı durdu, adım biterek mi?
    /// İkincisi araştırmanın YETERSİZ olduğunu söylüyor ve bu ayrım
    /// kayda giriyor.
    [Fact]
    public void DurmaSebebi_AyirtEdilir()
    {
        var reached = new ResearchBudget(10, 1);
        reached.SourceFound();

        var exhausted = new ResearchBudget(1, 10);
        exhausted.StepTaken();

        Assert.Equal(ResearchStop.TargetReached, reached.Stop);
        Assert.Equal(ResearchStop.StepsExhausted, exhausted.Stop);
    }

    /// Son adımda hedefe ulaşan bir araştırma "adım bitti" diye
    /// işaretlenmemeli — başarıyla bitti.
    [Fact]
    public void SonAdimdaHedefeUlasilirsa_BasariylaBiter()
    {
        var budget = new ResearchBudget(1, 1);

        budget.StepTaken();
        budget.SourceFound();

        Assert.Equal(ResearchStop.TargetReached, budget.Stop);
    }

    [Fact]
    public void SorgularBitince_Isaretlenir()
    {
        var budget = new ResearchBudget(10, 5);

        budget.StepTaken();
        budget.QueriesExhausted();

        Assert.Equal(ResearchStop.QueriesExhausted, budget.Stop);
    }

    /// Zaten durmuş bir bütçenin sebebi DEĞİŞMEMELİ: hedefe ulaşıp
    /// duran bir araştırma, sorgular da bittiği için "yetersiz"
    /// görünmemeli.
    [Fact]
    public void ZatenDurmusButce_SebebiDegismez()
    {
        var budget = new ResearchBudget(10, 1);

        budget.SourceFound();
        budget.QueriesExhausted();

        Assert.Equal(ResearchStop.TargetReached, budget.Stop);
    }

    /// En az bir kaynak varsa kısmi sonuç kullanılabilir: eksik
    /// araştırmayla senaryo yazmak, hiç yazmamaktan iyi — iddia
    /// doğrulama zaten desteksiz olanı işaretleyecek.
    [Fact]
    public void KismiSonuc_Kullanilabilir()
    {
        var budget = new ResearchBudget(10, 5);

        Assert.False(budget.HasUsableResult);

        budget.SourceFound();

        Assert.True(budget.HasUsableResult);
    }

    /// Sınırsız bir döngü kaynak bulamadıkça aramaya devam eder;
    /// sıfır ve negatif değerler en aza çekiliyor.
    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, -3)]
    public void GecersizSinirlar_EnAzaCekilir(int maxSteps, int target)
    {
        var budget = new ResearchBudget(maxSteps, target);

        Assert.Equal(1, budget.MaxSteps);
        Assert.Equal(1, budget.TargetSources);
    }

    [Fact]
    public void Ozet_AdimVeKaynakSayisiniIcerir()
    {
        var budget = new ResearchBudget(6, 3);
        budget.StepTaken();
        budget.SourceFound();

        var text = budget.ToString();

        Assert.Contains("1/6", text, StringComparison.Ordinal);
        Assert.Contains("1/3", text, StringComparison.Ordinal);
    }
}
