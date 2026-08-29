using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;

namespace BytemountsAiStudio.Providers.Llm;

public sealed record GeminiOptions
{
    /// Varsayılan adres — `config/providers.json` ile AYNI olmak
    /// zorunda; `ProviderEndpointTests` ikisini karşılaştırıyor.
    public static Uri DefaultEndpoint { get; } = new("https://generativelanguage.googleapis.com/v1beta/");

    public const string EndpointVariable = "BMAI_GEMINI_URL";

    /// Kodda sabit DEĞİL, VARSAYILAN: bölgesel bir uç nokta ya da vekil
    /// sunucu kullanmak yeniden derleme gerektirmesin.
    public Uri BaseAddress { get; init; } =
        Endpoints.Resolve(EndpointVariable, "https://generativelanguage.googleapis.com/v1beta/");

    public string KeyEnvironmentVariable { get; init; } = "GEMINI_API_KEY";

    public IReadOnlyDictionary<ModelTier, string> Models { get; init; } =
        new Dictionary<ModelTier, string>
        {
            [ModelTier.Cheap] = "gemini-2.0-flash",
            [ModelTier.Standard] = "gemini-2.0-flash",
            [ModelTier.Strong] = "gemini-2.5-pro",
        };

    /// Gemini gömme modeli boyutu ayarlanabiliyor; 768 zorunlu (ADR-003).
    public string EmbeddingModel { get; init; } = "text-embedding-004";

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);
}

/// Google Gemini (P1-02b).
///
/// AYRI BİR SINIF, `OpenAiCompatibleLlmProvider`'a sığdırılmadı.
/// Sebep yüzeysel değil: kablonun her parçası farklı.
///   - Anahtar `Authorization: Bearer` değil, kendi başlığında
///     (`x-goog-api-key`)
///   - Mesajlar `messages` değil `contents`, roller `assistant` değil
///     `model`
///   - Sistem istemi ayrı bir alan (`system_instruction`), mesaj değil
///   - Zorlanmış araç `tool_choice` değil `tool_config.function_calling_config`
///
/// Bunları tek sınıfta bayraklarla yönetmek, iki sağlayıcının da
/// okunmasını zorlaştıran bir if ormanı üretirdi.
public sealed class GeminiLlmProvider(
    HttpClient http, GeminiOptions? options = null, ICredentialSource? credentials = null) : ILlmProvider
{
    private readonly GeminiOptions _options = options ?? new GeminiOptions();

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Key => "gemini";

    public LlmCapabilities Capabilities { get; } = new()
    {
        SupportsToolUse = true,
        SupportsVision = true,
        ContextWindowTokens = 1_000_000,
        SupportsEmbeddings = true,
    };

    public async Task<Result<ProviderResponse<LlmResponse>>> CompleteAsync(
        LlmRequest request, ProviderContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var apiKey = ResolveKey();

        if (apiKey.IsFailure)
        {
            return Result.Failure<ProviderResponse<LlmResponse>>(apiKey.Error);
        }

        var model = ModelFor(request.Tier);

        // SİSTEM İSTEMİ AYRI ALANDA.
        //
        // Gemini'de sistem mesajı `contents` içine konulamıyor; oraya
        // konulsaydı kullanıcı mesajı gibi işlenir ve modelin ona
        // uyma zorunluluğu zayıflardı.
        var system = request.Messages.FirstOrDefault(m => m.Role == ChatRole.System)?.Content;

        var contents = request.Messages
            .Where(m => m.Role != ChatRole.System)
            .Select(m => new
            {
                role = m.Role == ChatRole.Assistant ? "model" : "user",
                parts = new[] { new { text = m.Content } },
            })
            .ToArray();

        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["contents"] = contents,
            ["generation_config"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["temperature"] = request.Temperature,
                ["max_output_tokens"] = request.MaxOutputTokens,
            },
        };

        if (system is { Length: > 0 })
        {
            body["system_instruction"] = new { parts = new[] { new { text = system } } };
        }

        if (request.ForcedTool is { } tool)
        {
            body["tools"] = new object[]
            {
                new
                {
                    function_declarations = new object[]
                    {
                        new
                        {
                            name = tool.Name,
                            description = tool.Description,
                            parameters = JsonDocument.Parse(tool.JsonSchema).RootElement,
                        },
                    },
                },
            };

            // ANY + izin verilen tek ad: model araç çağırmak ZORUNDA
            // ve başka bir araç uyduramıyor.
            body["tool_config"] = new
            {
                function_calling_config = new
                {
                    mode = "ANY",
                    allowed_function_names = new[] { tool.Name },
                },
            };
        }

        var response = await PostAsync<GenerateResponse>(
            $"models/{model}:generateContent", body, apiKey.Value, cancellationToken).ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result.Failure<ProviderResponse<LlmResponse>>(response.Error);
        }

        var candidate = response.Value.Candidates?.FirstOrDefault();
        var parts = candidate?.Content?.Parts ?? [];

        var call = parts.FirstOrDefault(p => p.FunctionCall is not null)?.FunctionCall;
        var text = string.Concat(parts.Where(p => p.Text is not null).Select(p => p.Text));

        if (request.ForcedTool is not null && call is null)
        {
            return Error.Transient("gemini.no_tool_call",
                $"'{request.ForcedTool.Name}' aracı zorunlu tutuldu ama model metin döndürdü.");
        }

        return Result.Success(new ProviderResponse<LlmResponse>(
            new LlmResponse
            {
                Text = request.ForcedTool is null ? text : null,
                // Gemini argümanları ZATEN nesne olarak veriyor;
                // çağıran taraf ham JSON beklediği için geri
                // seri hâle getiriliyor.
                ToolArguments = call?.Args is { } args ? args.GetRawText() : null,
                ModelId = model,
                Truncated = string.Equals(candidate?.FinishReason, "MAX_TOKENS", StringComparison.Ordinal),
            },
            new UsageUnits
            {
                InputTokens = response.Value.UsageMetadata?.PromptTokenCount ?? 0,
                OutputTokens = response.Value.UsageMetadata?.CandidatesTokenCount ?? 0,
            }));
    }

    public async Task<Result<ProviderResponse<IReadOnlyList<float>>>> EmbedAsync(
        string text, ProviderContext context, CancellationToken cancellationToken)
    {
        var apiKey = ResolveKey();

        if (apiKey.IsFailure)
        {
            return Result.Failure<ProviderResponse<IReadOnlyList<float>>>(apiKey.Error);
        }

        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = $"models/{_options.EmbeddingModel}",
            ["content"] = new { parts = new[] { new { text } } },
            // 768 ZORUNLU: şema öyle (ADR-003).
            ["output_dimensionality"] = 768,
        };

        var response = await PostAsync<EmbedResponse>(
            $"models/{_options.EmbeddingModel}:embedContent", body, apiKey.Value, cancellationToken)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result.Failure<ProviderResponse<IReadOnlyList<float>>>(response.Error);
        }

        var vector = response.Value.Embedding?.Values;

        if (vector is null || vector.Count == 0)
        {
            return Error.Transient("gemini.empty_embedding", "Boş gömme vektörü döndü.");
        }

        return Result.Success(new ProviderResponse<IReadOnlyList<float>>(vector, new UsageUnits()));
    }

    internal string ModelFor(ModelTier tier)
    {
        for (var current = tier; ; current--)
        {
            if (_options.Models.TryGetValue(current, out var model) && !string.IsNullOrWhiteSpace(model))
            {
                return model;
            }

            if (current == ModelTier.Cheap)
            {
                return _options.Models.Values.FirstOrDefault() ?? "gemini-2.0-flash";
            }
        }
    }

    private Result<string> ResolveKey()
    {
        var value = credentials is not null
            ? credentials.Get(_options.KeyEnvironmentVariable)
            : Environment.GetEnvironmentVariable(_options.KeyEnvironmentVariable);

        return string.IsNullOrWhiteSpace(value)
            ? Error.Permanent("gemini.no_key",
                $"Gemini için anahtar yok ({_options.KeyEnvironmentVariable} tanımlı değil).")
            : Result.Success(value);
    }

    private async Task<Result<T>> PostAsync<T>(
        string path, object body, string apiKey, CancellationToken cancellationToken)
    {
        using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(_options.Timeout);

        // ANAHTAR SORGU DİZİSİNDE DEĞİL BAŞLIKTA.
        //
        // Google her ikisini de kabul ediyor; başlık seçildi çünkü
        // sorgu dizisi sunucu erişim kayıtlarına ve vekil loglarına
        // düz metin olarak yazılıyor.
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_options.BaseAddress, path))
        {
            Content = JsonContent.Create(body, options: Json),
        };

        message.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);

        try
        {
            using var response = await http.SendAsync(message, source.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return await ClassifyAsync<T>(response, source.Token).ConfigureAwait(false);
            }

            var parsed = await response.Content.ReadFromJsonAsync<T>(Json, source.Token).ConfigureAwait(false);

            return parsed is null
                ? Error.Transient("gemini.bad_response", "Yanıt ayrıştırılamadı.")
                : Result.Success(parsed);
        }
        catch (HttpRequestException ex)
        {
            return Error.Transient("gemini.unreachable", ex.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Error.Transient("gemini.timeout", "İstek zaman aşımına uğradı.");
        }
    }

    private static async Task<Result<T>> ClassifyAsync<T>(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string detail;

        try
        {
            var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(raw);

            detail = document.RootElement.TryGetProperty("error", out var error)
                     && error.TryGetProperty("message", out var text)
                     && text.GetString() is { Length: > 0 } value
                ? value
                : raw;
        }
        catch (JsonException)
        {
            detail = response.ReasonPhrase ?? "ayrıştırılamayan gövde";
        }
        catch (HttpRequestException)
        {
            detail = response.ReasonPhrase ?? "gövde okunamadı";
        }

        var status = (int)response.StatusCode;

        return status switch
        {
            // 429 KAYNAK, geçici değil: Gemini'nin ücretsiz katmanında
            // bu genelde GÜNLÜK kota ve dakikalar içinde yeniden
            // denemek yalnızca kotayı tüketmeye devam ediyor.
            429 => Error.Resource("gemini.quota", detail, TimeSpan.FromHours(1)),
            401 or 403 => Error.Permanent("gemini.unauthorized", detail),
            >= 500 => Error.Transient("gemini.server_error", $"HTTP {status}: {detail}"),
            _ => Error.Permanent("gemini.rejected", $"HTTP {status}: {detail}"),
        };
    }

    internal sealed record GenerateResponse(List<Candidate>? Candidates, UsageMetadata? UsageMetadata);

    internal sealed record Candidate(Content? Content, string? FinishReason);

    internal sealed record Content(List<Part>? Parts);

    internal sealed record Part(string? Text, FunctionCall? FunctionCall);

    internal sealed record FunctionCall(string? Name, JsonElement? Args);

    internal sealed record UsageMetadata(int PromptTokenCount, int CandidatesTokenCount);

    internal sealed record EmbedResponse(EmbeddingValues? Embedding);

    internal sealed record EmbeddingValues(List<float>? Values);
}
