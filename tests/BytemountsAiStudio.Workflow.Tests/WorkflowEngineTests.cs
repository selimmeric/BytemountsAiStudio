using System.Collections.Concurrent;
using System.Text.Json;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Persistence;
using BytemountsAiStudio.Persistence.Entities;
using BytemountsAiStudio.Queue;
using BytemountsAiStudio.TestSupport;
using BytemountsAiStudio.Workflow.Definition;
using BytemountsAiStudio.Workflow.Engine;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Workflow.Tests;

/// Kaydedilebilir sahte işleyici: ne döneceğini test söyler.
internal sealed class ScriptedHandler(
    string nodeType,
    QueueClass queue,
    Func<NodeContext, Result<JsonElement>> behaviour) : INodeHandler
{
    public string NodeType => nodeType;

    public QueueClass Queue => queue;

    public ConcurrentBag<string> Calls { get; } = [];

    public Task<Result<JsonElement>> ExecuteAsync(NodeContext context, CancellationToken cancellationToken)
    {
        Calls.Add(context.IdempotencyKey);
        return Task.FromResult(behaviour(context));
    }

    public static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
}

[Collection(DatabaseCollection.Name)]
public sealed class WorkflowEngineTests(DatabaseFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (!fixture.Available)
        {
            return;
        }

        await using var db = fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM jobs");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM runs");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private void RequireDatabase()
        => Assert.True(fixture.Available, $"PostgreSQL erişilemiyor ({fixture.UnavailableReason}).");

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

    private static WorkflowGraph LinearGraph() => new()
    {
        Key = "linear",
        Name = "Dogrusal",
        Nodes =
        [
            new() { Id = "a", Type = "test.a", Config = ScriptedHandler.Json("{}") },
            new() { Id = "b", Type = "test.b", Config = ScriptedHandler.Json("{}") },
        ],
        Edges = [new() { From = "a", To = "b" }],
    };

    /// Kuyruk boşalana kadar worker döngüsünü koştur.
    private static async Task DrainAsync(WorkflowEngine engine, int maxSteps = 40)
    {
        for (var i = 0; i < maxSteps; i++)
        {
            foreach (var queue in Enum.GetValues<QueueClass>())
            {
                await engine.ExecuteNextAsync("test-worker", queue, CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task DogrusalRun_TumNodelariSirayla_Calistirir()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var a = new ScriptedHandler("test.a", QueueClass.Llm, _ => ScriptedHandler.Json("""{"ok":true}"""));
        var b = new ScriptedHandler("test.b", QueueClass.Llm, _ => ScriptedHandler.Json("""{"done":1}"""));
        var registry = new NodeRegistry().Register(a).Register(b);
        var engine = new WorkflowEngine(db, new JobQueue(db), registry);

        var versionId = await CreateWorkflowAsync(db, LinearGraph());
        var runId = await engine.StartRunAsync(versionId, null, null, CancellationToken.None);

        Assert.True(runId.IsSuccess, runId.IsFailure ? runId.Error.Message : "");

        await DrainAsync(engine);

        var run = await db.Runs.AsNoTracking().SingleAsync(r => r.Id == runId.Value, CancellationToken.None);

        Assert.Single(a.Calls);
        Assert.Single(b.Calls);
        Assert.Equal(RunState.Completed, run.State);
    }

    [Fact]
    public async Task NodeCiktisi_SonrakiNodeaBaglamOlarakGecer()
    {
        // Node'lar birbirine run bağlamı üzerinden bağlanıyor. Bu çalışmazsa
        // workflow bir dizi bağımsız işten ibaret kalır.
        RequireDatabase();
        await using var db = fixture.CreateContext();

        string? seen = null;

        var a = new ScriptedHandler("test.a", QueueClass.Llm, _ => ScriptedHandler.Json("""{"topic":"uzay"}"""));
        var b = new ScriptedHandler("test.b", QueueClass.Llm, ctx =>
        {
            seen = ctx.RunContext.GetProperty("a").GetProperty("topic").GetString();
            return ScriptedHandler.Json("{}");
        });

        var engine = new WorkflowEngine(db, new JobQueue(db), new NodeRegistry().Register(a).Register(b));
        var versionId = await CreateWorkflowAsync(db, LinearGraph());
        await engine.StartRunAsync(versionId, null, null, CancellationToken.None);

        await DrainAsync(engine);

        Assert.Equal("uzay", seen);
    }

    [Fact]
    public async Task KosulSaglanmazsa_KenarIzlenmez()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var a = new ScriptedHandler("test.a", QueueClass.Llm, _ => ScriptedHandler.Json("""{"passed":false}"""));
        var b = new ScriptedHandler("test.b", QueueClass.Llm, _ => ScriptedHandler.Json("{}"));

        var graph = LinearGraph() with
        {
            Edges = [new() { From = "a", To = "b", When = "a.passed" }],
        };

        var engine = new WorkflowEngine(db, new JobQueue(db), new NodeRegistry().Register(a).Register(b));
        var versionId = await CreateWorkflowAsync(db, graph);
        await engine.StartRunAsync(versionId, null, null, CancellationToken.None);

        await DrainAsync(engine);

        Assert.Single(a.Calls);
        Assert.Empty(b.Calls);
    }

    [Fact]
    public async Task IsleyiciHatasi_RunuDusurur()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var a = new ScriptedHandler("test.a", QueueClass.Llm,
            _ => Error.Permanent("test.bozuk", "Olmadi"));
        var b = new ScriptedHandler("test.b", QueueClass.Llm, _ => ScriptedHandler.Json("{}"));

        var engine = new WorkflowEngine(db, new JobQueue(db), new NodeRegistry().Register(a).Register(b));
        var versionId = await CreateWorkflowAsync(db, LinearGraph());
        var runId = await engine.StartRunAsync(versionId, null, null, CancellationToken.None);

        await DrainAsync(engine);

        var run = await db.Runs.AsNoTracking().SingleAsync(r => r.Id == runId.Value, CancellationToken.None);

        Assert.Equal(RunState.Failed, run.State);
        Assert.Empty(b.Calls);
        Assert.Contains("test.bozuk", run.ErrorJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IsleyiciIstisnaAtarsa_WorkerDusmez()
    {
        // Bir node'un beklenmedik istisnası tüm worker'ı düşürürse, tek bozuk
        // içerik bütün üretimi durdurur.
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var a = new ScriptedHandler("test.a", QueueClass.Llm,
            _ => throw new InvalidOperationException("beklenmedik"));

        var engine = new WorkflowEngine(db, new JobQueue(db),
            new NodeRegistry().Register(a).Register(
                new ScriptedHandler("test.b", QueueClass.Llm, _ => ScriptedHandler.Json("{}"))));

        var versionId = await CreateWorkflowAsync(db, LinearGraph());
        var runId = await engine.StartRunAsync(versionId, null, null, CancellationToken.None);

        await DrainAsync(engine);

        var run = await db.Runs.AsNoTracking().SingleAsync(r => r.Id == runId.Value, CancellationToken.None);

        Assert.Equal(RunState.Failed, run.State);
    }

    [Fact]
    public async Task KaynakHatasi_RunuDusurmezBekletir()
    {
        // ADR-011: kota bitişi başarısızlık değil. Run düşseydi kotası dolan
        // her kanal her gün bir sürü ölü run biriktirirdi.
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var a = new ScriptedHandler("test.a", QueueClass.Upload,
            _ => Error.Resource("quota", "Kota doldu", TimeSpan.FromHours(6)));

        var graph = LinearGraph() with
        {
            Nodes = [new() { Id = "a", Type = "test.a", Config = ScriptedHandler.Json("{}") }],
            Edges = [],
        };

        var engine = new WorkflowEngine(db, new JobQueue(db), new NodeRegistry().Register(a));
        var versionId = await CreateWorkflowAsync(db, graph);
        var runId = await engine.StartRunAsync(versionId, null, null, CancellationToken.None);

        await engine.ExecuteNextAsync("w1", QueueClass.Upload, CancellationToken.None);

        var run = await db.Runs.AsNoTracking().SingleAsync(r => r.Id == runId.Value, CancellationToken.None);

        Assert.Equal(RunState.WaitingResource, run.State);
    }

    [Fact]
    public async Task IptalEdilmisRun_BekleyenIsleriCalistirmaz()
    {
        // Kill-switch basıldıktan sonra kuyrukta kalanların para harcamaya
        // devam etmesi tam olarak engellemek istediğimiz şey.
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var a = new ScriptedHandler("test.a", QueueClass.Llm, _ => ScriptedHandler.Json("{}"));
        var engine = new WorkflowEngine(db, new JobQueue(db), new NodeRegistry().Register(a).Register(
            new ScriptedHandler("test.b", QueueClass.Llm, _ => ScriptedHandler.Json("{}"))));

        var versionId = await CreateWorkflowAsync(db, LinearGraph());
        var runId = await engine.StartRunAsync(versionId, null, null, CancellationToken.None);

        var run = await db.Runs.SingleAsync(r => r.Id == runId.Value, CancellationToken.None);
        run.State = RunState.Cancelled;
        await db.SaveChangesAsync(CancellationToken.None);

        await DrainAsync(engine);

        Assert.Empty(a.Calls);
    }

    [Fact]
    public async Task NodeCalistirmalari_KayitAltinaAlinir()
    {
        // "Bu video neden böyle oldu" sorusunun cevabı bu tablodan geliyor
        // (§17.4). Kayıt olmadan hiçbir kaliteyi iyileştiremezsiniz.
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var engine = new WorkflowEngine(db, new JobQueue(db), new NodeRegistry()
            .Register(new ScriptedHandler("test.a", QueueClass.Llm, _ => ScriptedHandler.Json("""{"x":1}""")))
            .Register(new ScriptedHandler("test.b", QueueClass.Llm, _ => ScriptedHandler.Json("""{"y":2}"""))));

        var versionId = await CreateWorkflowAsync(db, LinearGraph());
        var runId = await engine.StartRunAsync(versionId, null, null, CancellationToken.None);

        await DrainAsync(engine);

        var executions = await db.NodeExecutions.AsNoTracking()
            .Where(e => e.RunId == runId.Value)
            .OrderBy(e => e.CreatedAt)
            .ToListAsync(CancellationToken.None);

        Assert.Equal(2, executions.Count);
        Assert.All(executions, e => Assert.Equal(NodeState.Succeeded, e.State));
        Assert.All(executions, e => Assert.NotEmpty(e.IdempotencyKey));
        Assert.Contains(executions, e => e.OutputJson!.Contains("\"x\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BilinmeyenNodeTipi_RunuBaslatmaz()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var engine = new WorkflowEngine(db, new JobQueue(db), new NodeRegistry());
        var versionId = await CreateWorkflowAsync(db, LinearGraph());

        var result = await engine.StartRunAsync(versionId, null, null, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("engine.invalid_graph", result.Error.Code);
    }
}

public sealed class IdempotencyKeyTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void AyniGirdi_AyniAnahtar()
    {
        var runId = Guid.CreateVersion7();

        var first = IdempotencyKey.Compute(runId, "script", Json("""{"a":1}"""), Json("""{"b":2}"""));
        var second = IdempotencyKey.Compute(runId, "script", Json("""{"a":1}"""), Json("""{"b":2}"""));

        Assert.Equal(first, second);
    }

    [Fact]
    public void AlanSirasi_AnahtariDegistirmez()
    {
        // Kanonikleştirme olmasaydı aynı konfigürasyon farklı
        // serileştirmelerde farklı anahtar üretir ve idempotency sessizce
        // çalışmazdı — en sinsi başarısızlık türü.
        var runId = Guid.CreateVersion7();

        var first = IdempotencyKey.Compute(runId, "n", Json("""{"a":1,"b":2}"""), Json("{}"));
        var second = IdempotencyKey.Compute(runId, "n", Json("""{"b":2,"a":1}"""), Json("{}"));

        Assert.Equal(first, second);
    }

    [Fact]
    public void FarkliGirdi_FarkliAnahtar()
    {
        var runId = Guid.CreateVersion7();

        Assert.NotEqual(
            IdempotencyKey.Compute(runId, "n", Json("""{"a":1}"""), Json("{}")),
            IdempotencyKey.Compute(runId, "n", Json("""{"a":2}"""), Json("{}")));
    }

    [Fact]
    public void FarkliRun_FarkliAnahtar()
    {
        Assert.NotEqual(
            IdempotencyKey.Compute(Guid.CreateVersion7(), "n", Json("{}"), Json("{}")),
            IdempotencyKey.Compute(Guid.CreateVersion7(), "n", Json("{}"), Json("{}")));
    }
}
