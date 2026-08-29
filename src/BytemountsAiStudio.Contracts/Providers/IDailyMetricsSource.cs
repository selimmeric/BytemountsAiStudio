using BytemountsAiStudio.Core;

namespace BytemountsAiStudio.Contracts.Providers;

/// Bir videonun bir güne ait ölçümü (P5-01).
public readonly record struct DailyMetric(
    DateOnly Date,
    long Views,
    long EstimatedMinutesWatched,
    long Likes,
    long Comments,
    long SubscribersGained);

/// Yayın sonrası günlük ölçüm kaynağı (P5-01).
///
/// ARAYÜZ, ÇÜNKÜ KAYNAK PLATFORMA GÖRE DEĞİŞİYOR: YouTube Analytics,
/// TikTok Insights ve Instagram Insights ayrı API'ler ama sorulan soru
/// aynı — "bu video o gün kaç kez izlendi".
///
/// Toplayıcı somut sağlayıcıya bağlanmıyor: bağlansaydı depolama
/// katmanı sağlayıcı katmanına bağımlı olur ve ölçüm toplama mantığı
/// gerçek bir API olmadan sınanamazdı.
public interface IDailyMetricsSource
{
    /// "youtube", "tiktok", "instagram".
    string Platform { get; }

    /// O günün verisi OTURDU MU.
    ///
    /// Analitik raporları geriden geliyor; oturmamış bir günü çekmek
    /// tamamlanmamış bir sayıyı tam sanmak demek.
    bool IsSettled(DateOnly metricDate, DateOnly today);

    Task<Result<DailyMetric?>> DailyAsync(
        string externalId, DateOnly date, CancellationToken cancellationToken);
}
