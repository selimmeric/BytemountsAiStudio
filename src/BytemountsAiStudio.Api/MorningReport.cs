using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Persistence;
using BytemountsAiStudio.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Api;

/// Gece boyunca ne olduğunun raporu (P2-13).
///
/// KABUL KRİTERİNİN ÖLÇÜLDÜĞÜ YER BURASI. "Bir gecede 3–5 video insan
/// müdahalesi olmadan hazır" iddiası, sabah tek ekranda görülmediği
/// sürece bir iddia — ve otonom bir sistemde ilk sorulacak soru tam
/// olarak bu: "gece ne oldu?"
///
/// İNSAN MÜDAHALESİ AYRI SAYILIYOR. "5 video üretildi" ile "5 video
/// üretildi ama 4'ü onay bekliyor" tamamen farklı iki sonuç ve ikisini
/// aynı sayıya sıkıştırmak, kabul kriterinin sağlandığı izlenimi
/// verirdi.
public static class MorningReport
{
    /// Rapor penceresi.
    ///
    /// ON İKİ SAAT: "gece" tanımı. Yirmi dört saat almak, dünkü
    /// gündüzü de rapora sokup "gece ne oldu" sorusunu bulanıklaştırırdı.
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(12);

    public static async Task<MorningSummary> BuildAsync(
        StudioDbContext db, TimeSpan window, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        var since = DateTimeOffset.UtcNow - window;

        var runs = await db.Runs.AsNoTracking()
            .Where(r => r.CreatedAt >= since)
            .Select(r => new { r.Id, r.State, r.ChannelId, r.CreatedAt, r.FinishedAt, r.RetryLoop, r.ErrorJson })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var cost = await db.ProviderCalls.AsNoTracking()
            .Where(c => c.CreatedAt >= since)
            .SumAsync(c => c.Cost, cancellationToken)
            .ConfigureAwait(false);

        var scores = await QualityScoresAsync(db, since, cancellationToken).ConfigureAwait(false);

        // AÇIK ONAYLAR PENCEREDEN BAĞIMSIZ.
        //
        // Dün geceden kalmış bir onay bugünün penceresine girmiyor ama
        // hâlâ bekliyor ve hâlâ insanın işi. Yalnızca pencere içindeki
        // onayları saymak, birikmiş kuyruğu görünmez kılardı.
        var pendingApprovals = await db.Approvals.AsNoTracking()
            .CountAsync(a => a.State == ApprovalState.Pending, cancellationToken)
            .ConfigureAwait(false);

        var deadLettered = await db.Jobs.AsNoTracking()
            .CountAsync(j => j.State == JobState.DeadLettered
                             && j.CreatedAt >= since, cancellationToken)
            .ConfigureAwait(false);

        var completed = runs.Count(r => r.State == RunState.Completed);
        var failed = runs.Count(r => r.State == RunState.Failed);
        var waitingApproval = runs.Count(r => r.State == RunState.WaitingApproval);
        var stillRunning = runs.Count(r => r.State is RunState.Running or RunState.Pending);
        var waitingResource = runs.Count(r => r.State == RunState.WaitingResource);

        var durations = runs
            .Where(r => r.FinishedAt is not null)
            .Select(r => (r.FinishedAt!.Value - r.CreatedAt).TotalMinutes)
            .ToList();

        return new MorningSummary(
            (int)window.TotalHours,
            runs.Count,
            completed,
            failed,
            waitingApproval,
            waitingResource,
            stillRunning,
            pendingApprovals,
            deadLettered,
            // TUR SAYISI: kaç video düzeltme turuna girdi. Sıfır
            // olması iyi haber değil de olabilir — QC hiç koşmamış
            // olabilir; o yüzden skorlarla birlikte okunuyor.
            runs.Sum(r => r.RetryLoop),
            cost,
            runs.Count == 0 ? 0 : cost / runs.Count,
            durations.Count == 0 ? null : Math.Round(durations.Average(), 1),
            scores.Count == 0 ? null : Math.Round(scores.Average(), 3),
            scores.Count,
            // İNSAN MÜDAHALESİ GEREKTİRMEYENLER: kabul kriterinin
            // gerçek karşılığı. Tamamlanmış VE onay beklemeyen videolar.
            completed,
            [.. runs.Where(r => r.State == RunState.Failed && r.ErrorJson is not null)
                .Select(r => ErrorCodeOf(r.ErrorJson))
                .Where(c => c is not null)
                .GroupBy(c => c!, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .Select(g => new FailureCount(g.Key, g.Count()))]);
    }

    /// QC skorları `node_executions` çıktısından okunuyor.
    ///
    /// Ayrı bir kolon açmamak bilinçli: skor QC node'unun çıktısı ve
    /// çıktı zaten saklanıyor. Kolon açmak, aynı sayının iki yerde
    /// yaşaması ve birinin diğerinden habersiz değişmesi demekti.
    ///
    /// RUN BAŞINA **SON** SKOR SAYILIYOR, hepsi değil.
    ///
    /// Hedefli retry (P2-07) bir videoyu birden çok tura sokabiliyor ve
    /// her turda QC yeniden koşuyor. Hepsini ortalamaya katmak, düzelme
    /// ÖNCESİ skorları da gecenin kalitesine yazmak olurdu: retry ne
    /// kadar iyi çalışırsa ortalama o kadar düşerdi — yani sistemin
    /// kendini düzeltmesi rapora bir kusur gibi yansırdı. Gecenin
    /// kalitesi, teslim edilen videonun kalitesi.
    private static async Task<List<double>> QualityScoresAsync(
        StudioDbContext db, DateTimeOffset since, CancellationToken cancellationToken)
    {
        var executions = await db.NodeExecutions.AsNoTracking()
            .Where(n => n.CreatedAt >= since
                        && n.State == NodeState.Succeeded
                        && n.NodeType == "qc.mechanical"
                        && n.OutputJson != null)
            .Select(n => new { n.RunId, n.Loop, n.Attempt, n.OutputJson })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var scores = new List<double>();

        foreach (var group in executions.GroupBy(n => n.RunId))
        {
            var last = group
                .OrderByDescending(n => n.Loop)
                .ThenByDescending(n => n.Attempt)
                .First();

            if (ScoreOf(last.OutputJson) is { } score)
            {
                scores.Add(score);
            }
        }

        return scores;
    }

    internal static double? ScoreOf(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);

            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                   && document.RootElement.TryGetProperty("score", out var value)
                   && value.ValueKind == System.Text.Json.JsonValueKind.Number
                   && value.TryGetDouble(out var score)
                ? score
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            // Okunamayan tek bir çıktı ortalamayı bozmamalı: o run
            // sayılmıyor, diğerleri sayılmaya devam ediyor.
            return null;
        }
    }

    internal static string? ErrorCodeOf(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);

            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                   && document.RootElement.TryGetProperty("Code", out var code)
                   && code.ValueKind == System.Text.Json.JsonValueKind.String
                ? code.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
