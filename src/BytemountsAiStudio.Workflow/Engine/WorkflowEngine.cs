using System.Text.Json;
using System.Text.Json.Nodes;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Core.Learning;
using BytemountsAiStudio.Core.Observability;
using BytemountsAiStudio.Persistence;
using BytemountsAiStudio.Persistence.Entities;
using BytemountsAiStudio.Persistence.Providers;
using BytemountsAiStudio.Queue;
using BytemountsAiStudio.Workflow.Definition;
using BytemountsAiStudio.Workflow.Expressions;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Workflow.Engine;

/// Motor arayüzü.
///
/// ADR-004: kendi ince engine'imizi yazıyoruz ama bu arayüzün arkasında.
/// 1000 video/gün ölçeğinde Temporal'a geçmek bir implementasyon değişimi
/// olsun, boru hattının yeniden yazılması değil.
public interface IWorkflowEngine
{
    /// `initialContext`: run'a girdi olarak verilen JSON. Node'lar buna
    /// `input.*` yolundan erişir. Olmasaydı run'ı başlatan komutun verdiği
    /// bilgiyi (konu, dil) node'lara ulaştırmanın yolu olmazdı.
    Task<Result<Guid>> StartRunAsync(
        Guid workflowVersionId, Guid? channelId, Guid? topicId,
        CancellationToken cancellationToken, string? initialContext = null);

    Task<Result> ExecuteNextAsync(string workerId, QueueClass queue, CancellationToken cancellationToken);

    /// Bir node'dan SONRAKİ node'ları kuyruğa atar.
    ///
    /// ARAYÜZE SONRADAN EKLENDİ ve sebebi ADR-004'ün kendi iddiasını
    /// ölçmekti: "IWorkflowEngine arayüzü arkasında saklanır ki
    /// Temporal'a geçiş bir implementasyon değişimi olsun."
    ///
    /// İddia doğru DEĞİLDİ. `ApprovalService` ve `DeadLetterTriage`
    /// somut `WorkflowEngine` sınıfına bağlıydı, çünkü bu metot
    /// arayüzde yoktu. Motoru değiştirmek o iki servisi de yeniden
    /// yazmak demekti — yani "implementasyon değişimi" değil.
    ///
    /// AMA GEÇİŞİ KOLAYLAŞTIRMIYOR ve bunu yazmak dürüstlük gereği:
    /// imza `Run` ve `WorkflowGraph` taşıyor, ikisi de bizim
    /// modelimiz. Temporal'da "şu node'dan sonrasını kuyruğa at" diye
    /// bir kavram yok; devam kararı workflow fonksiyonunun içinde
    /// veriliyor. Arayüz artık gerçek yüzeyi GÖSTERİYOR, küçültmüyor.
    Task<int> EnqueueAfterAsync(
        Run run, WorkflowGraph graph, string nodeId, CancellationToken cancellationToken);
}

/// DAG yorumlayıcısı (mimari §6.4).
///
/// Merkezî fikir: **node = kuyruğa atılan iş**. Motor yalnızca "sıradaki node
/// hangisi" sorusunu cevaplar; işi kuyruk dağıtır, işleyici yapar.
///
/// En kritik ayrıntı adım 4-5'te: node çıktısının yazılması ile sonraki
/// node'ların kuyruğa atılması AYNI TRANSACTION'da olmak zorunda. Ayrı
/// olsalardı "iş bitti ama sonraki node kuyruğa girmedi" durumu oluşur ve
/// run sessizce asılı kalırdı — otonom bir sistemde fark edilmesi en zor
/// hata türü.
public sealed class WorkflowEngine(
    StudioDbContext db,
    JobQueue jobQueue,
    NodeRegistry registry,
    TimeProvider? timeProvider = null) : IWorkflowEngine
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    // Alan olarak yakalaniyor: ExecuteNextAsync'in parametre adi arayuzle
    // ayni olmak zorunda (CA1725) ve birincil kurucu parametresini golgeliyor.
    private readonly JobQueue _queue = jobQueue;

    public async Task<Result<Guid>> StartRunAsync(
        Guid workflowVersionId, Guid? channelId, Guid? topicId,
        CancellationToken cancellationToken, string? initialContext = null)
    {
        var version = await db.WorkflowVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == workflowVersionId, cancellationToken)
            .ConfigureAwait(false);

        if (version is null)
        {
            return Error.Permanent("engine.no_version", $"Workflow sürümü yok: {workflowVersionId}");
        }

        var graph = WorkflowGraph.Parse(version.GraphJson);
        if (graph is null)
        {
            return Error.Permanent("engine.bad_graph", "Workflow grafı okunamadı.");
        }

        var issues = WorkflowValidator.Validate(graph, registry.KnownTypes);
        if (issues.Count > 0)
        {
            return Error.Permanent("engine.invalid_graph",
                "Workflow geçersiz: " + string.Join(" | ", issues));
        }

        var run = new Run
        {
            WorkflowVersionId = workflowVersionId,
            ChannelId = channelId,
            TopicId = topicId,
            State = RunState.Running,
            StartedAt = _time.GetUtcNow(),
            ContextJson = initialContext ?? "{}",
        };

        db.Runs.Add(run);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // DENEY ATAMASI GRAFTA DEĞİL, MOTORDA (P5-03).
        //
        // Bir `experiment.assign` node'u olsaydı, o node'u eklemeyi
        // unutan her workflow deneylerin dışında kalırdı — ve bunu
        // fark etmenin yolu yok: run başarıyla koşar, video çıkar,
        // sadece deney hiç ölçmez. Motorda olması, ATLANAMAZ olması
        // demek.
        //
        // İLK NODE'DAN ÖNCE: kapak ve başlık node'ları bağlamdan
        // okuyor. Sonra atamak, ilk videoların kolsuz üretilmesi
        // ve deneyin ilk gününü kaybetmesi olurdu.
        var assigned = await new ExperimentService(db, _time)
            .AssignAsync(run.Id, channelId, cancellationToken)
            .ConfigureAwait(false);

        if (assigned.IsFailure)
        {
            return Result.Failure<Guid>(assigned.Error);
        }

        if (assigned.Value.Count > 0)
        {
            var merged = ExperimentContext.Merge(run.ContextJson, assigned.Value);

            if (merged.IsFailure)
            {
                return Result.Failure<Guid>(merged.Error);
            }

            run.ContextJson = merged.Value;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            await LogAsync(run.Id, null, "info",
                "Deney kolları: " + string.Join(", ",
                    assigned.Value.Select(a => a.Dimension + "=" + a.VariantName)),
                cancellationToken).ConfigureAwait(false);
        }

        await EnqueueNodesAsync(run, graph, graph.EntryNodes().Select(n => n.Id), cancellationToken)
            .ConfigureAwait(false);

        await LogAsync(run.Id, null, "info", $"Run başladı: {graph.Key}", cancellationToken)
            .ConfigureAwait(false);

        return run.Id;
    }

    /// Kuyruktan bir iş alıp çalıştırır. Worker döngüsünün tek adımı.
    public async Task<Result> ExecuteNextAsync(
        string workerId, QueueClass queue, CancellationToken cancellationToken)
    {
        var handlerLease = await _queue
            .LeaseAsync(queue, workerId, LeaseDurationFor(queue), cancellationToken)
            .ConfigureAwait(false);

        if (handlerLease is null)
        {
            return Result.Success();
        }

        if (handlerLease.RunId is not { } runId || handlerLease.NodeId is not { } nodeId)
        {
            await _queue.FailAsync(handlerLease,
                Error.Permanent("engine.orphan_job", "İş bir run/node'a bağlı değil."),
                cancellationToken).ConfigureAwait(false);

            return Result.Success();
        }

        var run = await db.Runs.FirstOrDefaultAsync(r => r.Id == runId, cancellationToken).ConfigureAwait(false);
        var version = run is null
            ? null
            : await db.WorkflowVersions.AsNoTracking()
                .FirstOrDefaultAsync(v => v.Id == run.WorkflowVersionId, cancellationToken)
                .ConfigureAwait(false);

        var graph = version is null ? null : WorkflowGraph.Parse(version.GraphJson);
        var node = graph?.Node(nodeId);

        if (run is null || graph is null || node is null)
        {
            await _queue.FailAsync(handlerLease,
                Error.Permanent("engine.missing_context", "Run, graf ya da node bulunamadı."),
                cancellationToken).ConfigureAwait(false);

            return Result.Success();
        }

        // İptal edilmiş run'ın bekleyen işleri çalıştırılmaz: kill-switch
        // basıldıktan sonra kuyrukta kalanların para harcamaya devam etmesi
        // tam olarak engellemek istediğimiz şey.
        if (run.State is RunState.Cancelled or RunState.Failed)
        {
            await _queue.CompleteAsync(handlerLease.Id, cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }

        var handler = registry.Find(node.Type);
        if (handler is null)
        {
            await FailRunAsync(run, handlerLease,
                Error.Permanent("engine.no_handler", $"'{node.Type}' için işleyici yok."),
                cancellationToken).ConfigureAwait(false);

            return Result.Success();
        }

        // Bu noktadan sonraki tum loglar run ve node kimligini tasiyor;
        // her cagriya elle parametre eklemek er gec bir yerde unutulurdu.
        using var correlation = CorrelationScope.Begin(run.Id.ToString("N"), node.Id);

        var runContext = JsonDocument.Parse(run.ContextJson).RootElement;
        var idempotencyKey = IdempotencyKey.Compute(run.Id, node.Id, node.Config, runContext);

        var context = new NodeContext
        {
            RunId = run.Id,
            NodeId = node.Id,
            NodeType = node.Type,
            Attempt = handlerLease.Attempt,
            Config = node.Config,
            RunContext = runContext,
            IdempotencyKey = idempotencyKey,
            CorrelationId = run.Id.ToString("N"),
            ChannelId = run.ChannelId,
        };

        var started = _time.GetUtcNow();
        Result<JsonElement> outcome;

        try
        {
            outcome = await handler.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // İşleyici hatası tüm worker'ı düşürmemeli.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Beklenmeyen istisna ZEHİRLİ sayılır: aynı girdiyle tekrar
            // denemek büyük olasılıkla aynı yere düşer.
            outcome = new Error("engine.handler_threw", ex.Message, ErrorKind.Poison, ex.GetType().Name);
        }

        var elapsed = (int)(_time.GetUtcNow() - started).TotalMilliseconds;

        if (outcome.IsFailure)
        {
            // KAYNAK hatasi bir CALISTIRMA degil, bir ERTELEME. Node hic
            // calismadi; `node_executions`'a yazmak iki sebeple yanlis:
            //
            //  1. Anlam: "bu node basarisiz oldu" demek olurdu, oysa hic
            //     denenmedi. Tanilama ekraninda yaniltici gorunurdu.
            //  2. Teknik: kuyruk kaynak hatasinda deneme sayacini geri
            //     aliyor (ADR-011), dolayisiyla sonraki kiralama AYNI
            //     attempt degerini uretiyor ve (run_id, node_id, attempt)
            //     essiz kisiti ihlal ediliyor.
            //
            // Ikinci madde uretimde bir cokmeyle ortaya cikti; kayit
            // `run_events`'e tasindi.
            if (outcome.Error.Kind == ErrorKind.Resource)
            {
                await LogAsync(run.Id, node.Id, "warn",
                    $"Ertelendi: {outcome.Error}", cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await RecordExecutionAsync(run, node, handlerLease.Attempt, NodeState.Failed,
                    null, outcome.Error, elapsed, idempotencyKey, workerId, cancellationToken).ConfigureAwait(false);
            }

            var disposition = await _queue.FailAsync(handlerLease, outcome.Error, cancellationToken)
                .ConfigureAwait(false);

            if (disposition is JobDisposition.Failed or JobDisposition.DeadLettered)
            {
                await FailRunAsync(run, null, outcome.Error, cancellationToken).ConfigureAwait(false);
            }
            else if (disposition == JobDisposition.Deferred)
            {
                run.State = RunState.WaitingResource;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            return Result.Success();
        }

        // ---- BAŞARI: çıktı + ilerleme TEK transaction ----
        await using var transaction = await db.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await RecordExecutionAsync(run, node, handlerLease.Attempt, NodeState.Succeeded,
            outcome.Value, null, elapsed, idempotencyKey, workerId, cancellationToken).ConfigureAwait(false);

        await MergeContextAsync(run, node.Id, outcome.Value, cancellationToken).ConfigureAwait(false);
        run.State = RunState.Running;

        // ---- ONAY KAPISI: RUN PARK EDİLİYOR (P1-27) ----
        //
        // Node BAŞARIYLA bitti; devam etmiyoruz çünkü sıradaki adım bir
        // insanın kararına bağlı. Sonraki node'lar kuyruğa GİRMİYOR ve
        // bu işin kirası kapanıyor: onay bekleyen bir run hiçbir worker
        // kaynağı tüketmiyor.
        //
        // Alternatifi — işin içinde uyuyup tekrar bakmak — bir worker'ı
        // saatlerce, belki günlerce tutardı ve o worker'ın sınıfındaki
        // bütün işler beklerdi.
        if (ApprovalGate.Awaits(outcome.Value))
        {
            await ParkForApprovalAsync(run, node.Id, outcome.Value, cancellationToken).ConfigureAwait(false);

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await _queue.CompleteAsync(handlerLease.Id, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return Result.Success();
        }

        // ---- HEDEFLİ YENİDEN KOŞMA (P2-07) ----
        //
        // QC düşen bir videoyu baştan değil, HEDEFTEN yeniden
        // koşturuyor: senaryo iyiyken render bozuksa senaryoyu
        // yeniden üretmenin bedeli var, faydası yok — ve o bedel her
        // turda tekrarlanıyor.
        //
        // Motor node TİPİNE değil ÇIKTIYA bakıyor; onay kapısıyla
        // aynı desen.
        if (RerunRequest.From(outcome.Value) is { } rerun)
        {
            // HEDEF ADLARI GRAFA ÇEVRİLİYOR.
            //
            // QC planlayıcısı boru hattı AŞAMALARINI adlandırıyor
            // (`media.render`); graf ise node'lara keyfi kimlikler
            // veriyor (`render`). Çeviri olmadan hedef hiçbir node'a
            // denk gelmiyordu.
            var targets = graph.ResolveTargets(rerun.Nodes);

            if (targets.Count == 0)
            {
                // HİÇBİR HEDEF BULUNAMADI: bu run devam ETMEMELİ.
                //
                // Devam etseydi sonuç şu olurdu: QC "video bozuk"
                // dedi, düzeltme hiçbir şey koşmadı, run başarıyla
                // tamamlandı ve bozuk video yayınlandı. Sessiz başarı
                // burada mümkün olan en kötü davranış.
                //
                // KALICI hata: aynı grafla yeniden denemek aynı
                // boşluğu üretiyor. Düzeltilecek şey iş akışının
                // kaydı, o da bir insanın işi.
                var error = Error.Permanent("retry.unknown_target",
                    $"Yeniden koşma hedefi grafta yok: {string.Join(", ", rerun.Nodes)}. "
                    + "İş akışı kaydı QC'nin hedeflediği aşamayı içermiyor.");

                await LogAsync(run.Id, node.Id, "error", error.Message, cancellationToken)
                    .ConfigureAwait(false);

                await FailRunAsync(run, null, error, cancellationToken).ConfigureAwait(false);

                await _queue.CompleteAsync(handlerLease.Id, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return Result.Success();
            }

            // TUR ARTIYOR: `node_executions` eşsizliği tura bağlı ve
            // artırmadan aynı node ikinci kez yazılamazdı.
            run.RetryLoop++;

            // YALNIZCA GİRİŞLER kuyruğa giriyor: plan hedefi ve
            // sonrasını listeliyor, hepsini birden atmak yeni
            // görseller üretilmeden timeline'ı derlemek olurdu.
            await EnqueueNodesAsync(run, graph, graph.EntryPointsOf(targets), cancellationToken)
                .ConfigureAwait(false);

            await LogAsync(run.Id, node.Id, "warn",
                $"Hedefli yeniden koşma (tur {run.RetryLoop}): {rerun.Reason}", cancellationToken)
                .ConfigureAwait(false);

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await _queue.CompleteAsync(handlerLease.Id, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return Result.Success();
        }

        var next = await ResolveNextNodesAsync(run, graph, node.Id, cancellationToken).ConfigureAwait(false);

        if (next.Count == 0 && await IsRunCompleteAsync(run, graph, cancellationToken).ConfigureAwait(false))
        {
            run.State = RunState.Completed;
            run.FinishedAt = _time.GetUtcNow();
        }
        else
        {
            await EnqueueNodesAsync(run, graph, next, cancellationToken).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _queue.CompleteAsync(handlerLease.Id, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }

    /// Run'ı park eder ve onay kaydını oluşturur.
    private async Task ParkForApprovalAsync(
        Run run, string nodeId, JsonElement output, CancellationToken cancellationToken)
    {
        run.State = RunState.WaitingApproval;

        // AYNI NODE İÇİN İKİNCİ BİR BEKLEYEN KAYIT AÇILMIYOR.
        //
        // Motor bu node'u yeniden çalıştırabiliyor (kira süresi dolar,
        // iş yeniden kiralanır) ve o zaman panelde aynı video iki kez
        // görünürdü. Veritabanında da kısmi eşsiz indeks var; buradaki
        // kontrol, o kısıtın bir istisnaya dönüşmesini engelliyor.
        var existing = await db.Approvals
            .FirstOrDefaultAsync(
                a => a.RunId == run.Id && a.NodeId == nodeId && a.State == ApprovalState.Pending,
                cancellationToken)
            .ConfigureAwait(false);

        var reason = output.TryGetProperty("reason", out var text) ? text.GetString() : null;

        if (existing is not null)
        {
            existing.Reason = reason ?? existing.Reason;
            return;
        }

        db.Approvals.Add(new Approval
        {
            RunId = run.Id,
            NodeId = nodeId,
            State = ApprovalState.Pending,
            Reason = reason ?? "onay bekleniyor",
        });

        await LogAsync(run.Id, nodeId, "info",
            $"Onay bekleniyor: {reason}", cancellationToken).ConfigureAwait(false);
    }

    /// Bir node bittiğinde hangi node'lar tetiklenir.
    ///
    /// İki kural: kenarın koşulu sağlanmalı VE hedef node'un tüm girdileri
    /// tamamlanmış olmalı. İkincisi olmadan birleşen dallarda node erken
    /// çalışır ve eksik girdiyle iş yapar.
    private async Task<List<string>> ResolveNextNodesAsync(
        Run run, WorkflowGraph graph, string completedNodeId, CancellationToken cancellationToken)
    {
        var context = JsonDocument.Parse(run.ContextJson).RootElement;
        var next = new List<string>();

        foreach (var edge in graph.OutgoingEdges(completedNodeId))
        {
            if (edge.When is { } condition)
            {
                var parsed = ExpressionParser.TryParse(condition);
                if (parsed.IsFailure || !parsed.Value.EvaluateAsBoolean(context))
                {
                    continue;
                }
            }

            // Döngü sınırı: bu node BU TURDA kaç kez çalıştı?
            //
            // SAYAÇ TURA BAĞLI ve bu ayrım hedefli retry'ı mümkün
            // kılan şey. Sayaç run'ın tamamını kapsasaydı ilk retry
            // turundan sonra hiçbir alt node yeniden koşamazdı:
            // render düzeltilir, ama onu denetleyecek QC "döngü
            // sınırına ulaştı" diye atlanır ve düzeltilmiş video hiç
            // kontrol edilmeden run tamamlanırdı. Yani retry
            // çalışır, doğrulama çalışmazdı.
            //
            // Toplam hâlâ sınırlı: retry turları da sınırlı (P2-07,
            // varsayılan 3), dolayısıyla en fazla tur × MaxLoops.
            var executions = await db.NodeExecutions
                .CountAsync(e => e.RunId == run.Id && e.NodeId == edge.To
                                 && e.Loop == run.RetryLoop
                                 && e.State == NodeState.Succeeded, cancellationToken)
                .ConfigureAwait(false);

            if (executions >= edge.MaxLoops)
            {
                await LogAsync(run.Id, edge.To, "warn",
                    $"'{edge.To}' döngü sınırına ulaştı ({edge.MaxLoops}); tetiklenmiyor.",
                    cancellationToken).ConfigureAwait(false);

                continue;
            }

            if (await AllPredecessorsDoneAsync(run, graph, edge.To, cancellationToken).ConfigureAwait(false))
            {
                next.Add(edge.To);
            }
        }

        return next;
    }

    /// Birleşen dalların hepsi hazır mı.
    ///
    /// TUR FARKI BURADA HAYATİ ve gerçek bir koşuda ortaya çıktı.
    ///
    /// Önceki hâli "bu node bir kez başarılı olmuş mu" diye soruyordu.
    /// İlk turda doğru; RETRY TURUNDA YANLIŞ: `timeline` iki daldan
    /// besleniyor (`visuals` ve `muzik`) ve retry turunda müzik önce
    /// bittiğinde, timeline "görseller zaten başarılı" görüp hemen
    /// koşuyordu — ESKİ turun görselleriyle. Yani düzeltme için
    /// yeniden üretilen görseller videoya hiç girmiyordu ve sonuç
    /// "retry çalıştı" diye raporlanıyordu.
    ///
    /// Doğru soru: "bu öncül BU TURDA hazır mı". İki yoldan hazır
    /// olabiliyor:
    ///
    /// 1. Bu turda başarıyla koştu, ya da
    /// 2. Bu turda HİÇ koşmayacak (retry kapsamında değil) ve daha
    ///    önce başarılı olmuş.
    ///
    /// İkincisi olmadan kilitlenirdik: hedefli retry'ın anlamı,
    /// dokunulmayan node'ların yeniden koşmaması.
    private async Task<bool> AllPredecessorsDoneAsync(
        Run run, WorkflowGraph graph, string nodeId, CancellationToken cancellationToken)
    {
        var predecessors = graph.Predecessors(nodeId);

        if (predecessors.Count <= 1)
        {
            return true;
        }

        var succeeded = await db.NodeExecutions.AsNoTracking()
            .Where(e => e.RunId == run.Id && e.State == NodeState.Succeeded)
            .Select(e => new { e.NodeId, e.Loop })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // BU TURDA KUYRUKTA OLAN ya da ÇALIŞAN node'lar: bunlar
        // beklenecek. `Pending` bir iş "henüz koşmadı" demek ve onu
        // hazır saymak, tam olarak yukarıdaki hatayı üretirdi.
        var scheduled = await db.Jobs.AsNoTracking()
            .Where(j => j.RunId == run.Id
                        && (j.State == JobState.Pending || j.State == JobState.Leased)
                        && j.NodeId != null)
            .Select(j => j.NodeId!)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var predecessor in predecessors)
        {
            var runs = succeeded.Where(e => string.Equals(e.NodeId, predecessor, StringComparison.Ordinal)).ToList();

            if (runs.Count == 0)
            {
                return false;
            }

            if (runs.Any(e => e.Loop == run.RetryLoop))
            {
                continue;
            }

            // Bu turda koşmamış: koşacak mı?
            if (scheduled.Contains(predecessor, StringComparer.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<bool> IsRunCompleteAsync(
        Run run, WorkflowGraph graph, CancellationToken cancellationToken)
    {
        var pending = await db.Jobs
            .CountAsync(j => j.RunId == run.Id
                             && (j.State == JobState.Pending || j.State == JobState.Leased),
                cancellationToken)
            .ConfigureAwait(false);

        // Kendi işimiz hâlâ 'Leased' görünüyor; onu saymıyoruz.
        return pending <= 1;
    }

    /// Bir node'dan SONRAKİ node'ları kuyruğa atar.
    ///
    /// Onay servisi bunu çağırıyor. Kendi eşlemesini yazması ilk
    /// tasarımdı ve yanlıştı: kuyruk sınıfı işleyici kaydından geliyor
    /// ve elle yazılmış ikinci bir eşleme er geç ayrışırdı — o gün
    /// onaydan sonra devam eden iş yanlış kuyruğa düşer ve orada
    /// kalırdı, çünkü o kuyruğun worker'ları o tipi hiç beklemiyor.
    public async Task<int> EnqueueAfterAsync(
        Run run, WorkflowGraph graph, string nodeId, CancellationToken cancellationToken)
    {
        var next = graph.OutgoingEdges(nodeId).Select(e => e.To).Distinct().ToList();

        await EnqueueNodesAsync(run, graph, next, cancellationToken).ConfigureAwait(false);

        return next.Count;
    }

    private async Task EnqueueNodesAsync(
        Run run, WorkflowGraph graph, IEnumerable<string> nodeIds, CancellationToken cancellationToken)
    {
        foreach (var nodeId in nodeIds)
        {
            var node = graph.Node(nodeId);
            var handler = node is null ? null : registry.Find(node.Type);

            if (node is null || handler is null)
            {
                // SESSİZCE ATLANMIYOR.
                //
                // Kayıtta olmayan bir node atlanırsa run "Running"
                // görünür, kuyrukta iş kalmaz ve hiçbir şey kırılmadığı
                // için kimse durduğunu fark etmez. Graf doğrulaması
                // bunu run başlangıcında yakalıyor ama onaydan sonra
                // devam eden yolda o doğrulama koşmuyor — kaydı eksik
                // bir süreçten (örneğin API) çağrıldığında burası tek
                // savunma.
                await LogAsync(run.Id, nodeId, "error",
                    $"'{node?.Type ?? nodeId}' için işleyici kayıtlı değil; node kuyruğa atılamadı.",
                    cancellationToken).ConfigureAwait(false);

                continue;
            }

            await _queue.EnqueueAsync(new EnqueueRequest
            {
                Queue = handler.Queue,
                RunId = run.Id,
                NodeId = node.Id,
                ChannelId = run.ChannelId,
                Priority = run.Priority,
                MaxAttempts = MaxAttemptsFor(handler.Queue),
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RecordExecutionAsync(
        Run run, WorkflowNode node, int attempt, NodeState state,
        JsonElement? output, Error? error, int durationMs, string idempotencyKey,
        string workerId, CancellationToken cancellationToken)
    {
        db.NodeExecutions.Add(new NodeExecution
        {
            RunId = run.Id,

            // HANGİ MAKİNE ÇALIŞTIRDI (P4-01): render worker'ları ayrı
            // makineye çıkınca "bu videoyu hangi makine üretti" gerçek
            // bir soru oldu. `jobs.leased_by` iş bitince temizleniyor,
            // yani tam da soruyu sorduğunuz anda kayıp.
            WorkerId = workerId,
            NodeId = node.Id,
            NodeType = node.Type,
            // TUR: hedefli retry aynı node'u ikinci bir turda
            // çalıştırıyor ve deneme sayacı yeni bir işle 1'den
            // başlıyor. Tur olmadan bu, (run, node, deneme) eşsiz
            // kısıtının ihlali ve run'ın çökmesi demekti (P2-07).
            Loop = run.RetryLoop,
            Attempt = attempt,
            State = state,
            IdempotencyKey = idempotencyKey,
            OutputJson = output?.GetRawText(),
            ErrorJson = error is null ? null : JsonSerializer.Serialize(error),
            DurationMs = durationMs,
            StartedAt = _time.GetUtcNow().AddMilliseconds(-durationMs),
            FinishedAt = _time.GetUtcNow(),
        });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task FailRunAsync(
        Run run, LeasedJob? job, Error error, CancellationToken cancellationToken)
    {
        run.State = RunState.Failed;
        run.FinishedAt = _time.GetUtcNow();
        run.ErrorJson = JsonSerializer.Serialize(error);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (job is not null)
        {
            await _queue.FailAsync(job, error, cancellationToken).ConfigureAwait(false);
        }

        await LogAsync(run.Id, null, "error", error.ToString(), cancellationToken).ConfigureAwait(false);
    }

    private async Task LogAsync(
        Guid runId, string? nodeId, string level, string message, CancellationToken cancellationToken)
    {
        // Sizinti noktasi burasi: bir saglayici istisnasinin mesaji istegin
        // URL'sini ya da basligini icerebiliyor ve o metin oldugu gibi
        // veritabanina yaziliyor. Suzgec cikista duruyor (P1-01).
        var safe = SecretRedactor.Redact(message);

        db.RunEvents.Add(new RunEvent
        {
            RunId = runId,
            NodeId = nodeId,
            Level = level,
            Message = safe.Length > 2000 ? safe[..2000] : safe,
        });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// Node çıktısını run bağlamına ekler: `{"script": {...}}`.
    internal static string MergeContext(string contextJson, string nodeId, JsonElement output)
    {
        var node = JsonNode.Parse(contextJson)?.AsObject() ?? [];
        node[nodeId] = JsonNode.Parse(output.GetRawText());
        return node.ToJsonString();
    }

    /// Node çıktısını run bağlamına VERİTABANINDA birleştirir.
    ///
    /// GERÇEK BİR KAYIP BURADAN ÇIKTI. Eski hâli bellekte
    /// birleştiriyordu: `run.ContextJson` bu çalıştırma başlarken
    /// okunmuş bir kopyaydı, üzerine kendi çıktısı yazılıyor ve
    /// KOLONUN TAMAMI geri yazılıyordu. Arada başka bir node
    /// commit ettiyse onun çıktısı siliniyordu.
    ///
    /// Ölçülen koşu: `music` düğümü 14:06:06.097–.119 arasında koştu,
    /// `visuals` 14:06:06.117'de — yani müziğin commit'inden **2 ms
    /// önce** — başladı ve 3 saniye sonra kendi bağlamını yazdı.
    /// Müzik çıktısı yok oldu. `music.select` başarıyla bitmiş,
    /// lisanslı bir parça seçmiş, `node_executions` içinde duruyordu;
    /// `runs.context_json` içinde yoktu. Timeline sessizce müziksiz
    /// derlendi ve video müziksiz çıktı.
    ///
    /// Kaybın sessizliği asıl mesele: müzik görülebilir bir alan
    /// olduğu için fark edildi, ama AYNI YARIŞ her paralel dalın
    /// çıktısını kaybedebilirdi ve hiçbiri iz bırakmazdı.
    ///
    /// CLI'da hiç görülmedi çünkü orada node'lar tek döngüde SIRAYLA
    /// koşuyor. Worker sekiz kuyruk sınıfını paralel dinliyor.
    ///
    /// Çözüm: `jsonb ||` ile TEK ifadede birleştirmek. Postgres satırı
    /// kilitliyor, yani ikinci node bu transaction commit edene kadar
    /// bekliyor ve sonra KENDİ yamasını güncel değere uyguluyor.
    /// İkisi de kalıyor.
    private async Task MergeContextAsync(
        Run run, string nodeId, JsonElement output, CancellationToken cancellationToken)
    {
        var patch = new JsonObject { [nodeId] = JsonNode.Parse(output.GetRawText()) }.ToJsonString();

        // `ToListAsync`, `SingleAsync` DEĞİL: ikincisi ifadenin
        // üzerine `LIMIT` ekliyor ve `UPDATE ... RETURNING` üzerine
        // sorgu bindirilemiyor. Tek satır zaten birincil anahtarla
        // güvence altında.
        var rows = await db.Database
            .SqlQuery<string>($"""
                UPDATE runs
                SET context_json = context_json || {patch}::jsonb
                WHERE id = {run.Id}
                RETURNING context_json AS "Value"
                """)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var merged = rows[0];

        // BELLEKTEKİ KOPYA TAZELENİYOR ve EF BU KOLONU BİR DAHA
        // YAZMIYOR.
        //
        // İkisi birden gerekiyor: sonraki node'lar bağlamı bu
        // nesneden okuyor, ama kolonun sahibi artık yukarıdaki tek
        // ifade — değişiklik izleyicisinin de yazmasına izin vermek,
        // düzeltilen yarışı `SaveChanges` anında geri getirirdi.
        //
        // ÖNCE ÖZGÜN DEĞER, SONRA GÜNCEL DEĞER. Sırası önemli:
        // `IsModified = false` tek başına güncel değeri ÖZGÜN DEĞERE
        // GERİ ALIYOR. İlk yazımda yalnızca onu çağırmıştım ve
        // sonuç şuydu: veritabanı doğru, bellekteki kopya eski.
        // Sıradaki node bağlamı boş gördü — yani düzeltmenin kendisi
        // aynı sınıftan yeni bir kayıp üretmişti. Var olan bir test
        // ("node çıktısı sonraki node'a bağlam olarak geçer") bunu
        // hemen yakaladı.
        var entry = db.Entry(run).Property(r => r.ContextJson);

        entry.OriginalValue = merged;
        entry.CurrentValue = merged;
        entry.IsModified = false;
    }

    /// §8.1: kiralama süresi işin gerçek süresine yakın olmalı. Render için
    /// dakikalar, LLM için saniyeler. Hepsine aynı süreyi vermek ya çöken
    /// render'ı saatlerce takılı bırakır ya da uzun işi ortasında kaybettirir.
    private static TimeSpan LeaseDurationFor(QueueClass queue) => queue switch
    {
        QueueClass.Render => TimeSpan.FromMinutes(60),
        QueueClass.Upload => TimeSpan.FromMinutes(30),
        QueueClass.Align => TimeSpan.FromMinutes(15),
        QueueClass.Tts or QueueClass.ImageGeneration => TimeSpan.FromMinutes(5),
        _ => TimeSpan.FromMinutes(3),
    };

    private static int MaxAttemptsFor(QueueClass queue) => queue switch
    {
        QueueClass.Upload => 5,
        QueueClass.Render or QueueClass.Align or QueueClass.ImageGeneration => 2,
        _ => 3,
    };
}
