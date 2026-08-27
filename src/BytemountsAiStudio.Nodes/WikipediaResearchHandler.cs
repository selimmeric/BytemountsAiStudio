using System.Text.Json;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Workflow.Engine;

namespace BytemountsAiStudio.Nodes;

/// Wikipedia üzerinden araştırma — anahtarsız gerçek kaynak.
///
/// §2.2/8'in temeli burada kuruluyor: her iddia bir KAYNAĞA bağlı olacak.
/// Bu node kaynakları topluyor; iddia çıkarma ve entailment doğrulaması
/// P1-10'da üstüne gelecek.
///
/// Şu an yalnızca özet metinleri topluyor, iddia üretmiyor. Bu kasıtlı:
/// kaynağı olmayan iddia üretmektense hiç iddia üretmemek doğru davranış.
public sealed class WikipediaResearchHandler(WikipediaProviderAdapter provider) : INodeHandler
{
    public string NodeType => "research.deep";

    public QueueClass Queue => QueueClass.Search;

    public async Task<Result<JsonElement>> ExecuteAsync(
        NodeContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var topic = NodeJson.Text(context.RunContext, "topic.topic") ?? "konu";
        var languageText = NodeJson.Text(context.RunContext, "topic.language") ?? "tr-TR";
        var language = LanguageTag.Create(languageText);

        var maxSources = context.Config.TryGetProperty("max_sources", out var configured)
            && configured.ValueKind == JsonValueKind.Number
                ? configured.GetInt32()
                : 3;

        var providerContext = new ProviderContext
        {
            IdempotencyKey = context.IdempotencyKey,
            CorrelationId = context.CorrelationId,
            Language = language,
        };

        var search = await provider.Search.SearchAsync(
            new SearchQuery { Text = topic, Language = language, MaxResults = maxSources },
            providerContext,
            cancellationToken).ConfigureAwait(false);

        if (search.IsFailure)
        {
            return Result.Failure<JsonElement>(search.Error);
        }

        var sources = new List<object>();

        foreach (var hit in search.Value.Value)
        {
            var document = await provider.Fetch.FetchAsync(hit.Url, providerContext, cancellationToken)
                .ConfigureAwait(false);

            if (document.IsFailure)
            {
                // Tek bir kaynağın çekilememesi araştırmayı düşürmez;
                // kalan kaynaklarla devam edilir. Hepsi düşerse aşağıda
                // yakalanıyor.
                continue;
            }

            var text = document.Value.Value.MainText;

            sources.Add(new
            {
                url = hit.Url.ToString(),
                title = document.Value.Value.Title,
                source_type = hit.SourceType.ToString(),
                content_hash = document.Value.Value.ContentHash,
                // Tam metin bağlama girmiyor: bir Wikipedia makalesi 50 KB
                // olabiliyor ve run bağlamı JSONB kolonuna yazılıyor.
                // Senaryo için özet yeterli; tam metin gerekirse varlık
                // deposuna alınacak (P1-11).
                excerpt = Excerpt(text, 1200),
                length = text.Length,
            });
        }

        if (sources.Count == 0)
        {
            return Error.Transient("research.no_sources",
                $"'{topic}' için hiçbir kaynak çekilemedi.");
        }

        return Result.Success(NodeJson.From(new
        {
            sources,
            source_count = sources.Count,
            language = language.Value,
        }));
    }

    /// Metnin başından okunabilir bir parça alır — cümle ortasından kesmez.
    internal static string Excerpt(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        var slice = text[..maxLength];
        var lastStop = slice.LastIndexOf('.');

        return lastStop > maxLength / 2 ? slice[..(lastStop + 1)] : slice;
    }
}

/// Wikipedia sağlayıcısının iki arayüzünü birlikte taşır.
///
/// Aynı nesne hem <see cref="ISearchProvider"/> hem
/// <see cref="IWebFetchProvider"/> uyguluyor; bu sarmalayıcı node'un
/// ikisini de tek bağımlılıkla almasını sağlıyor. Ayrı ayrı enjekte etmek
/// aynı nesneyi iki kez geçirmek olurdu.
public sealed record WikipediaProviderAdapter(ISearchProvider Search, IWebFetchProvider Fetch)
{
    public static WikipediaProviderAdapter From<T>(T provider)
        where T : ISearchProvider, IWebFetchProvider
        => new(provider, provider);
}
