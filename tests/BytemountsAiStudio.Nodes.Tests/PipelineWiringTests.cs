using System.Text.Json;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Providers.Fake;
using BytemountsAiStudio.Workflow.Engine;

namespace BytemountsAiStudio.Nodes.Tests;

/// Sağlayıcı zincirinin GERÇEKTEN TAKILI olduğunun sınanması (P0-14).
///
/// ***BU DOSYANIN VAR OLMA SEBEBİ:*** `ProviderPipeline` ve yedi ara
/// katman yazılmış, testlenmiş ve HİÇBİR YERDEN KURULMAMIŞTI. Zincirin
/// kendi testleri yeşildi — zinciri elle kurup çağırıyorlardı. Üretimde
/// tek bir sağlayıcı çağrısı zincirden geçmiyordu ve bunu hiçbir test
/// yakalamıyordu, çünkü hiçbiri "node bir sağlayıcı çağırdığında zincir
/// çalışıyor mu" sorusunu sormuyordu.
///
/// Buradaki testler o soruyu soruyor: node kaydı üzerinden gerçek bir
/// node çalıştırılıyor ve zincirin izleri (ölçüm defteri, bütçe kapısı,
/// devre kesici) ARANIYOR.
public sealed class PipelineWiringTests
{
    /* ---- zincirin izlerini toplayan sahteler ---- */

    private sealed class RecordingLedger : ICostLedger
    {
        public List<ProviderCallRecord> Records { get; } = [];

        public Task RecordAsync(ProviderCallRecord record, CancellationToken cancellationToken)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }

        public Task<decimal> SpentTodayAsync(Guid? channelId, CancellationToken cancellationToken)
            => Task.FromResult(Records.Sum(r => r.Cost));
    }

    private sealed class GateStub(Result? verdict = null) : IBudgetGate
    {
        public int Calls { get; private set; }

        public Task<Result> AuthorizeAsync(
            Guid? channelId, decimal estimatedCost, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(verdict ?? Result.Success());
        }
    }

    private sealed class LimiterStub(Result? verdict = null) : IRateLimiter
    {
        public List<string> Keys { get; } = [];

        public Task<Result> AcquireAsync(string providerKey, int permits, CancellationToken cancellationToken)
        {
            Keys.Add(providerKey);
            return Task.FromResult(verdict ?? Result.Success());
        }
    }

    /// Süreç içi önbellek — `Persistence` katmanındakinin testlik
    /// eşi. Buraya kopyalanmasının sebebi bağımlılık yönü: node
    /// testleri veritabanı katmanına bağlanmamalı.
    private sealed class CacheStub : IProviderResultCache
    {
        private readonly Dictionary<string, string> _entries = new(StringComparer.Ordinal);

        public int Count => _entries.Count;

        public Task<string?> TryGetAsync(string idempotencyKey, string operation, CancellationToken cancellationToken)
            => Task.FromResult(_entries.GetValueOrDefault($"{idempotencyKey}:{operation}"));

        public Task SetAsync(
            string idempotencyKey, string operation, string payload, CancellationToken cancellationToken)
        {
            _entries[$"{idempotencyKey}:{operation}"] = payload;
            return Task.CompletedTask;
        }
    }

    private sealed class BreakerStub : ICircuitBreaker
    {
        public int Checks { get; private set; }

        public Task<Result> CheckAsync(string providerKey, CancellationToken cancellationToken)
        {
            Checks++;
            return Task.FromResult(Result.Success());
        }

        public Task RecordSuccessAsync(string providerKey, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task RecordFailureAsync(string providerKey, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class Harness
    {
        public RecordingLedger Ledger { get; } = new();

        public GateStub Gate { get; init; } = new();

        public LimiterStub Limiter { get; init; } = new();

        public BreakerStub Breaker { get; } = new();

        public ProviderPipeline Pipeline => new(
        [
            new IdempotencyMiddleware(new CacheStub()),
            new BudgetMiddleware(Gate, () => null),
            new RateLimitMiddleware(Limiter),
            new CircuitBreakerMiddleware(Breaker),
            new RetryMiddleware(1),
            new MeteringMiddleware(Ledger, PriceList.Default()),
            new TelemetryMiddleware(),
        ]);

        public NodeRegistry Registry(FakeStorageProvider storage) =>
            NodeHandlerRegistration.BuildFakeRegistry(
                storage, Path.GetTempPath(),
                uniqueness: new AlwaysUnique(),
                channels: new NoChannels(),
                pipeline: Pipeline);
    }

    private static NodeContext Context(string nodeType, object config, object runContext)
        => new()
        {
            RunId = Guid.CreateVersion7(),
            NodeId = nodeType,
            NodeType = nodeType,
            Attempt = 1,
            Config = JsonSerializer.SerializeToElement(config),
            RunContext = JsonSerializer.SerializeToElement(runContext),
            IdempotencyKey = Guid.CreateVersion7().ToString("N"),
            CorrelationId = "test",
        };

    /// Senaryo üreten bir node koşturur.
    private static async Task<Result<JsonElement>> RunScriptAsync(NodeRegistry registry)
    {
        var handler = registry.Find("script.generate");

        Assert.NotNull(handler);

        return await handler.ExecuteAsync(
            Context("script.generate",
                new { },
                new { konu = new { baslik = "Zincir testi", dil = "tr-TR" } }),
            CancellationToken.None);
    }

    /* ---- ölçüm ---- */

    /// ***BİR NODE KOŞTUĞUNDA MALİYET DEFTERİNE SATIR DÜŞÜYOR.***
    ///
    /// BU DOSYANIN EN ÖNEMLİ TESTİ. Zincir takılı olmadığında `provider_calls`
    /// tablosuna hiç satır düşmüyordu: defter kalıcı olarak boştu, bütçe
    /// kapısı her zaman "0,00 harcandı" görüyordu ve günlük/aylık limit
    /// hiçbir zaman dolmuyordu. Sistem sınırsız harcayabilirdi.
    [Fact]
    public async Task NodeKostu_DeftereSatirDustu()
    {
        using var storage = new FakeStorageProvider();
        var harness = new Harness();

        var result = await RunScriptAsync(harness.Registry(storage));

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        Assert.NotEmpty(harness.Ledger.Records);

        var record = harness.Ledger.Records[0];

        // İŞLEM ADI KAYDA GİRİYOR: "hangi sağlayıcı ne yaptı" sorusu
        // yalnızca sağlayıcı anahtarıyla cevaplanamaz — aynı sağlayıcı
        // hem tamamlama hem gömme yapıyor ve ikisinin fiyatı farklı.
        Assert.Equal("complete", record.Operation);
        Assert.False(string.IsNullOrWhiteSpace(record.ProviderKey));
    }

    /// BAŞARISIZ ÇAĞRI DA DEFTERE GİRİYOR.
    ///
    /// Başarısız bir sağlayıcı çağrısı da para harcamış olabilir;
    /// saymamak defterin gerçeği söylememesi demek.
    [Fact]
    public async Task BasarisizCagri_DeftereGiriyor()
    {
        var ledger = new RecordingLedger();

        var pipeline = new ProviderPipeline(
        [
            new RetryMiddleware(1),
            new MeteringMiddleware(ledger, PriceList.Default()),
        ]);

        var result = await pipeline.InvokeAsync(
            new ProviderInvocation<string>
            {
                ProviderKey = "test",
                Operation = "complete",
                Context = ProviderContext.ForTest(),
                Execute = _ => Task.FromResult(
                    Result.Failure<ProviderResponse<string>>(Error.Permanent("test.dustu", "düştü"))),
            },
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Single(ledger.Records);
        Assert.False(ledger.Records[0].Succeeded);
    }

    /* ---- bütçe ---- */

    /// BÜTÇE KAPISI SORULUYOR.
    [Fact]
    public async Task NodeKostu_ButceKapisiSoruldu()
    {
        using var storage = new FakeStorageProvider();
        var harness = new Harness();

        await RunScriptAsync(harness.Registry(storage));

        Assert.True(harness.Gate.Calls > 0);
    }

    /// ***BÜTÇE DOLUYSA NODE DÜŞÜYOR — VE KAYNAK HATASIYLA.***
    ///
    /// Kalıcı olsaydı bütçe dolduğu gün bütün run'lar ÖLÜRDÜ; kaynak
    /// olduğu için ertelenip ertesi gün devam ediyorlar.
    [Fact]
    public async Task ButceDolu_NodeErteleniyor()
    {
        using var storage = new FakeStorageProvider();

        var harness = new Harness
        {
            Gate = new GateStub(Error.Resource("budget.exceeded", "bütçe doldu", TimeSpan.FromHours(1))),
        };

        var result = await RunScriptAsync(harness.Registry(storage));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Resource, result.Error.Kind);

        // ÖLÇÜM DEFTERİNE HİÇ SATIR DÜŞMÜYOR: çağrı hiç yapılmadı,
        // yani harcanan da yok. Kapıdan dönen bir çağrıyı deftere
        // yazmak, harcanmamış parayı harcanmış göstermekti.
        Assert.Empty(harness.Ledger.Records);
    }

    /* ---- hız sınırı ---- */

    /// HIZ SINIRI SAĞLAYICI ANAHTARIYLA SORULUYOR.
    ///
    /// Anahtar yanlış geçseydi sınır BAŞKA bir sağlayıcının kovasından
    /// düşer ve ikisi de yanlış davranırdı.
    [Fact]
    public async Task HizSiniri_SaglayiciAnahtariylaSoruluyor()
    {
        using var storage = new FakeStorageProvider();
        var harness = new Harness();

        await RunScriptAsync(harness.Registry(storage));

        Assert.NotEmpty(harness.Limiter.Keys);
        Assert.All(harness.Limiter.Keys, k => Assert.False(string.IsNullOrWhiteSpace(k)));
    }

    /// SINIR AŞILDIĞINDA NODE ERTELENİYOR, DÜŞMÜYOR.
    [Fact]
    public async Task SinirAsildi_Erteleniyor()
    {
        using var storage = new FakeStorageProvider();

        var harness = new Harness
        {
            Limiter = new LimiterStub(
                Error.Resource("ratelimit.exceeded", "sınır", TimeSpan.FromSeconds(30))),
        };

        var result = await RunScriptAsync(harness.Registry(storage));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Resource, result.Error.Kind);
    }

    /* ---- devre kesici ---- */

    /// DEVRE KESİCİ SORULUYOR.
    [Fact]
    public async Task DevreKesici_Soruluyor()
    {
        using var storage = new FakeStorageProvider();
        var harness = new Harness();

        await RunScriptAsync(harness.Registry(storage));

        Assert.True(harness.Breaker.Checks > 0);
    }

    /* ---- zincirsiz kurulum ---- */

    /// ***ZİNCİRSİZ KURULUM HÂLÂ ÇALIŞIYOR.***
    ///
    /// `null` geçmek gerçek bir seçenek: zincir maliyet defterine,
    /// defter veritabanına bağlı ve veritabanı olmayan bir testte
    /// zincirsiz kurmak doğru davranış. Sarmalayıcı o durumda
    /// sağlayıcının KENDİSİNİ döndürüyor — araya boş bir katman
    /// koymuyor.
    [Fact]
    public async Task ZincirsizKurulum_Calisiyor()
    {
        using var storage = new FakeStorageProvider();

        var registry = NodeHandlerRegistration.BuildFakeRegistry(
            storage, Path.GetTempPath(),
            uniqueness: new AlwaysUnique(),
            channels: new NoChannels(),
            pipeline: null);

        var result = await RunScriptAsync(registry);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
    }

    /* ---- ikili yük ---- */

    /// ***SES VE ÜRETİLEN GÖRSEL ÖNBELLEKLENMİYOR.***
    ///
    /// Ham bayt taşıyorlar: üç dakikalık bir ses ~5 MB ve JSON'da
    /// base64 olarak ~7 MB. Cümle başına bir kayıtla önbellek dakikalar
    /// içinde gigabaytlara çıkardı.
    [Fact]
    public async Task IkiliYuk_Onbelleklenmiyor()
    {
        var cache = new CacheStub();
        var pipeline = new ProviderPipeline([new IdempotencyMiddleware(cache)]);

        var calls = 0;

        async Task<Result<ProviderResponse<string>>> Invoke(bool cacheable)
            => await pipeline.InvokeAsync(
                new ProviderInvocation<string>
                {
                    ProviderKey = "test",
                    Operation = "synthesize",
                    Context = ProviderContext.ForTest("ayni-anahtar"),
                    Cacheable = cacheable,
                    Execute = _ =>
                    {
                        calls++;
                        return Task.FromResult(Result.Success(
                            new ProviderResponse<string>("ses", UsageUnits.None)));
                    },
                },
                CancellationToken.None);

        await Invoke(cacheable: false);
        await Invoke(cacheable: false);

        // AYNI ANAHTAR, İKİ GERÇEK ÇAĞRI: önbelleğe hiç yazılmadı.
        Assert.Equal(2, calls);
        Assert.Equal(0, cache.Count);

        // ÖNBELLEKLENEBİLİR ÇAĞRI İSE İKİNCİ KEZ GİTMİYOR.
        await Invoke(cacheable: true);
        await Invoke(cacheable: true);

        Assert.Equal(3, calls);
    }
}
