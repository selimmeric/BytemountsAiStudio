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
