using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Persistence;
using BytemountsAiStudio.Persistence.Entities;
using BytemountsAiStudio.Queue;
using BytemountsAiStudio.TestSupport;
using BytemountsAiStudio.Workflow.Definition;
using BytemountsAiStudio.Workflow.Engine;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Workflow.Tests;

/// DLQ triyajının testleri (P2-10).
///
/// Kabul kriteri: **takılan run insan müdahalesiyle üç tıkta
/// kurtarılıyor.** Üç eylem, üç farklı soruya cevap veriyor ve
/// ikisinin arasındaki fark önemli: "node'u atla", "sonsuza kadar
/// dene" ile "her şeyi çöpe at" arasındaki tek makul seçenek.
[Collection(DatabaseCollection.Name)]
public sealed class DeadLetterTriageTests(DatabaseFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (!fixture.Available)
        {
            return;
        }

        await using var db = fixture.CreateContext();

        await db.Database.ExecuteSqlRawAsync("DELETE FROM node_executions");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM run_events");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM jobs");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM runs");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private void RequireDatabase()
        => Assert.True(fixture.Available, $"PostgreSQL erişilemiyor ({fixture.UnavailableReason}).");

    private static WorkflowGraph Graph() => new()
    {
        Key = "triyaj",
        Name = "Triyaj hatti",
        Nodes =
        [
            new() { Id = "yaz", Type = "test.a", Config = ScriptedHandler.Json("{}") },
            new() { Id = "kapak", Type = "test.b", Config = ScriptedHandler.Json("{}") },
            new() { Id = "yayin", Type = "test.c", Config = ScriptedHandler.Json("{}") },
        ],
        Edges = [new() { From = "yaz", To = "kapak" }, new() { From = "kapak", To = "yayin" }],
    };

    private sealed record Harness(
        StudioDbContext Db, DeadLetterTriage Triage, Guid RunId, Guid JobId, ScriptedHandler Publish);

    private static async Task<Harness> SetupAsync(StudioDbContext db, RunState runState = RunState.Failed)
    {
        var write = new ScriptedHandler("test.a", QueueClass.Llm, _ => ScriptedHandler.Json("{}"));
        var cover = new ScriptedHandler("test.b", QueueClass.Asset, _ => ScriptedHandler.Json("{}"));
        var publish = new ScriptedHandler("test.c", QueueClass.Upload, _ => ScriptedHandler.Json("{}"));

        var registry = new NodeRegistry().Register(write).Register(cover).Register(publish);
        var engine = new WorkflowEngine(db, new JobQueue(db), registry);

        var graph = Graph();

        var workflow = new Persistence.Entities.Workflow
        {
            Key = graph.Key + "-" + Guid.NewGuid().ToString("N")[..6],
            Name = graph.Name,
            CurrentVersion = 1,
        };

        var version = new WorkflowVersion { Version = 1, GraphJson = graph.ToJson() };
        workflow.Versions.Add(version);
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync(CancellationToken.None);

        var run = new Run
        {
            WorkflowVersionId = version.Id,
            State = runState,
            StartedAt = DateTimeOffset.UtcNow,
            FinishedAt = runState is RunState.Failed or RunState.Cancelled ? DateTimeOffset.UtcNow : null,
        };

        db.Runs.Add(run);

        // "kapak" node'u kalıcı olarak düştü.
        var dead = new Job
        {
            Queue = QueueClass.Asset,
            RunId = run.Id,
            NodeId = "kapak",
            State = JobState.DeadLettered,
            Attempt = 3,
            MaxAttempts = 3,
            LastError = "kapak üretilemedi",
        };

        db.Jobs.Add(dead);
        await db.SaveChangesAsync(CancellationToken.None);

        return new Harness(db, new DeadLetterTriage(db, engine), run.Id, dead.Id, publish);
    }

    /// Deneme sayacı SIFIRLANIYOR: iş zaten sınırı doldurduğu için
    /// düştü ve sıfırlamadan kuyruğa almak, ilk denemede tekrar
    /// düşmesi demekti.
    [Fact]
    public async Task YenidenDene_SayaciSifirliyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var harness = await SetupAsync(db);

        var result = await harness.Triage.RetryAsync(harness.JobId, "selim", CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);

        var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == harness.JobId, CancellationToken.None);

        Assert.Equal(JobState.Pending, job.State);
        Assert.Equal(0, job.Attempt);
    }

    /// RUN DA CANLANIYOR. İş kuyruğa girip run `Failed` kalsaydı,
    /// worker o işi alıp hemen atardı (iptal edilmiş run'ın işleri
    /// çalıştırılmıyor) ve düğme hiçbir şey yapmamış görünürdü.
    [Fact]
    public async Task YenidenDene_RunuDaCanlandiriyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var harness = await SetupAsync(db);

        await harness.Triage.RetryAsync(harness.JobId, "selim", CancellationToken.None);

        var run = await db.Runs.AsNoTracking().SingleAsync(r => r.Id == harness.RunId, CancellationToken.None);

        Assert.Equal(RunState.Running, run.State);
        Assert.Null(run.FinishedAt);
    }

    /// NODE'U ATLA: "sonsuza kadar dene" ile "her şeyi çöpe at"
    /// arasındaki tek makul seçenek. Çoğu takılma isteğe bağlı bir
    /// adımda oluyor (kapak, müzik) ve o adım olmadan da yayına
    /// girebilecek bir video kalıyor.
    [Fact]
    public async Task NodeAtla_SonrakiNodeuKuyrugaAtiyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var harness = await SetupAsync(db);

        var result = await harness.Triage.SkipNodeAsync(harness.JobId, "selim", CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);

        var queued = await db.Jobs.AsNoTracking()
            .Where(j => j.RunId == harness.RunId && j.State == JobState.Pending)
            .ToListAsync(CancellationToken.None);

        Assert.Single(queued);
        Assert.Equal("yayin", queued[0].NodeId);
    }

    /// Atlanan node `Skipped`, `Succeeded` DEĞİL: ikisini eşitlemek,
    /// eksik bir videoyu tam sanmak olurdu. QC zaten eksikliği
    /// yakalayacak.
    [Fact]
    public async Task AtlananNode_SkippedOlarakKaydediliyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var harness = await SetupAsync(db);

        await harness.Triage.SkipNodeAsync(harness.JobId, "selim", CancellationToken.None);

        var execution = await db.NodeExecutions.AsNoTracking()
            .SingleAsync(n => n.RunId == harness.RunId && n.NodeId == "kapak", CancellationToken.None);

        Assert.Equal(NodeState.Skipped, execution.State);
        Assert.NotEqual(NodeState.Succeeded, execution.State);
    }

    /// Yalnızca düşen işi kapatmak YETMİYOR: aynı run'ın başka
    /// kuyruklarda bekleyen işleri varsa onlar çalışmaya devam eder ve
    /// iptal edilmiş bir video için para harcanırdı.
    [Fact]
    public async Task RunIptal_BekleyenButunIsleriKapatiyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var harness = await SetupAsync(db);

        db.Jobs.Add(new Job
        {
            Queue = QueueClass.Render,
            RunId = harness.RunId,
            NodeId = "yayin",
            State = JobState.Pending,
            MaxAttempts = 3,
        });

        await db.SaveChangesAsync(CancellationToken.None);

        await harness.Triage.CancelRunAsync(harness.JobId, "selim", CancellationToken.None);

        var alive = await db.Jobs.AsNoTracking()
            .CountAsync(j => j.RunId == harness.RunId
                             && (j.State == JobState.Pending
                                 || j.State == JobState.Leased
                                 || j.State == JobState.DeadLettered),
                CancellationToken.None);

        Assert.Equal(0, alive);

        var run = await db.Runs.AsNoTracking().SingleAsync(r => r.Id == harness.RunId, CancellationToken.None);

        Assert.Equal(RunState.Cancelled, run.State);
    }

    /// YALNIZCA DÜŞEN işler triyaj ediliyor. Çalışan bir işi "yeniden
    /// dene" ile kuyruğa atmak, aynı işin iki kez koşması demekti:
    /// biri kirasını sürdürüyor, diğeri yeni kiralanıyor.
    [Fact]
    public async Task DusmemisIs_Reddediliyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var harness = await SetupAsync(db);

        var running = new Job
        {
            Queue = QueueClass.Llm,
            RunId = harness.RunId,
            NodeId = "yaz",
            State = JobState.Leased,
            MaxAttempts = 3,
        };

        db.Jobs.Add(running);
        await db.SaveChangesAsync(CancellationToken.None);

        var result = await harness.Triage.RetryAsync(running.Id, "selim", CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("dlq.not_dead_lettered", result.Error.Code);
    }

    [Fact]
    public async Task OlmayanIs_AcikHata()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var harness = await SetupAsync(db);

        var result = await harness.Triage.RetryAsync(Guid.CreateVersion7(), "selim", CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("dlq.not_found", result.Error.Code);
    }

    /// Her eylem kayda giriyor: "bu run neden böyle bitti" sorusunun
    /// cevabı insan müdahalesi olabiliyor ve o müdahale görünmezse
    /// koşu kendiliğinden düzelmiş gibi görünür.
    [Fact]
    public async Task Eylemler_KaydaGiriyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var harness = await SetupAsync(db);

        await harness.Triage.SkipNodeAsync(harness.JobId, "selim", CancellationToken.None);

        var events = await db.RunEvents.AsNoTracking()
            .Where(e => e.RunId == harness.RunId)
            .ToListAsync(CancellationToken.None);

        Assert.Contains(events, e => e.Message.Contains("selim", StringComparison.Ordinal));
    }
}
