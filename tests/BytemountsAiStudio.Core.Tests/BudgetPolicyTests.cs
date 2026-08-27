using BytemountsAiStudio.Core.Execution;

namespace BytemountsAiStudio.Core.Tests;

/// Bütçe kapısı kararının testleri (P2-03).
///
/// Kabul kriteri tek cümle: **limit aşımında yarım videolar
/// bitiriliyor, yenisi başlamıyor.**
public sealed class BudgetPolicyTests
{
    private static readonly TimeSpan Day = TimeSpan.FromHours(8);

    private static BudgetWindow Channel(decimal spent, decimal? limit = 1.00m)
        => new("Kanal günlük", spent, limit, Day);

    private static BudgetWindow Global(decimal spent, decimal? limit = 50m)
        => new("Global aylık", spent, limit, TimeSpan.FromDays(12));

    [Fact]
    public void LimitIcinde_Geciyor()
    {
        var verdict = BudgetPolicy.Decide([Channel(0.10m)], 0.05m, runAlreadyStarted: false);

        Assert.True(verdict.Allowed);
    }

    /// YENİ İŞ BAŞLAMIYOR.
    [Fact]
    public void LimitAsimi_YeniIsiDurduruyor()
    {
        var verdict = BudgetPolicy.Decide([Channel(0.98m)], 0.05m, runAlreadyStarted: false);

        Assert.False(verdict.Allowed);
        Assert.Equal(BudgetOutcome.Deferred, verdict.Outcome);
        Assert.Equal(Day, verdict.RetryAfter);
    }

    /// YARIM VİDEO BİTİYOR.
    ///
    /// Durdurmak, o ana kadar harcanan her kuruşu çöpe atmak ve ertesi
    /// gün aynı adımları İKİNCİ KEZ ödemek demekti — senaryo yazılmış,
    /// ses üretilmiş, görseller indirilmiş ve hiçbiri kullanılmayacak.
    [Fact]
    public void LimitAsimi_YarimVideoyuBitiriyor()
    {
        var verdict = BudgetPolicy.Decide([Channel(0.98m)], 0.05m, runAlreadyStarted: true);

        Assert.True(verdict.Allowed);
    }

    /// `StopEverything` yarım videoyu da durduruyor: bilinçli olarak
    /// seçilmesi gereken sert seçenek.
    [Fact]
    public void StopEverything_YarimVideoyuDaDurduruyor()
    {
        var verdict = BudgetPolicy.Decide(
            [Channel(0.98m)], 0.05m, runAlreadyStarted: true, BudgetAction.StopEverything);

        Assert.False(verdict.Allowed);
    }

    /// Gözlem dönemi: limit bir bilgi, bir kural değil.
    [Fact]
    public void WarnOnly_HicbirSeyiDurdurmuyor()
    {
        var verdict = BudgetPolicy.Decide(
            [Channel(10m)], 5m, runAlreadyStarted: false, BudgetAction.WarnOnly);

        Assert.True(verdict.Allowed);
    }

    /// HANGİ limitin çarptığı gerekçede yazıyor: "bütçe aşıldı" tek
    /// başına, kanal limitini mi yoksa global aylığı mı büyütmek
    /// gerektiğini söylemiyor.
    [Fact]
    public void Gerekce_HangiPencereyiSoyluyor()
    {
        var verdict = BudgetPolicy.Decide(
            [Channel(0.10m), Global(49.99m)], 0.05m, runAlreadyStarted: false);

        Assert.False(verdict.Allowed);
        Assert.Contains("Global aylık", verdict.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("Kanal", verdict.Reason, StringComparison.Ordinal);
    }

    /// Aylık limit dolduğunda bir saat sonra denemek anlamsız: ayın
    /// kalanında hiçbir şey değişmeyecek.
    [Fact]
    public void AylikPencere_AySonunaKadarBekliyor()
    {
        var verdict = BudgetPolicy.Decide([Global(50m)], 0.05m, runAlreadyStarted: false);

        Assert.Equal(TimeSpan.FromDays(12), verdict.RetryAfter);
    }

    /// Limiti olmayan pencere hiçbir şeyi engellemiyor.
    [Fact]
    public void LimitsizPencere_Geciyor()
    {
        var verdict = BudgetPolicy.Decide(
            [Channel(1000m, limit: null)], 500m, runAlreadyStarted: false);

        Assert.True(verdict.Allowed);
    }

    [Fact]
    public void HicPencereYok_Geciyor()
    {
        Assert.True(BudgetPolicy.Decide([], 999m, runAlreadyStarted: false).Allowed);
    }

    /// Sınırın TAM üstünde kalmak geçiyor; aşmak geçmiyor.
    [Theory]
    [InlineData(0.95, true)]
    [InlineData(0.96, false)]
    public void SinirDavranisi_UstundeGecmiyor(double spent, bool expected)
    {
        var verdict = BudgetPolicy.Decide([Channel((decimal)spent)], 0.05m, runAlreadyStarted: false);

        Assert.Equal(expected, verdict.Allowed);
    }

    /// Tahmin bir ÖLÇÜM değil ve öyle olduğu adında yazıyor; tek işi
    /// kapıyı videonun ortasında değil başlamadan önce çalıştırmak.
    [Fact]
    public void Tahmin_UcretsizHatIcinSifir()
    {
        var estimate = BudgetPolicy.EstimateRun(
            sentenceCount: 8, paidTts: false, paidLlm: false, paidImages: false);

        Assert.Equal(0m, estimate);
    }

    [Fact]
    public void Tahmin_UcretliHatIcinArtiyor()
    {
        var small = BudgetPolicy.EstimateRun(4, paidTts: true, paidLlm: true, paidImages: true);
        var large = BudgetPolicy.EstimateRun(20, paidTts: true, paidLlm: true, paidImages: true);

        Assert.True(large > small);
        Assert.True(small > 0);
    }

    /// Tanınmayan bir eylem VARSAYILANA düşüyor. `StopEverything`'e
    /// düşmek daha güvenli görünürdü ama değil: bir yazım hatası
    /// yüzünden yarım videoların çöpe gitmesi, bütçenin biraz
    /// aşılmasından pahalı.
    [Theory]
    [InlineData("stop", BudgetAction.StopEverything)]
    [InlineData("WARN_ONLY", BudgetAction.WarnOnly)]
    [InlineData("finish", BudgetAction.FinishInFlight)]
    [InlineData("bilinmeyen", BudgetAction.FinishInFlight)]
    [InlineData(null, BudgetAction.FinishInFlight)]
    public void EylemAdi_Okunuyor(string? text, BudgetAction expected)
    {
        Assert.Equal(expected, BudgetPolicy.ParseAction(text));
    }

    [Fact]
    public void PencereSifirlanmalari_IleriDe()
    {
        var now = new DateTimeOffset(2026, 8, 28, 14, 30, 0, TimeSpan.Zero);

        Assert.True(BudgetPolicy.UntilTomorrow(now) > TimeSpan.Zero);
        Assert.True(BudgetPolicy.UntilNextMonth(now) > BudgetPolicy.UntilTomorrow(now));
    }

    [Fact]
    public void KalanButce_Okunabiliyor()
    {
        Assert.Equal(0.40m, Channel(0.60m).Remaining);
        Assert.Equal(0m, Channel(1.50m).Remaining);
    }
}
