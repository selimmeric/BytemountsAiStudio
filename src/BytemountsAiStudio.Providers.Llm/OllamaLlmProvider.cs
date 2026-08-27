using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;

namespace BytemountsAiStudio.Providers.Llm;

public sealed record OllamaOptions
{
    public Uri BaseAddress { get; init; } = new("http://localhost:11434");

    /// Katman → model eşlemesi. ADR-015: hacimli işler yerel modele düşüyor.
    /// Güçlü katman burada da tanımlı ama gerçek hatta ücretli sağlayıcıya
    /// yönlendirilecek — yerel model senaryo kalitesinde yetmiyor.
    public IReadOnlyDictionary<ModelTier, string> Models { get; init; } =
        new Dictionary<ModelTier, string>
        {
            [ModelTier.Cheap] = "qwen2.5-coder:7b",
            [ModelTier.Standard] = "qwen2.5-coder:7b",
            [ModelTier.Strong] = "qwen2.5-coder:7b",
        };

    public string EmbeddingModel { get; init; } = "nomic-embed-text";

    /// Yerel model ilk çağrıda belleğe yükleniyor; bu birkaç dakika sürebilir.
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
}

/// Yerel LLM sağlayıcısı (ADR-015).
///
/// Bu sağlayıcı sistemin maliyet tabanını belirliyor: konu skorlama, iddia
/// çıkarma, sınıflandırma ve normalizasyon gibi HACİMLİ işler buraya düşünce
/// video başına maliyet neredeyse tamamen TTS'e iniyor.
///
/// Zorunlu araç çağrısı, Ollama'nın `format` alanıyla yapılıyor: modele bir
/// JSON şeması veriliyor ve çıktının ona uyması sağlanıyor. Araç çağrısı
/// (`tools`) yerine bunun seçilmesi bilinçli — araç desteği modele göre
/// değişiyor, `format` ise Ollama seviyesinde zorlanıyor ve her modelde
/// aynı şekilde çalışıyor (§7.2'nin yerel karşılığı).
public sealed class OllamaLlmProvider(HttpClient http, OllamaOptions? options = null) : ILlmProvider
{
    private readonly OllamaOptions _options = options ?? new OllamaOptions();

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Key => "ollama";

    public LlmCapabilities Capabilities { get; } = new()
    {
        SupportsToolUse = true,
        SupportsVision = false,
        ContextWindowTokens = 32_768,
        SupportsEmbeddings = true,
    };

    /// Sağlayıcının erişilebilir olup olmadığını söyler.
    /// Yönlendirme politikası bunu kullanarak sağlıksız sağlayıcıyı atlayabilir.
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http
                .GetAsync(new Uri(_options.BaseAddress, "/api/tags"), cancellationToken)
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    public async Task<Result<ProviderResponse<LlmResponse>>> CompleteAsync(
        LlmRequest request, ProviderContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var model = _options.Models.TryGetValue(request.Tier, out var configured)
            ? configured
            : _options.Models[ModelTier.Cheap];

        var payload = new OllamaChatRequest
        {
            Model = model,
            Stream = false,
            Messages = request.Messages
                .Select(m => new OllamaMessage(RoleOf(m.Role), m.Content))
                .ToList(),
            Options = new OllamaGenerationOptions
            {
                Temperature = request.Temperature,
                NumPredict = request.MaxOutputTokens,
                Seed = request.Seed,
            },
            // Zorunlu araç varsa çıktı şemaya kilitleniyor.
            Format = request.ForcedTool is { } tool
                ? JsonSerializer.Deserialize<JsonElement>(tool.JsonSchema)
                : null,
        };

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_options.Timeout);

            using var response = await http
                .PostAsJsonAsync(new Uri(_options.BaseAddress, "/api/chat"), payload, Json, cts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

                // 5xx geçici, 4xx kalıcı: yeniden denemenin bir şeyi
                // değiştirip değiştirmeyeceğini durum kodu söylüyor.
                return (int)response.StatusCode >= 500
                    ? Error.Transient("ollama.server_error", $"Ollama {(int)response.StatusCode}: {body}")
                    : Error.Permanent("ollama.request_error", $"Ollama {(int)response.StatusCode}: {body}");
            }

            var parsed = await response.Content
                .ReadFromJsonAsync<OllamaChatResponse>(Json, cts.Token)
                .ConfigureAwait(false);

            if (parsed?.Message is null)
            {
                return Error.Transient("ollama.empty_response", "Ollama boş cevap döndü.");
            }

            var content = parsed.Message.Content ?? string.Empty;

            var llm = new LlmResponse
            {
                ModelId = model,
                Text = request.ForcedTool is null ? content : null,
                ToolArguments = request.ForcedTool is null ? null : content,
                // `length` sebebi model çıktıyı kendi kesti demek; sessizce
                // kısalmış senaryoyu "başarılı" saymamak için taşınıyor.
                Truncated = string.Equals(parsed.DoneReason, "length", StringComparison.Ordinal),
            };

            var usage = UsageUnits.Tokens(parsed.PromptEvalCount, parsed.EvalCount);

            return Result.Success(new ProviderResponse<LlmResponse>(llm, usage));
        }
        catch (HttpRequestException ex)
        {
            return Error.Transient("ollama.unreachable", $"Ollama'ya ulaşılamadı: {ex.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Error.Transient("ollama.timeout",
                $"Ollama {_options.Timeout.TotalSeconds:0} saniyede cevap vermedi.");
        }
    }

    public async Task<Result<ProviderResponse<IReadOnlyList<float>>>> EmbedAsync(
        string text, ProviderContext context, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_options.Timeout);

            using var response = await http.PostAsJsonAsync(
                new Uri(_options.BaseAddress, "/api/embed"),
                new { model = _options.EmbeddingModel, input = text },
                Json,
                cts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

                return (int)response.StatusCode >= 500
                    ? Error.Transient("ollama.server_error", body)
                    : Error.Permanent("ollama.embed_error", body);
            }

            var parsed = await response.Content
                .ReadFromJsonAsync<OllamaEmbedResponse>(Json, cts.Token)
                .ConfigureAwait(false);

            var vector = parsed?.Embeddings?.FirstOrDefault();

            if (vector is null || vector.Count == 0)
            {
                return Error.Transient("ollama.empty_embedding", "Boş gömme vektörü döndü.");
            }

            return Result.Success(new ProviderResponse<IReadOnlyList<float>>(
                vector, UsageUnits.Tokens(parsed!.PromptEvalCount, 0)));
        }
        catch (HttpRequestException ex)
        {
            return Error.Transient("ollama.unreachable", ex.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Error.Transient("ollama.timeout", "Gömme isteği zaman aşımına uğradı.");
        }
    }

    private static string RoleOf(ChatRole role) => role switch
    {
        ChatRole.System => "system",
        ChatRole.Assistant => "assistant",
        _ => "user",
    };

    private sealed record OllamaChatRequest
    {
        public required string Model { get; init; }

        public required List<OllamaMessage> Messages { get; init; }

        public bool Stream { get; init; }

        public JsonElement? Format { get; init; }

        public OllamaGenerationOptions? Options { get; init; }
    }

    private sealed record OllamaMessage(string Role, string Content);

    private sealed record OllamaGenerationOptions
    {
        public double Temperature { get; init; }

        [JsonPropertyName("num_predict")]
        public int? NumPredict { get; init; }

        public int? Seed { get; init; }
    }

    private sealed record OllamaChatResponse
    {
        public OllamaMessage? Message { get; init; }

        [JsonPropertyName("done_reason")]
        public string? DoneReason { get; init; }

        [JsonPropertyName("prompt_eval_count")]
        public int PromptEvalCount { get; init; }

        [JsonPropertyName("eval_count")]
        public int EvalCount { get; init; }
    }

    private sealed record OllamaEmbedResponse
    {
        public List<List<float>>? Embeddings { get; init; }

        [JsonPropertyName("prompt_eval_count")]
        public int PromptEvalCount { get; init; }
    }
}
