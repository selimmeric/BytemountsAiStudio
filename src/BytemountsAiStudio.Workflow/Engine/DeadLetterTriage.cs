using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Persistence;
using BytemountsAiStudio.Persistence.Entities;
using BytemountsAiStudio.Workflow.Definition;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Workflow.Engine;

/// Ölü mektup kuyruğu triyajı (P2-10).
///
/// Kabul kriteri: **takılan run insan müdahalesiyle üç tıkta
/// kurtarılıyor.** Üç eylem, üç farklı soruya cevap veriyor:
///
///   - YENİDEN DENE: "geçici bir arızaydı, artık düzeldi"
///   - NODE'U ATLA:  "bu adım bu koşuda çalışmayacak ama videonun
///                    geri kalanı kurtarılabilir"
///   - RUN'I İPTAL:  "bu video kurtarılamaz"
///
/// İkincisi olmadan seçenek yalnızca "sonsuza kadar dene" ya da "her
/// şeyi çöpe at" olurdu — oysa çoğu takılma tek bir isteğe bağlı
/// adımda oluyor (kapak görseli, müzik) ve o adım olmadan da yayına
/// girebilecek bir video elde kalıyor.
public sealed class DeadLetterTriage(StudioDbContext db, WorkflowEngine engine, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// Düşen işi YENİDEN kuyruğa alır.
    ///
    /// Deneme sayacı SIFIRLANIYOR: iş zaten sınırı doldurduğu için
    /// düştü ve sıfırlamadan yeniden kuyruğa almak, ilk denemede
    /// tekrar düşmesi demekti. İnsan "artık düzeldi" dediğine göre
    /// yeni bir bütçe hak ediyor.
    public async Task<Result> RetryAsync(Guid jobId, string decidedBy, CancellationToken cancellationToken)
    {
        var job = await LoadAsync(jobId, cancellationToken).ConfigureAwait(false);

        if (job.IsFailure)
        {
            return Result.Failure(job.Error);
        }

        job.Value.State = JobState.Pending;
        job.Value.Attempt = 0;
        job.Value.LeasedBy = null;
        job.Value.LeaseExpiresAt = null;
        job.Value.RunAfter = _time.GetUtcNow();

        // RUN DA CANLANIYOR: iş kuyruğa girip run `Failed` kalsaydı,
        // worker o işi alıp hemen atardı (iptal edilmiş run'ın işleri
        // çalıştırılmıyor) ve düğme hiçbir şey yapmamış görünürdü.
        await ReviveRunAsync(job.Value.RunId, decidedBy,
            $"DLQ: iş yeniden kuyruğa alındı ({decidedBy})", cancellationToken).ConfigureAwait(false);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }

    /// Node'u ATLAR ve run'ı sonraki node'lardan sürdürür.
    ///
    /// Atlanan node `Skipped` olarak kaydediliyor, `Succeeded` değil:
    /// "bu adım atlandı" ile "bu adım başarılı oldu" aynı şey değil ve
    /// ikisini eşitlemek, eksik bir videoyu tam sanmak olurdu. QC
    /// zaten eksikliği yakalayacak.
    public async Task<Result> SkipNodeAsync(Guid jobId, string decidedBy, CancellationToken cancellationToken)
    {
        var job = await LoadAsync(jobId, cancellationToken).ConfigureAwait(false);

        if (job.IsFailure)
        {
            return Result.Failure(job.Error);
        }

        if (job.Value.RunId is not { } runId || job.Value.NodeId is not { } nodeId)
        {
            return Error.Permanent("dlq.orphan_job", "İş bir run/node'a bağlı değil; atlanamaz.");
        }

        var run = await db.Runs.FirstOrDefaultAsync(r => r.Id == runId, cancellationToken)
            .ConfigureAwait(false);

        if (run is null)
        {
            return Error.Permanent("dlq.no_run", $"Run bulunamadı: {runId}");
        }

        var version = await db.WorkflowVersions.AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == run.WorkflowVersionId, cancellationToken)
            .ConfigureAwait(false);

        var graph = version is null ? null : WorkflowGraph.Parse(version.GraphJson);

        if (graph is null)
        {
            return Error.Permanent("dlq.no_graph", "Run'ın grafı okunamadı.");
        }

        var node = graph.Node(nodeId);

        job.Value.State = JobState.Cancelled;

        db.NodeExecutions.Add(new NodeExecution
        {
            RunId = runId,
            NodeId = nodeId,
            NodeType = node?.Type ?? "unknown",
            State = NodeState.Skipped,
            Attempt = job.Value.Attempt,
            DurationMs = 0,
            IdempotencyKey = $"skip:{jobId:N}",
        });

        run.State = RunState.Running;

        var queued = await engine.EnqueueAfterAsync(run, graph, nodeId, cancellationToken).ConfigureAwait(false);

        if (queued == 0)
        {
            // Atlanan node grafın SONUNDAYSA run tamamlanıyor.
            run.State = RunState.Completed;
            run.FinishedAt = _time.GetUtcNow();
        }

        db.RunEvents.Add(new RunEvent
        {
            RunId = runId,
            NodeId = nodeId,
            Level = "warn",
            Message = $"DLQ: '{nodeId}' node'u atlandı ({decidedBy})",
        });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }

    /// Run'ı iptal eder ve BEKLEYEN bütün işlerini kapatır.
    ///
    /// Yalnızca düşen işi kapatmak yetmiyor: aynı run'ın başka
    /// kuyruklarda bekleyen işleri varsa onlar çalışmaya devam eder ve
    /// iptal edilmiş bir video için para harcanırdı.
    public async Task<Result> CancelRunAsync(Guid jobId, string decidedBy, CancellationToken cancellationToken)
    {
        var job = await LoadAsync(jobId, cancellationToken).ConfigureAwait(false);

        if (job.IsFailure)
        {
            return Result.Failure(job.Error);
        }

        if (job.Value.RunId is not { } runId)
        {
            return Error.Permanent("dlq.orphan_job", "İş bir run'a bağlı değil; iptal edilecek run yok.");
        }

        var run = await db.Runs.FirstOrDefaultAsync(r => r.Id == runId, cancellationToken)
            .ConfigureAwait(false);

        if (run is null)
        {
            return Error.Permanent("dlq.no_run", $"Run bulunamadı: {runId}");
        }

        run.State = RunState.Cancelled;
        run.FinishedAt = _time.GetUtcNow();

        var pending = await db.Jobs
            .Where(j => j.RunId == runId
                        && (j.State == JobState.Pending
                            || j.State == JobState.Leased
                            || j.State == JobState.DeadLettered))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var item in pending)
        {
            item.State = JobState.Cancelled;
        }

        db.RunEvents.Add(new RunEvent
        {
            RunId = runId,
            NodeId = job.Value.NodeId,
            Level = "warn",
            Message = $"DLQ: run iptal edildi ({decidedBy}), {pending.Count} iş kapatıldı",
        });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }

    private async Task ReviveRunAsync(
        Guid? runId, string decidedBy, string message, CancellationToken cancellationToken)
    {
        if (runId is not { } id)
        {
            return;
        }

        var run = await db.Runs.FirstOrDefaultAsync(r => r.Id == id, cancellationToken).ConfigureAwait(false);

        if (run is null)
        {
            return;
        }

        run.State = RunState.Running;
        run.FinishedAt = null;

        db.RunEvents.Add(new RunEvent { RunId = id, Level = "info", Message = message });
    }

    private async Task<Result<Job>> LoadAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken)
            .ConfigureAwait(false);

        if (job is null)
        {
            return Error.Permanent("dlq.not_found", $"İş bulunamadı: {jobId}");
        }

        // YALNIZCA DÜŞEN işler triyaj ediliyor.
        //
        // Çalışan bir işi "yeniden dene" ile kuyruğa atmak, aynı işin
        // iki kez koşması demekti: biri kirasını sürdürüyor, diğeri
        // yeni kiralanıyor ve ikisi de aynı node'u çalıştırıyor.
        if (job.State != JobState.DeadLettered)
        {
            return Error.Permanent("dlq.not_dead_lettered",
                $"Bu iş ölü mektup kuyruğunda değil (durum: {job.State}).");
        }

        return Result.Success(job);
    }
}
