using System.Globalization;
using System.Text.Json;
using BytemountsAiStudio.Contracts.Prompts;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Providers.Open;
using BytemountsAiStudio.Workflow.Engine;

namespace BytemountsAiStudio.Nodes;

/// Planlı araştırma ajanı (P1-09, §7.3).
///
/// Sistemin TEK GERÇEK ARAÇ DÖNGÜSÜ olan agent'ı. Önceki hâl sabitti:
/// "konuyu Wikipedia'da ara, ilk üç sonucu çek". Bu bir konu için
/// çalışıyor, diğeri için hiç sonuç vermiyordu ve neden vermediğini
/// söyleyecek bir şey yoktu.
///
/// Şimdi iki aşama:
///   1. PLANLAMA — model konuyu farklı açılardan soran sorgular üretiyor
///   2. YÜRÜTME — sorgular sırayla deneniyor, bütçe dolunca duruluyor
///
/// Ayrımın sebebi öngörülebilirlik: "kaç arama yapacağız" sorusunun
/// cevabı çağrı başında biliniyor. Model her adımda "şimdi ne yapayım"
/// diye sorsaydı maliyet önceden kestirilemezdi.
///
/// Bütçe bir GÜVENLİK KEMERİ, optimizasyon değil: sınırsız bir döngü
/// kaynak bulamadıkça aramaya devam eder.
public sealed class ResearchAgentHandler(
    ILlmProvider planner,
    WikipediaProviderAdapter wikipedia,
    WikidataProvider? wikidata = null,
    PromptRegistry? prompts = null) : INodeHandler
{
    private static readonly ToolSchema PlanSchema = new(
        "emit_plan",
        "Arama sorgulari",
        """
        {"type":"object","properties":{
          "queries":{"type":"array","items":{"type":"object","properties":{
            "text":{"type":"string"},
            "language":{"type":"string"},
            "intent":{"type":"string"}
          },"required":["text","language"]}}
        },"required":["queries"]}
        """);

    public string NodeType => "research.deep";

    public QueueClass Queue => QueueClass.Search;

    public async Task<Result<JsonElement>> ExecuteAsync(NodeContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var topic = NodeJson.Text(context.RunContext, "topic.topic") ?? "konu";
        var languageText = NodeJson.Text(context.RunContext, "topic.language") ?? "tr-TR";
        var language = LanguageTag.Create(languageText);

        var maxSteps = ConfigInt(context.Config, "max_steps", 6);
        var targetSources = ConfigInt(context.Config, "max_sources", 3);

        var plan = await PlanAsync(topic, languageText, context, cancellationToken).ConfigureAwait(false);

        if (plan.IsFailure)
        {
            return Result.Failure<JsonElement>(plan.Error);
        }

        var budget = new ResearchBudget(maxSteps, targetSources);
        var sources = new List<object>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var attempted = new List<object>();

        foreach (var query in plan.Value.Queries)
        {
            if (!budget.CanContinue)
            {
                break;
            }

            var found = await RunQueryAsync(query, language, sources, seen, budget, context, cancellationToken)
                .ConfigureAwait(false);

            attempted.Add(new
            {
                text = query.Text,
                language = query.Language,
                intent = query.Intent,
                found,
            });
        }

        budget.QueriesExhausted();

        // KISMİ sonuç kabul ediliyor: eksik araştırmayla senaryo yazmak,
        // hiç senaryo yazmamaktan iyi — iddia doğrulama zaten desteksiz
        // olanı işaretleyecek. Sıfır kaynakla devam etmenin ise anlamı
        // yok ve o zaman GEÇİCİ hata dönüyor: başka bir zamanda başka
        // sonuç gelebilir.
        if (!budget.HasUsableResult)
        {
            return Error.Transient("research.no_sources",
                $"'{topic}' için hiçbir kaynak çekilemedi ({budget}).");
        }

        var facts = await FactsAsync(topic, language, cancellationToken).ConfigureAwait(false);

        return Result.Success(NodeJson.From(new
        {
            sources,
            source_count = sources.Count,
            facts,
            language = language.Value,
            // Planın kendisi ve DENENENLER kayda giriyor. Araştırma zayıf
            // çıktığında "hangi açıları denedik" sorusunun cevabı burada;
            // yalnızca bulunanları yazmak, denenip bulunamayanı görünmez
            // kılardı.
            plan = new
            {
                queries = attempted,
                steps = budget.Steps,
                max_steps = budget.MaxSteps,
                stop = budget.Stop.ToString(),
            },
        }));
    }

    /// Bir sorguyu koşturur ve bulduğu kaynakları listeye ekler.
    private async Task<int> RunQueryAsync(
        ResearchQuery query,
        LanguageTag contentLanguage,
        List<object> sources,
        HashSet<string> seen,
        ResearchBudget budget,
        NodeContext context,
        CancellationToken cancellationToken)
    {
        budget.StepTaken();

        // SORGU DİLİ içerik dilinden farklı olabilir (§20.1) ve
        // planlayıcı bunu seçiyor. Wikipedia için bu, farklı bir dil
        // sürümünde aramak demek.
        var queryLanguage = LanguageTag.TryCreate(query.Language);

        var providerContext = new ProviderContext
        {
            IdempotencyKey = $"{context.IdempotencyKey}:{query.Text}",
            CorrelationId = context.CorrelationId,
            Language = queryLanguage.IsSuccess ? queryLanguage.Value : contentLanguage,
        };

        var search = await wikipedia.Search.SearchAsync(
            new SearchQuery
            {
                Text = query.Text,
                Language = providerContext.Language,
                MaxResults = 3,
            },
            providerContext,
            cancellationToken).ConfigureAwait(false);

        if (search.IsFailure)
        {
            return 0;
        }

        var found = 0;

        foreach (var hit in search.Value.Value)
        {
            if (!budget.CanContinue)
            {
                break;
            }

            // Aynı sayfa iki sorgudan da gelebiliyor; ikinci kez çekmek
            // hem adım hem bant genişliği harcardı.
            if (!seen.Add(hit.Url.ToString()))
            {
                continue;
            }

            var document = await wikipedia.Fetch
                .FetchAsync(hit.Url, providerContext, cancellationToken)
                .ConfigureAwait(false);

            if (document.IsFailure)
            {
                // Tek bir kaynağın çekilememesi araştırmayı düşürmüyor.
                continue;
            }

            var text = document.Value.Value.MainText;

            sources.Add(new
            {
                url = hit.Url.ToString(),
                title = document.Value.Value.Title,
                source_type = hit.SourceType.ToString(),
                content_hash = document.Value.Value.ContentHash,
                excerpt = WikipediaResearchHandler.Excerpt(text, 1200),
                length = text.Length,
                // Hangi sorgudan geldiği: bir kaynağın alakasız çıkması
                // hâlinde suçlu sorgu bulunabilsin.
                via_query = query.Text,
            });

            budget.SourceFound();
            found++;
        }

        return found;
    }

    /// Konudan arama planı üretir.
    ///
    /// Model plan üretemezse KONUNUN KENDİSİ tek sorgu olarak
    /// kullanılıyor. Düşürmek yanlış olurdu: eski davranış zaten buydu
    /// ve işe yarıyordu; plan bir iyileştirme, bir önkoşul değil.
    private async Task<Result<ResearchPlan>> PlanAsync(
        string topic, string language, NodeContext context, CancellationToken cancellationToken)
    {
        var fallback = new ResearchPlan
        {
            Queries = [new ResearchQuery { Text = topic, Language = language, Intent = "konunun kendisi" }],
        };

        var registry = prompts is not null ? Result.Success(prompts) : PromptRegistry.Embedded;

        if (registry.IsFailure)
        {
            return Result.Success(fallback);
        }

        var template = registry.Value.Get("research.plan");

        if (template.IsFailure)
        {
            return Result.Success(fallback);
        }

        var rendered = template.Value.Render(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["topic"] = topic,
            ["language"] = language,
        });

        if (rendered.IsFailure)
        {
            return Result.Success(fallback);
        }

        var response = await planner.CompleteAsync(
            new LlmRequest
            {
                Tier = ModelTier.Standard,
                Temperature = 0.4,
                Messages =
                [
                    new(ChatRole.System, rendered.Value.System ?? string.Empty),
                    new(ChatRole.User, rendered.Value.User),
                ],
                ForcedTool = PlanSchema,
            },
            ScriptGenerateHandler.Context(context),
            cancellationToken).ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result.Success(fallback);
        }

        var parsed = ParsePlan(response.Value.Value.ToolArguments, topic, language);

        return Result.Success(parsed ?? fallback);
    }

    /// Plan çıktısını ayrıştırır. Ayrı ve `internal`: LLM olmadan
    /// sınanabilsin.
    internal static ResearchPlan? ParsePlan(string? toolArguments, string topic, string language)
    {
        if (string.IsNullOrWhiteSpace(toolArguments))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(toolArguments);

            if (!document.RootElement.TryGetProperty("queries", out var array)
                || array.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var queries = new List<ResearchQuery>();

            foreach (var element in array.EnumerateArray())
            {
                var text = element.TryGetProperty("text", out var t) ? t.GetString() : null;

                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                queries.Add(new ResearchQuery
                {
                    Text = text.Trim(),
                    // Dil belirtilmemişse İÇERİK dili varsayılıyor.
                    // Boş bırakmak, sağlayıcının varsayılanına düşmek
                    // demekti ve o varsayılan İngilizce.
                    Language = element.TryGetProperty("language", out var l) && l.GetString() is { Length: > 0 } lang
                        ? lang
                        : language,
                    Intent = element.TryGetProperty("intent", out var i) ? i.GetString() : null,
                });
            }

            return queries.Count == 0 ? null : new ResearchPlan { Queries = queries };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// Wikidata olguları — başarısızlığı araştırmayı düşürmüyor.
    private async Task<List<object>> FactsAsync(
        string topic, LanguageTag language, CancellationToken cancellationToken)
    {
        var facts = new List<object>();

        if (wikidata is null)
        {
            return facts;
        }

        var search = await wikidata.SearchAsync(
            new SearchQuery { Text = topic, Language = language, MaxResults = 1 },
            ProviderContext.ForTest($"wikidata:{topic}"),
            cancellationToken).ConfigureAwait(false);

        if (search.IsFailure || search.Value.Value.Count == 0)
        {
            return facts;
        }

        var entityId = search.Value.Value[0].Url.Segments[^1];
        var result = await wikidata.FactsAsync(entityId, language, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            return facts;
        }

        foreach (var fact in result.Value)
        {
            facts.Add(new
            {
                entity = entityId,
                property = fact.PropertyId,
                label = fact.Label,
                value = fact.Value,
            });
        }

        return facts;
    }

    private static int ConfigInt(JsonElement config, string name, int fallback)
        => config.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var parsed)
            ? Math.Clamp(parsed, 1, 50)
            : fallback;
}
