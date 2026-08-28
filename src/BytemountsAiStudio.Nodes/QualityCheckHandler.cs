using System.Text.Json;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Assets;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Media.Timeline;
using BytemountsAiStudio.Quality;
using BytemountsAiStudio.Workflow.Engine;

namespace BytemountsAiStudio.Nodes;

/// Mekanik kalite kontrolü node'u (P1-21'i hatta bağlıyor).
///
/// EKSİK HALKA BUYDU. `MechanicalQc` yazılmış ve otuz testi geçiyordu
/// ama hiçbir node onu çağırmıyordu — yani gerçek bir koşuda QC hiç
/// koşmuyor, skor hiç üretilmiyordu. Onay kapısı skoru run
/// bağlamından okuyor ve bulamadığı için HER videoyu insana
/// soruyordu: seçici onay (P2-08) yazılmıştı ama pratikte hiç
/// devreye giremiyordu.
///
/// KONTROL DÜŞSE DE NODE DÜŞMÜYOR. QC'nin işi karar vermek değil
/// ÖLÇMEK; kararı onay kapısı (P2-08) ve retry planlayıcısı (P2-07)
/// veriyor. Node'u düşürmek, düşük skorlu bir videoyu hiç
/// değerlendirilemeden çöpe atmak olurdu.
public sealed class QualityCheckHandler(IStorageProvider storage) : INodeHandler
{
    public string NodeType => "qc.mechanical";

    /// Ağa çıkmıyor, model çağırmıyor: en hafif sınıf.
    public QueueClass Queue => QueueClass.Search;

    public async Task<Result<JsonElement>> ExecuteAsync(NodeContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var timeline = await LoadTimelineAsync(context.RunContext, cancellationToken).ConfigureAwait(false);

        if (timeline.IsFailure)
        {
            return Result.Failure<JsonElement>(timeline.Error);
        }

        var report = MechanicalQc.Run(new QcInput
        {
            Timeline = timeline.Value,
            Media = MediaFrom(context.RunContext),
            Metadata = MetadataFrom(context.RunContext),
            Claims = ClaimsFrom(context.RunContext),
            Uniqueness = UniquenessFrom(context.RunContext),
        });

        var plan = RetryPlanner.Plan(report, LoopsFrom(context.RunContext));

        return Result.Success(NodeJson.From(new
        {
            // Onay kapısı bu alanı okuyor. 0–100 değil 0–1: eşik
            // yapılandırması kesirle yazılıyor (`min_score: 0.75`) ve
            // iki farklı ölçek, eşiği yanlışlıkla yüz kat düşük ya da
            // yüksek ayarlamanın kolay yolu olurdu.
            score = report.Score / 100.0,
            score_100 = report.Score,
            decision = report.Decision.ToString(),
            blocking_failure = report.HasBlockingFailure,
            retry_target = report.Target.ToString(),
            // Hedefli retry planı: hangi node'lardan yeniden
            // koşulacağı ve baştan koşmaya göre kaç node atlandığı.
            retry = new
            {
                decision = plan.Decision.ToString(),
                loop = plan.Loop,
                reason = plan.Reason,
                nodes = RetryPlanner.NodesFrom(plan.Target),
                saved_nodes = RetryPlanner.Saved(plan.Target),
            },
            // HER kontrol yazılıyor, yalnızca düşenler değil: "bu
            // videoya hangi kontroller uygulandı" sorusunun cevabı
            // ancak tamamı kayıtlıysa var ve ölçülmeyen bir kontrol
            // geçmiş sayılmamalı.
            checks = report.Checks.Select(c => new
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

    /// Timeline'ı depodan okur.
    ///
    /// Run bağlamında yalnızca varlık referansı var, belgenin kendisi
    /// yok: bir timeline 50 KB ve her node'un bağlamında taşımak,
    /// bağlamı her adımda büyütürdü.
    private async Task<Result<TimelineDocument>> LoadTimelineAsync(
        JsonElement runContext, CancellationToken cancellationToken)
    {
        var reference = NodeJson.Text(runContext, "timeline.timeline_asset");

        if (string.IsNullOrWhiteSpace(reference))
        {
            // QC timeline OLMADAN çalışamaz: kontrollerin çoğu ona
            // bakıyor. Kalıcı hata, çünkü yeniden denemek eksik bir
            // node'u tamamlamıyor.
            return Error.Permanent("qc.no_timeline",
                "Timeline bulunamadı; QC'den önce `timeline.compile` koşmalı.");
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

    /// Render ÖLÇÜMLERİ.
    ///
    /// Render koşmadıysa null dönüyor ve render'a bağlı kontroller
    /// "ölçülemedi" olarak düşüyor — "geçti" değil. İkisini eşitlemek,
    /// hiç render edilmemiş bir videoyu tam puanla geçirmek olurdu
    /// (P1-21).
    internal static MediaMeasurements? MediaFrom(JsonElement runContext)
    {
        if (!runContext.TryGetProperty("render", out var render)
            || render.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new MediaMeasurements
        {
            DurationSeconds = Number(render, "duration_seconds") ?? 0,
            Width = (int)(Number(render, "width") ?? 0),
            Height = (int)(Number(render, "height") ?? 0),
            HasAudio = render.TryGetProperty("audio_codec", out var codec)
                       && codec.ValueKind == JsonValueKind.String
                       && !string.IsNullOrWhiteSpace(codec.GetString()),
            LoudnessLufs = Number(render, "loudness_lufs"),
            TruePeakDb = Number(render, "true_peak_db"),
            SpeechRatio = Number(render, "speech_ratio"),
            SizeBytes = (long)(Number(render, "size_bytes") ?? 0),
        };
    }

    internal static Quality.PublishMetadata? MetadataFrom(JsonElement runContext)
    {
        if (!runContext.TryGetProperty("seo", out var seo) || seo.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var tags = new List<string>();

        if (seo.TryGetProperty("tags", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            tags.AddRange(array.EnumerateArray()
                .Where(t => t.ValueKind == JsonValueKind.String)
                .Select(t => t.GetString()!)
                .Where(t => !string.IsNullOrWhiteSpace(t)));
        }

        ThumbnailInfo? thumbnail = null;

        if (runContext.TryGetProperty("thumbnail", out var thumb) && thumb.ValueKind == JsonValueKind.Object)
        {
            thumbnail = new ThumbnailInfo(
                (int)(Number(thumb, "width") ?? 0),
                (int)(Number(thumb, "height") ?? 0),
                (long)(Number(thumb, "size_bytes") ?? 0));
        }

        return new Quality.PublishMetadata
        {
            Title = seo.TryGetProperty("title", out var title) ? title.GetString() ?? string.Empty : string.Empty,
            Description = seo.TryGetProperty("description", out var description)
                ? description.GetString() ?? string.Empty
                : string.Empty,
            Tags = tags,
            Thumbnail = thumbnail,
        };
    }

    internal static ClaimCoverage? ClaimsFrom(JsonElement runContext)
    {
        if (!runContext.TryGetProperty("claims", out var claims) || claims.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var total = Number(claims, "total") ?? Number(claims, "claim_count");
        var sourced = Number(claims, "supported") ?? Number(claims, "sourced");

        return total is null ? null : new ClaimCoverage((int)total.Value, (int)(sourced ?? 0));
    }

    internal static UniquenessCheck? UniquenessFrom(JsonElement runContext)
    {
        if (!runContext.TryGetProperty("topic", out var topic) || topic.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!topic.TryGetProperty("is_unique", out var unique) || unique.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return null;
        }

        return new UniquenessCheck(
            unique.ValueKind == JsonValueKind.True,
            Number(topic, "similarity"),
            topic.TryGetProperty("conflicting_title", out var conflict) ? conflict.GetString() : null);
    }

    /// Kaçıncı düzeltme turundayız.
    ///
    /// Önceki QC çıktısından okunuyor: sayaç bir yerde tutulmazsa
    /// döngü sınırı hiç dolmuyor ve aynı hata sonsuza kadar para
    /// harcayarak tekrarlanıyor.
    internal static int LoopsFrom(JsonElement runContext)
        => runContext.TryGetProperty("qc", out var qc)
           && qc.ValueKind == JsonValueKind.Object
           && qc.TryGetProperty("retry", out var retry)
           && retry.ValueKind == JsonValueKind.Object
            ? (int)(Number(retry, "loop") ?? 0)
            : 0;

    private static double? Number(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetDouble(out var parsed)
            ? parsed
            : null;
}
