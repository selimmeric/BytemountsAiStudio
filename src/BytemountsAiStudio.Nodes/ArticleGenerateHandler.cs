using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BytemountsAiStudio.Contracts.Prompts;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Core.Learning;
using BytemountsAiStudio.Workflow.Engine;

namespace BytemountsAiStudio.Nodes;

/// Blog makalesi üretimi (P6-04).
///
/// MAKALE SENARYO DEĞİL — ve bu, işin tamamı.
///
/// Senaryo dinlenmek için yazılıyor: kısa cümleler, başlık yok, sayılar
/// okunuşuyla ("bin dört yüz elli üç"), kaynak metnin içinde
/// geçmiyor çünkü kimse sesli bir videoda dipnot duyamıyor. Makale
/// okunmak için yazılıyor: başlıklar, rakamlar, tıklanabilir kaynaklar.
///
/// Senaryoyu bir sayfaya yapıştırmak "blog içerik türü eklendi" demek
/// değil. Yapıştırılmış senaryo, okunduğunda garip ve kaynaksız bir
/// metin oluyor — ve garipliği kimse bir hata olarak raporlamıyor.
///
/// KURALLAR İSTEMDE, DENETİM KODDA (§7.2 deseni): modele "yalnızca
/// verilen kaynakları kullan" demek çoğu zaman işe yarıyor; işe
/// yaramadığı sefer uydurulmuş bir `[7]` atıfı yayına giriyor ve
/// makale doğrulanamaz hâle geliyor.
public sealed partial class ArticleGenerateHandler(ILlmProvider llm, PromptRegistry? prompts = null)
    : INodeHandler
{
    public string NodeType => "article.generate";

    public QueueClass Queue => QueueClass.Llm;

    /// Varsayılan uzunluk. Kısa bir makale kaynakları özetleyemiyor,
    /// çok uzunu da modeli tekrara itiyor.
    public const int DefaultWordCount = 800;

    /// En az kaç kelime kabul edilir.
    ///
    /// Kelime sayısı ÖLÇÜLÜYOR, istenen sayıya güvenilmiyor: model
    /// "800 kelime" isteğine 200 kelimeyle cevap verdiğinde ortaya
    /// yayınlanabilir görünen, aslında yarım bir makale çıkıyor.
    public const int MinimumWords = 200;

    /// En az kaç başlık.
    ///
    /// Başlıksız bir metin duvarı, yapıştırılmış senaryonun en belirgin
    /// işareti.
    public const int MinimumHeadings = 2;

    public async Task<Result<JsonElement>> ExecuteAsync(NodeContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var topic = NodeJson.Text(context.RunContext, "topic.topic") ?? "konu";
        var language = NodeJson.Text(context.RunContext, "topic.language") ?? "tr-TR";

        var sources = SourcesOf(context.RunContext);

        if (sources.Count == 0)
        {
            // KAYNAKSIZ MAKALE ÜRETİLMİYOR. Videoda iddia doğrulama
            // desteksiz cümleyi işaretliyor; makalede kaynak METNİN
            // İÇİNDE ve kaynaksız yazmak, uydurmayı zorunlu kılmak
            // demek.
            return Error.Permanent("article.no_sources",
                "Makale için kaynak yok; `research.deep` sonrasında koşmalı.");
        }

        var registry = prompts is not null ? Result.Success(prompts) : PromptRegistry.Embedded;

        if (registry.IsFailure)
        {
            return Result.Failure<JsonElement>(registry.Error);
        }

        var template = registry.Value.Get(
            "article.generate", PromptSelection.Version(context.RunContext, "article.generate"));

        if (template.IsFailure)
        {
            return Result.Failure<JsonElement>(template.Error);
        }

        var wordCount = context.Config.ValueKind == JsonValueKind.Object
            && context.Config.TryGetProperty("word_count", out var configured)
            && configured.ValueKind == JsonValueKind.Number
                ? configured.GetInt32()
                : DefaultWordCount;

        var rendered = template.Value.Render(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["topic"] = topic,
            ["language"] = language,
            ["word_count"] = wordCount.ToString(CultureInfo.InvariantCulture),
            ["sources"] = Numbered(sources),
        });

        if (rendered.IsFailure)
        {
            return Result.Failure<JsonElement>(rendered.Error);
        }

        var response = await llm.CompleteAsync(
            new LlmRequest
            {
                // GÜÇLÜ KATMAN: makale kanalın en uzun ömürlü içeriği —
                // video bir hafta, arama sonucundaki bir yazı yıllarca
                // okunuyor.
                Tier = ModelTier.Strong,
                Temperature = 0.7,
                Messages =
                [
                    new(ChatRole.System, rendered.Value.System ?? string.Empty),
                    new(ChatRole.User, rendered.Value.User),
                ],
            },
            ScriptGenerateHandler.Context(context),
            cancellationToken).ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result.Failure<JsonElement>(response.Error);
        }

        return Build(response.Value.Value.Text, sources, rendered.Value.Stamp);
    }

    /// Model çıktısını DENETLER ve kayda çevirir.
    ///
    /// Ayrı ve `internal`: denetim LLM olmadan sınanabilsin.
    internal static Result<JsonElement> Build(
        string? markdown, IReadOnlyList<ArticleSource> sources, string promptStamp)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return Error.Transient("article.empty", "Model boş makale döndürdü.");
        }

        var text = markdown.Trim();
        var words = WordCount(text);

        if (words < MinimumWords)
        {
            // GEÇİCİ: ikinci deneme genellikle tam uzunlukta çıkıyor.
            return Error.Transient("article.too_short",
                string.Create(CultureInfo.InvariantCulture,
                    $"Makale {words} kelime; en az {MinimumWords} bekleniyor."));
        }

        var headings = Headings(text);

        if (headings.Count < MinimumHeadings)
        {
            return Error.Transient("article.no_headings",
                string.Create(CultureInfo.InvariantCulture,
                    $"Makalede {headings.Count} başlık var; en az {MinimumHeadings} gerekiyor. ")
                + "Başlıksız metin duvarı, yapıştırılmış senaryonun işareti.");
        }

        var citations = Citations(text);

        if (citations.Count == 0)
        {
            // ATIFSIZ MAKALE DOĞRULANAMAZ. Videoda iddia doğrulama
            // ayrı bir adım; makalede kaynak metnin içinde ve yoksa
            // okuyucunun elinde hiçbir şey kalmıyor.
            return Error.Transient("article.no_citations",
                "Makalede hiç kaynak atfı yok; her olgusal cümle bir kaynağa dayanmalı.");
        }

        // UYDURULMUŞ ATIF YAKALANIYOR.
        //
        // Üç kaynak verilip `[7]` yazılması, modelin var olmayan bir
        // kaynağa dayandığını söylüyor. Sessiz geçirmek, okuyucunun
        // tıklayacağı bir yer olmayan bir "kaynak" göstermek olurdu.
        var invalid = citations.Where(c => c < 1 || c > sources.Count).Distinct().Order().ToList();

        if (invalid.Count > 0)
        {
            return Error.Transient("article.bad_citation",
                string.Create(CultureInfo.InvariantCulture,
                    $"{sources.Count} kaynak var ama makale ")
                + string.Join(", ", invalid.Select(i => $"[{i}]"))
                + " atfı yapıyor.");
        }

        var used = citations.Distinct().Order().ToList();

        return Result.Success(NodeJson.From(new
        {
            markdown = text,
            title = Title(text),
            word_count = words,
            heading_count = headings.Count,

            // CÜMLELER DE YAZILIYOR: iddia doğrulama (`claim.check`)
            // bunları okuyor. Makale videodan farklı bir biçim ama
            // "her cümle bir kaynağa dayanmalı" kuralı ortak.
            sentences = Sentences(text),

            // KULLANILAN kaynaklar ayrı yazılıyor: üç kaynak verilip
            // birine atıf yapılmışsa, araştırmanın üçte ikisi boşa
            // gitmiş demektir ve bu görülmeli.
            sources = sources.Select((s, i) => new
            {
                index = i + 1,
                s.Url,
                s.Title,
                cited = used.Contains(i + 1),
            }),
            cited_source_count = used.Count,
            source_count = sources.Count,
            prompt = promptStamp,
        }));
    }

    /// Araştırma çıktısındaki kaynaklar.
    internal static IReadOnlyList<ArticleSource> SourcesOf(JsonElement runContext)
    {
        if (runContext.ValueKind != JsonValueKind.Object
            || !runContext.TryGetProperty("research", out var research)
            || research.ValueKind != JsonValueKind.Object
            || !research.TryGetProperty("sources", out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var sources = new List<ArticleSource>();

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var url = item.TryGetProperty("url", out var u) ? u.GetString() : null;

            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            sources.Add(new ArticleSource(
                url,
                item.TryGetProperty("title", out var t) ? t.GetString() ?? url : url,
                item.TryGetProperty("excerpt", out var e) ? e.GetString() ?? string.Empty : string.Empty));
        }

        return sources;
    }

    /// Kaynakları isteme girecek numaralı listeye çevirir.
    ///
    /// NUMARA BURADA VERİLİYOR: modelin kendi numaralandırması, iki
    /// çağrı arasında değişir ve atıflar kaynaklarla eşleşmezdi.
    internal static string Numbered(IReadOnlyList<ArticleSource> sources)
    {
        var builder = new StringBuilder();

        for (var i = 0; i < sources.Count; i++)
        {
            builder.Append('[').Append(i + 1).Append("] ")
                .Append(sources[i].Title).Append(" — ").AppendLine(sources[i].Url);

            if (sources[i].Excerpt.Length > 0)
            {
                builder.AppendLine(sources[i].Excerpt);
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    /// `[3]` biçimindeki atıflar.
    internal static IReadOnlyList<int> Citations(string markdown)
        => [.. CitationPattern().Matches(markdown)
            .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))];

    internal static IReadOnlyList<string> Headings(string markdown)
        => [.. markdown.Split('\n')
            .Select(l => l.TrimEnd())
            .Where(l => l.StartsWith("##", StringComparison.Ordinal))];

    /// Birinci seviye başlık ya da ilk satır.
    internal static string Title(string markdown)
    {
        foreach (var line in markdown.Split('\n'))
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith("# ", StringComparison.Ordinal))
            {
                return trimmed[2..].Trim();
            }
        }

        // BAŞLIK UYDURULMUYOR: ilk anlamlı satır alınıyor. Boş bir
        // başlık, SEO adımının hiçbir şeyle çalışması demekti.
        return markdown.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "Makale";
    }

    internal static int WordCount(string markdown)
        => markdown.Split([' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Length;

    /// Makale cümleleri — başlıklar ve liste işaretleri hariç.
    internal static IReadOnlyList<string> Sentences(string markdown)
    {
        var sentences = new List<string>();

        foreach (var line in markdown.Split('\n'))
        {
            var trimmed = line.Trim();

            if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith('>'))
            {
                continue;
            }

            foreach (var piece in trimmed.Split(['.', '!', '?'], StringSplitOptions.RemoveEmptyEntries))
            {
                var sentence = piece.Trim();

                if (sentence.Length > 20)
                {
                    sentences.Add(sentence);
                }
            }
        }

        return sentences;
    }

    [GeneratedRegex(@"\[(\d{1,3})\]")]
    private static partial Regex CitationPattern();
}

/// Makaleye giren bir kaynak.
public readonly record struct ArticleSource(string Url, string Title, string Excerpt);
