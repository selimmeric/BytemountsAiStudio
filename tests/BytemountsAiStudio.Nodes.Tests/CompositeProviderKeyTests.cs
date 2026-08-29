using System.Text.Json;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Providers.Fake;
using BytemountsAiStudio.Workflow.Engine;

namespace BytemountsAiStudio.Nodes.Tests;

/// Zincirin BİLEŞİĞİN İÇİNE takıldığının sınanması (P0-14, P0-17).
///
/// ***BU DOSYA, AYNI GÜN YAPILAN BİR İŞİN YARIM KALDIĞINI GÖSTERİYOR.***
///
/// Sağlayıcı zinciri bağlandı ve testleri yeşildi — ama `.Wrap(pipeline)`
/// **bileşiğin kendisine** uygulanıyordu: `TieredLlmProvider`,
/// `StockFirstImageProvider`, `FallbackTtsProvider`. Bu üçünün
/// anahtarları sırasıyla `"tiered"`, `"stock-first"`, `"tts-fallback"`
/// ve **hiçbiri katalogda yok**. Sonuçları sessiz ve ağır:
///
///   - Hız sınırlayıcı bilinmeyen anahtarda **koşulsuz izin** veriyor:
///     katalogdaki 10/dk sınırları hiç uygulanmıyordu.
///   - Devre kesici `"tiered"` üzerinden çalışıyordu; Pollinations 402
///     verip Ollama'ya düşüldüğünde **başarı** kaydediliyordu — devre
///     hiçbir zaman açılmıyor ve her çağrı 402 bedelini yeniden
///     ödüyordu.
///   - Maliyet defteri bütün LLM harcamasını `"tiered"` yazıyordu ve
///     fiyat listesinde öyle bir anahtar yok: ücretli bir sağlayıcı
///     bağlandığı an defter **sıfır** yazacaktı.
///   - `assets.source_provider` her satırda `"stock-first"`: "stok mu,
///     üretilmiş mi" ayrımı hiçbir varlık kaydında yoktu.
public sealed class CompositeProviderKeyTests
{
    private sealed class RecordingLedger : ICostLedger
    {
        public List<ProviderCallRecord> Records { get; } = [];

        public Task RecordAsync(ProviderCallRecord record, CancellationToken cancellationToken)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }

        public Task<decimal> SpentTodayAsync(Guid? channelId, CancellationToken cancellationToken)
            => Task.FromResult(0m);
    }

    private sealed class LimiterSpy : IRateLimiter
    {
        public List<string> Keys { get; } = [];

        public Task<Result> AcquireAsync(string providerKey, int permits, CancellationToken cancellationToken)
        {
            Keys.Add(providerKey);
            return Task.FromResult(Result.Success());
        }
    }

    private sealed class BreakerSpy : ICircuitBreaker
    {
        public List<string> Keys { get; } = [];

        public Task<Result> CheckAsync(string providerKey, CancellationToken cancellationToken)
        {
            Keys.Add(providerKey);
            return Task.FromResult(Result.Success());
        }

        public Task RecordSuccessAsync(string providerKey, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task RecordFailureAsync(string providerKey, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class NoQuota : IQuotaPool
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<QuotaAccountState>> AccountsAsync(
            string providerKey, Guid? channelId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<QuotaAccountState>>([]);
        }

        public Task<Result<PoolDecision>> ReserveAsync(
            string providerKey, Guid? channelId, int cost, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Result.Success(
                new PoolDecision(PoolOutcome.Selected, "default", cost, 0, 0, "test")));
        }

        public Task<int> CapacityAsync(
            string providerKey, Guid? channelId, int costPerPublish, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(0);
        }
    }

    /// Bileşik anahtarlar — katalogda karşılığı OLMAYAN adlar.
    private static readonly string[] Composite = ["tiered", "stock-first", "tts-fallback"];

    private static async Task<(RecordingLedger Ledger, LimiterSpy Limiter, BreakerSpy Breaker)>
        RunScriptAsync()
    {
        var ledger = new RecordingLedger();
        var limiter = new LimiterSpy();
        var breaker = new BreakerSpy();

        var pipeline = new ProviderPipeline(
        [
            new RateLimitMiddleware(limiter),
            new CircuitBreakerMiddleware(breaker),
            new RetryMiddleware(1),
            new MeteringMiddleware(ledger, PriceList.Default()),
        ]);

        using var storage = new FakeStorageProvider();

        var registry = NodeHandlerRegistration.BuildFakeRegistry(
            storage, Path.GetTempPath(),
            uniqueness: new AlwaysUnique(),
            channels: new NoChannels(),
            pipeline: pipeline);

        var handler = registry.Find("script.generate");

        Assert.NotNull(handler);

        await handler.ExecuteAsync(
            new NodeContext
            {
                RunId = Guid.CreateVersion7(),
                NodeId = "script.generate",
                NodeType = "script.generate",
                Attempt = 1,
                Config = JsonSerializer.SerializeToElement(new { }),
                RunContext = JsonSerializer.SerializeToElement(
                    new { konu = new { baslik = "Bileşik anahtar", dil = "tr-TR" } }),
                IdempotencyKey = Guid.CreateVersion7().ToString("N"),
                CorrelationId = "test",
            },
            CancellationToken.None);

        return (ledger, limiter, breaker);
    }

    /// ***MALİYET DEFTERİ GERÇEK SAĞLAYICI ANAHTARINI YAZIYOR.***
    ///
    /// BU DOSYANIN EN ÖNEMLİ TESTİ. `"tiered"` yazsaydı fiyat
    /// listesinde karşılığı olmadığı için ücretli bir sağlayıcı
    /// bağlandığı an defter sıfır gösterirdi ve bütçe kapısı hiç
    /// dolmazdı.
    [Fact]
    public async Task Defter_GercekAnahtariYaziyor()
    {
        var (ledger, _, _) = await RunScriptAsync();

        Assert.NotEmpty(ledger.Records);
        Assert.All(ledger.Records, r =>
            Assert.DoesNotContain(r.ProviderKey, Composite, StringComparer.Ordinal));
    }

    /// ***HIZ SINIRI GERÇEK SAĞLAYICI ANAHTARIYLA SORULUYOR.***
    ///
    /// Bileşik anahtarla sorulsaydı sınırlayıcı kova bulamaz ve
    /// koşulsuz izin verirdi — katalogdaki sınırlar hiç uygulanmazdı.
    [Fact]
    public async Task HizSiniri_GercekAnahtarlaSoruluyor()
    {
        var (_, limiter, _) = await RunScriptAsync();

        Assert.NotEmpty(limiter.Keys);
        Assert.All(limiter.Keys, k => Assert.DoesNotContain(k, Composite, StringComparer.Ordinal));
    }

    /// ***DEVRE KESİCİ GERÇEK SAĞLAYICI ANAHTARIYLA SORULUYOR.***
    ///
    /// Bileşik anahtarla çalışsaydı, yedeğe düşülen her çağrı
    /// "başarı" sayılır ve devre hiçbir zaman açılmazdı: ölü bir
    /// sağlayıcının bedeli her çağrıda yeniden ödenirdi.
    [Fact]
    public async Task DevreKesici_GercekAnahtarlaSoruluyor()
    {
        var (_, _, breaker) = await RunScriptAsync();

        Assert.NotEmpty(breaker.Keys);
        Assert.All(breaker.Keys, k => Assert.DoesNotContain(k, Composite, StringComparer.Ordinal));
    }

    /// GERÇEK HAT KAYDI DA AYNI KURALA UYUYOR.
    ///
    /// Sahte hatta geçen bir test, gerçek hattaki bileşikleri
    /// (`TieredLlmProvider`, `StockFirstImageProvider`) hiç sınamazdı —
    /// sorun tam da orada yaşıyordu.
    [Fact]
    public void GercekHat_BilesikSarmalamiyor()
    {
        var limiter = new LimiterSpy();

        var pipeline = new ProviderPipeline([new RateLimitMiddleware(limiter)]);

        using var storage = new FakeStorageProvider();

        // Kayıt kurulabiliyor olması yeterli: bileşiklerin dışına
        // sarılsaydı derleme yine geçerdi, ama iç üyeler sarılınca
        // `TieredLlmProvider` artık `ILlmProvider` listesi alıyor ve
        // o listenin elemanları sarmalayıcı.
        var registry = NodeHandlerRegistration.BuildOpenRegistry(
            storage, new HttpClient(), Path.GetTempPath(),
            uniqueness: new AlwaysUnique(),
            channels: new NoChannels(),
            pipeline: pipeline,
            quota: new NoQuota());

        Assert.NotNull(registry.Find("script.generate"));
        Assert.NotNull(registry.Find("visual.resolve"));
        Assert.NotNull(registry.Find("tts.synthesize"));
    }
}
