using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;

namespace BytemountsAiStudio.Providers.Llm;

/// OpenAI uyumlu bir sağlayıcının yapılandırması (P1-02b).
public sealed record OpenAiCompatibleOptions
{
    public required string Key { get; init; }

    public required Uri BaseAddress { get; init; }

    /// Anahtarın okunacağı ortam değişkeni.
    public required string KeyEnvironmentVariable { get; init; }

    public required IReadOnlyDictionary<ModelTier, string> Models { get; init; }

    public string? EmbeddingModel { get; init; }

    /// Görme modeli (P2-06). `null` ise sağlayıcı görme sunmuyor.
    public string? VisionModel { get; init; }

    /// ***ANAHTAR ZORUNLU MU.***
    ///
    /// Varsayılan EVET ve istisna olması gereken şey `false`: ücretli
    /// bir sağlayıcıya yanlışlıkla anahtarsız istek atmak, 401
    /// döngüsünde kota harcamak demek.
    ///
    /// `false` olduğunda anahtar YİNE OKUNUYOR: varsa gönderiliyor.
    /// Pollinations anahtarsız çalışıyor ama jeton verildiğinde daha
    /// yüksek hız sınırı tanıyor — "anahtarsız" ile "anahtar
    /// kullanılmaz" farklı şeyler.
    public bool KeyRequired { get; init; } = true;

    public int ContextWindowTokens { get; init; } = 128_000;

    /// OpenRouter kendini tanıtan iki başlık istiyor; diğerlerinde boş.
    public IReadOnlyDictionary<string, string> ExtraHeaders { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);

    /// Varsayılan adresler — `config/providers.json` ile AYNI olmak
    /// zorunda; `ProviderEndpointTests` ikisini karşılaştırıyor.
    public const string OpenAiEndpoint = "https://api.openai.com/v1/";

    public const string OpenAiEndpointVariable = "BMAI_OPENAI_URL";

    public const string OpenRouterEndpoint = "https://openrouter.ai/api/v1/";

    public const string OpenRouterEndpointVariable = "BMAI_OPENROUTER_URL";

    /// OpenAI. Katmanlar §7.4'e göre: para YALNIZCA senaryoda harcanıyor.
    public static OpenAiCompatibleOptions OpenAi() => new()
    {
        Key = "openai",
        BaseAddress = Endpoints.Resolve(OpenAiEndpointVariable, OpenAiEndpoint),
        KeyEnvironmentVariable = "OPENAI_API_KEY",
        Models = new Dictionary<ModelTier, string>
        {
            [ModelTier.Cheap] = "gpt-4o-mini",
            [ModelTier.Standard] = "gpt-4o-mini",
            [ModelTier.Strong] = "gpt-4o",
        },
        // 1536 DEĞİL 768: şema 768 boyutlu (ADR-003) ve OpenAI'nin
        // `dimensions` parametresi bunu veriyor. 1536 seçilseydi yerel
        // modele geri dönmek imkânsız olurdu ve ADR-015'in altı
        // oyulurdu.
        EmbeddingModel = "text-embedding-3-small",
    };

    public const string PollinationsEndpoint = "https://text.pollinations.ai/openai/";

    public const string PollinationsEndpointVariable = "BMAI_POLLINATIONS_TEXT_URL";

    /// ***POLLINATIONS: ANAHTARSIZ, ÜCRETSİZ, GPU İSTEMEYEN METİN VE
    /// GÖRME MODELİ (ADR-015).***
    ///
    /// Katalogda `pollinations-text` satırı VARDI ve karşılığında
    /// HİÇBİR KOD YOKTU: anahtarsız hattın tek LLM'i Ollama'ydı, yani
    /// yerel bir GPU. GPU'su olmayan (ya da GPU'sunu kullanamayan) bir
    /// makinede anahtarsız hat senaryo üretemiyordu — "anahtarsız
    /// çalışır" iddiası pratikte "yerel modeli olan makinede çalışır"
    /// demekti.
    ///
    /// OpenAI uyumlu kabloyu konuşuyor, o yüzden ayrı bir sınıf değil
    /// ayrı bir YAPILANDIRMA: aynı ayrıştırma ve hata sınıflandırma
    /// mantığı geçerli.
    ///
    /// SINIRI DÜŞÜK (dakikada 10, katalogda yazılı) ve bu bir kusur
    /// değil bir GERÇEK: ücretsiz servis. Hız sınırı zincirde
    /// uygulanıyor (P0-17), yani sınır aşıldığında iş DÜŞMÜYOR,
    /// erteleniyor.
    ///
    /// ***ÖLÇÜLDÜ: 29 Ağu 2026'da BU AĞDAN 402 DÖNÜYOR.***
    ///
    /// Gerçek bir çağrı yapıldı ve cevap şu oldu: `402 Payment
    /// Required — API key budget too low`. Servis bu ağı anahtarlı
    /// sayıyor ve anahtarın bakiyesi sıfır; anonim uç de aynı cevabı
    /// veriyor. Yani ADAPTÖR DOĞRU, SERVİS BU MAKİNEDEN ÜCRETSİZ
    /// DEĞİL.
    ///
    /// Kod yine de duruyor ve zincirde İLK sırada, üç sebeple:
    ///   - 402 `Kaynak` sınıfına düşüyor ve `ProviderRouter` kaynak
    ///     hatasında YEDEĞE GEÇİYOR, yani hat durmuyor.
    ///   - Devre kesici (P0-17, artık gerçekten takılı) birkaç
    ///     denemeden sonra bu sağlayıcıyı atlıyor: kayıp birkaç
    ///     istekle sınırlı.
    ///   - Aynı yapılandırma OpenAI uyumlu HERHANGİ bir uca işaret
    ///     edebiliyor (`BMAI_POLLINATIONS_TEXT_URL`): yerel bir
    ///     llama.cpp sunucusu ya da bakiyesi olan bir Pollinations
    ///     jetonu tek satırlık ayar.
    ///
    /// Pollinations'ın GÖRSEL ucu (`image.pollinations.ai`) aynı
    /// ağdan 200 dönüyor ve çalışıyor — sorun metin/görme ucunda.
    public static OpenAiCompatibleOptions Pollinations() => new()
    {
        Key = "pollinations-text",
        BaseAddress = Endpoints.Resolve(PollinationsEndpointVariable, PollinationsEndpoint),

        // Anahtar İSTEĞE BAĞLI: tanımlıysa gönderiliyor ve daha yüksek
        // sınır tanınıyor.
        KeyEnvironmentVariable = "POLLINATIONS_TOKEN",
        KeyRequired = false,

        Models = new Dictionary<ModelTier, string>
        {
            [ModelTier.Cheap] = "openai-fast",
            [ModelTier.Standard] = "openai",
            [ModelTier.Strong] = "openai-large",
        },

        // GÖMME SUNMUYOR: `null` bırakmak, çağrıldığında açık bir hata
        // vermesini sağlıyor. Boş bir model adıyla göndermek
        // anlaşılmaz bir 404 üretirdi.
        EmbeddingModel = null,

        // GÖRME MODELİ (P2-06): semantik QC'nin "bu görsel bu cümleyi
        // destekliyor mu" sorusunu soran yer. Anahtarsız bir görme
        // modeli, 6 GB'lık yerel bir modeli karta yüklemeden semantik
        // kontrolün ÖLÇÜLEBİLMESİ demek.
        VisionModel = "openai",
    };

    /// OpenRouter: tek anahtarla onlarca modele erişim. Ücretsiz
    /// katmanı da var, o yüzden anahtar geldiğinde ilk denenecek yer.
    public static OpenAiCompatibleOptions OpenRouter() => new()
    {
        Key = "openrouter",
        BaseAddress = Endpoints.Resolve(OpenRouterEndpointVariable, OpenRouterEndpoint),
        KeyEnvironmentVariable = "OPENROUTER_API_KEY",
        Models = new Dictionary<ModelTier, string>
        {
            [ModelTier.Cheap] = "meta-llama/llama-3.3-70b-instruct",
            [ModelTier.Standard] = "meta-llama/llama-3.3-70b-instruct",
            [ModelTier.Strong] = "anthropic/claude-sonnet-4",
        },
        // OpenRouter embedding SUNMUYOR: null bırakmak, çağrıldığında
        // açık bir hata vermesini sağlıyor. Boş bir model adıyla
        // göndermek anlaşılmaz bir 404 üretirdi.
        EmbeddingModel = null,
        ExtraHeaders = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HTTP-Referer"] = "https://github.com/bytemounts",
            ["X-Title"] = "BytemountsAiStudio",
        },
    };
}

/// OpenAI uyumlu sağlayıcılar: OpenAI, OpenRouter ve aynı kabloyu
/// konuşan diğerleri (P1-02b).
///
/// TEK SINIF, çünkü aralarındaki fark yalnızca adres, anahtar ve model
/// adı. Üç ayrı sınıf yazmak, aynı ayrıştırma ve hata sınıflandırma
/// mantığının üç kopyası demekti — ve o kopyalar er geç ayrışırdı.
///
/// Gemini AYRI (`GeminiLlmProvider`): kablosu gerçekten farklı,
/// zorlanmış araç çağrısı bile başka bir alanda.
public sealed class OpenAiCompatibleLlmProvider(
    HttpClient http, OpenAiCompatibleOptions options, ICredentialSource? credentials = null) : ILlmProvider
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Key => options.Key;

    public LlmCapabilities Capabilities { get; } = new()
    {
        SupportsToolUse = true,
        SupportsVision = true,
        ContextWindowTokens = options.ContextWindowTokens,
        SupportsEmbeddings = options.EmbeddingModel is not null,
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

        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = model,
            ["messages"] = request.Messages.Select(m => new
            {
                role = m.Role switch
                {
                    ChatRole.System => "system",
                    ChatRole.Assistant => "assistant",
                    _ => "user",
                },
                content = m.Content,
            }).ToArray(),
            ["temperature"] = request.Temperature,
            ["max_tokens"] = request.MaxOutputTokens,
            ["seed"] = request.Seed,
        };

        if (request.ForcedTool is { } tool)
        {
            // ZORLANMIŞ araç: `tool_choice` ile modele seçenek
            // bırakılmıyor. Serbest bırakmak, cevabın bazen metin bazen
            // araç çağrısı gelmesi demekti ve çağıran tarafın ikisini de
            // ayrıştırması gerekirdi (§7.2).
            body["tools"] = new object[]
            {
                new
                {
                    type = "function",
                    function = new
                    {
                        name = tool.Name,
                        description = tool.Description,
                        parameters = JsonDocument.Parse(tool.JsonSchema).RootElement,
                    },
                },
            };

            body["tool_choice"] = new
            {
                type = "function",
                function = new { name = tool.Name },
            };
        }

        var response = await PostAsync<ChatResponse>("chat/completions", body, apiKey.Value, cancellationToken)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result.Failure<ProviderResponse<LlmResponse>>(response.Error);
        }

        var choice = response.Value.Choices?.FirstOrDefault();

        if (choice?.Message is not { } message)
        {
            return Error.Transient($"{options.Key}.empty", "Sağlayıcı hiç seçenek döndürmedi.");
        }

        var arguments = message.ToolCalls?.FirstOrDefault()?.Function?.Arguments;

        // ARAÇ ZORLANDIYSA ARAÇ ARGÜMANI ZORUNLU.
        //
        // Metin dönen bir cevabı "başarılı" saymak, çağıran tarafta
        // null bir şemaya ve orada anlaşılmaz bir hataya dönüşürdü.
        // Geçici sayılıyor çünkü aynı istek ikinci denemede araç
        // çağırabiliyor.
        if (request.ForcedTool is not null && string.IsNullOrWhiteSpace(arguments))
        {
            return Error.Transient($"{options.Key}.no_tool_call",
                $"'{request.ForcedTool.Name}' aracı zorunlu tutuldu ama model metin döndürdü.");
        }

        return Result.Success(new ProviderResponse<LlmResponse>(
            new LlmResponse
            {
                Text = request.ForcedTool is null ? message.Content : null,
                ToolArguments = arguments,
                ModelId = response.Value.Model ?? model,
                // Sessizce kısalmış bir senaryoyu "başarılı" saymamak
                // için gerekli.
                Truncated = string.Equals(choice.FinishReason, "length", StringComparison.Ordinal),
            },
            new UsageUnits
            {
                InputTokens = response.Value.Usage?.PromptTokens ?? 0,
                OutputTokens = response.Value.Usage?.CompletionTokens ?? 0,
            }));
    }

    public async Task<Result<ProviderResponse<IReadOnlyList<float>>>> EmbedAsync(
        string text, ProviderContext context, CancellationToken cancellationToken)
    {
        if (options.EmbeddingModel is not { } embeddingModel)
        {
            // KALICI: yeniden denemek bu sağlayıcıya embedding
            // yeteneği kazandırmıyor.
            return Error.Permanent($"{options.Key}.no_embeddings",
                $"{options.Key} embedding sunmuyor; yönlendirme başka bir sağlayıcıya gitmeli.");
        }

        var apiKey = ResolveKey();

        if (apiKey.IsFailure)
        {
            return Result.Failure<ProviderResponse<IReadOnlyList<float>>>(apiKey.Error);
        }

        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = embeddingModel,
            ["input"] = text,
            // 768 ZORUNLU: şema öyle (ADR-003). Varsayılan 1536
            // gönderilseydi vektör kolona hiç yazılamazdı ve hata
            // veritabanı katmanında, sebebi görünmeden çıkardı.
            ["dimensions"] = 768,
        };

        var response = await PostAsync<EmbeddingResponse>("embeddings", body, apiKey.Value, cancellationToken)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result.Failure<ProviderResponse<IReadOnlyList<float>>>(response.Error);
        }

        var vector = response.Value.Data?.FirstOrDefault()?.Embedding;

        if (vector is null || vector.Count == 0)
        {
            return Error.Transient($"{options.Key}.empty_embedding", "Boş gömme vektörü döndü.");
        }

        return Result.Success(new ProviderResponse<IReadOnlyList<float>>(
            vector,
            new UsageUnits { InputTokens = response.Value.Usage?.PromptTokens ?? 0 }));
    }

    /// Katman tanımlı değilse BİR ALT katmana düşülüyor.
    ///
    /// Tanımsız bir katman yüzünden çağrının tamamen düşmesi saçma
    /// olurdu: `TieredLlmProvider` ile aynı mantık (P1-03).
    internal string ModelFor(ModelTier tier)
    {
        for (var current = tier; ; current--)
        {
            if (options.Models.TryGetValue(current, out var model) && !string.IsNullOrWhiteSpace(model))
            {
                return model;
            }

            if (current == ModelTier.Cheap)
            {
                return options.Models.Values.FirstOrDefault() ?? "unknown";
            }
        }
    }

    private Result<string?> ResolveKey()
    {
        var value = credentials is not null
            ? credentials.Get(options.KeyEnvironmentVariable)
            : Environment.GetEnvironmentVariable(options.KeyEnvironmentVariable);

        if (string.IsNullOrWhiteSpace(value))
        {
            // ANAHTAR ZORUNLU DEĞİLSE YOKLUĞU HATA DEĞİL: Pollinations
            // anahtarsız çalışıyor. `null` dönmek, `Authorization`
            // başlığının HİÇ eklenmemesi demek — boş bir "Bearer "
            // başlığı bazı sunucularda 401 üretiyor.
            if (!options.KeyRequired)
            {
                return Result.Success<string?>(null);
            }

            // KİMLİK hatası KALICI ama ayrı bir kod taşıyor: katmanlı
            // sağlayıcı bunu görünce yedeğe DÜŞÜYOR (P1-03), çünkü
            // "bu anahtar yok" isteğin değil yapılandırmanın kusuru.
            return Error.Permanent($"{options.Key}.no_key",
                $"{options.Key} için anahtar yok ({options.KeyEnvironmentVariable} tanımlı değil).");
        }

        return Result.Success<string?>(value);
    }

    private async Task<Result<T>> PostAsync<T>(
        string path, object body, string? apiKey, CancellationToken cancellationToken)
    {
        using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(options.Timeout);

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(options.BaseAddress, path))
        {
            Content = JsonContent.Create(body, options: Json),
        };

        if (apiKey is { Length: > 0 })
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        foreach (var (name, value) in options.ExtraHeaders)
        {
            message.Headers.TryAddWithoutValidation(name, value);
        }

        try
        {
            using var response = await http.SendAsync(message, source.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return await ClassifyAsync<T>(response, source.Token).ConfigureAwait(false);
            }

            var parsed = await response.Content.ReadFromJsonAsync<T>(Json, source.Token).ConfigureAwait(false);

            return parsed is null
                ? Error.Transient($"{options.Key}.bad_response", "Yanıt ayrıştırılamadı.")
                : Result.Success(parsed);
        }
        catch (HttpRequestException ex)
        {
            return Error.Transient($"{options.Key}.unreachable", ex.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Error.Transient($"{options.Key}.timeout", "İstek zaman aşımına uğradı.");
        }
    }

    /// HTTP durumunu hata sınıfına çevirir (ADR-011).
    ///
    /// Kuyruğun kararını bu belirliyor:
    ///   - 429 ve 5xx GEÇİCİ: yeniden denemek işe yarayabilir
    ///   - 402 KAYNAK: bakiye bitti. Başarısızlık değil, ERTELEME —
    ///     bakiye yüklenince aynı iş çalışacak. Kalıcı saymak, ödeme
    ///     yapıldıktan sonra bile çalışmayacak bir işe dönüştürürdü.
    ///   - 401/403 KALICI: anahtar geçersiz. Yeniden denemek geçerli
    ///     yapmıyor.
    ///   - 400 KALICI: istek hatalı; ikinci kez göndermek aynı cevabı
    ///     verir ve ikinci kez para harcatır.
    private async Task<Result<T>> ClassifyAsync<T>(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var detail = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
        var status = (int)response.StatusCode;

        return status switch
        {
            429 => Error.Transient($"{options.Key}.rate_limited", detail, RetryAfter(response)),
            402 => Error.Resource($"{options.Key}.insufficient_funds", detail, TimeSpan.FromHours(6)),
            401 or 403 => Error.Permanent($"{options.Key}.unauthorized", detail),
            >= 500 => Error.Transient($"{options.Key}.server_error", $"HTTP {status}: {detail}"),
            _ => Error.Permanent($"{options.Key}.rejected", $"HTTP {status}: {detail}"),
        };
    }

    /// Sunucunun söylediği bekleme süresi, tahmin edilene tercih
    /// ediliyor: kendi geri çekilmemiz ya çok erken denerdi ya da
    /// gereğinden uzun beklerdi.
    private static TimeSpan? RetryAfter(HttpResponseMessage response)
        => response.Headers.RetryAfter?.Delta
           ?? (response.Headers.RetryAfter?.Date is { } date
               ? date - DateTimeOffset.UtcNow
               : null);

    private static async Task<string> ReadErrorAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(body))
            {
                return response.ReasonPhrase ?? "gövde yok";
            }

            using var document = JsonDocument.Parse(body);

            // OpenAI ailesinin hata biçimi: {"error":{"message":"..."}}
            return document.RootElement.TryGetProperty("error", out var error)
                   && error.ValueKind == JsonValueKind.Object
                   && error.TryGetProperty("message", out var text)
                   && text.GetString() is { Length: > 0 } value
                ? value
                : body;
        }
        catch (JsonException)
        {
            return response.ReasonPhrase ?? "ayrıştırılamayan gövde";
        }
        catch (HttpRequestException)
        {
            return response.ReasonPhrase ?? "gövde okunamadı";
        }
    }

    internal sealed record ChatResponse(string? Model, List<Choice>? Choices, Usage? Usage);

    internal sealed record Choice(Message? Message, string? FinishReason);

    internal sealed record Message(string? Content, List<ToolCall>? ToolCalls);

    internal sealed record ToolCall(FunctionCall? Function);

    internal sealed record FunctionCall(string? Name, string? Arguments);

    internal sealed record Usage(int PromptTokens, int CompletionTokens);

    internal sealed record EmbeddingResponse(List<EmbeddingItem>? Data, Usage? Usage);

    internal sealed record EmbeddingItem(List<float>? Embedding);
}
