using System.Globalization;
using System.Text.Json;
using BytemountsAiStudio.Contracts.Prompts;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Core.Time;
using BytemountsAiStudio.Workflow.Engine;

namespace BytemountsAiStudio.Nodes;

/// Uzun video bölüm planı node'u (P3-02).
///
/// İKİ AYRI İŞ, İKİ AYRI YER: model bölüm BAŞLIKLARINI ve SORULARINI
/// üretiyor, `ChapterPlanner` zamanı paylaştırıyor. Model'e "her bölüm
/// kaç saniye olsun" diye sormak, aritmetiği olasılıklı bir şeye
/// havale etmekti — ve toplamı tutmayan bir plan, chapter
/// işaretlerinin videonun sonunda kaymasıyla ortaya çıkardı.
public sealed class ChapterPlanHandler(ILlmProvider llm, PromptRegistry? prompts = null) : INodeHandler
{
    private static readonly ToolSchema Schema = new(
        "emit_chapters",
        "Bolum basliklari ve sorulari",
        """
        {"type":"object","properties":{
          "chapters":{"type":"array","items":{"type":"object","properties":{
            "title":{"type":"string"},
            "question":{"type":"string"}
          },"required":["title"]}}
        },"required":["chapters"]}
        """);

    public string NodeType => "chapter.plan";

    public QueueClass Queue => QueueClass.Llm;

    /// Varsayılan hedef süre.
    ///
    /// ON İKİ DAKİKA: 8–15 aralığının ortası. Ayar verilmediğinde
    /// sınırın kenarına oturmak, bir bölüm eklendiğinde ya da
    /// çıkarıldığında planın hemen geçersiz olması demekti.
    public const int DefaultMinutes = 12;

    public async Task<Result<JsonElement>> ExecuteAsync(NodeContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var topic = NodeJson.Text(context.RunContext, "topic.topic") ?? "konu";
        var language = NodeJson.Text(context.RunContext, "topic.language") ?? "tr-TR";

        var minutes = ConfigInt(context.Config, "target_minutes", DefaultMinutes);
        var target = ChapterPlanner.Clamp(new Ms(minutes * 60 * 1000));

        var sources = SourcesOf(context.RunContext);

        var rendered = Render(topic, language, target, sources);

        if (rendered.IsFailure)
        {
            return Result.Failure<JsonElement>(rendered.Error);
        }

        var response = await llm.CompleteAsync(
            new LlmRequest
            {
                Tier = ModelTier.Standard,
                // YAPI İŞİ, YARATICILIK İŞİ DEĞİL: aynı konuya iki
                // koşuda farklı bölüm yapısı vermek, bir sorunun
                // tekrarlanabilirliğini bozardı. Senaryo metni ayrı bir
                // adımda ve orada sıcaklık yüksek.
                Temperature = 0.3,
                Messages =
                [
                    new(ChatRole.System, rendered.Value.System ?? string.Empty),
                    new(ChatRole.User, rendered.Value.User),
                ],
                ForcedTool = Schema,
            },
            ScriptGenerateHandler.Context(context),
            cancellationToken).ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result.Failure<JsonElement>(response.Error);
        }

        var sections = ParseSections(response.Value.Value.ToolArguments);

        if (sections.Count == 0)
        {
            // BOŞ PLAN SESSİZCE GEÇMİYOR: geçseydi bölümsüz bir uzun
            // video üretilir ve "yapı olmadan uzunluk" tam da
            // kaçındığımız şey olurdu.
            return Error.Transient("chapter.empty_plan",
                "Model hiç bölüm üretmedi.");
        }

        var plan = ChapterPlanner.Plan(sections, target);

        if (plan.IsFailure)
        {
            return Result.Failure<JsonElement>(plan.Error);
        }

        var dropped = ChapterPlanner.Dropped(sections, plan.Value);

        return Result.Success(NodeJson.From(new
        {
            chapters = plan.Value.Chapters.Select(c => new
            {
                index = c.Index,
                title = c.Title,
                question = c.Question,
                start_ms = c.Start.Value,
                target_ms = c.TargetDuration.Value,
            }),
            total_ms = plan.Value.TotalDuration.Value,
            chapter_count = plan.Value.Count,
            // MODELİN ÖNERDİĞİ İLE PLANA GİREN AYRI YAZILIYOR.
            //
            // Kırpma sessiz olsaydı, modelin planının aynen
            // uygulandığı sanılırdı ve videonun neden beklenenden
            // farklı çıktığı açıklanamazdı.
            requested_count = sections.Count,
            dropped_count = dropped,
            // Hedef süre de yazılıyor: istenen ile üretilen farklı
            // olabiliyor (az bölüm videoyu kısaltıyor).
            requested_minutes = minutes,
        }));
    }

    /// Model çıktısını okur. `internal`: model olmadan sınanabilsin.
    internal static IReadOnlyList<(string Title, string? Question)> ParseSections(string? toolArguments)
    {
        if (string.IsNullOrWhiteSpace(toolArguments))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(toolArguments);

            if (!document.RootElement.TryGetProperty("chapters", out var array)
                || array.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var sections = new List<(string, string?)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var title = Text(item, "title")?.Trim();

                // AYNI BAŞLIK İKİ KEZ GELİRSE İKİNCİSİ DÜŞÜYOR: model
                // bunu yapıyor ve iki özdeş bölüm, chapter listesinde
                // aynı adı iki kez göstermek olurdu.
                if (string.IsNullOrWhiteSpace(title) || !seen.Add(title))
                {
                    continue;
                }

                sections.Add((title, Text(item, "question")));
            }

            return sections;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// Araştırma kaynaklarını istem için özetler.
    ///
    /// KAYNAK VERİLMEZSE MODEL UYDURUYOR ve uydurduğu bölüm iddia
    /// doğrulamada düşüyor — o noktaya kadar harcanan her şey boşa
    /// gidiyor. Kaynak yoksa bunu açıkça söylemek, boş bırakmaktan iyi.
    internal static string SourcesOf(JsonElement runContext)
    {
        if (!runContext.TryGetProperty("research", out var research)
            || research.ValueKind != JsonValueKind.Object
            || !research.TryGetProperty("sources", out var array)
            || array.ValueKind != JsonValueKind.Array
            || array.GetArrayLength() == 0)
        {
            return "(kaynak bulunamadı — yalnızca genel bilgiyle plan çıkar)";
        }

        var lines = new List<string>();

        foreach (var source in array.EnumerateArray())
        {
            if (source.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var title = Text(source, "title") ?? Text(source, "url") ?? "kaynak";
            var excerpt = Text(source, "excerpt");

            lines.Add(excerpt is null
                ? $"- {title}"
                : $"- {title}: {Shorten(excerpt)}");
        }

        return lines.Count == 0 ? "(kaynak okunamadı)" : string.Join("\n", lines);
    }

    /// Alıntılar KISALTILIYOR.
    ///
    /// Tam metinleri vermek istemi binlerce kelime büyütüyor ve küçük
    /// bir yerel model o uzunlukta talimatları zaten dikkate almıyor —
    /// bölüm planı için gereken şey kaynağın NE HAKKINDA olduğu, ne
    /// söylediğinin tamamı değil.
    private static string Shorten(string text)
        => text.Length <= 200 ? text : text[..200] + "…";

    private Result<RenderedPrompt> Render(string topic, string language, Ms target, string sources)
    {
        var registry = prompts is not null ? Result.Success(prompts) : PromptRegistry.Embedded;

        if (registry.IsFailure)
        {
            return Result.Failure<RenderedPrompt>(registry.Error);
        }

        var template = registry.Value.Get("chapter.plan");

        if (template.IsFailure)
        {
            return Result.Failure<RenderedPrompt>(template.Error);
        }

        return template.Value.Render(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["topic"] = topic,
            ["language"] = language,
            ["minutes"] = (target.Value / 60000).ToString(CultureInfo.InvariantCulture),
            ["sources"] = sources,
        });
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int ConfigInt(JsonElement config, string name, int fallback)
        => config.ValueKind == JsonValueKind.Object
           && config.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var parsed)
            ? parsed
            : fallback;
}
