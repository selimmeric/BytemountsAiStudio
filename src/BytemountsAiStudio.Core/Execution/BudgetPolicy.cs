using System.Globalization;

namespace BytemountsAiStudio.Core.Execution;

/// Limit aşıldığında ne yapılacağı (P2-03, `action_on_exceed`).
public enum BudgetAction
{
    /// Yeni iş başlatma, yarım kalanları bitir. VARSAYILAN.
    FinishInFlight = 0,

    /// Her şeyi durdur — yarım videolar dâhil.
    StopEverything = 1,

    /// Uyar ama devam et. Yalnızca gözlem dönemi için.
    WarnOnly = 2,
}

/// Bir bütçe kararının sonucu.
public enum BudgetOutcome
{
    Allowed = 0,

    /// Limit aşıldı ve iş ERTELENDİ. Hata değil (ADR-011).
    Deferred = 1,
}

public sealed record BudgetVerdict(BudgetOutcome Outcome, string Reason, TimeSpan RetryAfter)
{
    public bool Allowed => Outcome == BudgetOutcome.Allowed;
}

/// Bir bütçe penceresinin durumu.
public sealed record BudgetWindow(string Name, decimal Spent, decimal? Limit, TimeSpan UntilReset)
{
    public bool Exceeded(decimal additional) => Limit is { } limit && Spent + additional > limit;

    public decimal Remaining => Limit is { } limit ? Math.Max(limit - Spent, 0) : decimal.MaxValue;
}

/// Bütçe kapısının KARARI (P2-03, §13.2).
///
/// SAF: veritabanı yok. "Bu çağrı yapılabilir mi" kararı, gerçek para
/// harcanarak öğrenilecek bir şey olmamalı.
///
/// EN ÖNEMLİ AYRIM: yeni bir run başlatmak ile YARIM KALMIŞ bir run'ı
/// sürdürmek aynı şey değil.
///
/// Yarım bir videoyu bütçe yüzünden durdurmak, o ana kadar harcanan
/// her kuruşu çöpe atmak demek — senaryo yazılmış, ses üretilmiş,
/// görseller indirilmiş ve hiçbiri kullanılmayacak. Üstelik ertesi gün
/// devam edilse bile o adımlar yeniden çalışacak ve İKİNCİ KEZ para
/// harcayacak.
///
/// Bu yüzden varsayılan davranış `FinishInFlight`: kapı yeni işlere
/// kapanıyor, çalışanlar bitiyor. Kabul kriteri tam olarak bu.
public static class BudgetPolicy
{
    /// Bir yayının kabaca maliyeti.
    ///
    /// TAHMİN, ölçüm değil — ve öyle olduğu adında yazıyor. Gerçek
    /// maliyet `provider_calls`'tan geliyor (ADR-006'nın maliyet
    /// karşılığı). Bu sayının tek işi kapıyı önceden çalıştırmak:
    /// bütçeyi 40 saniyelik bir videonun ortasında değil, başlamadan
    /// önce kontrol etmek.
    public static decimal EstimateRun(int sentenceCount, bool paidTts, bool paidLlm, bool paidImages)
    {
        var sentences = Math.Clamp(sentenceCount, 1, 200);

        // Ortalama cümle ~90 karakter; ElevenLabs karakter başına
        // fiyatlıyor.
        var tts = paidTts ? sentences * 90 * 0.00003m : 0m;

        // Senaryo tek güçlü çağrı; kalanı yerel modele düşüyor
        // (ADR-015).
        var llm = paidLlm ? 0.02m : 0m;

        // Sahne başına bir görsel; sahne sayısı cümleden az oluyor
        // (birleşme, P1-16).
        var images = paidImages ? Math.Max(sentences / 2, 1) * 0.01m : 0m;

        return decimal.Round(tts + llm + images, 4);
    }

    /// Bütün pencereleri değerlendirir.
    ///
    /// Pencereler SIRAYLA bakılıyor ve ilk aşan kazanıyor: hangi
    /// limitin çarptığını bilmek gerekiyor. "Bütçe aşıldı" tek başına,
    /// kanal limitini mi yoksa global aylığı mı büyütmek gerektiğini
    /// söylemiyor.
    public static BudgetVerdict Decide(
        IReadOnlyList<BudgetWindow> windows,
        decimal estimatedCost,
        bool runAlreadyStarted,
        BudgetAction action = BudgetAction.FinishInFlight)
    {
        ArgumentNullException.ThrowIfNull(windows);

        foreach (var window in windows)
        {
            if (!window.Exceeded(estimatedCost))
            {
                continue;
            }

            var reason = string.Create(CultureInfo.InvariantCulture,
                $"{window.Name} bütçesi aşılacaktı: {window.Spent:0.####} + {estimatedCost:0.####} > {window.Limit:0.####}");

            switch (action)
            {
                case BudgetAction.WarnOnly:
                    // Gözlem dönemi: limit bir bilgi, bir kural değil.
                    continue;

                case BudgetAction.FinishInFlight when runAlreadyStarted:
                    // YARIM VİDEO BİTİYOR. Durdurmak, o ana kadar
                    // harcanan her kuruşu çöpe atmak ve ertesi gün
                    // aynı adımları İKİNCİ KEZ ödemek demekti.
                    continue;

                default:
                    return new BudgetVerdict(BudgetOutcome.Deferred, reason, window.UntilReset);
            }
        }

        return new BudgetVerdict(BudgetOutcome.Allowed, "bütçe uygun", TimeSpan.Zero);
    }

    /// Eylem adını okur. Tanınmayan bir değer VARSAYILANA düşüyor.
    ///
    /// `StopEverything`'e düşmek daha "güvenli" görünürdü ama değil:
    /// yapılandırmadaki bir yazım hatası yüzünden yarım videoların
    /// çöpe gitmesi, bütçenin biraz aşılmasından pahalı.
    public static BudgetAction ParseAction(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "STOP" or "STOP_EVERYTHING" => BudgetAction.StopEverything,
        "WARN" or "WARN_ONLY" => BudgetAction.WarnOnly,
        _ => BudgetAction.FinishInFlight,
    };

    /// Gün sonuna kalan süre — günlük pencerenin sıfırlanması.
    public static TimeSpan UntilTomorrow(DateTimeOffset now)
        => now.Date.AddDays(1) - now.DateTime;

    /// Ay sonuna kalan süre — aylık pencerenin sıfırlanması.
    ///
    /// Aylık limit dolduğunda işi bir saat sonra denemek anlamsız:
    /// ayın kalanında hiçbir şey değişmeyecek. Doğru bekleme ayın
    /// sonuna kadar.
    public static TimeSpan UntilNextMonth(DateTimeOffset now)
        => new DateTime(now.Year, now.Month, 1).AddMonths(1) - now.DateTime;
}
