using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Persistence;
using BytemountsAiStudio.Persistence.Entities;
using BytemountsAiStudio.Workflow.Definition;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Workflow.Engine;

/// Onay kuyruğundaki bir kayıt — panelin gördüğü hâl.
public sealed record PendingApproval(
    Guid Id,
    Guid RunId,
    string NodeId,
    string Reason,
    DateTimeOffset RequestedAt,
    Guid? ChannelId,
    string? Topic);

/// Onay kuyruğu ve kararların uygulanması (P1-27).
///
/// Motordan AYRI, çünkü tetikleyicisi farklı: motoru worker döngüsü
/// çağırıyor, burayı bir insan. İkisini aynı sınıfa koymak, worker
/// döngüsüne hiç kullanmayacağı bir bağımlılık taşıtırdı.
public sealed class ApprovalService(StudioDbContext db, IWorkflowEngine engine, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// Bekleyen onaylar, en eski önce.
    ///
    /// EN ESKİ ÖNCE: onay kuyruğu bir yığın değil sıra. En yeniyi
    /// üstte göstermek, yoğun bir günde en eski videoların hiç
    /// bakılmadan kalması demekti.
    public async Task<IReadOnlyList<PendingApproval>> PendingAsync(
        Guid? channelId, int limit, CancellationToken cancellationToken)
    {
        var query =
            from approval in db.Approvals.AsNoTracking()
            join run in db.Runs.AsNoTracking() on approval.RunId equals run.Id
            where approval.State == ApprovalState.Pending
            select new { approval, run };

        if (channelId is { } channel)
        {
            query = query.Where(x => x.run.ChannelId == channel);
        }

        var rows = await query
            .OrderBy(x => x.approval.CreatedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(x => new PendingApproval(
            x.approval.Id,
            x.approval.RunId,
            x.approval.NodeId,
            x.approval.Reason,
            x.approval.CreatedAt,
            x.run.ChannelId,
            TopicOf(x.run)))];
    }

    /// Onaylar ve run'ı KALDIĞI YERDEN sürdürür.
    public async Task<Result> ApproveAsync(
        Guid approvalId, string decidedBy, string? note, CancellationToken cancellationToken)
    {
        var approval = await LoadPendingAsync(approvalId, cancellationToken).ConfigureAwait(false);

        if (approval.IsFailure)
        {
            return Result.Failure(approval.Error);
        }

        var run = await db.Runs.FirstOrDefaultAsync(r => r.Id == approval.Value.RunId, cancellationToken)
            .ConfigureAwait(false);

        if (run is null)
        {
            return Error.Permanent("approval.no_run", $"Run bulunamadı: {approval.Value.RunId}");
        }

        var version = await db.WorkflowVersions.AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == run.WorkflowVersionId, cancellationToken)
            .ConfigureAwait(false);

        var graph = version is null ? null : WorkflowGraph.Parse(version.GraphJson);

        if (graph is null)
        {
            return Error.Permanent("approval.no_graph", "Run'ın grafı okunamadı.");
        }

        // KARAR VE DEVAM AYNI TRANSACTION'DA.
        //
        // Ayrı olsalardı "onaylandı ama iş kuyruğa girmedi" durumu
        // oluşurdu ve run sessizce asılı kalırdı — üstelik panelde
        // onaylanmış göründüğü için kimse aramazdı. Motordaki aynı
        // kural (§6.4 adım 4-5) burada da geçerli.
        await using var transaction = await db.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        approval.Value.State = ApprovalState.Approved;
        approval.Value.DecidedBy = decidedBy;
        approval.Value.Note = note;
        approval.Value.DecidedAt = _time.GetUtcNow();

        run.State = RunState.Running;

        // Kuyruğa atmayı MOTOR yapıyor: kuyruk sınıfı ve deneme
        // sayısı tek yerde kalsın.
        var queued = await engine.EnqueueAfterAsync(run, graph, approval.Value.NodeId, cancellationToken)
            .ConfigureAwait(false);

        if (queued == 0)
        {
            // Onay kapısı grafın SONUNDAYSA run tamamlanıyor. Bu geçerli
            // bir tasarım: "yayınlamadan önce onayla" akışında yayın
            // node'u kapının kendisinden önce gelmiş olabilir.
            run.State = RunState.Completed;
            run.FinishedAt = _time.GetUtcNow();
        }

        db.RunEvents.Add(new RunEvent
        {
            RunId = run.Id,
            NodeId = approval.Value.NodeId,
            Level = "info",
            Message = $"Onaylandı ({decidedBy}){(string.IsNullOrWhiteSpace(note) ? string.Empty : $": {note}")}",
        });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }

    /// Reddeder ve run'ı İPTAL eder.
    ///
    /// İptal, başarısızlık DEĞİL: bir insan "bu yayınlanmasın" dedi ve
    /// bu sistemin doğru çalıştığının kanıtı. `Failed` işaretlemek,
    /// hata panellerini insan kararlarıyla doldurur ve gerçek
    /// arızaları görünmez kılardı.
    public async Task<Result> RejectAsync(
        Guid approvalId, string decidedBy, string? note, CancellationToken cancellationToken)
    {
        var approval = await LoadPendingAsync(approvalId, cancellationToken).ConfigureAwait(false);

        if (approval.IsFailure)
        {
            return Result.Failure(approval.Error);
        }

        var run = await db.Runs.FirstOrDefaultAsync(r => r.Id == approval.Value.RunId, cancellationToken)
            .ConfigureAwait(false);

        if (run is null)
        {
            return Error.Permanent("approval.no_run", $"Run bulunamadı: {approval.Value.RunId}");
        }

        approval.Value.State = ApprovalState.Rejected;
        approval.Value.DecidedBy = decidedBy;
        approval.Value.Note = note;
        approval.Value.DecidedAt = _time.GetUtcNow();

        run.State = RunState.Cancelled;
        run.FinishedAt = _time.GetUtcNow();

        db.RunEvents.Add(new RunEvent
        {
            RunId = run.Id,
            NodeId = approval.Value.NodeId,
            Level = "warn",
            Message = $"Reddedildi ({decidedBy}){(string.IsNullOrWhiteSpace(note) ? string.Empty : $": {note}")}",
        });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }

    private async Task<Result<Approval>> LoadPendingAsync(Guid approvalId, CancellationToken cancellationToken)
    {
        var approval = await db.Approvals
            .FirstOrDefaultAsync(a => a.Id == approvalId, cancellationToken)
            .ConfigureAwait(false);

        if (approval is null)
        {
            return Error.Permanent("approval.not_found", $"Onay kaydı yok: {approvalId}");
        }

        // ZATEN KARARA BAĞLANMIŞ bir onay ikinci kez işlenmiyor.
        //
        // İki kişi paneli aynı anda açıp ikisi de onaylarsa, ikinci
        // karar sonraki node'ları BİR KEZ DAHA kuyruğa atardı: aynı
        // video iki kez render edilir, iki kez yüklenirdi.
        if (approval.State != ApprovalState.Pending)
        {
            return Error.Permanent("approval.already_decided",
                $"Bu onay zaten {approval.State} durumunda ({approval.DecidedBy}).");
        }

        return Result.Success(approval);
    }

    private static string? TopicOf(Run run)
        => run.TopicId?.ToString();
}
