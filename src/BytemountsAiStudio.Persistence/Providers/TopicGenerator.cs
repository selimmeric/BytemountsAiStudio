using System.Globalization;
using System.Text.Json;
using BytemountsAiStudio.Contracts.Prompts;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Persistence.Providers;

/// Bir doldurma turunun sonucu (P2-01).
public sealed record RefillResult(int Accepted, int Held, int Rejected, IReadOnlyList<string> Notes)
{
    public int Total => Accepted + Held + Rejected;

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture,
            $"{Accepted} kabul, {Held} beklemede, {Rejected} red");
}

/// Konu havuzunu dolduran üretici (P2-01'in kalan yarısı).
///
/// HAVUZ KENDİ KENDİNE DOLMUYORDU. Karar (`TopicPoolPolicy`) ve kabul
/// (`TopicPool.AdmitAsync`) yazılmıştı; aradaki halka — konuları
/// gerçekten ÜRETEN adım — yoktu. Yani "eşiğin altına düştü"
/// kararı veriliyor ve hiçbir şey olmuyordu. Gece boyu çalışması
/// beklenen bir sistemde havuz bir kez boşalınca sabaha kadar boş
/// kalırdı.
///
/// KABUL KARARI BURADA DEĞİL: skor eşiği, risk vetosu ve tekillik
/// `TopicPool.AdmitAsync` içinde. Üretici yalnızca aday getiriyor.
/// Aynı kararı iki yerde vermek, birinin diğerinden habersiz
/// değişmesi demekti.
public sealed class TopicGenerator(
    StudioDbContext db,
    TopicPool pool,
    ILlmProvider llm,
    PromptRegistry? prompts = null)
{
    /// Modelden istenen şema.
    ///
    /// ZORUNLU ARAÇ: serbest metin isteyip ayrıştırmak, modelin
    /// "işte beş konu:" diye başlaması ve ilk konunun başlığına o
    /// cümlenin karışması demek.
    private static readonly ToolSchema Schema = new(
        "emit_topics",
        "Aday konular ve skorlari",
        """
        {"type":"object","properties":{
          "topics":{"type":"array","items":{"type":"object","properties":{
            "title":{"type":"string"},
            "angle":{"type":"string"},
            "demand":{"type":"integer"},
            "fit":{"type":"integer"},
            "sourceability":{"type":"integer"},
            "visualizability":{"type":"integer"},
            "freshness":{"type":"integer"},
            "risk":{"type":"integer"},
            "rationale":{"type":"string"}
          },"required":["title","demand","fit","sourceability","visualizability","freshness","risk"]}}
        },"required":["topics"]}
        """);

    /// Kaçınma listesine kaç başlık giriyor.
    ///
    /// YİRMİ: liste uzadıkça istem büyüyor ve küçük bir yerel model
    /// uzun listeyi zaten dikkate almıyor. Yirmi başlık, son birkaç
    /// günün üretimini kapsıyor — tekrar riski en yüksek aralık bu.
    public const int AvoidLimit = 20;

    public async Task<Result<RefillResult>> RefillAsync(
        Channel channel, int count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);

        if (count <= 0)
        {
            return Result.Success(new RefillResult(0, 0, 0, ["istenen konu sayısı sıfır"]));
        }

        var avoid = await RecentTitlesAsync(channel.Id, cancellationToken).ConfigureAwait(false);

        var rendered = Render(channel, count, avoid);

        if (rendered.IsFailure)
        {
            return Result.Failure<RefillResult>(rendered.Error);
        }

        var response = await llm.CompleteAsync(
            new LlmRequest
            {
                Tier = ModelTier.Standard,

                // SICAKLIK YÜKSEK (0.9) ve bu bilinçli.
                //
                // Şema doldurma işlerinde düşük tutuluyor ama burada
                // istenen şey ÇEŞİTLİLİK: düşük sıcaklıkta aynı kanal
                // için her gece neredeyse aynı konu listesi geliyor ve
                // hepsi tekillik kontrolünde eleniyor — havuz dolmuyor
                // ama LLM parası harcanıyor.
                Temperature = 0.9,
                Messages =
                [
                    new(ChatRole.System, rendered.Value.System ?? string.Empty),
                    new(ChatRole.User, rendered.Value.User),
                ],
                ForcedTool = Schema,
            },
            new ProviderContext
            {
                IdempotencyKey = $"topic.generate:{channel.Id}:{DateTimeOffset.UtcNow:yyyyMMddHHmm}",
                CorrelationId = $"refill:{channel.Id}",
                Language = LanguageTag.Create(channel.Language),
            },
            cancellationToken).ConfigureAwait(false);

        if (response.IsFailure)
        {
            // Model erişilemezse bu bir KAYNAK sorunu: Ollama kapalı
            // olabilir ve birazdan açılabilir. Kalıcı saymak, o gece
            // bir daha hiç denememek olurdu.
            return Result.Failure<RefillResult>(response.Error);
        }

        var candidates = Parse(response.Value.Value.ToolArguments);

        if (candidates.Count == 0)
        {
            // BOŞ CEVAP SESSİZCE GEÇMİYOR. Geçseydi havuz boş kalır,
            // hiçbir hata görünmez ve "neden video üretilmedi"
            // sorusunun cevabı hiçbir yerde yazmazdı.
            return Error.Transient("topic.no_candidates",
                "Model hiç aday konu üretmedi.");
        }

        return Result.Success(
            await AdmitAllAsync(channel, candidates, cancellationToken).ConfigureAwait(false));
    }

    private async Task<RefillResult> AdmitAllAsync(
        Channel channel, IReadOnlyList<Candidate> candidates, CancellationToken cancellationToken)
    {
        int accepted = 0, held = 0, rejected = 0;
        var notes = new List<string>();

        foreach (var candidate in candidates)
        {
            var embedding = await EmbedAsync(candidate.Title, cancellationToken).ConfigureAwait(false);

            if (embedding is null)
            {
                // GÖMME OLMADAN DA HAVUZA ALINIYOR ama not düşülüyor.
                //
                // Almamak, embedding modeli yokken havuzun hiç
                // dolmaması demekti. Bedeli şu: tekillik kontrolü o
                // konu için çalışmıyor — kayıtsız bırakılırsa daha
                // sonra "bu tekrar nasıl geçti" sorusu cevapsız kalır.
                notes.Add($"'{candidate.Title}' gömme olmadan alındı; tekillik kontrol edilmedi");
            }

            var decision = await pool.AdmitAsync(
                channel.Id, channel.Language, candidate.Title, candidate.Score,
                embedding, cancellationToken).ConfigureAwait(false);

            if (decision.IsFailure)
            {
                notes.Add($"'{candidate.Title}' alınamadı: {decision.Error.Message}");
                continue;
            }

            switch (decision.Value)
            {
                case TopicDecision.Accept:
                    accepted++;
                    break;
                case TopicDecision.Hold:
                    held++;
                    break;
                default:
                    rejected++;
                    break;
            }
        }

        if (accepted == 0)
        {
            // HİÇBİRİ KABUL EDİLMEDİYSE BU BİR BULGU.
            //
            // Üretim çalıştı, para harcandı, havuz yine boş. Sebebi
            // ya eşik çok yüksek ya model konuyu anlamıyor — ikisi de
            // görünmesi gereken şeyler.
            notes.Add($"{candidates.Count} adayın hiçbiri kabul edilmedi; eşik ya da istem gözden geçirilmeli");
        }

        return new RefillResult(accepted, held, rejected, notes);
    }

    private async Task<IReadOnlyList<float>?> EmbedAsync(string title, CancellationToken cancellationToken)
    {
        var result = await llm.EmbedAsync(title,
            new ProviderContext
            {
                IdempotencyKey = $"topic.embed:{title}",
                CorrelationId = "refill",
            },
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess ? result.Value.Value : null;
    }

    /// Son üretilen başlıklar — modele "bunları tekrarlama" demek için.
    ///
    /// Havuzdaki VE üretilmiş olanların hepsi: yalnızca yayınlananlara
    /// bakmak, henüz yayınlanmamış ama kuyrukta bekleyen bir konunun
    /// ikinci kez üretilmesi demekti.
    private async Task<IReadOnlyList<string>> RecentTitlesAsync(
        Guid channelId, CancellationToken cancellationToken)
        => await db.Topics.AsNoTracking()
            .Where(t => t.ChannelId == channelId && t.State != TopicState.Rejected)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => t.Title)
            .Take(AvoidLimit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    private Result<RenderedPrompt> Render(Channel channel, int count, IReadOnlyList<string> avoid)
    {
        var registry = prompts is not null ? Result.Success(prompts) : PromptRegistry.Embedded;

        if (registry.IsFailure)
        {
            return Result.Failure<RenderedPrompt>(registry.Error);
        }

        // SÜRÜM YOK: konu üretimi bir run'ın İÇİNDE değil, ÖNCESİNDE
        // çalışıyor. Ortada henüz run yok, dolayısıyla deney ataması da
        // yok. İstem deneyi konu üretimine ulaşmak isterse ayrı bir
        // mekanizma gerekir; şimdi olmayan bir bağı varmış gibi
        // göstermiyoruz.
        var template = registry.Value.Get("topic.generate", version: null);

        if (template.IsFailure)
        {
            return Result.Failure<RenderedPrompt>(template.Error);
        }

        return template.Value.Render(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["channel"] = channel.Name,
            ["count"] = count.ToString(CultureInfo.InvariantCulture),
            ["language"] = channel.Language,
            ["avoid"] = avoid.Count == 0
                ? string.Empty
                : "Kaçınılacak konular: " + string.Join(", ", avoid),
        });
    }

    internal sealed record Candidate(string Title, string? Angle, TopicScore Score);

    /// Model çıktısını ayrıştırır. `internal`: LLM olmadan sınanabilsin.
    internal static IReadOnlyList<Candidate> Parse(string? toolArguments)
    {
        if (string.IsNullOrWhiteSpace(toolArguments))
        {
            return [];
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(toolArguments);
        }
        catch (JsonException)
        {
            return [];
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("topics", out var array)
                || array.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var candidates = new List<Candidate>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var title = Text(item, "title")?.Trim();

                if (string.IsNullOrWhiteSpace(title) || !seen.Add(title))
                {
                    // AYNI BAŞLIK İKİ KEZ GELİRSE İKİNCİSİ DÜŞÜYOR.
                    // Model bunu yapıyor ve ikisini de havuza almak,
                    // "beş konu ürettim" derken üçünün aynı olması
                    // demekti.
                    continue;
                }

                candidates.Add(new Candidate(title, Text(item, "angle"), new TopicScore
                {
                    Demand = Int(item, "demand"),
                    Fit = Int(item, "fit"),
                    Sourceability = Int(item, "sourceability"),
                    Visualizability = Int(item, "visualizability"),
                    Freshness = Int(item, "freshness"),
                    Risk = Int(item, "risk"),
                    Rationale = Text(item, "rationale"),
                }));
            }

            return candidates;
        }
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// Eksik ya da sayı olmayan boyut -1 dönüyor, 0 değil.
    ///
    /// Sıfır GEÇERLİ bir skor ("bu konuya hiç talep yok"); eksik bir
    /// alanı sıfır saymak, modelin cevaplamadığı boyutu cevaplanmış
    /// gibi göstermek olurdu. -1 `TopicScore.IsValid` tarafından
    /// yakalanıyor ve aday reddediliyor.
    private static int Int(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var parsed)
            ? parsed
            : -1;
}
