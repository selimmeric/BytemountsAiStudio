using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Persistence;
using BytemountsAiStudio.Persistence.Entities;
using BytemountsAiStudio.Queue;
using BytemountsAiStudio.TestSupport;
using BytemountsAiStudio.Workflow.Definition;
using BytemountsAiStudio.Workflow.Engine;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Workflow.Tests;

/// Hedefli yeniden koşmanın motor tarafı (P2-07).
///
/// Kabul kriteri: **QC retry'ı tüm boru hattını yeniden koşturmuyor.**
/// Burada o iddia sayı olarak sınanıyor: hangi node'ların kaç kez
/// çalıştığına bakılıyor.
[Collection(DatabaseCollection.Name)]
public sealed class TargetedRetryTests(DatabaseFixture fixture) : IAsyncLifetime
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

    /// yaz → gorsel → render → qc
    private static WorkflowGraph Graph() => new()
    {
        Key = "retry",
        Name = "Retry hatti",
        Nodes =
        [
            new() { Id = "yaz", Type = "test.script", Config = ScriptedHandler.Json("{}") },
            new() { Id = "gorsel", Type = "test.visual", Config = ScriptedHandler.Json("{}") },
            new() { Id = "render", Type = "test.render", Config = ScriptedHandler.Json("{}") },
            new() { Id = "qc", Type = "test.qc", Config = ScriptedHandler.Json("{}") },
        ],
        Edges =
        [
            new() { From = "yaz", To = "gorsel" },
            new() { From = "gorsel", To = "render" },
            new() { From = "render", To = "qc" },
        ],
    };

    private static async Task DrainAsync(WorkflowEngine engine, int maxSteps = 30)
    {
        for (var i = 0; i < maxSteps; i++)
        {
            foreach (var queue in Enum.GetValues<QueueClass>())
            {
                await engine.ExecuteNextAsync("test-worker", queue, CancellationToken.None);
            }
        }
    }

    private static async Task<Guid> CreateWorkflowAsync(StudioDbContext db, WorkflowGraph graph)
    {
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

        return version.Id;
    }

    /// KABUL KRİTERİ: render'a dönen bir retry, senaryoyu yeniden
    /// üretmiyor.
    [Fact]
    public async Task RenderRetry_SenaryoyuYenidenUretmiyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var script = new ScriptedHandler("test.script", QueueClass.Llm, _ => ScriptedHandler.Json("{}"));
        var visual = new ScriptedHandler("test.visual", QueueClass.ImageGeneration, _ => ScriptedHandler.Json("{}"));
        var render = new ScriptedHandler("test.render", QueueClass.Render, _ => ScriptedHandler.Json("{}"));

        // QC ilk turda render'dan yeniden koşma istiyor, ikinci turda
        // geçiyor.
        var qcCalls = 0;

        var qc = new ScriptedHandler("test.qc", QueueClass.Search, _ =>
        {
            qcCalls++;

            return qcCalls == 1
                ? ScriptedHandler.Json(
                    """{"retry":{"decision":"Rerun","reason":"render bozuk","nodes":["test.render"]}}""")
                : ScriptedHandler.Json("""{"retry":{"decision":"None"}}""");
        });

        var registry = new NodeRegistry()
            .Register(script).Register(visual).Register(render).Register(qc);

        var engine = new WorkflowEngine(db, new JobQueue(db), registry);
        var versionId = await CreateWorkflowAsync(db, Graph());

        var run = await engine.StartRunAsync(versionId, null, null, CancellationToken.None);
        Assert.True(run.IsSuccess, run.IsFailure ? run.Error.Message : string.Empty);

        await DrainAsync(engine);

        // Senaryo ve görsel BİRER kez; render İKİ kez.
        Assert.Single(script.Calls);
        Assert.Single(visual.Calls);
        Assert.Equal(2, render.Calls.Count);
        Assert.Equal(2, qcCalls);
    }

    /// TUR NUMARASI OLMADAN BU KOŞU ÇÖKERDİ.
    ///
    /// `node_executions` eşsizliği (run, node, tur, deneme) üzerinden
    /// ve yeni bir iş deneme sayacını 1'den başlatıyor. Tur olmasaydı
    /// render'ın ikinci çalıştırması aynı anahtarla yazılmaya
    /// çalışılır ve kısıt ihlali run'ı düşürürdü.
    [Fact]
    public async Task IkinciTur_CalistirmaKaydiniCakismadanYaziyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var script = new ScriptedHandler("test.script", QueueClass.Llm, _ => ScriptedHandler.Json("{}"));
        var visual = new ScriptedHandler("test.visual", QueueClass.ImageGeneration, _ => ScriptedHandler.Json("{}"));
        var render = new ScriptedHandler("test.render", QueueClass.Render, _ => ScriptedHandler.Json("{}"));

        var qcCalls = 0;

        var qc = new ScriptedHandler("test.qc", QueueClass.Search, _ =>
        {
            qcCalls++;

            return qcCalls == 1
                ? ScriptedHandler.Json(
                    """{"retry":{"decision":"Rerun","reason":"tekrar","nodes":["test.render"]}}""")
                : ScriptedHandler.Json("{}");
        });

        var engine = new WorkflowEngine(db, new JobQueue(db),
            new NodeRegistry().Register(script).Register(visual).Register(render).Register(qc));

        var versionId = await CreateWorkflowAsync(db, Graph());
        var run = await engine.StartRunAsync(versionId, null, null, CancellationToken.None);

        await DrainAsync(engine);

        var executions = await db.NodeExecutions.AsNoTracking()
            .Where(n => n.RunId == run.Value && n.NodeId == "render")
            .OrderBy(n => n.Loop)
            .ToListAsync(CancellationToken.None);

        Assert.Equal(2, executions.Count);
        Assert.Equal(0, executions[0].Loop);
        Assert.Equal(1, executions[1].Loop);

        var stored = await db.Runs.AsNoTracking().SingleAsync(r => r.Id == run.Value, CancellationToken.None);

        Assert.Equal(1, stored.RetryLoop);
    }

    /// Boş bir node listesi istek SAYILMIYOR: "yeniden koş ama hiçbir
    /// şeyi koşma" anlamsız ve o hâlde run sessizce dururdu —
    /// kuyrukta iş kalmaz, kimse bir şeyin durduğunu fark etmez.
    [Fact]
    public async Task BosNodeListesi_RetrySayilmiyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var script = new ScriptedHandler("test.script", QueueClass.Llm, _ => ScriptedHandler.Json("{}"));
        var visual = new ScriptedHandler("test.visual", QueueClass.ImageGeneration, _ => ScriptedHandler.Json("{}"));
        var render = new ScriptedHandler("test.render", QueueClass.Render, _ => ScriptedHandler.Json("{}"));

        var qc = new ScriptedHandler("test.qc", QueueClass.Search,
            _ => ScriptedHandler.Json("""{"retry":{"decision":"Rerun","nodes":[]}}"""));

        var engine = new WorkflowEngine(db, new JobQueue(db),
            new NodeRegistry().Register(script).Register(visual).Register(render).Register(qc));

        var versionId = await CreateWorkflowAsync(db, Graph());
        var run = await engine.StartRunAsync(versionId, null, null, CancellationToken.None);

        await DrainAsync(engine);

        var stored = await db.Runs.AsNoTracking().SingleAsync(r => r.Id == run.Value, CancellationToken.None);

        // Run TAMAMLANDI, asılı kalmadı.
        Assert.Equal(RunState.Completed, stored.State);
        Assert.Equal(0, stored.RetryLoop);
    }

    /// Sözleşme: motor node TİPİNE değil ÇIKTIYA bakıyor.
    [Theory]
    [InlineData("""{"retry":{"decision":"None","nodes":["a"]}}""")]
    [InlineData("""{"retry":{"decision":"LoopLimitReached","nodes":["a"]}}""")]
    [InlineData("""{"retry":{"nodes":["a"]}}""")]
    [InlineData("""{"baska":"alan"}""")]
    public void RetryIstegi_YalnizcaRerunKararinda(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);

        Assert.Null(RerunRequest.From(document.RootElement));
    }

    [Fact]
    public void RetryIstegi_NodeListesiniOkuyor()
    {
        using var document = System.Text.Json.JsonDocument.Parse(
            """{"retry":{"decision":"Rerun","reason":"x","nodes":["a","b",""]}}""");

        var request = RerunRequest.From(document.RootElement);

        Assert.NotNull(request);
        Assert.Equal(2, request.Nodes.Count);
        Assert.Equal("x", request.Reason);
    }
}
