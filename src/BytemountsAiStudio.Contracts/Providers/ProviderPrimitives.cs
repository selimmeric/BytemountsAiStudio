using BytemountsAiStudio.Core.Content;

namespace BytemountsAiStudio.Contracts.Providers;

/// Her sağlayıcı çağrısında taşınan bağlam.
///
/// Mimari §9.2: idempotency, bütçe, rate limit ve ölçüm dekoratör zincirinde
/// yapılır — adaptörlerin içinde değil. Bu bağlam o zincirin taşıyıcısıdır.
/// Adaptörler genellikle yalnızca <see cref="Language"/> alanını okur.
public sealed record ProviderContext
{
    /// Aynı anahtarla daha önce başarılı bir çağrı varsa API'ye gidilmez.
    /// ADR-010: retry'ın ikinci kez ödeme yapmasını engelleyen tek mekanizma.
    public required string IdempotencyKey { get; init; }

    /// Run → node → çağrı zincirini loglarda birleştiren kimlik.
    public required string CorrelationId { get; init; }

    /// Bu çağrı için kalan bütçe (USD). Bütçe kapısı buna bakar.
    /// null = sınırsız (yerel sağlayıcılar, fake'ler).
    public decimal? RemainingBudget { get; init; }

    /// İçeriğin dili. Ses seçimi, arama sorgusu dili ve metin normalizasyonu buna bağlı.
    public LanguageTag? Language { get; init; }

    public static ProviderContext ForTest(string key = "test") => new()
    {
        IdempotencyKey = key,
        CorrelationId = key,
    };
}

/// Bir çağrının tükettiği ölçülebilir birimler.
///
/// Maliyet burada hesaplanmaz — burada yalnızca SAYILIR. Fiyatlandırma
/// sağlayıcıya ve zamana göre değişir; birim sayısı değişmez. `provider_calls`
/// tablosuna bu yazılır, maliyet ondan türetilir (mimari §13).
public readonly record struct UsageUnits
{
    public int InputTokens { get; init; }

    public int OutputTokens { get; init; }

    /// TTS için: sentezlenen karakter sayısı.
    public int Characters { get; init; }

    /// Üretilen görsel sayısı.
    public int Images { get; init; }

    /// İşlenen ses/video saniyesi (ASR, render).
    public double Seconds { get; init; }

    /// Yapılan arama isteği sayısı.
    public int Requests { get; init; }

    public static UsageUnits None => default;

    public static UsageUnits Tokens(int input, int output)
        => new() { InputTokens = input, OutputTokens = output };

    public static UsageUnits OfCharacters(int characters) => new() { Characters = characters };

    public static UsageUnits OfRequests(int requests = 1) => new() { Requests = requests };
}

/// Tüm sağlayıcı adaptörlerinin ortak yüzeyi.
public interface IProvider
{
    /// Yönlendirme politikasında ve `provider_calls` kaydında kullanılan anahtar.
    /// Örn. "ollama", "pexels", "elevenlabs".
    string Key { get; }
}

/// Bir sağlayıcı çağrısının sonucu: değer + ne tüketildiği.
///
/// Ölçümü sonuçtan ayırmak yerine birlikte döndürüyoruz; ayrı kanaldan
/// raporlansa hata yolunda kaybolur ve başarısız çağrının maliyeti kayda geçmez.
/// Başarısız çağrı da para harcamış olabilir.
public sealed record ProviderResponse<T>(T Value, UsageUnits Usage)
{
    public static ProviderResponse<T> Free(T value) => new(value, UsageUnits.None);
}
