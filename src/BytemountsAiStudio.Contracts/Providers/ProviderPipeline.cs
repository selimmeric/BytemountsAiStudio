using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;

namespace BytemountsAiStudio.Contracts.Providers;

/// Bir sağlayıcı çağrısının tanımı — hangi sağlayıcı, hangi işlem, ne yapıyor.
public sealed record ProviderInvocation<T>
{
    public required string ProviderKey { get; init; }

    /// "complete", "synthesize", "search"… `provider_calls` kaydına gider.
    public required string Operation { get; init; }

    public required ProviderContext Context { get; init; }

    public required Func<CancellationToken, Task<Result<ProviderResponse<T>>>> Execute { get; init; }

    /// Çağrı yapılmadan önceki maliyet tahmini (USD). Bütçe kapısı buna bakar.
    /// Bilinmiyorsa 0 — tahmin edememek, çağrıyı engellemek için sebep değil.
    public decimal EstimatedCost { get; init; }

    /// Sonuç önbelleğe yazılabilir mi.
    ///
    /// VARSAYILAN EVET, çünkü idempotency ADR-010'un tek mekanizması ve
    /// istisna olması gereken şey ONU KAPATMAK.
    ///
    /// İSTİSNA HAM BAYT TAŞIYAN ÇAĞRILAR: `TtsResponse.Audio` ve
    /// `GeneratedImage.Data`. Üç dakikalık bir ses ~5 MB ve JSON'da
    /// base64 olarak ~7 MB; cümle başına bir kayıtla önbellek dakikalar
    /// içinde gigabaytlara çıkar. Idempotency'nin amacı ikinci kez
    /// ÖDEME yapmamak, üretilen medyayı saklamak değil — medyanın yeri
    /// depo (`assets`), önbellek değil.
    public bool Cacheable { get; init; } = true;
}

/// Sağlayıcı çağrısının çevresindeki kesişen kaygılardan biri.
///
/// Mimari §9.2: idempotency, bütçe, rate limit, devre kesici, retry, ölçüm ve
/// telemetri adaptörlerin İÇİNDE değil, burada. Her adaptör yazarının bu yedi
/// şeyi doğru yapmasını beklemek, er geç birinin unutması demekti.
public interface IProviderMiddleware
{
    /// Zincirdeki sıra. Küçük olan dışta — yani önce çalışır.
    int Order { get; }

    Task<Result<ProviderResponse<T>>> InvokeAsync<T>(
        ProviderInvocation<T> invocation,
        Func<CancellationToken, Task<Result<ProviderResponse<T>>>> next,
        CancellationToken cancellationToken);
}

/// Kesişen kaygıları sabit bir sırayla uygulayan zincir.
///
/// SIRA DAVRANIŞIN KENDİSİ, tercih meselesi değil:
///   1. Idempotency  — önbellekte varsa hiçbir şey çalışmaz, para harcanmaz
///   2. Budget       — bütçe yoksa rate limit beklemenin anlamı yok
///   3. RateLimit    — devre kesiciyi denemeden önce sıraya gir
///   4. CircuitBreak — sağlayıcı zaten ölüyse retry'a hiç girme
///   5. Retry        — buradan içerisi tekrarlanır
///   6. Metering     — HER denemenin maliyeti sayılır, sonuncusunun değil
///   7. Telemetry    — gerçek çağrıya en yakın nokta, süre doğru ölçülsün
///
/// Metering'in retry'ın İÇİNDE olması kritik: dışında olsaydı üç kez denenip
/// başarısız olan bir çağrının maliyeti bir kez sayılırdı ve maliyet defteri
/// gerçeği söylemezdi.
public sealed class ProviderPipeline(IEnumerable<IProviderMiddleware> middlewares)
{
    private readonly List<IProviderMiddleware> _ordered =
        middlewares.OrderBy(m => m.Order).ToList();

    public IReadOnlyList<string> Order => _ordered.Select(m => m.GetType().Name).ToList();

    public Task<Result<ProviderResponse<T>>> InvokeAsync<T>(
        ProviderInvocation<T> invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        Func<CancellationToken, Task<Result<ProviderResponse<T>>>> next = invocation.Execute;

        // Zinciri sondan başa kuruyoruz ki ilk middleware en dışta kalsın.
        for (var i = _ordered.Count - 1; i >= 0; i--)
        {
            var middleware = _ordered[i];
            var inner = next;
            next = ct => middleware.InvokeAsync(invocation, inner, ct);
        }

        return next(cancellationToken);
    }
}

/// Zincirdeki standart sıralar. Sayılar aralıklı: araya yeni bir kaygı
/// eklemek mevcutları yeniden numaralandırmayı gerektirmesin.
public static class MiddlewareOrder
{
    public const int Idempotency = 100;
    public const int Budget = 200;
    public const int RateLimit = 300;
    public const int CircuitBreaker = 400;
    public const int Retry = 500;
    public const int Metering = 600;
    public const int Telemetry = 700;
}

/// Sağlayıcı çağrılarının sonuçlarını saklayan önbellek.
///
/// ADR-010'un çalışabilmesi için gerekli: aynı idempotency anahtarıyla
/// yapılan ikinci çağrı API'ye gitmemeli.
public interface IProviderResultCache
{
    Task<string?> TryGetAsync(string idempotencyKey, string operation, CancellationToken cancellationToken);

    Task SetAsync(string idempotencyKey, string operation, string payload, CancellationToken cancellationToken);
}

/// Maliyet defterine yazan taraf.
public interface ICostLedger
{
    Task RecordAsync(ProviderCallRecord record, CancellationToken cancellationToken);

    /// Verilen kapsamda bugüne kadar harcanan tutar.
    Task<decimal> SpentTodayAsync(Guid? channelId, CancellationToken cancellationToken);
}

public sealed record ProviderCallRecord
{
    public required string ProviderKey { get; init; }

    public required string Operation { get; init; }

    public required UsageUnits Units { get; init; }

    public required decimal Cost { get; init; }

    public required int LatencyMs { get; init; }

    public required bool Succeeded { get; init; }

    public Guid? RunId { get; init; }

    public string? NodeId { get; init; }
}

/// Birim sayısını paraya çeviren fiyat listesi.
///
/// Maliyet sağlayıcıdan gelmiyor, BURADA hesaplanıyor: birim sayısı
/// değişmez, fiyat zamanla değişir. İkisini ayırmak, geçmiş kayıtların
/// yeniden fiyatlandırılabilmesini sağlıyor.
public interface IPriceList
{
    decimal Price(string providerKey, string operation, UsageUnits units);
}

/// Bütçe kapısı: harcamadan önce izin ister.
public interface IBudgetGate
{
    Task<Result> AuthorizeAsync(
        Guid? channelId, decimal estimatedCost, CancellationToken cancellationToken);
}

/// Sağlayıcı hesabı başına istek hızı sınırı.
public interface IRateLimiter
{
    /// İzin alınamazsa <see cref="ErrorKind.Resource"/> döner — hata değil,
    /// erteleme. İş kuyruğu bunu görünce işi ileri tarihe atar.
    Task<Result> AcquireAsync(string providerKey, int permits, CancellationToken cancellationToken);
}

/// Sağlıksız sağlayıcıya gitmeyi erken keser.
///
/// ARAYÜZ ASENKRON ve bu, dağıtık uygulamanın (P4-03) zorunlu kıldığı
/// bir değişiklik. Süreç içi uygulama yazılırken senkron olması
/// doğaldı: bir sözlüğe bakmak zaman almıyor.
///
/// Redis'te durum ağın ötesinde. Senkron bir arayüzü orada
/// karşılamanın tek yolu `.Result` beklemek olurdu — worker iş
/// parçacığını bloke eden ve klasik kilitlenme üreten desen. Bir
/// worker'ın bütün render döngüsünü bir Redis çağrısı için durdurmak,
/// devre kesicinin engellemeye çalıştığı israftan daha pahalı.
public interface ICircuitBreaker
{
    Task<Result> CheckAsync(string providerKey, CancellationToken cancellationToken);

    Task RecordSuccessAsync(string providerKey, CancellationToken cancellationToken);

    Task RecordFailureAsync(string providerKey, CancellationToken cancellationToken);
}
