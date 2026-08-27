using System.Collections.Concurrent;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;

namespace BytemountsAiStudio.Contracts.Tests;

internal sealed class RecordingCache : IProviderResultCache
{
    private readonly ConcurrentDictionary<string, string> _entries = new(StringComparer.Ordinal);

    public int Hits { get; private set; }

    public Task<string?> TryGetAsync(string idempotencyKey, string operation, CancellationToken cancellationToken)
    {
        var found = _entries.TryGetValue(idempotencyKey + operation, out var value);
        if (found)
        {
            Hits++;
        }

        return Task.FromResult(found ? value : null);
    }

    public Task SetAsync(string idempotencyKey, string operation, string payload, CancellationToken cancellationToken)
    {
        _entries[idempotencyKey + operation] = payload;
        return Task.CompletedTask;
    }
}

internal sealed class RecordingLedger : ICostLedger
{
    public ConcurrentBag<ProviderCallRecord> Records { get; } = [];

    public Task RecordAsync(ProviderCallRecord record, CancellationToken cancellationToken)
    {
        Records.Add(record);
        return Task.CompletedTask;
    }

    public Task<decimal> SpentTodayAsync(Guid? channelId, CancellationToken cancellationToken)
        => Task.FromResult(Records.Sum(r => r.Cost));
}

internal sealed class FixedBudget(bool allow) : IBudgetGate
{
    public int Calls { get; private set; }

    public Task<Result> AuthorizeAsync(Guid? channelId, decimal estimatedCost, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(allow
            ? Result.Success()
            : Result.Failure(Error.Resource("budget.test", "Butce yok", TimeSpan.FromHours(1))));
    }
}

/// Dekoratör zincirinin testleri.
///
/// En değerlisi sıranın kendisini sabitleyen test: sıra bir tercih değil,
/// davranışın kendisi. Metering retry'ın dışına çıkarsa maliyet defteri
/// sessizce yanlış rakamlar göstermeye başlar.
public sealed class ProviderPipelineTests
{
    private static ProviderInvocation<string> Invocation(
        Func<CancellationToken, Task<Result<ProviderResponse<string>>>> execute,
        string key = "k1")
        => new()
        {
            ProviderKey = "test-provider",
            Operation = "complete",
            Context = ProviderContext.ForTest(key),
            Execute = execute,
            EstimatedCost = 0.01m,
        };

    private static Task<Result<ProviderResponse<string>>> Ok(string value, UsageUnits units = default)
        => Task.FromResult(Result.Success(new ProviderResponse<string>(value, units)));

    [Fact]
    public void ZincirSirasi_Sabittir()
    {
        // Sıra karışırsa: bütçe kontrolü önbellekten önce çalışır ve zaten
        // ödenmiş bir çağrı için bütçe harcanmış sayılır; ya da metering
        // retry'ın dışına çıkar ve başarısız denemeler sayılmaz.
        var pipeline = new ProviderPipeline(
        [
            new TelemetryMiddleware(),
            new MeteringMiddleware(new RecordingLedger(), PriceList.Default()),
            new RetryMiddleware(),
            new CircuitBreakerMiddleware(new CircuitBreaker()),
            new RateLimitMiddleware(new TokenBucketRateLimiter()),
            new BudgetMiddleware(new FixedBudget(true), () => null),
            new IdempotencyMiddleware(new RecordingCache()),
        ]);

        Assert.Equal(
            [
                nameof(IdempotencyMiddleware),
                nameof(BudgetMiddleware),
                nameof(RateLimitMiddleware),
                nameof(CircuitBreakerMiddleware),
                nameof(RetryMiddleware),
                nameof(MeteringMiddleware),
                nameof(TelemetryMiddleware),
            ],
            pipeline.Order);
    }

    [Fact]
    public async Task AyniAnahtarlaIkinciCagri_SaglayiciyaGitmez()
    {
        // ADR-010'un tamamı bu testte.
        var calls = 0;
        var cache = new RecordingCache();
        var pipeline = new ProviderPipeline([new IdempotencyMiddleware(cache)]);

        Task<Result<ProviderResponse<string>>> Execute(CancellationToken _)
        {
            calls++;
            return Ok("sonuc");
        }

        var first = await pipeline.InvokeAsync(Invocation(Execute), CancellationToken.None);
        var second = await pipeline.InvokeAsync(Invocation(Execute), CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal("sonuc", first.Value.Value);
        Assert.Equal("sonuc", second.Value.Value);
    }

    [Fact]
    public async Task OnbellektenGelenSonuc_MaliyetSifir()
    {
        // Para daha önce harcandı; ikinci kez saymak defteri şişirirdi.
        var cache = new RecordingCache();
        var pipeline = new ProviderPipeline([new IdempotencyMiddleware(cache)]);

        await pipeline.InvokeAsync(
            Invocation(_ => Ok("x", UsageUnits.Tokens(100, 50))), CancellationToken.None);

        var second = await pipeline.InvokeAsync(
            Invocation(_ => Ok("x", UsageUnits.Tokens(100, 50))), CancellationToken.None);

        Assert.Equal(0, second.Value.Usage.InputTokens);
    }

    [Fact]
    public async Task ButceYoksa_CagriYapilmaz()
    {
        var calls = 0;
        var pipeline = new ProviderPipeline([new BudgetMiddleware(new FixedBudget(false), () => null)]);

        var result = await pipeline.InvokeAsync(
            Invocation(_ => { calls++; return Ok("x"); }), CancellationToken.None);

        Assert.Equal(0, calls);
        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Resource, result.Error.Kind);
    }

    [Fact]
    public async Task BasarisizDenemelerinTumu_DeftereYazilir()
    {
        // Metering retry'ın İÇİNDE. Dışında olsaydı üç kez denenip başarısız
        // olan çağrı bir kez sayılır ve defter gerçeği söylemezdi.
        var ledger = new RecordingLedger();

        var pipeline = new ProviderPipeline(
        [
            new RetryMiddleware(maxAttempts: 3),
            new MeteringMiddleware(ledger, PriceList.Default()),
        ]);

        await pipeline.InvokeAsync(
            Invocation(_ => Task.FromResult(Result.Failure<ProviderResponse<string>>(
                Error.Transient("http.429", "Rate limit", TimeSpan.FromMilliseconds(-1))))),
            CancellationToken.None);

        Assert.Equal(3, ledger.Records.Count);
        Assert.All(ledger.Records, r => Assert.False(r.Succeeded));
    }

    [Fact]
    public async Task KaliciHata_TekrarDenenmez()
    {
        var calls = 0;
        var pipeline = new ProviderPipeline([new RetryMiddleware(maxAttempts: 3)]);

        await pipeline.InvokeAsync(
            Invocation(_ =>
            {
                calls++;
                return Task.FromResult(Result.Failure<ProviderResponse<string>>(
                    Error.Permanent("bad.request", "Gecersiz")));
            }),
            CancellationToken.None);

        Assert.Equal(1, calls);
    }
}

public sealed class RateLimiterTests
{
    [Fact]
    public async Task SinirDolunca_KaynakHatasiDoner()
    {
        // Hata DEĞİL, erteleme: iş kuyruğu bunu görünce işi ileri tarihe atar.
        var limiter = new TokenBucketRateLimiter()
            .Configure("p", new RateLimitPolicy(2, TimeSpan.FromMinutes(1)));

        Assert.True((await limiter.AcquireAsync("p", 1, CancellationToken.None)).IsSuccess);
        Assert.True((await limiter.AcquireAsync("p", 1, CancellationToken.None)).IsSuccess);

        var third = await limiter.AcquireAsync("p", 1, CancellationToken.None);

        Assert.True(third.IsFailure);
        Assert.Equal(ErrorKind.Resource, third.Error.Kind);
        Assert.NotNull(third.Error.RetryAfter);
    }

    [Fact]
    public async Task YapilandirilmamisSaglayici_Sinirsiz()
    {
        // Varsayılan olarak kısıtlamak, her yeni sağlayıcının sessizce
        // yavaşlaması demekti.
        var limiter = new TokenBucketRateLimiter();

        for (var i = 0; i < 50; i++)
        {
            Assert.True((await limiter.AcquireAsync("bilinmeyen", 1, CancellationToken.None)).IsSuccess);
        }
    }

    [Fact]
    public async Task PencereGecince_TokenlarYenilenir()
    {
        var time = new FakeTimeProvider();
        var limiter = new TokenBucketRateLimiter(time)
            .Configure("p", new RateLimitPolicy(1, TimeSpan.FromMinutes(1)));

        Assert.True((await limiter.AcquireAsync("p", 1, CancellationToken.None)).IsSuccess);
        Assert.True((await limiter.AcquireAsync("p", 1, CancellationToken.None)).IsFailure);

        time.Advance(TimeSpan.FromMinutes(2));

        Assert.True((await limiter.AcquireAsync("p", 1, CancellationToken.None)).IsSuccess);
    }
}

public sealed class CircuitBreakerTests
{
    [Fact]
    public void EsikAsilinca_DevreAcilir()
    {
        var breaker = new CircuitBreaker(failureThreshold: 3);

        for (var i = 0; i < 3; i++)
        {
            breaker.RecordFailure("p");
        }

        Assert.True(breaker.IsOpen("p"));
        Assert.True(breaker.Check("p").IsFailure);
    }

    [Fact]
    public void Basari_SayaciSifirlar()
    {
        var breaker = new CircuitBreaker(failureThreshold: 3);

        breaker.RecordFailure("p");
        breaker.RecordFailure("p");
        breaker.RecordSuccess("p");
        breaker.RecordFailure("p");

        Assert.False(breaker.IsOpen("p"));
    }

    [Fact]
    public void SureDolunca_YariAcikDenemeyeIzinVerir()
    {
        var time = new FakeTimeProvider();
        var breaker = new CircuitBreaker(2, TimeSpan.FromMinutes(5), time);

        breaker.RecordFailure("p");
        breaker.RecordFailure("p");

        Assert.True(breaker.Check("p").IsFailure);

        time.Advance(TimeSpan.FromMinutes(6));

        Assert.True(breaker.Check("p").IsSuccess);
    }

    [Fact]
    public async Task KaliciHata_DevreyiAcmaz()
    {
        // Kalıcı hata sağlayıcının sağlıksız olduğu anlamına gelmez;
        // bizim isteğimiz bozuktur. Devreyi açmak diğer run'ları da durdururdu.
        var breaker = new CircuitBreaker(failureThreshold: 2);
        var pipeline = new ProviderPipeline([new CircuitBreakerMiddleware(breaker)]);

        for (var i = 0; i < 5; i++)
        {
            await pipeline.InvokeAsync(new ProviderInvocation<string>
            {
                ProviderKey = "p",
                Operation = "x",
                Context = ProviderContext.ForTest(),
                Execute = _ => Task.FromResult(Result.Failure<ProviderResponse<string>>(
                    Error.Permanent("bad", "Gecersiz istek"))),
            }, CancellationToken.None);
        }

        Assert.False(breaker.IsOpen("p"));
    }
}

public sealed class PriceListTests
{
    [Fact]
    public void YerelSaglayicilar_SifirMaliyet()
    {
        // ADR-015'in maliyet karşılığı: hacimli işler yerel modele düşünce
        // defter sıfır gösteriyor.
        var prices = PriceList.Default();

        Assert.Equal(0m, prices.Price("ollama", "complete", UsageUnits.Tokens(1_000_000, 500_000)));
        Assert.Equal(0m, prices.Price("fake-tts", "synthesize", UsageUnits.OfCharacters(50_000)));
    }

    [Fact]
    public void BirimlerDogruCarpilir()
    {
        var prices = new PriceList().Configure("x", new ProviderPrices
        {
            PerMillionInputTokens = 3m,
            PerMillionOutputTokens = 15m,
            PerImage = 0.04m,
        });

        var cost = prices.Price("x", "complete", new UsageUnits
        {
            InputTokens = 1_000_000,
            OutputTokens = 200_000,
            Images = 5,
        });

        Assert.Equal(3m + 3m + 0.20m, cost);
    }
}

/// Test için ilerletilebilir saat.
internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}
