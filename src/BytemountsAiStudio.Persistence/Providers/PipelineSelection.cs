using BytemountsAiStudio.Contracts.Providers;

namespace BytemountsAiStudio.Persistence.Providers;

/// Sağlayıcı zincirinin KURULDUĞU TEK YER (P0-14).
///
/// `StorageSelection` ve `ResilienceSelection` ile aynı gerekçe: Worker,
/// API ve CLI aynı zinciri kurmalı. Üç yerde ayrı kurmak, birinin
/// maliyeti sayıp diğerinin saymaması demekti — ve bu depoda tam olarak
/// o hata (CLI ile Worker'ın farklı kurulması) bir günü götürdü.
///
/// ***ZİNCİR NEDEN BURADA, `Contracts` İÇİNDE DEĞİL:*** maliyet defteri
/// ve bütçe kapısı veritabanına bağlı. `Contracts` katmanı arayüzleri ve
/// saf ara katmanları tanımlıyor; onları gerçek bir deftere bağlamak
/// kalıcılık katmanının işi.
public static class PipelineSelection
{
    /// Ara katman zincirinin varsayılan deneme sayısı.
    ///
    /// KUYRUK DENEMESİYLE AYNI ŞEY DEĞİL: kuyruk bütün NODE'u yeniden
    /// koşuyor (senaryo yeniden üretiliyor, ses yeniden sentezleniyor),
    /// buradaki deneme yalnızca DÜŞEN ÇAĞRIYI tekrarlıyor. İkisi
    /// çarpılıyor ve bu kasıtlı: 3 çağrı denemesi x 3 node denemesi,
    /// geçici bir kesintide toplam dokuz şans.
    public const string AttemptsVariable = "BMAI_PROVIDER_ATTEMPTS";

    /// Devre kesici eşiği — kaç ardışık hatadan sonra sağlayıcı ölü
    /// sayılıyor.
    public const string BreakerThresholdVariable = "BMAI_BREAKER_THRESHOLD";

    /// Süreç içi sonuç önbelleği TEK ÖRNEK olmak zorunda.
    ///
    /// Her istekte yeni bir örnek kurulsaydı önbellek her çağrıda boş
    /// olurdu: idempotency hiç çalışmaz ve retry ikinci kez para
    /// harcardı — yani ADR-010 sessizce ölürdü. Zincirin kendisi
    /// scoped (deftere bağlı), ama önbellek süreç ömrü boyunca yaşıyor.
    private static readonly InMemoryResultCache SharedCache = new();

    /// Zinciri kurar.
    ///
    /// `ledger` AYNI ZAMANDA bağlam taşıyıcısı: `RunId`/`NodeId`/
    /// `ChannelId` alanları node çalışmadan önce doldurulup zincirin
    /// yazdığı her satıra geçiyor. Ayrı bir "bağlam" nesnesi eklemek,
    /// ikisinin ayrışması ve maliyetin sahipsiz satırlara yazılması
    /// demekti.
    public static ProviderPipeline Build(
        CostLedger ledger,
        IBudgetGate budget,
        IRateLimiter limiter,
        ICircuitBreaker breaker,
        IPriceList? prices = null,
        IProviderResultCache? cache = null,
        int? maxAttempts = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(ledger);

        return new ProviderPipeline(
        [
            new IdempotencyMiddleware(cache ?? SharedCache),

            // KANAL DEFTERDEN OKUNUYOR, ayrı bir parametreden değil:
            // bütçe kapısının baktığı kanal ile maliyetin yazıldığı
            // kanal aynı olmalı. İki kaynaktan gelselerdi, biri
            // güncellenip diğeri unutulduğunda bütçe BAŞKA bir kanalın
            // limitini uygular ve bunu hiçbir test yakalamazdı.
            new BudgetMiddleware(budget, () => ledger.ChannelId),
            new RateLimitMiddleware(limiter),
            new CircuitBreakerMiddleware(breaker),
            new RetryMiddleware(maxAttempts ?? Attempts(), timeProvider),
            new MeteringMiddleware(ledger, prices ?? PriceList.Default(), timeProvider),
            new TelemetryMiddleware(),
        ]);
    }

    /// Katalog dosyasının yeri.
    ///
    /// PARAMETRİK: `config/providers.json` çalışma dizinine göre
    /// çözülüyor ve `BMAI_PROVIDERS` ile ezilebiliyor. Konteynerde
    /// katalog başka bir yola bağlanabilir; sabit yol, o kurulumda
    /// bütün hız sınırlarının sessizce kaybolması demekti.
    public const string CatalogVariable = "BMAI_PROVIDERS";

    public static string CatalogPath()
        => Environment.GetEnvironmentVariable(CatalogVariable) is { Length: > 0 } custom
            ? custom
            : Path.Combine(Directory.GetCurrentDirectory(), "config", "providers.json");

    /// Zincirin HOST'LARDA kurulan hâli.
    ///
    /// Worker, API ve CLI bu tek satırı çağırıyor. Üç yerde ayrı
    /// kurmak, birinin dağıtık birinin süreç içi sınır kullanması ya da
    /// birinin ölçüm katmanını unutması demekti.
    ///
    /// ***KATALOG OKUNAMAZSA ZİNCİR YİNE KURULUYOR, SINIRSIZ OLARAK.***
    /// Dosya yoksa ya da bozuksa doğru davranış üretimi durdurmak
    /// değil — ama sessiz de kalmamak: `onDegraded` çağrılıyor ve host
    /// bunu logluyor. Sınırsız koşmayı fark etmemek, sağlayıcının
    /// hesabı kesmesiyle öğrenilirdi.
    public static ProviderPipeline BuildFrom(
        StudioDbContext db,
        Action<string>? onDegraded = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(db);

        var ledger = new CostLedger(db, timeProvider);
        var catalog = ProviderCatalog.Load(CatalogPath());

        if (catalog.IsFailure)
        {
            onDegraded?.Invoke(
                $"Sağlayıcı kataloğu okunamadı ({CatalogPath()}): {catalog.Error.Message}. "
                + "Hız sınırları UYGULANMIYOR.");
        }

        var policies = catalog.IsSuccess
            ? catalog.Value.RateLimitPolicies()
            : new Dictionary<string, RateLimitPolicy>(StringComparer.Ordinal);

        // BAĞLANTI BİR KEZ, İKİ TÜKETİCİYE: hız sınırı ve devre kesici
        // aynı Redis'i kullanıyor. İki bağlantı açmak, birinin düşüp
        // diğerinin ayakta kalması ve sistemin yarı dağıtık davranması
        // demekti.
        var redis = ResilienceSelection.TryConnect(
            ex => onDegraded?.Invoke($"Redis'e bağlanılamadı: {ex.Message}. Sınırlar süreç içi."));

        return Build(
            ledger,
            new BudgetGate(db, ledger),
            ResilienceSelection.RateLimiter(redis, policies,
                ex => onDegraded?.Invoke($"Dağıtık hız sınırı düştü: {ex.Message}")),
            ResilienceSelection.CircuitBreaker(redis, BreakerThreshold(),
                onDegraded: ex => onDegraded?.Invoke($"Dağıtık devre kesici düştü: {ex.Message}")),
            timeProvider: timeProvider);
    }

    /// Ortamdan okunan deneme sayısı; geçersizse 3.
    ///
    /// SIFIR VE NEGATİF REDDEDİLİYOR: sıfır deneme "hiç çağırma"
    /// demek olurdu ve bunu bir yazım hatasıyla elde etmek, hattın
    /// sessizce durması demekti.
    public static int Attempts()
        => int.TryParse(Environment.GetEnvironmentVariable(AttemptsVariable),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0
                ? value
                : 3;

    public static int BreakerThreshold()
        => int.TryParse(Environment.GetEnvironmentVariable(BreakerThresholdVariable),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0
                ? value
                : 5;
}
