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

    /// ***HIZ SINIRI VE DEVRE KESICI SUREC OMURLU OLMAK ZORUNDA.***
    ///
    /// Onceden her cagrida yeniden kuruluyorlardi ve zincir SCOPED:
    /// `QueueWorker` her is icin yeni bir kapsam aciyor, yani kapsam =
    /// TEK NODE CALISTIRMASI. Sonucu sessiz ve tam da onlemeye
    /// calistiklari seydi:
    ///
    ///   - Jeton kovasi her iste DOLU basliyordu. Sinir "hesap basina"
    ///     degil "node calistirmasi basina" uygulaniyordu -- yani hic
    ///     uygulanmiyordu. `ResilienceSelection`'in kendi yorumu
    ///     "sinir WORKER basina degil HESAP basina" diyor; gercekte
    ///     worker basina bile degildi.
    ///   - Devre kesici her iste KAPALI basliyordu. Bes ardisik hata
    ///     esigi TEK node icinde dolmadikca devre hicbir zaman
    ///     acilmiyordu.
    ///   - `BMAI_REDIS` tanimliysa her is yeni bir
    ///     `ConnectionMultiplexer` aciyor ve hicbiri kapanmiyordu.
    ///
    /// Maliyet defteri ve butce kapisi SCOPED KALIYOR: ikisi de
    /// veritabanina bagli ve `DbContext` kapsam omurlu.
    private static readonly Lazy<Resilience> Shared = new(CreateResilience, isThreadSafe: true);

    private sealed record Resilience(IRateLimiter Limiter, ICircuitBreaker Breaker, string? Warning);

    private static Resilience CreateResilience()
    {
        string? warning = null;

        var catalog = ProviderCatalog.Load(CatalogPath());

        if (catalog.IsFailure)
        {
            warning = $"Saglayici katalogu okunamadi ({CatalogPath()}): {catalog.Error.Message}. "
                + "Hiz sinirlari UYGULANMIYOR.";
        }

        var policies = catalog.IsSuccess
            ? catalog.Value.RateLimitPolicies()
            : new Dictionary<string, RateLimitPolicy>(StringComparer.Ordinal);

        // BAGLANTI BIR KEZ, IKI TUKETICIYE: hiz siniri ve devre kesici
        // ayni Redis'i kullaniyor. Iki baglanti acmak, birinin dusup
        // digerinin ayakta kalmasi ve sistemin yari dagitik davranmasi
        // demekti.
        var redis = ResilienceSelection.TryConnect();

        return new Resilience(
            ResilienceSelection.RateLimiter(redis, policies),
            ResilienceSelection.CircuitBreaker(redis, BreakerThreshold()),
            warning);
    }

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

        // SUREC OMURLU BOLUM BIR KEZ KURULUYOR; buradaki cagri onu
        // yalnizca OKUYOR. Uyari her kapsamda tekrar edilmiyor --
        // is basina bir satir, gunde binlerce satir demekti.
        var shared = Shared.Value;

        if (shared.Warning is { } warning && Interlocked.Exchange(ref _warned, 1) == 0)
        {
            onDegraded?.Invoke(warning);
        }

        return Build(
            ledger,
            new BudgetGate(db, ledger),
            shared.Limiter,
            shared.Breaker,
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

    private static int _warned;

    public static int BreakerThreshold()
        => int.TryParse(Environment.GetEnvironmentVariable(BreakerThresholdVariable),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0
                ? value
                : 5;
}
