using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Api;

/// Run sorguları (P1-28).
///
/// Uç noktalardan AYRI: aynı sorgular hem HTTP'den hem SSE
/// döngüsünden çağrılıyor. Uç noktanın içine yazılsalardı SSE tarafı
/// ya kopyalanır ya da bir HTTP çağrısı yapardı — ikisi de saçma.
internal static class RunQueries
{
    /// Run listesi. Sayfa boyutu SINIRLI: sınırsız bir liste, bir
    /// yıllık koşu birikince paneli de veritabanını da kilitlerdi.
    public static async Task<IReadOnlyList<RunSummary>> ListAsync(
        StudioDbContext db, RunState? state, Guid? channelId, int limit, CancellationToken cancellationToken)
    {
        var query = db.Runs.AsNoTracking();

        if (state is { } value)
        {
            query = query.Where(r => r.State == value);
        }

        if (channelId is { } channel)
        {
            query = query.Where(r => r.ChannelId == channel);
        }

        var runs = await query
            .OrderByDescending(r => r.CreatedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .Select(r => new
            {
                r.Id,
                r.State,
                r.ChannelId,
                r.TopicId,
                r.ActualCost,
                r.StartedAt,
                r.FinishedAt,
                NodeCount = r.NodeExecutions.Count,
                FailedNodes = r.NodeExecutions.Count(n => n.State == NodeState.Failed),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. runs.Select(r => new RunSummary(
            r.Id, r.State, r.ChannelId, r.TopicId, r.ActualCost,
            r.StartedAt, r.FinishedAt, r.NodeCount, r.FailedNodes))];
    }

    public static async Task<RunDetail?> DetailAsync(
        StudioDbContext db, Guid runId, CancellationToken cancellationToken)
    {
        var run = await db.Runs.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == runId, cancellationToken)
            .ConfigureAwait(false);

        if (run is null)
        {
            return null;
        }

        var executions = await db.NodeExecutions.AsNoTracking()
            .Where(n => n.RunId == runId)
            .OrderBy(n => n.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Loglar EN YENİ önce ama sınırlı: bir run binlerce olay
        // üretebiliyor ve panelin ilk ekranında hepsi gerekmiyor.
        var events = await db.RunEvents.AsNoTracking()
            .Where(e => e.RunId == runId)
            .OrderByDescending(e => e.CreatedAt)
            .Take(200)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var costs = await CostsAsync(db, runId, cancellationToken).ConfigureAwait(false);

        var summary = new RunSummary(
            run.Id, run.State, run.ChannelId, run.TopicId, run.ActualCost,
            run.StartedAt, run.FinishedAt,
            executions.Count,
            executions.Count(n => n.State == NodeState.Failed));

        return new RunDetail(
            summary,
            [.. executions.Select(n => new NodeTimelineEntry(
                n.NodeId, n.NodeType, n.State, n.Attempt, n.DurationMs, n.CreatedAt,
                ErrorCodeOf(n.ErrorJson), ErrorMessageOf(n.ErrorJson)))],
            [.. events.Select(e => new RunEventEntry(e.CreatedAt, e.Level, e.NodeId, e.Message))],
            costs);
    }

    /// Sağlayıcı × işlem kırılımında maliyet.
    ///
    /// `runs.actual_cost` tek bir sayı; "neden bu kadar" sorusuna
    /// cevap vermiyor. Kırılım `provider_calls`'tan geliyor çünkü
    /// ÖLÇÜLEN yer orası.
    public static async Task<IReadOnlyList<ProviderCostEntry>> CostsAsync(
        StudioDbContext db, Guid? runId, CancellationToken cancellationToken)
    {
        var query = db.ProviderCalls.AsNoTracking();

        if (runId is { } id)
        {
            query = query.Where(c => c.RunId == id);
        }

        var rows = await query
            .GroupBy(c => new { c.ProviderKey, c.Operation })
            .Select(g => new
            {
                g.Key.ProviderKey,
                g.Key.Operation,
                Calls = g.Count(),
                Cost = g.Sum(c => c.Cost),
                Latency = g.Sum(c => c.LatencyMs),
                // BAŞARISIZ çağrılar da sayılıyor: başarısız bir çağrı
                // da para harcamış olabilir ve "maliyet yüksek ama
                // video yok" durumunun tek açıklaması bu.
                Failures = g.Count(c => !c.Succeeded),
            })
            .OrderByDescending(g => g.Cost)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(r => new ProviderCostEntry(
            r.ProviderKey, r.Operation, r.Calls, r.Cost, r.Latency, r.Failures))];
    }

    /// SSE'nin gönderdiği küçük ilerleme belgesi.
    public static async Task<RunProgress?> ProgressAsync(
        StudioDbContext db, Guid runId, CancellationToken cancellationToken)
    {
        var run = await db.Runs.AsNoTracking()
            .Where(r => r.Id == runId)
            .Select(r => new { r.Id, r.State, r.ActualCost })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (run is null)
        {
            return null;
        }

        var executions = await db.NodeExecutions.AsNoTracking()
            .Where(n => n.RunId == runId)
            .Select(n => new { n.NodeId, n.State, n.CreatedAt })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var pending = await db.Jobs.AsNoTracking()
            .CountAsync(j => j.RunId == runId
                             && (j.State == JobState.Pending || j.State == JobState.Leased),
                cancellationToken)
            .ConfigureAwait(false);

        return new RunProgress(
            run.Id,
            run.State,
            executions.Count(n => n.State == NodeState.Succeeded),
            executions.Count(n => n.State == NodeState.Failed),
            pending,
            executions.OrderByDescending(n => n.CreatedAt).FirstOrDefault()?.NodeId,
            run.ActualCost);
    }

    /// Ölü mektup kuyruğu.
    ///
    /// EN YENİ ÖNCE: DLQ'ya bakmanın sebebi neredeyse her zaman "az
    /// önce ne düştü". Onay kuyruğunun tersi — orada en eski önemli,
    /// burada en yeni.
    public static async Task<IReadOnlyList<DeadLetterEntry>> DeadLettersAsync(
        StudioDbContext db, int limit, CancellationToken cancellationToken)
    {
        var jobs = await db.Jobs.AsNoTracking()
            .Where(j => j.State == JobState.DeadLettered)
            .OrderByDescending(j => j.CreatedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .Select(j => new DeadLetterEntry(
                j.Id, j.Queue.ToString(), j.RunId, j.NodeId,
                j.Attempt, j.MaxAttempts, j.LastError, j.CreatedAt))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return jobs;
    }

    /// Hata belgesinden kod ve mesaj.
    ///
    /// Ayrı ve `internal`: bozuk bir hata belgesi panelin tamamını
    /// düşürmemeli. Bir run zaten hatalıysa, hatanın kaydının da
    /// bozuk olması ihtimali düşük değil.
    internal static string? ErrorCodeOf(string? json) => Field(json, "code");

    internal static string? ErrorMessageOf(string? json) => Field(json, "message");

    private static string? Field(string? json, string name)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return null;
            }

            // ***ALAN ADI BUYUK/KUCUK HARFTEN BAGIMSIZ ARANIYOR.***
            //
            // `TryGetProperty` HARFE DUYARLI ve motor hatayi
            // `JsonSerializer.Serialize(error)` ile secenek VERMEDEN
            // yaziyor: cikan JSON `{"Code":...,"Message":...}`. Burasi
            // `"code"` ariyordu ve ikisi HICBIR ZAMAN eslesmiyordu --
            // yani kosu detayindaki hata kodu ve mesaji HER ZAMAN
            // bostu, tam da onlara ihtiyac duyulan anda.
            //
            // Mevcut test bunu yakalayamiyordu cunku ELLE yazilmis
            // kucuk harfli JSON besliyordu: uretimin hic uretmedigi
            // bir sekli sinamak.
            //
            // OKUYAN TARAF DUZELTILDI, YAZAN DEGIL: veritabaninda
            // zaten `Code` ile yazilmis satirlar var ve yazani
            // degistirmek onlari kalici olarak okunamaz kilardi.
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value.ValueKind == System.Text.Json.JsonValueKind.String
                        ? property.Value.GetString()
                        : null;
                }
            }

            return null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// Sağlayıcı sağlığı (P2-04): son pencerede ne oldu.
    ///
    /// PENCERE ŞART. Pencere olmasaydı, aylar önce bir kez bozulmuş
    /// bir sağlayıcı sonsuza kadar "hatalı" görünürdü ve panel bir
    /// süre sonra hiçbir şey söylemez olurdu.
    ///
    /// ART ARDA HATA, toplam hata oranından ayrı hesaplanıyor: sabah
    /// beş hata alıp düzelmiş bir sağlayıcı ile şu an art arda beş
    /// hata veren sağlayıcı aynı orana sahip olabiliyor ama biri
    /// sağlıklı, diğeri ölü.
    public static async Task<IReadOnlyList<ProviderHealthEntry>> ProviderHealthAsync(
        StudioDbContext db, TimeSpan window, int failureThreshold, CancellationToken cancellationToken)
    {
        var since = DateTimeOffset.UtcNow - window;

        var calls = await db.ProviderCalls.AsNoTracking()
            .Where(c => c.CreatedAt >= since)
            .OrderBy(c => c.CreatedAt)
            .Select(c => new { c.ProviderKey, c.Succeeded, c.CreatedAt, c.LatencyMs, c.Cost })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. calls
            .GroupBy(c => c.ProviderKey, StringComparer.Ordinal)
            .Select(g =>
            {
                // Sondan geriye: ilk başarıda duruyor.
                var consecutive = 0;

                foreach (var call in g.Reverse())
                {
                    if (call.Succeeded)
                    {
                        break;
                    }

                    consecutive++;
                }

                var failures = g.Count(c => !c.Succeeded);

                return new ProviderHealthEntry(
                    g.Key,
                    g.Count(),
                    failures,
                    consecutive,
                    consecutive >= failureThreshold,
                    g.Max(c => c.CreatedAt),
                    g.Where(c => c.Succeeded).Select(c => (DateTimeOffset?)c.CreatedAt).Max(),
                    (int)g.Average(c => c.LatencyMs),
                    g.Sum(c => c.Cost));
            })
            // SAĞLIKSIZ OLANLAR ÖNCE: panelde ilk görülmesi gereken
            // satır, sorunu olan satır.
            .OrderByDescending(p => p.Unhealthy)
            .ThenByDescending(p => p.ConsecutiveFailures)
            .ThenBy(p => p.ProviderKey, StringComparer.Ordinal)];
    }
}
