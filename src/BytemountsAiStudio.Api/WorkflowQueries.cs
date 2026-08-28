using BytemountsAiStudio.Persistence;
using BytemountsAiStudio.Workflow.Definition;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Api;

/// İş akışı sürümleri ekranı (P3-06).
///
/// SORUNUN KENDİSİ: "bu video hangi grafla üretildi." Graf değişince
/// yeni bir sürüm ekleniyor ve eski sürüm SİLİNMİYOR — hâlihazırda
/// koşan run'lar ona bağlı (§6.2). Bu ekran o bağı görünür kılıyor.
///
/// KOŞAN RUN'LARIN ESKİ SÜRÜMDE KALMASI bir hata değil, tasarım: bir
/// run başladığı grafla bitmeli. Ortasında graf değiştirmek, yarısı
/// eski yarısı yeni kurallarla üretilmiş bir video demekti.
public static class WorkflowQueries
{
    public static async Task<IReadOnlyList<WorkflowSummary>> ListAsync(
        StudioDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        var workflows = await db.Workflows.AsNoTracking()
            .Select(w => new { w.Id, w.Key, w.Name, w.ContentKind, w.CurrentVersion })
            .OrderBy(w => w.Key)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var versions = await db.WorkflowVersions.AsNoTracking()
            .Select(v => new { v.Id, v.WorkflowId, v.Version, v.GraphJson, v.CreatedAt })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // KAÇ RUN HANGİ SÜRÜMDE: tek sorguda, sürüm başına ayrı sorgu
        // değil. Yirmi sürümlü bir iş akışında yirmi sorgu, ekranı
        // açan herkesin veritabanını yorması demekti.
        var runCounts = await db.Runs.AsNoTracking()
            .GroupBy(r => r.WorkflowVersionId)
            .Select(g => new
            {
                VersionId = g.Key,
                Total = g.Count(),
                // AKTİF RUN SAYISI AYRI: eski bir sürümü silmenin
                // güvenli olup olmadığı buna bağlı ve "toplam run"
                // bu soruya cevap vermiyor.
                Active = g.Count(r => r.State == Core.Execution.RunState.Running
                                      || r.State == Core.Execution.RunState.Pending
                                      || r.State == Core.Execution.RunState.WaitingApproval
                                      || r.State == Core.Execution.RunState.WaitingResource),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return
        [
            .. workflows.Select(w =>
            {
                var own = versions
                    .Where(v => v.WorkflowId == w.Id)
                    .OrderByDescending(v => v.Version)
                    .Select(v =>
                    {
                        var counts = runCounts.FirstOrDefault(c => c.VersionId == v.Id);
                        var graph = WorkflowGraph.Parse(v.GraphJson);

                        return new WorkflowVersionSummary(
                            v.Version,
                            v.Version == w.CurrentVersion,
                            graph?.Nodes.Count ?? 0,
                            graph?.Edges.Count ?? 0,
                            counts?.Total ?? 0,
                            counts?.Active ?? 0,
                            v.CreatedAt);
                    })
                    .ToList();

                return new WorkflowSummary(
                    w.Key, w.Name, w.ContentKind.ToString(), w.CurrentVersion, own);
            })
        ];
    }

    /// Tek bir sürümün grafını döndürür — düğüm ve kenar listesi.
    ///
    /// HAM JSON DEĞİL, ÇÖZÜMLENMİŞ YAPI: ham metin ekranda okunmaz ve
    /// "bu sürümde ne değişti" sorusuna cevap vermez. Düğüm tipleri ve
    /// bağlantılar cevap verir.
    public static async Task<WorkflowGraphView?> GraphAsync(
        StudioDbContext db, string key, int version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        var json = await db.WorkflowVersions.AsNoTracking()
            .Where(v => v.Workflow!.Key == key && v.Version == version)
            .Select(v => v.GraphJson)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (json is null)
        {
            return null;
        }

        var graph = WorkflowGraph.Parse(json);

        if (graph is null)
        {
            // OKUNAMAYAN GRAF SESSİZCE BOŞ GÖRÜNMÜYOR: depoda bozuk
            // bir kayıt varsa ekran bunu söylemeli, yoksa "bu sürümde
            // hiç node yok" gibi okunurdu.
            return new WorkflowGraphView(key, version, [], [], "graf okunamadı");
        }

        return new WorkflowGraphView(
            key,
            version,
            [.. graph.Nodes.Select(n => new GraphNodeView(n.Id, n.Type))],
            [.. graph.Edges.Select(e => new GraphEdgeView(e.From, e.To, e.When))],
            null);
    }
}
