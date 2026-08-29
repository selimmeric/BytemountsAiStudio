using System.Globalization;

namespace BytemountsAiStudio.Core.Execution;

/// Havuzdaki bir hesabın o günkü durumu (P4-04).
public readonly record struct QuotaAccountState(string Account, int SpentToday, int DailyLimit)
{
    public int Remaining => Math.Max(0, DailyLimit - SpentToday);
}

/// Havuz kararı.
public enum PoolOutcome
{
    /// Bir hesap seçildi.
    Selected = 0,

    /// Havuzda hiç hesap yok — yapılandırma eksik, kota değil.
    NoAccounts = 1,

    /// Bütün hesapların kotası dolu.
    Exhausted = 2,
}

/// Havuzun verdiği karar ve GEREKÇESİ.
public sealed record PoolDecision(
    PoolOutcome Outcome,
    string? Account,
    int Cost,
    int RemainingAfter,
    int PoolRemaining,
    string Reason)
{
    public bool Granted => Outcome == PoolOutcome.Selected;
}

/// Birden fazla hesap arasında kota havuzu (P4-04).
///
/// YouTube günlük 10.000 birim veriyor ve bir yükleme 1.600 birim —
/// yani PROJE BAŞINA GÜNDE ALTI VİDEO. Faz 4'ün hedefi günde 100 video
/// ve tek proje bunun on altıda birini bile karşılamıyor. Ölçek sorunu
/// burada bir performans sorunu değil, bir MUHASEBE sorunu.
///
/// SAF: veritabanı yok. "Hangi hesap kullanılmalı" kararı, gerçek bir
/// kota tüketilerek öğrenilecek bir şey olmamalı — `QuotaLedger` ile
/// aynı gerekçe.
public static class QuotaPool
{
    /// Havuzdan bir hesap seçer.
    ///
    /// ***EN ÇOK KALANI SEÇİLİYOR, SIRAYLA DEĞİL.***
    ///
    /// Sırayla (round-robin) dağıtmak "adil" görünüyor ve kapasiteyi
    /// MAHSUR BIRAKIYOR: her hesapta 1.500 birim kalmışken 1.600'lük
    /// bir iş hiçbirine sığmıyor — toplamda binlerce birim boşta
    /// dururken sistem "kota bitti" diyor. En çok kalanı seçmek,
    /// harcamayı bir hesapta yoğunlaştırıp diğerlerini büyük işlere
    /// bütün bırakıyor.
    ///
    /// EŞİTLİKTE AD SIRASI: aynı kalanı olan iki hesap arasında
    /// rastgele seçim, aynı girdiye farklı cevap vermek ve bir hatayı
    /// yeniden üretilemez kılmak olurdu.
    public static PoolDecision Select(
        IReadOnlyList<QuotaAccountState> accounts, int cost, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(accounts);

        var needed = Math.Max(cost, 0);

        if (accounts.Count == 0)
        {
            // HESAP YOKLUĞU KOTA BİTMESİ DEĞİL. İkisini aynı saymak,
            // hiç yapılandırılmamış bir sistemin "yarın dolar" diye
            // beklemesi ve hiç uyarmaması demekti.
            return new PoolDecision(PoolOutcome.NoAccounts, null, needed, 0, 0,
                "Havuzda hiç hesap yok; kota bitmedi, hesap tanımlanmamış.");
        }

        var poolRemaining = accounts.Sum(a => a.Remaining);

        var best = accounts
            .Where(a => a.Remaining >= needed)
            .OrderByDescending(a => a.Remaining)
            .ThenBy(a => a.Account, StringComparer.Ordinal)
            .FirstOrDefault();

        if (best.Account is null)
        {
            var largest = accounts.Max(a => a.Remaining);

            return new PoolDecision(PoolOutcome.Exhausted, null, needed, 0, poolRemaining,
                string.Create(CultureInfo.InvariantCulture,
                    $"{needed} birim gerekiyor; en dolu hesapta {largest} kaldı, ")
                + string.Create(CultureInfo.InvariantCulture,
                    $"havuzda toplam {poolRemaining}. ")
                + "Kısmi yükleme diye bir şey yok: yarım kotayla başlanan yükleme "
                + "ortasında reddedilir ve harcanan kısım geri gelmez.");
        }

        return new PoolDecision(
            PoolOutcome.Selected,
            best.Account,
            needed,
            best.Remaining - needed,
            poolRemaining - needed,
            string.Create(CultureInfo.InvariantCulture,
                $"'{best.Account}' seçildi: {best.Remaining} kalan, {needed} rezerve."));
    }

    /// Havuzun bugünkü toplam kapasitesi — kaç yayın sığar.
    ///
    /// PARÇALANMA SAYILIYOR: hesap başına ayrı ayrı bölünüyor, toplam
    /// kalan tek bir havuzmuş gibi bölünmüyor. Üç hesapta 1.500'er
    /// birim "toplam 4.500" görünüyor ama 1.600'lük bir iş HİÇBİRİNE
    /// sığmıyor — kapasite sıfır.
    public static int Capacity(IReadOnlyList<QuotaAccountState> accounts, int costPerPublish)
    {
        ArgumentNullException.ThrowIfNull(accounts);

        if (costPerPublish <= 0)
        {
            return 0;
        }

        return accounts.Sum(a => a.Remaining / costPerPublish);
    }

    /// Kotanın gün anahtarı — Pasifik tarihi.
    ///
    /// YouTube kotayı Pasifik saatiyle gece yarısı sıfırlıyor. Anahtarı
    /// UTC tarihinden üretmek, günün yedi–sekiz saatinde YANLIŞ GÜNE
    /// yazmak demekti: sabaha karşı yapılan bir yükleme dünün kotasına
    /// düşer ve bugünün havuzu olduğundan dolu görünürdü.
    public static string DayKey(DateTimeOffset now)
    {
        var pacific = TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "Pacific Standard Time" : "America/Los_Angeles");

        return TimeZoneInfo.ConvertTime(now, pacific)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
