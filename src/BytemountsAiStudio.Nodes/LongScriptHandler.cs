using System.Globalization;
using System.Text.Json;
using BytemountsAiStudio.Contracts.Prompts;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Core.Learning;
using BytemountsAiStudio.Workflow.Engine;

namespace BytemountsAiStudio.Nodes;

/// Uzun video senaryosu: bölüm bölüm (P3-02).
///
/// BÖLÜM BAŞINA AYRI ÇAĞRI, tek bir "on beş dakikalık senaryo yaz"
/// çağrısı değil. Sebebi teknik değil, içeriksel: tek çağrıda model
/// ilk bölümü ayrıntılı yazıp sonrakileri özet geçiyor — bağlam
/// penceresi doldukça cümleler kısalıyor ve son bölüm bir liste
/// hâline geliyor. Ayrı çağrılarda her bölüm aynı özeni görüyor.
///
/// ÖNCEKİ BÖLÜMÜN SON CÜMLELERİ BAĞLAMA GİRİYOR: bölümler birbirini
/// tekrar etmemeli ve "daha önce gördüğümüz gibi" demeden devam
/// etmeli. Hiç bağlam vermemek, aynı olguyu üç bölümde üç kez anlatan
/// bir video demekti.
public sealed class LongScriptHandler(ILlmProvider llm, PromptRegistry? prompts = null) : INodeHandler
{
    private static readonly ToolSchema Schema = new(
        "emit_chapter_script",
        "Bir bolumun cumleleri",
        """
        {"type":"object","properties":{
          "sentences":{"type":"array","items":{"type":"string"}}
        },"required":["sentences"]}
        """);

    public string NodeType => "script.long";

    public QueueClass Queue => QueueClass.Llm;

    /// Saniyede kaç kelime konuşuluyor.
    ///
    /// 2,4 kelime/saniye: Türkçe ve İngilizce belgesel anlatımının
    /// ortalaması. Cümle sayısı bundan türüyor — modele "kaç saniye"
    /// demek işe yaramıyor, "kaç cümle" demek yarıyor.
    public const double WordsPerSecond = 2.4;

    /// Ortalama cümle uzunluğu (kelime).
    ///
    /// ON İKİ: daha uzun cümle seslendirmede nefes bırakmıyor ve
    /// altyazıya sığmıyor.
    public const int WordsPerSentence = 12;

    /// Önceki bölümden kaç cümle bağlama giriyor.
    ///
    /// ÜÇ: bağlamı kurmaya yetiyor, istemi şişirmiyor. Tamamını
    /// vermek dördüncü bölümde istemi binlerce kelime yapardı ve
    /// küçük bir yerel model o uzunluğu zaten dikkate almıyor.
    public const int PreviousSentenceCount = 3;

    public async Task<Result<JsonElement>> ExecuteAsync(NodeContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var chapters = ChaptersOf(context.RunContext);

        if (chapters.Count == 0)
        {
            // KALICI: yeniden denemek bölüm planı üretmiyor. Sıranın
            // yanlış olduğunu söylemek, sessizce kısa video senaryosu
            // yazmaktan iyi.
            return Error.Permanent("script.no_chapters",
                "Bölüm planı yok; `script.long` node'u `chapter.plan` sonrasında koşmalı.");
        }

        var topic = NodeJson.Text(context.RunContext, "topic.topic") ?? "konu";
        var language = NodeJson.Text(context.RunContext, "topic.language") ?? "tr-TR";
        var sources = ChapterPlanHandler.SourcesOf(context.RunContext);

        var all = new List<string>();
        var perChapter = new List<object>();
        var previous = new List<string>();

        foreach (var chapter in chapters)
        {
            var sentenceCount = SentenceCountFor(chapter.TargetMs);

            var written = await WriteChapterAsync(
                topic, language, chapter, sentenceCount, previous, sources, context, cancellationToken)
                .ConfigureAwait(false);

            if (written.IsFailure)
            {
                // BİR BÖLÜM DÜŞERSE NODE DÜŞÜYOR.
                //
                // Eksik bölümle devam etmek, chapter işaretleri olan
                // ama o bölümde hiçbir şey anlatmayan bir video
                // demekti — ve boşluk videonun ortasında sessizlik
                // olarak görünürdü.
                return Result.Failure<JsonElement>(written.Error);
            }

            all.AddRange(written.Value);

            perChapter.Add(new
            {
                index = chapter.Index,
                title = chapter.Title,
                start_ms = chapter.StartMs,
                sentence_count = written.Value.Count,
                // İLK CÜMLE İNDEKSİ: altyazı ve chapter işaretleri
                // (P3-04) hangi cümlenin hangi bölüme ait olduğunu
                // buradan biliyor.
                first_sentence = all.Count - written.Value.Count,
            });

            previous = [.. written.Value.TakeLast(PreviousSentenceCount)];
        }

        return Result.Success(NodeJson.From(new
        {
            sentences = all,
            chapters = perChapter,
            sentence_count = all.Count,
            chapter_count = chapters.Count,
        }));
    }

    private async Task<Result<List<string>>> WriteChapterAsync(
        string topic,
        string language,
        ChapterRef chapter,
        int sentenceCount,
        IReadOnlyList<string> previous,
        string sources,
        NodeContext context,
        CancellationToken cancellationToken)
    {
        var rendered = Render(
            topic, language, chapter, sentenceCount, previous, sources,
            PromptSelection.Version(context.RunContext, "script.chapter"));

        if (rendered.IsFailure)
        {
            return Result.Failure<List<string>>(rendered.Error);
        }

        var response = await llm.CompleteAsync(
            new LlmRequest
            {
                Tier = ModelTier.Standard,
                // METİN İŞİ: sıcaklık yüksek. Bölüm planı kararlı
                // olmalıydı (yapı), metin ise tekdüze olmamalı.
                Temperature = 0.8,
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
            return Result.Failure<List<string>>(response.Error);
        }

        var sentences = ParseSentences(response.Value.Value.ToolArguments);

        return sentences.Count == 0
            ? Error.Transient("script.empty_chapter",
                $"'{chapter.Title}' bölümü için hiç cümle üretilmedi.")
            : Result.Success(sentences);
    }

    /// Hedef süreden cümle sayısı.
    ///
    /// Modele "kaç saniye" demek işe yaramıyor — süreyi tahmin
    /// edemiyor. "Kaç cümle" demek yarıyor ve gerçek süre zaten
    /// seslendirmeden SONRA ölçülüyor (ADR-006). Bu sayı bir hedef,
    /// bir taahhüt değil.
    internal static int SentenceCountFor(int targetMs)
    {
        var words = targetMs / 1000.0 * WordsPerSecond;

        // EN AZ ÜÇ CÜMLE: iki cümlelik bir "bölüm" bir bölüm değil.
        return Math.Max((int)Math.Round(words / WordsPerSentence), 3);
    }

    internal sealed record ChapterRef(int Index, string Title, string? Question, int StartMs, int TargetMs);

    /// Bölüm planını run bağlamından okur.
    internal static IReadOnlyList<ChapterRef> ChaptersOf(JsonElement runContext)
    {
        if (!runContext.TryGetProperty("chapters", out var node)
            || node.ValueKind != JsonValueKind.Object
            || !node.TryGetProperty("chapters", out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var chapters = new List<ChapterRef>();

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var title = Text(item, "title");

            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            chapters.Add(new ChapterRef(
                Int(item, "index") ?? chapters.Count,
                title,
                Text(item, "question"),
                Int(item, "start_ms") ?? 0,
                Int(item, "target_ms") ?? 120_000));
        }

        return chapters;
    }

    internal static List<string> ParseSentences(string? toolArguments)
    {
        if (string.IsNullOrWhiteSpace(toolArguments))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(toolArguments);

            if (!document.RootElement.TryGetProperty("sentences", out var array)
                || array.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return [.. array.EnumerateArray()
                .Where(s => s.ValueKind == JsonValueKind.String)
                .Select(s => s.GetString()!.Trim())
                .Where(s => s.Length > 0)];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private Result<RenderedPrompt> Render(
        string topic,
        string language,
        ChapterRef chapter,
        int sentenceCount,
        IReadOnlyList<string> previous,
        string sources,
        int? promptVersion)
    {
        var registry = prompts is not null ? Result.Success(prompts) : PromptRegistry.Embedded;

        if (registry.IsFailure)
        {
            return Result.Failure<RenderedPrompt>(registry.Error);
        }

        var template = registry.Value.Get("script.chapter", promptVersion);

        if (template.IsFailure)
        {
            return Result.Failure<RenderedPrompt>(template.Error);
        }

        return template.Value.Render(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["topic"] = topic,
            ["language"] = language,
            ["chapter_title"] = chapter.Title,
            // SORU YOKSA BAŞLIK KULLANILIYOR ama bu bir düşüş: soru
            // olmadan model başlığı tekrar eden bir paragraf yazıyor.
            ["chapter_question"] = chapter.Question ?? chapter.Title,
            ["sentence_count"] = sentenceCount.ToString(CultureInfo.InvariantCulture),
            ["previous"] = previous.Count == 0
                ? "Bu ilk bölüm; öncesi yok."
                : "Önceki bölümün son cümleleri:\n" + string.Join("\n", previous.Select(s => "- " + s)),
            ["sources"] = sources,
        });
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Int(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var parsed)
            ? parsed
            : null;
}
