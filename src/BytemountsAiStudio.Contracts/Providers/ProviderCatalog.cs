using System.Text.Json;
using System.Text.Json.Serialization;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;

namespace BytemountsAiStudio.Contracts.Providers;

/// Sağlayıcı kataloğu — hangi servis hangi rolde, anahtar gerekiyor mu,
/// sınırları ne (`config/providers.json`).
///
/// Neden veri, kod değil: yeni bir servis eklemek ya da ücretliyi ücretsizin
/// önüne almak bir dosya değişikliği olmalı, bir derleme değil. Anahtar
/// geldiğinde yapılacak tek şey `enabled` alanını açıp yönlendirme
/// listesinin başına almak (ADR-015).
///
/// Katalog sağlayıcıyı ÜRETMİYOR, yalnızca TARİF EDİYOR. Nesne üretimi
/// `ProviderFactory`'de kalıyor — çünkü her sağlayıcının kurucusu farklı
/// ve bunu veriyle ifade etmek erken soyutlama olurdu.
public sealed record ProviderCatalog
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = 1;

    public IReadOnlyList<ProviderDescriptor> Providers { get; init; } = [];

    /// Rol → sıralı sağlayıcı listesi. İlk çalışan kullanılır.
    public IReadOnlyDictionary<string, List<string>> Routing { get; init; } =
        new Dictionary<string, List<string>>(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static Result<ProviderCatalog> Load(string path)
    {
        if (!File.Exists(path))
        {
            return Error.Permanent("catalog.missing", $"Sağlayıcı kataloğu yok: {path}");
        }

        try
        {
            var catalog = JsonSerializer.Deserialize<ProviderCatalog>(
                File.ReadAllText(path), Options);

            if (catalog is null)
            {
                return Error.Permanent("catalog.empty", "Katalog okunamadı.");
            }

            var issues = catalog.Validate();

            return issues.Count > 0
                ? Error.Permanent("catalog.invalid", string.Join(" | ", issues))
                : Result.Success(catalog);
        }
        catch (JsonException ex)
        {
            return Error.Permanent("catalog.parse_failed", ex.Message);
        }
    }

    /// Yönlendirme listesinde adı geçen ama tanımlı olmayan bir sağlayıcı,
    /// çalışma zamanında "sağlayıcı bulunamadı" hatası verirdi. Burada
    /// yakalanıyor.
    public IReadOnlyList<string> Validate()
    {
        var issues = new List<string>();
        var keys = Providers.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var (role, list) in Routing)
        {
            if (role.StartsWith('_'))
            {
                continue;
            }

            foreach (var key in list.Where(k => !keys.Contains(k)))
            {
                issues.Add($"'{role}' yönlendirmesi tanımlı olmayan sağlayıcıya işaret ediyor: '{key}'");
            }

            foreach (var key in list)
            {
                var provider = Providers.FirstOrDefault(p => p.Key == key);

                if (provider is { Enabled: false })
                {
                    issues.Add($"'{role}' yönlendirmesindeki '{key}' kapalı (enabled=false).");
                }

                // Anahtar isteyen bir sağlayıcıyı anahtarsız yönlendirmeye
                // koymak, ilk çağrıda beklenmedik bir kimlik hatası demek.
                if (provider is { RequiresKey: true, KeyEnv: { } env }
                    && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(env)))
                {
                    issues.Add($"'{key}' anahtar istiyor ama {env} tanımlı değil.");
                }
            }
        }

        var duplicates = Providers.GroupBy(p => p.Key, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);

        issues.AddRange(duplicates.Select(key => $"Sağlayıcı anahtarı tekrarlanıyor: '{key}'"));

        return issues;
    }

    /// Bir rol için sıradaki sağlayıcılar — kapalı olanlar elenmiş hâlde.
    public IReadOnlyList<ProviderDescriptor> For(string role)
    {
        if (!Routing.TryGetValue(role, out var keys))
        {
            return [];
        }

        return keys
            .Select(k => Providers.FirstOrDefault(p => p.Key == k))
            .Where(p => p is { Enabled: true })
            .Select(p => p!)
            .ToList();
    }

    /// Anahtar gerektirmeyen sağlayıcılar. "Şu an ne ile çalışabiliyorum"
    /// sorusunun cevabı.
    public IReadOnlyList<ProviderDescriptor> KeyFree()
        => Providers.Where(p => !p.RequiresKey).ToList();

    /// Anahtar bekleyenler. Rapor ve kurulum rehberi bundan üretiliyor.
    public IReadOnlyList<ProviderDescriptor> AwaitingKeys()
        => Providers.Where(p => p.RequiresKey && !p.Enabled).ToList();

    /// Katalogdaki hız sınırlarının çalışır karşılığı.
    ///
    /// ***BU METOT UZUN SÜRE YOKTU VE `limits` SESSİZCE ÖLÜ VERİYDİ.***
    ///
    /// Katalogda "bu servis dakikada 10 istek kaldırır" yazmak hiçbir
    /// şey yapmıyordu: istekler doğrudan çıkıyor, sağlayıcı 429
    /// dönünce ancak kuyruk geri çekilmesi devreye giriyordu. Sınır
    /// yazıp güvende sanmak, gerçekte sınırsız istek demekti.
    ///
    /// DÖRT PENCERE DE OKUNUYOR (`per_second`, `per_minute`,
    /// `per_hour`, `per_month`) çünkü katalogda dördü de kullanılıyor:
    /// Wikipedia saniye, Pollinations dakika, Openverse saat, Brave ay
    /// bazında sınırlıyor. Yalnızca dakikayı okumak, üçünü sessizce
    /// atmak olurdu — düzeltilen `endpoint_env` hatasının aynısı.
    ///
    /// AYLIK SINIR DA KOVAYA GİRİYOR ve bu bilinçli bir yaklaşıklık:
    /// token bucket ayı bir pencere olarak taşıyor, yani 2.000 istek
    /// ay başında bir anda harcanabilir. Doğru davranış değil ama
    /// hiç sınır olmamasından iyi ve sınırın nerede yazılı olduğu
    /// tek yerde kalıyor.
    ///
    /// SINIRSIZ SAĞLAYICI LİSTEYE GİRMİYOR: `requests_per_minute:
    /// null` yazan yerel servisler (Ollama, Piper, yan servis) için
    /// kova kurmak, olmayan bir sınırı uygulamak olurdu.
    public IReadOnlyDictionary<string, RateLimitPolicy> RateLimitPolicies()
    {
        var policies = new Dictionary<string, RateLimitPolicy>(StringComparer.Ordinal);

        foreach (var provider in Providers)
        {
            if (provider.Limits is not { } limits)
            {
                continue;
            }

            var policy = Window(limits, "requests_per_second", TimeSpan.FromSeconds(1))
                ?? Window(limits, "requests_per_minute", TimeSpan.FromMinutes(1))
                ?? Window(limits, "requests_per_hour", TimeSpan.FromHours(1))
                ?? Window(limits, "requests_per_month", TimeSpan.FromDays(30));

            if (policy is not null)
            {
                policies[provider.Key] = policy;
            }
        }

        return policies;
    }

    /// Bir sağlayıcının katalogdaki tam sayı sınırı.
    ///
    /// YouTube günlük kota havuzu (`quota_units_per_day`) ve yükleme
    /// başına birim (`quota_units_per_publish`) buradan okunuyor:
    /// Google kota artırımı verdiğinde (10.000 → 1.000.000 mümkün)
    /// değişecek tek şey katalog satırı olmalı, kod değil.
    public int? Limit(string providerKey, string name)
        => Providers.FirstOrDefault(p => p.Key == providerKey)?.Limits is { } limits
            ? Number(limits, name)
            : null;

    private static RateLimitPolicy? Window(
        IReadOnlyDictionary<string, JsonElement> limits, string name, TimeSpan window)
        => Number(limits, name) is { } permits and > 0 ? new RateLimitPolicy(permits, window) : null;

    /// `null` yazan alan SINIRSIZ demek, sıfır değil — `TryGetInt32`
    /// zaten `Null` üzerinde patlıyor, o yüzden tür açıkça sınanıyor.
    private static int? Number(IReadOnlyDictionary<string, JsonElement> limits, string name)
        => limits.TryGetValue(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
                ? number
                : null;
}

public sealed record ProviderDescriptor
{
    public required string Key { get; init; }

    /// "llm", "search", "image.stock", "image.generative", "tts", "asr", "publish".
    public required string Role { get; init; }

    [JsonPropertyName("display_name")]
    public required string DisplayName { get; init; }

    public bool Enabled { get; init; }

    [JsonPropertyName("requires_key")]
    public bool RequiresKey { get; init; }

    /// Anahtarın okunacağı ortam değişkeni. Anahtar dosyada DEĞİL:
    /// katalog depoya giriyor, anahtarlar girmiyor.
    [JsonPropertyName("key_env")]
    public string? KeyEnv { get; init; }

    public string? Cost { get; init; }

    public string? Quality { get; init; }

    /// Servisin adresi. KATALOGDA, KODDA DEĞİL: adres değişince
    /// yeniden derleme gerekmesin.
    public string? Endpoint { get; init; }

    /// Adresi ezen ortam değişkeni.
    ///
    /// BU ALAN DOSYADA VARDI AMA BURADA YOKTU ve `JsonSerializer`
    /// tanımadığı alanı sessizce atıyordu: katalog `BMAI_OLLAMA_URL`
    /// yazıyordu, kod onu hiç okumuyordu. Kataloğu okuyup değişkeni
    /// tanımlayan biri, hiçbir şeyin değişmediğini ancak deneyerek
    /// anlardı.
    public string? EndpointEnv { get; init; }

    public string? Platform { get; init; }

    public string? Setup { get; init; }

    public string? Notes { get; init; }

    public IReadOnlyDictionary<string, string>? Voices { get; init; }

    public IReadOnlyDictionary<string, JsonElement>? Limits { get; init; }

    public bool IsUsableNow
        => Enabled && (!RequiresKey
            || (KeyEnv is { } env && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(env))));
}
