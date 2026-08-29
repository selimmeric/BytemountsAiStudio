using System.Collections.Concurrent;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;

namespace BytemountsAiStudio.Providers.Fake;

/// Deterministik sahte LLM.
///
/// İki modda çalışır:
///   - serbest metin: girdinin hash'inden türetilmiş sabit bir cevap
///   - zorunlu araç: <see cref="SetToolResponse"/> ile kaydedilmiş JSON
///
/// İkincisi testin asıl ihtiyacı: gerçek ajanlar şemaya uygun JSON bekliyor.
/// Sahte modelden "akıllı" cevap beklemek yerine, testin ne göreceğini testin
/// kendisi söyler — böylece boru hattı testi modelin kaprisine bağlı olmaz.
public sealed class FakeLlmProvider : ILlmProvider
{
    private readonly ConcurrentDictionary<string, string> _toolResponses = new(StringComparer.Ordinal);

    public string Key => "fake-llm";

    public LlmCapabilities Capabilities { get; } = new()
    {
        SupportsToolUse = true,
        SupportsVision = false,
        ContextWindowTokens = 128_000,
        SupportsEmbeddings = true,
    };

    /// Kaç çağrı yapıldığı. Idempotency dekoratörünün gerçekten çağrıyı
    /// engellediğini doğrulamak için sayaç şart.
    public int CompletionCount => _completionCount;

    private int _completionCount;

    public int EmbeddingCount => _embeddingCount;

    private int _embeddingCount;

    /// Kayitli cevap yoksa devreye giren uretici.
    ///
    /// Bunun olmasi, handler'larin sahte saglayiciyi ONCEDEN doldurmak
    /// zorunda kalmamasini sagliyor: handler yalnizca ILlmProvider goruyor,
    /// sahte ile gercek arasindaki fark kayit noktasinda kaliyor.
    public Func<ToolSchema, IReadOnlyList<ChatMessage>, string?>? ToolResponder { get; set; }

    /// Belirli bir araç çağrıldığında dönecek JSON'u kaydeder.
    public FakeLlmProvider SetToolResponse(string toolName, string json)
    {
        _toolResponses[toolName] = json;
        return this;
    }

    public Task<Result<ProviderResponse<LlmResponse>>> CompleteAsync(
        LlmRequest request,
        ProviderContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _completionCount);

        var prompt = string.Join('\n', request.Messages.Select(m => m.Content));
        var hash = Determinism.Hash(prompt, request.Tier.ToString(), request.ForcedTool?.Name);

        if (request.ForcedTool is { } tool)
        {
            var json = _toolResponses.GetValueOrDefault(tool.Name)
                       ?? ToolResponder?.Invoke(tool, request.Messages);

            if (json is null)
            {
                // Sessizce boş JSON dönmek, testin yanlış yerde patlamasına yol açar.
                return Task.FromResult(Result.Failure<ProviderResponse<LlmResponse>>(
                    Error.Permanent(
                        "fake.llm.no_tool_response",
                        $"'{tool.Name}' aracı için cevap kaydedilmemiş. " +
                        "SetToolResponse ile testin beklediği JSON'u verin.")));
            }

            return Task.FromResult(Result.Success(new ProviderResponse<LlmResponse>(
                new LlmResponse
                {
                    ToolArguments = json,
                    ModelId = ModelIdFor(request.Tier),
                },
                UsageUnits.Tokens(EstimateTokens(prompt), EstimateTokens(json)))));
        }

        // SERBEST METİN CEVAPLAYICI (P6-04).
        //
        // Varsayılan tek satırlık yer tutucu, biçim denetimi olan
        // node'larda (makale: başlık, uzunluk, atıf) haklı olarak
        // düşüyor. Sahte hattın işi denetimi atlatmak değil, GEÇERLİ
        // bir çıktı üretmek — denetimi atlatan bir sahte, denetimin
        // çalışıp çalışmadığını da gizlerdi.
        var text = TextResponder?.Invoke(request)
            ?? Determinism.Format(
                $"[fake:{request.Tier}] {Determinism.Token(hash, 8)} — {prompt.Length} karakterlik istem alındı.");

        return Task.FromResult(Result.Success(new ProviderResponse<LlmResponse>(
            new LlmResponse { Text = text, ModelId = ModelIdFor(request.Tier) },
            UsageUnits.Tokens(EstimateTokens(prompt), EstimateTokens(text)))));
    }

    /// Serbest metin isteklerine cevap veren isteğe bağlı işlev.
    ///
    /// `ToolResponder`'ın metin karşılığı: zorunlu araç kullanmayan
    /// node'lar (makale) için sahte ama BİÇİMİ GEÇERLİ çıktı üretmeye
    /// yarıyor.
    public Func<LlmRequest, string?>? TextResponder { get; init; }

    public Task<Result<ProviderResponse<IReadOnlyList<float>>>> EmbedAsync(
        string text,
        ProviderContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _embeddingCount);

        // Kelime kümesinden türeyen vektör: benzer metinler benzer vektör verir.
        // Tekillik testinin anlamlı olması için bu şart — rastgele vektörle
        // "En Tehlikeli 10 Yer" ile "En Tehlikeli 10 Bölge" asla yakın çıkmazdı.
        const int dimensions = 64;
        var vector = new float[dimensions];

        foreach (var word in (text ?? string.Empty).Split(
                     [' ', '\t', '\n', ',', '.', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            var slot = (int)(Determinism.Hash(word.ToLowerInvariant()) % dimensions);
            vector[slot] += 1f;
        }

        Normalize(vector);

        return Task.FromResult(Result.Success(
            new ProviderResponse<IReadOnlyList<float>>(vector, UsageUnits.Tokens(EstimateTokens(text), 0))));
    }

    private static void Normalize(float[] vector)
    {
        var length = MathF.Sqrt(vector.Sum(v => v * v));
        if (length <= 0f)
        {
            return;
        }

        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] /= length;
        }
    }

    private static string ModelIdFor(ModelTier tier) => $"fake-{tier.ToString().ToLowerInvariant()}";

    /// Kaba token tahmini. Gerçek tokenizer değil; maliyet defterinin
    /// sıfırdan farklı bir sayı görmesi yeterli.
    private static int EstimateTokens(string? text) => Math.Max(1, (text?.Length ?? 0) / 4);
}
