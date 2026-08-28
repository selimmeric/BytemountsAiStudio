using System.Text.Json;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Assets;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Media.Timeline;
using BytemountsAiStudio.Quality;
using BytemountsAiStudio.Workflow.Engine;

namespace BytemountsAiStudio.Nodes;

/// Semantik kalite kontrolü node'u (P2-06).
///
/// GÖRME MODELİ OLMADAN DA KOŞUYOR ve bu bir taviz değil, tasarımın
/// kendisi: model yokken kontroller "ölçülemedi" diye DÜŞÜYOR, "geçti"
/// demiyor. Skoru düşen video onay kapısından insana gidiyor. Tersi —
/// model yokken sessizce geçmek — kalite kontrolünün hiç koşmadığı bir
/// sistemde her videonun tam puan alması demekti.
///
/// Bu ayrım şu an teorik değil: ana makinenin ekran kartı model
/// yüklenince sistemi çökertiyor (bkz. `docs/DONANIM-VE-MODEL.md`), yani
/// "model yok" hâli üretimde gerçekten yaşanan hâl. Sağlayıcı
/// arayüzünün arkasında olduğu için yerine dışarıdan bir API ya da
/// başka bir ücretsiz servis takılabiliyor — QC mantığı hangisi
/// olduğunu bilmiyor.
public sealed class SemanticQualityHandler(
    IStorageProvider storage,
    IVisionProvider? vision = null,
    ILlmProvider? judge = null) : INodeHandler
{
    public string NodeType => "qc.semantic";

    /// Görme modeli hattın en yavaş adımı; LLM kuyruğunda.
    public QueueClass Queue => QueueClass.Llm;

    public async Task<Result<JsonElement>> ExecuteAsync(NodeContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var timeline = await LoadTimelineAsync(context.RunContext, cancellationToken).ConfigureAwait(false);

        if (timeline.IsFailure)
        {
            return Result.Failure<JsonElement>(timeline.Error);
        }

        var sampled = SemanticQc.SampleIndices(timeline.Value.Scenes.Count);

        var relevance = await JudgeScenesAsync(
            timeline.Value, sampled, context, cancellationToken).ConfigureAwait(false);

        var judgement = await JudgeTextAsync(context, cancellationToken).ConfigureAwait(false);

        var checks = SemanticQc.Evaluate(relevance, judgement);

        var report = new QualityReport { Checks = checks };

        return Result.Success(NodeJson.From(new
        {
            score = report.Score / 100.0,
            score_100 = report.Score,
            blocking_failure = report.HasBlockingFailure,
            retry_target = report.Target.ToString(),
            // KAÇ SAHNENİN ÖLÇÜLDÜĞÜ YAZILIYOR.
            //
            // Örnekleme yapıldığı için "hepsi kontrol edildi"
            // izlenimi doğmamalı: altı sahne ölçülmüş yirmi sahnelik
            // bir videoda kalan on dördü kimse görmedi ve bu bilgi
            // triyaj eden insanın hakkı.
            sampled_scenes = sampled,
            total_scenes = timeline.Value.Scenes.Count,
            vision_available = vision is not null,
            judge_available = judge is not null,
            scenes = relevance.Select(r => new
            {
                index = r.SceneIndex,
                score = r.Score,
                measured = r.Measured,
                reason = r.Reason,
            }),
            checks = checks.Select(c => new
            {
                code = c.Code,
                name = c.Name,
                passed = c.Passed,
                severity = c.Severity.ToString(),
                weight = c.Weight,
                detail = c.Detail,
                target = c.Target.ToString(),
            }),
        }));
    }

    private async Task<IReadOnlyList<VisualRelevance>> JudgeScenesAsync(
        TimelineDocument timeline,
        IReadOnlyList<int> sampled,
        NodeContext context,
        CancellationToken cancellationToken)
    {
        var results = new List<VisualRelevance>();
        var available = vision is not null;

        foreach (var index in sampled)
        {
            var scene = timeline.Scenes[index];

            if (!available)
            {
                results.Add(new VisualRelevance(index, null, "görme modeli yok"));
                continue;
            }

            var image = await ReadAssetAsync(scene.Visual.Asset, cancellationToken).ConfigureAwait(false);

            if (image is null)
            {
                results.Add(new VisualRelevance(index, null, "görsel okunamadı"));
                continue;
            }

            var verdict = await vision!.JudgeAsync(
                new VisionQuery
                {
                    Image = image.Value,
                    Sentence = SentenceOf(timeline, scene),
                    Language = timeline.Language,
                },
                ScriptGenerateHandler.Context(context),
                cancellationToken).ConfigureAwait(false);

            if (verdict.IsFailure)
            {
                // BİR SAHNENİN ÖLÇÜLEMEMESİ DİĞERLERİNİ DURDURMUYOR
                // ama ölçülemediği kaydediliyor: model yarı yolda
                // düşerse elimizde yarım bir ölçüm kalıyor ve o
                // yarımın yarım olduğu görünmeli.
                results.Add(new VisualRelevance(index, null, verdict.Error.Message));

                // Model art arda düşüyorsa kalan sahneleri denemenin
                // anlamı yok: aynı hatayı sahne sayısı kadar tekrar
                // eder ve her biri bir çağrı süresi harcardı.
                if (verdict.Error.Kind is ErrorKind.Resource or ErrorKind.Permanent)
                {
                    available = false;
                }

                continue;
            }

            results.Add(new VisualRelevance(
                index, verdict.Value.Value.Relevance, verdict.Value.Value.Reason));
        }

        return results;
    }

    /// Metin üzerinden yargılar.
    ///
    /// Model yoksa hepsi `null` — yani "ölçülemedi". Varsayılan olarak
    /// `true` dönmek, politika kontrolünün hiç koşmadığı bir sistemde
    /// her videoyu "politika riski yok" diye işaretlemek olurdu.
    private async Task<SemanticJudgement> JudgeTextAsync(
        NodeContext context, CancellationToken cancellationToken)
    {
        if (judge is null)
        {
            return new SemanticJudgement();
        }

        var title = NodeJson.Text(context.RunContext, "seo.title");
        var sentences = SentencesOf(context.RunContext);

        if (string.IsNullOrWhiteSpace(title) || sentences.Count == 0)
        {
            return new SemanticJudgement
            {
                Rationale = "başlık ya da senaryo yok; metin yargıları ölçülemedi",
            };
        }

        var response = await judge.CompleteAsync(
            new LlmRequest
            {
                Tier = ModelTier.Standard,
                // Yargı işi: kararlılık isteniyor. Aynı videoya iki
                // koşuda farklı karar vermek, eşiği ayarlamayı
                // imkânsız kılardı.
                Temperature = 0,
                Messages =
                [
                    new(ChatRole.System,
                        "Sen bir yayın editörüsün. Verilen başlık ve senaryoyu değerlendiriyorsun. "
                        + "Yanıltıcı başlık, uygunsuz ton ve politika riski arıyorsun. "
                        + "Emin değilsen RİSK VAR de: gözden kaçan bir ihlal, gereksiz bir insan "
                        + "incelemesinden pahalı."),
                    new(ChatRole.User,
                        $"Başlık: {title}\n\nSenaryo:\n{string.Join("\n", sentences)}"),
                ],
                ForcedTool = JudgeSchema,
            },
            ScriptGenerateHandler.Context(context),
            cancellationToken).ConfigureAwait(false);

        return response.IsFailure
            ? new SemanticJudgement { Rationale = $"model yanıt vermedi: {response.Error.Message}" }
            : ParseJudgement(response.Value.Value.ToolArguments);
    }

    private static readonly ToolSchema JudgeSchema = new(
        "emit_judgement",
        "Baslik, ton ve politika yargisi",
        """
        {"type":"object","properties":{
          "title_matches_content":{"type":"boolean"},
          "tone_appropriate":{"type":"boolean"},
          "policy_safe":{"type":"boolean"},
          "rationale":{"type":"string"}
        },"required":["title_matches_content","tone_appropriate","policy_safe"]}
        """);

    /// Model çıktısını okur. `internal`: model olmadan sınanabilsin.
    ///
    /// EKSİK ALAN `null` KALIYOR, `false` değil: cevaplanmamış bir soru
    /// "hayır" değil "bilmiyoruz" demek ve ikisi farklı kontrol sonucu
    /// üretiyor — biri düşen bir kontrol, diğeri ölçülememiş bir
    /// kontrol.
    internal static SemanticJudgement ParseJudgement(string? toolArguments)
    {
        if (string.IsNullOrWhiteSpace(toolArguments))
        {
            return new SemanticJudgement { Rationale = "model boş yanıt verdi" };
        }

        try
        {
            using var document = JsonDocument.Parse(toolArguments);
            var root = document.RootElement;

            return new SemanticJudgement
            {
                TitleMatchesContent = Bool(root, "title_matches_content"),
                ToneAppropriate = Bool(root, "tone_appropriate"),
                PolicySafe = Bool(root, "policy_safe"),
                Rationale = root.TryGetProperty("rationale", out var rationale)
                            && rationale.ValueKind == JsonValueKind.String
                    ? rationale.GetString()
                    : null,
            };
        }
        catch (JsonException ex)
        {
            return new SemanticJudgement { Rationale = $"model yanıtı okunamadı: {ex.Message}" };
        }
    }

    private static bool? Bool(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
           && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.ValueKind == JsonValueKind.True
            : null;

    /// Sahnenin altında duyulan cümle.
    ///
    /// Sahne birden çok ses parçası kapsıyorsa hepsi birleştiriliyor:
    /// modele yalnızca ilkini vermek, "bu görsel bu cümleyle ilgisiz"
    /// yargısını eksik bilgiyle aldırmak olurdu.
    internal static string SentenceOf(TimelineDocument timeline, Scene scene)
    {
        var texts = timeline.Audio.VoiceSegments
            .Where(v => scene.VoiceSegmentIds.Contains(v.Id, StringComparer.Ordinal))
            .Select(v => v.SpeechText)
            .Where(t => !string.IsNullOrWhiteSpace(t));

        var joined = string.Join(" ", texts);

        return string.IsNullOrWhiteSpace(joined) ? "(bu sahnede konuşma yok)" : joined;
    }

    private static IReadOnlyList<string> SentencesOf(JsonElement runContext)
    {
        if (!runContext.TryGetProperty("script", out var script)
            || script.ValueKind != JsonValueKind.Object
            || !script.TryGetProperty("sentences", out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. array.EnumerateArray()
            .Where(s => s.ValueKind == JsonValueKind.String)
            .Select(s => s.GetString()!)
            .Where(s => !string.IsNullOrWhiteSpace(s))];
    }

    private async Task<ReadOnlyMemory<byte>?> ReadAssetAsync(
        AssetRef asset, CancellationToken cancellationToken)
    {
        var opened = await storage.OpenAsync(asset, cancellationToken).ConfigureAwait(false);

        if (opened.IsFailure)
        {
            return null;
        }

        using var buffer = new MemoryStream();

        await using (var stream = opened.Value)
        {
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        return buffer.ToArray();
    }

    private async Task<Result<TimelineDocument>> LoadTimelineAsync(
        JsonElement runContext, CancellationToken cancellationToken)
    {
        var reference = NodeJson.Text(runContext, "timeline.timeline_asset");

        if (string.IsNullOrWhiteSpace(reference))
        {
            return Error.Permanent("qc.no_timeline",
                "Timeline bulunamadı; semantik QC'den önce `timeline.compile` koşmalı.");
        }

        var asset = AssetRef.TryCreate(reference);

        if (asset.IsFailure)
        {
            return Result.Failure<TimelineDocument>(asset.Error);
        }

        var opened = await storage.OpenAsync(asset.Value, cancellationToken).ConfigureAwait(false);

        if (opened.IsFailure)
        {
            return Result.Failure<TimelineDocument>(opened.Error);
        }

        string json;

        await using (var stream = opened.Value)
        using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8))
        {
            json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        var parsed = TimelineJson.Deserialize(json);

        return parsed is null
            ? Error.Permanent("qc.bad_timeline", "Timeline belgesi okunamadı.")
            : Result.Success(parsed);
    }
}
