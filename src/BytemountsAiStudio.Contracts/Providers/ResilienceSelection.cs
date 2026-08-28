using StackExchange.Redis;

namespace BytemountsAiStudio.Contracts.Providers;

/// Hız sınırı ve devre kesici nereden gelecek (P4-03).
///
/// TEK KARAR NOKTASI — `StorageSelection` ile aynı gerekçe: Worker, API
/// ve CLI aynı seçimi yapmalı. Üç yerde ayrı `if` yazmak, birinin
/// dağıtık birinin süreç içi sınır kullanması demekti ve o fark ancak
/// sağlayıcı bizi kestiğinde görülürdü.
///
/// SEÇİM ORTAM DEĞİŞKENİNDEN: `BMAI_REDIS` doluysa dağıtık, boşsa
/// süreç içi. Varsayılanın süreç içi olması bilinçli — tek makinede
/// çalışan biri için süreç içi sınır hem yeterli hem DOĞRU, ve
/// `dotnet run` yapan birinin çalışan bir Redis'e ihtiyacı olmamalı.
public static class ResilienceSelection
{
    public static bool UsesRedis
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BMAI_REDIS"));

    /// Bağlantı — kurulamıyorsa `null`.
    ///
    /// AÇILIŞTA ÇÖKMÜYOR: Redis geçici olarak erişilemez olabilir ve
    /// o zaman doğru davranış süreç içi sınırla devam etmek. Üretimi
    /// bir yardımcı servisin kesintisinde tamamen durdurmak, dağıtık
    /// sınırın kazandırdığından fazlasını kaybettirirdi.
    ///
    /// Ama SESSİZ değil: `onFailure` çağrılıyor.
    public static IConnectionMultiplexer? TryConnect(Action<Exception>? onFailure = null)
    {
        var endpoint = Environment.GetEnvironmentVariable("BMAI_REDIS");

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return null;
        }

        try
        {
            return ConnectionMultiplexer.Connect(endpoint);
        }
        catch (RedisConnectionException ex)
        {
            onFailure?.Invoke(ex);
            return null;
        }
    }

    /// Hız sınırlayıcı. `connection` yoksa süreç içi.
    public static IRateLimiter RateLimiter(
        IConnectionMultiplexer? connection,
        IReadOnlyDictionary<string, RateLimitPolicy> policies,
        Action<Exception>? onDegraded = null)
    {
        ArgumentNullException.ThrowIfNull(policies);

        if (connection is null)
        {
            var local = new TokenBucketRateLimiter();

            foreach (var (key, policy) in policies)
            {
                local.Configure(key, policy);
            }

            return local;
        }

        var shared = new RedisRateLimiter(connection, onDegraded);

        foreach (var (key, policy) in policies)
        {
            shared.Configure(key, policy);
        }

        return shared;
    }

    /// Devre kesici. `connection` yoksa süreç içi.
    public static ICircuitBreaker CircuitBreaker(
        IConnectionMultiplexer? connection,
        int failureThreshold = 5,
        TimeSpan? openDuration = null,
        Action<Exception>? onDegraded = null)
        => connection is null
            ? new CircuitBreaker(failureThreshold, openDuration)
            : new RedisCircuitBreaker(connection, failureThreshold, openDuration, onDegraded: onDegraded);
}
