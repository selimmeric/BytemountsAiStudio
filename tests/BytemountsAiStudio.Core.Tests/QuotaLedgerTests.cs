using BytemountsAiStudio.Core.Execution;

namespace BytemountsAiStudio.Core.Tests;

/// Kota rezervasyonunun testleri (P1-24).
///
/// Kabul kriteri: **kota bitince iş `WaitingResource`, ertesi güne
/// kayıyor; hata sayılmıyor.** Bu kararın saf olması şart — "bugün
/// çalışabilir mi" sorusu, gerçek bir kota tüketilerek öğrenilecek bir
/// şey olmamalı.
public sealed class QuotaLedgerTests
{
    private static readonly DateTimeOffset Noon = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BosKota_RezerveEdiliyor()
    {
        var reservation = QuotaLedger.Reserve(spentToday: 0, cost: QuotaLedger.UploadCost, Noon);

        Assert.True(reservation.Granted);
        Assert.Equal(QuotaLedger.DailyUnits - QuotaLedger.UploadCost, reservation.RemainingAfter);
    }

    /// Günde altı video: 6 × 1600 = 9600, yedincisi sığmıyor.
    [Fact]
    public void GunlukSinir_AltiVideo()
    {
        var sixth = QuotaLedger.Reserve(5 * QuotaLedger.UploadCost, QuotaLedger.UploadCost, Noon);
        var seventh = QuotaLedger.Reserve(6 * QuotaLedger.UploadCost, QuotaLedger.UploadCost, Noon);

        Assert.True(sixth.Granted);
        Assert.False(seventh.Granted);
    }

    /// TAM SIĞMASI gerekiyor: yarım kotayla başlanan bir yükleme
    /// ortasında reddedilir ve harcanan kısım geri gelmez.
    [Fact]
    public void KismiSigma_ReddEdiliyor()
    {
        var reservation = QuotaLedger.Reserve(
            spentToday: QuotaLedger.DailyUnits - 100, cost: QuotaLedger.UploadCost, Noon);

        Assert.False(reservation.Granted);
        Assert.Equal(QuotaOutcome.Exhausted, reservation.Outcome);
        Assert.Equal(100, reservation.RemainingAfter);
    }

    /// Kapak AYRI sayılıyor: hep kapaklı varsaymak, günde bir
    /// videoluk kotayı boşuna rezerve etmek demekti.
    [Fact]
    public void KapakVeListe_AyriUcretli()
    {
        Assert.Equal(1600, QuotaLedger.CostOf(withThumbnail: false, withPlaylist: false));
        Assert.Equal(1650, QuotaLedger.CostOf(withThumbnail: true, withPlaylist: false));
        Assert.Equal(1700, QuotaLedger.CostOf(withThumbnail: true, withPlaylist: true));
    }

    /// YouTube kotayı PASİFİK saatiyle gece yarısı sıfırlıyor, UTC'yle
    /// değil. UTC varsayılsaydı 7–8 saat sapma olurdu: iş ya erken
    /// uyanıp boşuna denerdi ya da geç uyanıp bir günü kaybederdi.
    [Fact]
    public void Sifirlanma_PasifikGeceYarisi()
    {
        var reset = QuotaLedger.NextReset(Noon);

        // 28 Ağustos yaz saati: Pasifik UTC−7. Gece yarısı → UTC 07:00.
        Assert.Equal(29, reset.UtcDateTime.Day);
        Assert.Equal(7, reset.UtcDateTime.Hour);
    }

    [Fact]
    public void Sifirlanma_HerZamanIleride()
    {
        foreach (var hour in new[] { 0, 6, 7, 8, 12, 23 })
        {
            var now = new DateTimeOffset(2026, 8, 28, hour, 0, 0, TimeSpan.Zero);

            Assert.True(QuotaLedger.NextReset(now) > now, $"saat {hour}");
        }
    }

    /// Kaynak hatasının `retryAfter` değeri sıfırlanma anına kadar:
    /// sabit bir süre vermek, kota gece yarısı sıfırlanırken sabaha
    /// kadar boşuna uyanmak demekti.
    [Fact]
    public void BeklemeSuresi_SifirlanmayaKadar()
    {
        var reservation = QuotaLedger.Reserve(QuotaLedger.DailyUnits, QuotaLedger.UploadCost, Noon);

        var wait = reservation.RetryAfter(Noon);

        Assert.Equal(reservation.ResetsAt - Noon, wait);
        Assert.True(wait > TimeSpan.Zero);
    }

    /// Sıfırlanma anı geçmişte kalmışsa (saat kayması) hemen tekrar
    /// denenmiyor ama sonsuza da beklenmiyor.
    [Fact]
    public void GecmisSifirlanma_KisaBekleme()
    {
        var reservation = QuotaLedger.Reserve(QuotaLedger.DailyUnits, QuotaLedger.UploadCost, Noon);

        var wait = reservation.RetryAfter(reservation.ResetsAt.AddHours(1));

        Assert.Equal(TimeSpan.FromMinutes(1), wait);
    }

    [Theory]
    [InlineData(-500)]
    [InlineData(0)]
    public void GecersizDegerler_CokmuYor(int spent)
    {
        var reservation = QuotaLedger.Reserve(spent, cost: -10, Noon);

        Assert.True(reservation.Granted);
        Assert.Equal(0, reservation.Cost);
    }

    /// Sıfır limit her şeyi reddediyor: kotası kapatılmış bir kanal
    /// sessizce yayın yapmamalı.
    [Fact]
    public void SifirLimit_HicbirSeyGecmiyor()
    {
        Assert.False(QuotaLedger.Reserve(0, QuotaLedger.UploadCost, Noon, dailyLimit: 0).Granted);
    }
}
