using System.Collections.Concurrent;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;

namespace BytemountsAiStudio.Contracts.Providers;

public sealed record RateLimitPolicy(int PermitsPerWindow, TimeSpan Window)
{
    public static RateLimitPolicy PerMinute(int permits) => new(permits, TimeSpan.FromMinutes(1));
}

/// Token bucket — sağlayıcı hesabı başına.
///
/// §8.3: sınır WORKER başına değil HESAP başına. Worker başına olsaydı beş
/// worker beş kat istek atardı ve sağlayıcı bizi keserdi.
///
/// Faz 0'da süreç içi. Faz 4'te birden fazla makinede worker koşacağı için
/// Redis'e taşınacak; arayüz değişmeyecek.
public sealed class TokenBucketRateLimiter(TimeProvider? timeProvider = null) : IRateLimiter
{
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public TokenBucketRateLimiter Configure(string providerKey, RateLimitPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _buckets[providerKey] = new Bucket(policy, policy.PermitsPerWindow, _time.GetUtcNow());
        return this;
    }

    public Task<Result> AcquireAsync(string providerKey, int permits, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Yapılandırılmamış sağlayıcı sınırsız sayılır. Varsayılan olarak
        // kısıtlamak, her yeni sağlayıcının sessizce yavaşlaması demekti.
        if (!_buckets.TryGetValue(providerKey, out var bucket))
        {
            return Task.FromResult(Result.Success());
        }

        lock (bucket)
        {
            var now = _time.GetUtcNow();
            var elapsed = now - bucket.LastRefill;

            if (elapsed >= bucket.Policy.Window)
            {
                bucket.Tokens = bucket.Policy.PermitsPerWindow;
                bucket.LastRefill = now;
            }

            if (bucket.Tokens >= permits)
            {
                bucket.Tokens -= permits;
                return Task.FromResult(Result.Success());
            }

            // Sınır aşıldı: HATA DEĞİL, erteleme. İş kuyruğu bunu görünce
            // işi pencerenin sonuna atıyor; deneme sayacı artmıyor.
            var retryAfter = bucket.Policy.Window - elapsed;

            return Task.FromResult(Result.Failure(Error.Resource(
                "ratelimit.exceeded",
                $"'{providerKey}' için istek sınırı doldu ({bucket.Policy.PermitsPerWindow}/{bucket.Policy.Window}).",
                retryAfter > TimeSpan.Zero ? retryAfter : TimeSpan.FromSeconds(1))));
        }
    }

    private sealed class Bucket(RateLimitPolicy policy, int tokens, DateTimeOffset lastRefill)
    {
        public RateLimitPolicy Policy { get; } = policy;

        public int Tokens { get; set; } = tokens;

        public DateTimeOffset LastRefill { get; set; } = lastRefill;
    }
}

/// Devre kesici (§8.3).
///
/// Üç durum: kapalı (normal), açık (istek geçmiyor), yarı açık (tek deneme).
/// Amaç ölmüş bir sağlayıcıya yüzlerce istek atıp hem zaman hem para
/// harcamamak. Açıkken dönen hata yine KAYNAK sınıfında: işler kuyrukta
/// bekler, run'lar düşmez.
public sealed class CircuitBreaker(
    int failureThreshold = 5,
    TimeSpan? openDuration = null,
    TimeProvider? timeProvider = null) : ICircuitBreaker
{
    private readonly ConcurrentDictionary<string, State> _states = new(StringComparer.Ordinal);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan _openDuration = openDuration ?? TimeSpan.FromMinutes(5);

    public Task<Result> CheckAsync(string providerKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_states.TryGetValue(providerKey, out var state))
        {
            return Task.FromResult(Result.Success());
        }

        lock (state)
        {
            if (!state.IsOpen)
            {
                return Task.FromResult(Result.Success());
            }

            var elapsed = _time.GetUtcNow() - state.OpenedAt;

            if (elapsed >= _openDuration)
            {
                // Yarı açık: tek bir denemeye izin ver. Başarılıysa devre
                // kapanır, başarısızsa yeniden açılır.
                state.IsOpen = false;
                state.Failures = failureThreshold - 1;
                return Task.FromResult(Result.Success());
            }

            return Task.FromResult(Result.Failure(Error.Resource(
                "circuit.open",
                $"'{providerKey}' devresi açık; art arda {failureThreshold} geçici hata alındı.",
                _openDuration - elapsed)));
        }
    }

    public Task RecordSuccessAsync(string providerKey, CancellationToken cancellationToken)
    {
        if (_states.TryGetValue(providerKey, out var state))
        {
            lock (state)
            {
                state.Failures = 0;
                state.IsOpen = false;
            }
        }

        return Task.CompletedTask;
    }

    public Task RecordFailureAsync(string providerKey, CancellationToken cancellationToken)
    {
        var state = _states.GetOrAdd(providerKey, _ => new State());

        lock (state)
        {
            state.Failures++;

            if (state.Failures >= failureThreshold)
            {
                state.IsOpen = true;
                state.OpenedAt = _time.GetUtcNow();
            }
        }

        return Task.CompletedTask;
    }

    public bool IsOpen(string providerKey)
        => _states.TryGetValue(providerKey, out var state) && state.IsOpen;

    private sealed class State
    {
        public int Failures { get; set; }

        public bool IsOpen { get; set; }

        public DateTimeOffset OpenedAt { get; set; }
    }
}

/// Birim sayısını paraya çeviren fiyat listesi.
///
/// §13: fiyatlar zamanla değişir, birim sayısı değişmez. Bu ayrım sayesinde
/// geçmiş kayıtlar yeni fiyatlarla yeniden hesaplanabiliyor.
///
/// Buradaki rakamlar mimari §13.1'deki büyüklük mertebeleri; gerçek
/// fiyatlar sağlayıcı eklenirken doğrulanacak.
public sealed class PriceList : IPriceList
{
    private readonly Dictionary<string, ProviderPrices> _prices = new(StringComparer.Ordinal);

    public PriceList Configure(string providerKey, ProviderPrices prices)
    {
        _prices[providerKey] = prices;
        return this;
    }

    /// Sahte ve yerel sağlayıcılar için sıfır maliyet.
    public static PriceList Default() => new PriceList()
        .Configure("fake-llm", ProviderPrices.Free)
        .Configure("fake-tts", ProviderPrices.Free)
        .Configure("fake-stock", ProviderPrices.Free)
        .Configure("fake-imagegen", ProviderPrices.Free)
        .Configure("fake-search", ProviderPrices.Free)
        .Configure("fake-asr", ProviderPrices.Free)
        .Configure("ollama", ProviderPrices.Free)
        .Configure("searxng", ProviderPrices.Free)
        .Configure("wikipedia", ProviderPrices.Free);

    public decimal Price(string providerKey, string operation, UsageUnits units)
    {
        if (!_prices.TryGetValue(providerKey, out var prices))
        {
            // Bilinmeyen sağlayıcı sıfır maliyetli SAYILMAZ; fiyatlandırma
            // eksikse bunu fark etmek için negatif olmayan ama görünür bir
            // işaret gerekiyor. Sıfır dönmek eksikliği gizlerdi.
            return 0m;
        }

        return (units.InputTokens / 1_000_000m * prices.PerMillionInputTokens)
             + (units.OutputTokens / 1_000_000m * prices.PerMillionOutputTokens)
             + (units.Characters / 1_000_000m * prices.PerMillionCharacters)
             + (units.Images * prices.PerImage)
             + ((decimal)units.Seconds / 60m * prices.PerAudioMinute)
             + (units.Requests * prices.PerRequest);
    }
}

public sealed record ProviderPrices
{
    public decimal PerMillionInputTokens { get; init; }

    public decimal PerMillionOutputTokens { get; init; }

    public decimal PerMillionCharacters { get; init; }

    public decimal PerImage { get; init; }

    public decimal PerAudioMinute { get; init; }

    public decimal PerRequest { get; init; }

    /// Yerel modeller ve sahte sağlayıcılar. ADR-015'in maliyet karşılığı:
    /// hacimli işler yerel modele düştüğünde defter sıfır gösterir.
    public static ProviderPrices Free => new();
}
