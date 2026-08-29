using System.Text;
using System.Text.Json;
using BytemountsAiStudio.Contracts.Prompts;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Core.Learning;
using BytemountsAiStudio.Workflow.Engine;

namespace BytemountsAiStudio.Nodes;

/// Yayın metadata'sı üretimi (P1-22, §15).
///
/// İki katman, ve ayrımı önemli:
///   1. MODEL başlığı, açıklamayı ve etiketleri yazıyor — yaratıcı iş.
///   2. KOD sınırları uyguluyor — mekanik iş.
///
/// İkinciyi modele bırakmak yaygın ama yanlış: isteme "100 karakteri
/// geçme" yazmak çoğu zaman işe yarıyor, bazen yaramıyor, ve yaramadığı
/// sefer upload REDDEDİLİYOR — hem de videonun kalan her adımı
/// yapıldıktan sonra. Bir üretim hattında en pahalı hata, son adımda
/// ortaya çıkan hatadır.
///
/// İstem yine de sınırı söylüyor (90 karakter, gerçek sınırın altında):
/// modelin sınıra yakın yazması, kırpmanın hiç devreye girmemesi
/// demek ve kırpılmamış başlık her zaman daha iyi.
public sealed class SeoGenerateHandler(ILlmProvider llm, PromptRegistry? prompts = null) : INodeHandler
{
    public string NodeType => "seo.generate";

    public QueueClass Queue => QueueClass.Llm;

    /// Modelin doldurmak zorunda olduğu şema (§7.2).
    private static readonly ToolSchema Schema = new(
        "emit_metadata",
        "Video basligi, aciklamasi ve etiketleri",
        """
        {"type":"object","properties":{
          "title":{"type":"string"},
          "description":{"type":"string"},
          "tags":{"type":"array","items":{"type":"string"}}
        },"required":["title","description","tags"]}
        """);

    public async Task<Result<JsonElement>> ExecuteAsync(NodeContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var topic = NodeJson.Text(context.RunContext, "topic.topic") ?? "konu";
        var language = NodeJson.Text(context.RunContext, "topic.language") ?? "tr-TR";
        var script = ScriptText(context.RunContext);

        if (script is null)
        {
            return Error.Permanent("seo.no_script", "Senaryo bulunamadı; metadata üretilemez.");
        }

        var registry = prompts is not null ? Result.Success(prompts) : PromptRegistry.Embedded;

        if (registry.IsFailure)
        {
            return Result.Failure<JsonElement>(registry.Error);
        }

        var template = registry.Value.Get(
            "seo.generate", PromptSelection.Version(context.RunContext, "seo.generate"));

        if (template.IsFailure)
        {
            return Result.Failure<JsonElement>(template.Error);
        }

        // BAŞLIK STİLİ DENEYE AÇIK (P5-03).
        //
        // Deney yoksa `duz` — bugünkü davranış. Deney varsa kol adı
        // isteme giriyor ve stilin ne demek olduğu istem dosyasında
        // yazıyor; böylece stil metni sürümlü ve gözle
        // karşılaştırılabilir kalıyor (§7.3).
        var style = TitleVariant.DefaultStyle;
        var experiment = ExperimentContext.ConfigFor(context.RunContext, "title");

        if (experiment is not null)
        {
            var parsed = TitleVariant.Parse(experiment);

            if (parsed.IsFailure)
            {
                return Result.Failure<JsonElement>(parsed.Error);
            }

            // İSTEMİN STİLİ GERÇEKTEN ALDIĞI DOĞRULANIYOR.
            //
            // Şablon, kendisinde olmayan yer tutuculara verilen
            // değerleri sessizce yutuyor. Bu kontrol olmadan
            // `{{baslik_stili}}` içermeyen bir istem sürümüyle koşan
            // deney iki kolda da AYNI istemi kullanır ve haftalarca
            // hiçbir şey ölçmeden "fark yok" der.
            var wired = TitleVariant.Verify(
                (template.Value.System ?? string.Empty) + template.Value.User);

            if (wired.IsFailure)
            {
                return Result.Failure<JsonElement>(wired.Error);
            }

            style = parsed.Value;
        }

        var rendered = template.Value.Render(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["topic"] = topic,
            ["language"] = language,
            ["script"] = script,
            [TitleVariant.Placeholder] = style,
        });

        if (rendered.IsFailure)
        {
            return Result.Failure<JsonElement>(rendered.Error);
        }

        var response = await llm.CompleteAsync(
            new LlmRequest
            {
                Tier = ModelTier.Standard,
                // Başlıkta biraz çeşitlilik isteniyor: sıfır sıcaklık
                // her videoda aynı kalıbı üretiyor ve kanal tekdüze
                // görünüyor.
                Temperature = 0.6,
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

        return Build(response.Value.Value.ToolArguments, rendered.Value.Stamp, style, context.RunContext);
    }

    /// Model çıktısını sınırlara sığdırır ve sonucu DOĞRULAR.
    ///
    /// Ayrı ve `internal`: kırpma mantığı LLM olmadan sınanabilsin.
    internal static Result<JsonElement> Build(
        string? toolArguments, string promptStamp, string style = TitleVariant.DefaultStyle,
        JsonElement? runContext = null)
    {
        if (string.IsNullOrWhiteSpace(toolArguments))
        {
            return Error.Permanent("seo.empty", "Model metadata döndürmedi.");
        }

        string rawTitle;
        string rawDescription;
        List<string> rawTags;

        try
        {
            using var document = JsonDocument.Parse(toolArguments);
            var root = document.RootElement;

            rawTitle = root.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            rawDescription = root.TryGetProperty("description", out var d) ? d.GetString() ?? string.Empty : string.Empty;

            rawTags = root.TryGetProperty("tags", out var g) && g.ValueKind == JsonValueKind.Array
                ? [.. g.EnumerateArray().Select(e => e.GetString() ?? string.Empty)]
                : [];
        }
        catch (JsonException ex)
        {
            // Zorunlu araç şemasına rağmen bozuk JSON gelebiliyor.
            // GEÇİCİ: ikinci deneme genellikle geçerli çıkıyor.
            return Error.Transient("seo.bad_json", ex.Message);
        }

        if (string.IsNullOrWhiteSpace(rawTitle))
        {
            return Error.Transient("seo.no_title", "Model boş başlık döndürdü.");
        }

        var title = PlatformLimits.TrimTitle(rawTitle);

        // ***BÖLÜM İŞARETLERİ AÇIKLAMAYA EKLENİYOR (P3-04).***
        //
        // `ChapterMarkers` yazılmış, testlenmişti ve HİÇBİR NODE
        // ÇAĞIRMIYORDU: uzun videolarda bölüm planı hesaplanıyor,
        // timeline'da kesim olarak kullanılıyor, ama "00:00 Giriş"
        // satırları video AÇIKLAMASINA hiç yazılmıyordu. YouTube bölüm
        // işaretlerini YALNIZCA açıklamadan okuyor — oynatıcıda hiçbir
        // bölüm görünmüyordu ve bu hata vermiyor, yalnızca hiç
        // çıkmıyor.
        //
        // KIRPMADAN ÖNCE EKLENİYOR: sonra eklenseydi işaretler
        // açıklamayı sınırın üstüne taşıyabilir ve `Violations`
        // kontrolü node'u düşürürdü. Önce eklendiğinde kırpma
        // gerekirse metnin SONU gidiyor — yani işaretler değil,
        // serbest metin.
        var markers = runContext is { } chapters ? MarkerText(chapters) : null;

        // SATIR SONU AÇIKÇA `\n`, `Environment.NewLine` DEĞİL.
        //
        // Windows'ta `\r\n` üretirdi ve aynı girdi iki platformda iki
        // farklı açıklama verirdi: geliştirici makinesinde geçen bir
        // test CI'da düşerdi — ya da tersi, ki daha kötü. Açıklama
        // metni bir veri ve verinin biçimi makineye göre değişmemeli.
        var description = PlatformLimits.TrimDescription(
            markers is null ? rawDescription : rawDescription.TrimEnd() + "\n\n" + markers);

        var tags = PlatformLimits.TrimTags(rawTags);

        // Kırpmanın KENDİSİ denetleniyor. Bir hata yüzünden sınırı hâlâ
        // aşan bir metin üretirsek bunu upload sırasında değil burada
        // görmek istiyoruz.
        var violations = PlatformLimits.Violations(title, description, tags);

        if (violations.Count > 0)
        {
            return Error.Permanent(
                "seo.limits_violated",
                "Kirpma sonrasi sinir ihlali kaldi: " + string.Join("; ", violations));
        }

        return Result.Success(NodeJson.From(new
        {
            title,
            description,
            tags,
            prompt = promptStamp,
            // KOL ÇIKTIYA YAZILIYOR: hangi başlığın hangi stille
            // üretildiği sonradan atama tablosuna bakmadan görülüyor
            // ve iki kayıt birbirini denetliyor.
            title_style = style,
            // Kırpma DEVREYE GİRDİ Mİ — kayda geçiyor. Sürekli kırpılan
            // bir kanal, istemin sınırı yeterince baskılamadığını
            // söylüyor ve bu istem sürümüyle düzeltilecek bir şey.
            title_trimmed = title.Length != rawTitle.Trim().Length,
            tags_dropped = rawTags.Count - tags.Count,
            // İŞARET EKLENDİ Mİ — KAYDA GEÇİYOR. Yazılmasaydı, bölüm
            // listesi olmayan bir uzun videonun sebebi (plan yok mu,
            // kurallar mı tutmadı, yoksa hiç mi denenmedi) hiçbir yerde
            // olmazdı.
            chapter_markers = markers is not null,
        }));
    }

    /// Bölüm işaret satırlarını run bağlamından okur.
    ///
    /// ***İŞARETLER BURADA HESAPLANMIYOR, OKUNUYOR.*** Hesap
    /// `timeline.compile` içinde yapılıyor çünkü gerçek sahne sınırları
    /// yalnızca orada var. Burada yeniden hesaplansaydı, plandaki
    /// hedef sürelerden türetilirdi ve işaretler videonun gerçek
    /// geçişlerinden saniyeler saparadı — izleyici bir bölüme atlayıp
    /// önceki bölümün son cümlesini dinlerdi.
    ///
    /// KISA VİDEODA `null` DÖNÜYOR: Shorts'ta bölüm yok ve açıklamaya
    /// boş bir blok eklemek anlamsız.
    internal static string? MarkerText(JsonElement runContext)
    {
        if (!runContext.TryGetProperty("timeline", out var timeline)
            || timeline.ValueKind != JsonValueKind.Object
            || !timeline.TryGetProperty("chapter_markers", out var markers)
            || markers.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var lines = new List<string>();

        foreach (var marker in markers.EnumerateArray())
        {
            if (marker.ValueKind != JsonValueKind.Object
                || !marker.TryGetProperty("start_ms", out var start)
                || !start.TryGetInt32(out var startMs)
                || NodeJson.Text(marker, "title") is not { Length: > 0 } title)
            {
                continue;
            }

            lines.Add(ChapterMarkers.Timestamp(new Core.Time.Ms(startMs)) + " " + title);
        }

        // ***ÜÇTEN AZ İŞARET LİSTEYİ GEÇERSİZ KILIYOR*** (YouTube
        // kuralı). Üretici taraf zaten doğruluyor; burada ikinci kez
        // bakılmasının sebebi, bu metodun run bağlamına doğrudan
        // bakması: bağlamı elle düzenleyen bir test ya da hedefli
        // retry, geçersiz bir liste bırakabilir.
        return lines.Count >= ChapterMarkers.MinimumMarkers
            ? string.Join('\n', lines)
            : null;
    }

    /// Senaryo cümlelerini isteme girecek metne çevirir.
    private static string? ScriptText(JsonElement runContext)
    {
        if (!runContext.TryGetProperty("script", out var script)
            || !script.TryGetProperty("sentences", out var sentences)
            || sentences.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var builder = new StringBuilder();

        foreach (var sentence in sentences.EnumerateArray())
        {
            if (sentence.GetString() is { Length: > 0 } text)
            {
                builder.AppendLine(text);
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }
}
