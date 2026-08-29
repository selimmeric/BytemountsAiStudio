using System.Diagnostics;
using System.Text.Json;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;

namespace BytemountsAiStudio.Contracts.Providers;

/// 1. Idempotency — önbellekte varsa hiçbir şey çalışmaz.
///
/// ADR-010: retry'ın ikinci kez para harcamasını engelleyen tek mekanizma.
/// En dışta olması şart: bütçe kontrolü, rate limit ve devre kesici bile
/// gereksiz — çağrı zaten yapılmayacak.
public sealed class IdempotencyMiddleware(IProviderResultCache cache) : IProviderMiddleware
{
    public int Order => MiddlewareOrder.Idempotency;

    public async Task<Result<ProviderResponse<T>>> InvokeAsync<T>(
        ProviderInvocation<T> invocation,
        Func<CancellationToken, Task<Result<ProviderResponse<T>>>> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(next);

        // ÖNBELLEKLENEMEYEN ÇAĞRI ZİNCİRİN GERİ KALANINI ATLAMIYOR:
        // yalnızca bu katman devre dışı. Bütçe, hız sınırı, devre
        // kesici ve ölçüm aynen çalışıyor.
        if (!invocation.Cacheable)
        {
            return await next(cancellationToken).ConfigureAwait(false);
        }

        var key = invocation.Context.IdempotencyKey;

        var cached = await cache.TryGetAsync(key, invocation.Operation, cancellationToken)
            .ConfigureAwait(false);

        if (cached is not null)
        {
            try
            {
                var value = JsonSerializer.Deserialize<T>(cached);

                if (value is not null)
                {
                    // Önbellekten gelen sonucun maliyeti SIFIR: para daha önce
                    // harcandı, ikinci kez saymak defteri şişirirdi.
                    return Result.Success(new ProviderResponse<T>(value, UsageUnits.None));
                }
            }
            catch (JsonException)
            {
                // Bozuk önbellek kaydı çağrıyı engellememelidir; yeniden yapılır.
            }
        }

        var result = await next(cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            await cache.SetAsync(key, invocation.Operation,
                JsonSerializer.Serialize(result.Value.Value), cancellationToken).ConfigureAwait(false);
        }

        return result;
    }
}

/// 2. Bütçe kapısı — para yoksa sıraya girmenin anlamı yok.
public sealed class BudgetMiddleware(IBudgetGate gate, Func<Guid?> channelResolver) : IProviderMiddleware
{
    public int Order => MiddlewareOrder.Budget;

    public async Task<Result<ProviderResponse<T>>> InvokeAsync<T>(
        ProviderInvocation<T> invocation,
        Func<CancellationToken, Task<Result<ProviderResponse<T>>>> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(next);

        var authorized = await gate
            .AuthorizeAsync(channelResolver(), invocation.EstimatedCost, cancellationToken)
            .ConfigureAwait(false);

        return authorized.IsFailure
            ? Result.Failure<ProviderResponse<T>>(authorized.Error)
            : await next(cancellationToken).ConfigureAwait(false);
    }
}

/// 3. Rate limit — sağlayıcı hesabı başına, worker başına DEĞİL.
///
/// Worker başına olsaydı beş worker beş kat istek atardı ve sağlayıcı
/// bizi keserdi. §8.3.
public sealed class RateLimitMiddleware(IRateLimiter limiter) : IProviderMiddleware
{
    public int Order => MiddlewareOrder.RateLimit;

    public async Task<Result<ProviderResponse<T>>> InvokeAsync<T>(
        ProviderInvocation<T> invocation,
        Func<CancellationToken, Task<Result<ProviderResponse<T>>>> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(next);

        var permit = await limiter.AcquireAsync(invocation.ProviderKey, 1, cancellationToken)
            .ConfigureAwait(false);

        return permit.IsFailure
            ? Result.Failure<ProviderResponse<T>>(permit.Error)
            : await next(cancellationToken).ConfigureAwait(false);
    }
}

/// 4. Devre kesici — sağlayıcı zaten ölüyse retry'a hiç girme.
public sealed class CircuitBreakerMiddleware(ICircuitBreaker breaker) : IProviderMiddleware
{
    public int Order => MiddlewareOrder.CircuitBreaker;

    public async Task<Result<ProviderResponse<T>>> InvokeAsync<T>(
        ProviderInvocation<T> invocation,
        Func<CancellationToken, Task<Result<ProviderResponse<T>>>> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(next);

        var state = await breaker.CheckAsync(invocation.ProviderKey, cancellationToken)
            .ConfigureAwait(false);
        if (state.IsFailure)
        {
            return Result.Failure<ProviderResponse<T>>(state.Error);
        }

        var result = await next(cancellationToken).ConfigureAwait(false);

        // Yalnızca GEÇİCİ hatalar devreyi açar. Kalıcı hata sağlayıcının
        // sağlıksız olduğu anlamına gelmez — bizim isteğimiz bozuktur.
        if (result.IsSuccess)
        {
            await breaker.RecordSuccessAsync(invocation.ProviderKey, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (result.Error.Kind == ErrorKind.Transient)
        {
            await breaker.RecordFailureAsync(invocation.ProviderKey, cancellationToken)
                .ConfigureAwait(false);
        }

        return result;
    }
}

/// 5. Retry — buradan içerisi tekrarlanır.
public sealed class RetryMiddleware(int maxAttempts = 3, TimeProvider? timeProvider = null) : IProviderMiddleware
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public int Order => MiddlewareOrder.Retry;

    public async Task<Result<ProviderResponse<T>>> InvokeAsync<T>(
        ProviderInvocation<T> invocation,
        Func<CancellationToken, Task<Result<ProviderResponse<T>>>> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        Result<ProviderResponse<T>> result = default;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            result = await next(cancellationToken).ConfigureAwait(false);

            if (result.IsSuccess || result.Error.Kind != ErrorKind.Transient)
            {
                return result;
            }

            if (attempt == maxAttempts)
            {
                break;
            }

            var delay = result.Error.RetryAfter ?? TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt - 1));

            // Negatif gecikme testlerde beklemeyi atlamak için kullanılıyor.
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
            }
        }

        return result;
    }
}

/// 6. Ölçüm — HER denemenin maliyeti sayılır.
///
/// Retry'ın İÇİNDE olması kritik. Dışında olsaydı üç kez denenip başarısız
/// olan bir çağrının maliyeti bir kez sayılır ve defter gerçeği söylemezdi.
/// Başarısız çağrı da para harcamış olabilir.
public sealed class MeteringMiddleware(
    ICostLedger ledger, IPriceList prices, TimeProvider? timeProvider = null) : IProviderMiddleware
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public int Order => MiddlewareOrder.Metering;

    public async Task<Result<ProviderResponse<T>>> InvokeAsync<T>(
        ProviderInvocation<T> invocation,
        Func<CancellationToken, Task<Result<ProviderResponse<T>>>> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(next);

        var started = _time.GetTimestamp();
        var result = await next(cancellationToken).ConfigureAwait(false);
        var latency = (int)_time.GetElapsedTime(started).TotalMilliseconds;

        var units = result.IsSuccess ? result.Value.Usage : UsageUnits.None;

        await ledger.RecordAsync(new ProviderCallRecord
        {
            ProviderKey = invocation.ProviderKey,
            Operation = invocation.Operation,
            Units = units,
            Cost = prices.Price(invocation.ProviderKey, invocation.Operation, units),
            LatencyMs = latency,
            Succeeded = result.IsSuccess,
        }, cancellationToken).ConfigureAwait(false);

        return result;
    }
}

/// 7. Telemetri — gerçek çağrıya en yakın nokta.
public sealed class TelemetryMiddleware : IProviderMiddleware
{
    /// Tüm sağlayıcı çağrıları tek kaynaktan izlenir; correlation id
    /// sayesinde bir run'ın tamamı tek sorguyla toplanabilir (§2.4/22).
    public static readonly ActivitySource Source = new("BytemountsAiStudio.Providers");

    public int Order => MiddlewareOrder.Telemetry;

    public async Task<Result<ProviderResponse<T>>> InvokeAsync<T>(
        ProviderInvocation<T> invocation,
        Func<CancellationToken, Task<Result<ProviderResponse<T>>>> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(next);

        using var activity = Source.StartActivity(
            $"{invocation.ProviderKey}.{invocation.Operation}", ActivityKind.Client);

        activity?.SetTag("provider.key", invocation.ProviderKey);
        activity?.SetTag("provider.operation", invocation.Operation);
        activity?.SetTag("run.correlation_id", invocation.Context.CorrelationId);

        var result = await next(cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            activity?.SetStatus(ActivityStatusCode.Error, result.Error.Code);
            activity?.SetTag("error.kind", result.Error.Kind.ToString());
        }

        return result;
    }
}
