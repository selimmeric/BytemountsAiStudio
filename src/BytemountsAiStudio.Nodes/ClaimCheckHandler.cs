using System.Globalization;
using System.Text;
using System.Text.Json;
using BytemountsAiStudio.Contracts.Prompts;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Workflow.Engine;

namespace BytemountsAiStudio.Nodes;

/// İddia çıkarma ve kaynak doğrulama (P1-10, §2.2/8).
///
/// İKİ AYRI ÇAĞRI, ve ayrılığı bu işin bel kemiği:
///   1. ÇIKARIM — senaryodan atomik olgu iddiaları
///   2. DOĞRULAMA — her iddia kaynak metince destekleniyor mu
///
/// Tek çağrıda yapmak cazip ve yanlış: aynı model hem iddiayı üretip
/// hem kendi ürettiğini onaylıyor, ve modeller kendi çıktılarını
/// onaylamaya eğilimli. İki çağrıda ikinci model iddiayı METİN olarak
/// görüyor, kendi ürettiği bir şey olarak değil.
///
/// Mimari "farklı model ailesi" diyor. Şu an tek yerel model var, o
/// yüzden ayrım İSTEM ve SICAKLIK düzeyinde: doğrulama istemi modele
/// "kendi bilgini kullanma" diyor ve sıcaklık sıfıra yakın. Anahtar
/// geldiğinde doğrulayıcıyı başka bir aileye almak tek satır — bu
/// yüzden ayrı bir sağlayıcı parametresi var.
public sealed class ClaimCheckHandler(
    ILlmProvider extractor,
    ILlmProvider? verifier = null,
    PromptRegistry? prompts = null) : INodeHandler
{
    /// Doğrulayıcı verilmezse çıkarıcı kullanılıyor. İdeal değil ve
    /// çıktıda işaretleniyor: "aynı model kendi iddiasını onayladı"
    /// bilgisi, sonuca ne kadar güvenileceğini söylüyor.
    private readonly ILlmProvider _verifier = verifier ?? extractor;

    private readonly bool _sameModel = verifier is null;

    /// Doğrulama iddia başına bir çağrı; senaryo uzunsa bu pahalı.
    /// Sınır konuyor: bir kısa videoda bundan fazla olgu iddiası varsa
    /// senaryo zaten fazla yoğun demektir.
    private const int MaxClaims = 12;

    private static readonly ToolSchema ExtractSchema = new(
        "emit_claims",
        "Senaryodan cikarilan olgu iddialari",
        """
        {"type":"object","properties":{
          "claims":{"type":"array","items":{"type":"object","properties":{
            "text":{"type":"string"},
            "sentence_index":{"type":"integer"}
          },"required":["text","sentence_index"]}}
        },"required":["claims"]}
        """);

    private static readonly ToolSchema VerifySchema = new(
        "emit_verdict",
        "Iddianin kaynak karsisindaki durumu",
        """
        {"type":"object","properties":{
          "verdict":{"type":"string","enum":["supported","unsupported","contradicted"]},
          "reason":{"type":"string"}
        },"required":["verdict","reason"]}
        """);

    public string NodeType => "claim.check";

    public QueueClass Queue => QueueClass.Llm;

    public async Task<Result<JsonElement>> ExecuteAsync(NodeContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var sentences = Sentences(context.RunContext);

        if (sentences.Count == 0)
        {
            return Error.Permanent("claim.no_script", "Senaryo bulunamadı.");
        }

        var sources = Sources(context.RunContext);

        if (sources.Count == 0)
        {
            // Kaynak yoksa DOĞRULAMA YAPILAMAZ. "Hepsi desteksiz" demek
            // teknik olarak doğru ama yanıltıcı: sorun senaryoda değil,
            // araştırmada. Ayrı bir hata kodu bunu söylüyor.
            return Error.Permanent("claim.no_sources",
                "Araştırma kaynağı yok; iddialar doğrulanamaz.");
        }

        var registry = prompts is not null ? Result.Success(prompts) : PromptRegistry.Embedded;

        if (registry.IsFailure)
        {
            return Result.Failure<JsonElement>(registry.Error);
        }

        var extracted = await ExtractAsync(registry.Value, sentences, context, cancellationToken)
            .ConfigureAwait(false);

        if (extracted.IsFailure)
        {
            return Result.Failure<JsonElement>(extracted.Error);
        }

        var claims = new List<Claim>(extracted.Value.Count);

        foreach (var claim in extracted.Value.Take(MaxClaims))
        {
            var verdict = await VerifyAsync(registry.Value, claim, sources, context, cancellationToken)
                .ConfigureAwait(false);

            if (verdict.IsFailure)
            {
                // Tek bir doğrulamanın düşmesi node'u düşürmüyor: iddia
                // DESTEKSİZ sayılıyor ve gerekçesi yazılıyor. Düşürmek,
                // geçici bir model hatasında bütün senaryoyu çöpe atmak
                // olurdu.
                claims.Add(claim with
                {
                    Verdict = ClaimVerdict.Unsupported,
                    Reason = $"Dogrulama yapilamadi: {verdict.Error.Message}",
                });

                continue;
            }

            claims.Add(verdict.Value);
        }

        var report = new ClaimReport { Claims = claims };

        return Result.Success(NodeJson.From(new
        {
            claims = claims.Select(c => new
            {
                text = c.Text,
                sentence = c.SentenceIndex,
                verdict = c.Verdict.ToString().ToLowerInvariant(),
                source = c.SourceUrl,
                reason = c.Reason,
            }),
            total = report.Total,
            supported = report.Supported,
            unsupported = report.Unsupported,
            contradicted = report.Contradicted,
            all_sourced = report.AllSourced,
            has_contradiction = report.HasContradiction,
            problem_sentences = report.ProblemSentences,
            // Doğrulamanın ÇIKARIMLA aynı modelden gelip gelmediği.
            // Aynıysa sonuç iyimser olma eğiliminde ve bunu bilmek
            // gerekiyor.
            same_model = _sameModel,
        }));
    }

    private async Task<Result<IReadOnlyList<Claim>>> ExtractAsync(
        PromptRegistry registry,
        List<string> sentences,
        NodeContext context,
        CancellationToken cancellationToken)
    {
        var template = registry.Get("claim.extract");

        if (template.IsFailure)
        {
            return Result.Failure<IReadOnlyList<Claim>>(template.Error);
        }

        // Cümleler NUMARALI veriliyor: model hangi cümleden çıkardığını
        // söyleyebilsin. Numarasız verirsek indeksi tahmin ediyor ve
        // hedefli düzeltme (P2-07) yanlış cümleye gidiyor.
        var numbered = new StringBuilder();

        for (var i = 0; i < sentences.Count; i++)
        {
            numbered.Append('[').Append(i.ToString(CultureInfo.InvariantCulture)).Append("] ")
                .AppendLine(sentences[i]);
        }

        var rendered = template.Value.Render(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["script"] = numbered.ToString(),
        });

        if (rendered.IsFailure)
        {
            return Result.Failure<IReadOnlyList<Claim>>(rendered.Error);
        }

        var response = await extractor.CompleteAsync(
            new LlmRequest
            {
                Tier = ModelTier.Cheap,
                // Çıkarım yaratıcı bir iş değil: aynı senaryo aynı
                // iddiaları vermeli.
                Temperature = 0.0,
                Messages =
                [
                    new(ChatRole.System, rendered.Value.System ?? string.Empty),
                    new(ChatRole.User, rendered.Value.User),
                ],
                ForcedTool = ExtractSchema,
            },
            ScriptGenerateHandler.Context(context),
            cancellationToken).ConfigureAwait(false);

        return response.IsFailure
            ? Result.Failure<IReadOnlyList<Claim>>(response.Error)
            : ParseClaims(response.Value.Value.ToolArguments, sentences.Count);
    }

    private async Task<Result<Claim>> VerifyAsync(
        PromptRegistry registry,
        Claim claim,
        List<(string Url, string Text)> sources,
        NodeContext context,
        CancellationToken cancellationToken)
    {
        var template = registry.Get("claim.verify");

        if (template.IsFailure)
        {
            return Result.Failure<Claim>(template.Error);
        }

        // Kaynakların TAMAMI veriliyor, tek tek değil.
        //
        // İddianın hangi kaynakta olduğunu önceden bilmiyoruz; kaynak
        // başına bir çağrı yapmak maliyeti kaynak sayısıyla çarpardı.
        // Modelin hangi kaynağı kullandığını söylemesini istiyoruz.
        var combined = new StringBuilder();

        foreach (var (url, text) in sources)
        {
            combined.AppendLine(CultureInfo.InvariantCulture, $"--- {url}")
                .AppendLine(text)
                .AppendLine();
        }

        var rendered = template.Value.Render(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["claim"] = claim.Text,
            ["source"] = combined.ToString(),
        });

        if (rendered.IsFailure)
        {
            return Result.Failure<Claim>(rendered.Error);
        }

        var response = await _verifier.CompleteAsync(
            new LlmRequest
            {
                Tier = ModelTier.Cheap,
                // Doğrulama kesinlikle yaratıcı olmamalı.
                Temperature = 0.0,
                Messages =
                [
                    new(ChatRole.System, rendered.Value.System ?? string.Empty),
                    new(ChatRole.User, rendered.Value.User),
                ],
                ForcedTool = VerifySchema,
            },
            ScriptGenerateHandler.Context(context),
            cancellationToken).ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result.Failure<Claim>(response.Error);
        }

        return ParseVerdict(claim, response.Value.Value.ToolArguments, sources[0].Url);
    }

    /// Model çıktısını iddia listesine çevirir.
    ///
    /// Ayrı ve `internal`: ayrıştırma mantığı LLM olmadan sınanabilsin.
    internal static Result<IReadOnlyList<Claim>> ParseClaims(string? toolArguments, int sentenceCount)
    {
        if (string.IsNullOrWhiteSpace(toolArguments))
        {
            return Error.Transient("claim.empty", "Model iddia listesi döndürmedi.");
        }

        try
        {
            using var document = JsonDocument.Parse(toolArguments);

            if (!document.RootElement.TryGetProperty("claims", out var array)
                || array.ValueKind != JsonValueKind.Array)
            {
                return Error.Transient("claim.bad_shape", "Beklenen 'claims' dizisi yok.");
            }

            var claims = new List<Claim>();

            foreach (var element in array.EnumerateArray())
            {
                var text = element.TryGetProperty("text", out var t) ? t.GetString() : null;

                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var index = element.TryGetProperty("sentence_index", out var i) && i.TryGetInt32(out var value)
                    ? value
                    : 0;

                claims.Add(new Claim
                {
                    Text = text.Trim(),
                    // Model uydurma bir indeks verebiliyor; sınıra
                    // sıkıştırılıyor. Aksi hâlde hedefli düzeltme var
                    // olmayan bir cümleye giderdi.
                    SentenceIndex = Math.Clamp(index, 0, Math.Max(sentenceCount - 1, 0)),
                });
            }

            return Result.Success<IReadOnlyList<Claim>>(claims);
        }
        catch (JsonException ex)
        {
            return Error.Transient("claim.bad_json", ex.Message);
        }
    }

    internal static Result<Claim> ParseVerdict(Claim claim, string? toolArguments, string fallbackUrl)
    {
        if (string.IsNullOrWhiteSpace(toolArguments))
        {
            return Error.Transient("claim.no_verdict", "Model karar döndürmedi.");
        }

        try
        {
            using var document = JsonDocument.Parse(toolArguments);
            var root = document.RootElement;

            var verdictText = root.TryGetProperty("verdict", out var v) ? v.GetString() : null;
            var reason = root.TryGetProperty("reason", out var r) ? r.GetString() : null;

            // TANINMAYAN karar DESTEKSİZ sayılıyor, desteklenmiş değil.
            //
            // Yön önemli: belirsizlikte iyimser davranmak, doğrulanmamış
            // bir iddianın yayına çıkması demek. Kötümser davranmak
            // yalnızca gereksiz bir düzeltme turu.
            var verdict = verdictText?.Trim().ToLowerInvariant() switch
            {
                "supported" => ClaimVerdict.Supported,
                "contradicted" => ClaimVerdict.Contradicted,
                _ => ClaimVerdict.Unsupported,
            };

            return Result.Success(claim with
            {
                Verdict = verdict,
                Reason = reason,
                SourceUrl = verdict == ClaimVerdict.Unsupported ? null : fallbackUrl,
            });
        }
        catch (JsonException ex)
        {
            return Error.Transient("claim.bad_json", ex.Message);
        }
    }

    private static List<string> Sentences(JsonElement runContext)
    {
        var sentences = new List<string>();

        if (!runContext.TryGetProperty("script", out var script)
            || !script.TryGetProperty("sentences", out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return sentences;
        }

        foreach (var element in array.EnumerateArray())
        {
            if (element.GetString() is { Length: > 0 } text)
            {
                sentences.Add(text);
            }
        }

        return sentences;
    }

    private static List<(string Url, string Text)> Sources(JsonElement runContext)
    {
        var sources = new List<(string, string)>();

        if (!runContext.TryGetProperty("research", out var research)
            || !research.TryGetProperty("sources", out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return sources;
        }

        foreach (var element in array.EnumerateArray())
        {
            var url = element.TryGetProperty("url", out var u) ? u.GetString() : null;
            var excerpt = element.TryGetProperty("excerpt", out var e) ? e.GetString() : null;

            if (!string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(excerpt))
            {
                sources.Add((url, excerpt));
            }
        }

        return sources;
    }
}
