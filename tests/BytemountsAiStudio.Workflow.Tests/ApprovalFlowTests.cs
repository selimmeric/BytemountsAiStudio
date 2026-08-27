using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Persistence;
using BytemountsAiStudio.Persistence.Entities;
using BytemountsAiStudio.Queue;
using BytemountsAiStudio.TestSupport;
using BytemountsAiStudio.Workflow.Definition;
using BytemountsAiStudio.Workflow.Engine;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Workflow.Tests;

/// Onay akışının testleri (P1-27).
///
/// Kabul kriteri tek cümle: **onay bekleyen run worker kaynağı
/// tüketmiyor**. Bunu sınamanın tek yolu kuyruğa bakmak — park edilmiş
/// bir run'ın kuyrukta bekleyen işi olmamalı.
[Collection(DatabaseCollection.Name)]
public sealed class ApprovalFlowTests(DatabaseFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (!fixture.Available)
        {
            return;
        }

        await using var db = fixture.CreateContext();

        // Kendi actigimizi temizliyoruz. Bu depoda iki kez, testlerin
        // birbirinin verisini bozmasi yuzunden CI kirmizi yandi.
        await db.Database.ExecuteSqlRawAsync("DELETE FROM approvals");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM jobs");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM runs");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private void RequireDatabase()
        => Assert.True(fixture.Available, $"PostgreSQL erişilemiyor ({fixture.UnavailableReason}).");

    /// senaryo → onay → yayın.
    private static WorkflowGraph GateGraph(string mode, double minScore = 0.75) => new()
    {
        Key = "onay",
        Name = "Onayli hat",
        Nodes =
        [
            new() { Id = "yaz", Type = "test.a", Config = ScriptedHandler.Json("{}") },
            new()
            {
                Id = "onay",
                Type = "human.approval",
                Config = ScriptedHandler.Json(
                    $$"""{"mode":"{{mode}}","min_score":{{minScore.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}"""),
            },
            new() { Id = "yayin", Type = "test.b", Config = ScriptedHandler.Json("{}") },
        ],
        Edges = [new() { From = "yaz", To = "onay" }, new() { From = "onay", To = "yayin" }],
    };

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

    private static async Task DrainAsync(WorkflowEngine engine, int maxSteps = 20)
    {
        for (var i = 0; i < maxSteps; i++)
        {
            foreach (var queue in Enum.GetValues<QueueClass>())
            {
                await engine.ExecuteNextAsync("test-worker", queue, CancellationToken.None);
            }
        }
    }

    private sealed record Harness(
        StudioDbContext Db, WorkflowEngine Engine, ApprovalService Approvals,
        ScriptedHandler Publish, Guid RunId);

    /// Onay kapısı SÖZLEŞMEYİ üretiyor, gerçek işleyiciyi değil.
    ///
    /// Burada sınanan şey MOTOR: `awaiting_approval` gören motor run'ı
    /// park ediyor mu. Gerçek `ApprovalGateHandler` bağlanmış olsaydı
    /// test, kapının karar mantığına da bağlanırdı — o mantığın kendi
    /// testleri ayrı (ApprovalGateHandlerTests) ve orada veritabanı
    /// gerekmiyor.
    private static async Task<Harness> StartAsync(StudioDbContext db, string mode)
    {
        var write = new ScriptedHandler("test.a", QueueClass.Llm, _ => ScriptedHandler.Json("{}"));
        var publish = new ScriptedHandler("test.b", QueueClass.Upload, _ => ScriptedHandler.Json("{}"));

        var awaiting = mode == "approval" ? "true" : "false";

        var gate = new ScriptedHandler("human.approval", QueueClass.Search,
            _ => ScriptedHandler.Json(
                "{\"awaiting_approval\":" + awaiting + ",\"reason\":\"test kapisi\"}"));

        var registry = new NodeRegistry()
            .Register(write)
            .Register(publish)
            .Register(gate);

        var engine = new WorkflowEngine(db, new JobQueue(db), registry);
        var versionId = await CreateWorkflowAsync(db, GateGraph(mode));

        var run = await engine.StartRunAsync(versionId, null, null, CancellationToken.None);
        Assert.True(run.IsSuccess, run.IsFailure ? run.Error.Message : string.Empty);

        await DrainAsync(engine);

        return new Harness(db, engine, new ApprovalService(db, engine), publish, run.Value);
    }

    /// KABUL KRİTERİ: park edilmiş run'ın kuyrukta bekleyen işi yok.
    [Fact]
    public async Task OnayBekleyenRun_KuyruktaIsBirakmiyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var harness = await StartAsync(db, "approval");

        var run = await db.Runs.AsNoTracking().SingleAsync(r => r.Id == harness.RunId, CancellationToken.None);

        Assert.Equal(RunState.WaitingApproval, run.State);

        var waiting = await db.Jobs.AsNoTracking()
            .CountAsync(j => j.RunId == harness.RunId
                             && (j.State == JobState.Pending || j.State == JobState.Leased),
                CancellationToken.None);

        Assert.Equal(0, waiting);

        // Yayın node'u HİÇ çalışmadı: kapı gerçekten durdurdu.
        Assert.Empty(harness.Publish.Calls);
    }

    [Fact]
    public async Task OnayKaydi_GerekceyleOlusuyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var harness = await StartAsync(db, "approval");

        var approval = await db.Approvals.AsNoTracking()
            .SingleAsync(a => a.RunId == harness.RunId, CancellationToken.None);

        Assert.Equal(ApprovalState.Pending, approval.State);
        Assert.Equal("onay", approval.NodeId);
        Assert.NotEmpty(approval.Reason);
    }

    /// Otonom kanalda kapı park ETMİYOR ve run sonuna kadar gidiyor.
    [Fact]
    public async Task OtonomKanal_ParkEtmedenTamamlaniyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var harness = await StartAsync(db, "auto");

        var run = await db.Runs.AsNoTracking().SingleAsync(r => r.Id == harness.RunId, CancellationToken.None);

        Assert.Equal(RunState.Completed, run.State);
        Assert.Empty(await db.Approvals.AsNoTracking().ToListAsync(CancellationToken.None));
        Assert.Single(harness.Publish.Calls);
    }

    [Fact]
    public async Task Onay_RunuKaldigiYerdenSurduruyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var harness = await StartAsync(db, "approval");

        var approval = await db.Approvals.SingleAsync(a => a.RunId == harness.RunId, CancellationToken.None);

        var result = await harness.Approvals.ApproveAsync(
            approval.Id, "selim", "iyi görünüyor", CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);

        await DrainAsync(harness.Engine);

        var run = await db.Runs.AsNoTracking().SingleAsync(r => r.Id == harness.RunId, CancellationToken.None);

        Assert.Equal(RunState.Completed, run.State);
        Assert.Single(harness.Publish.Calls);

        var decided = await db.Approvals.AsNoTracking().SingleAsync(a => a.Id == approval.Id, CancellationToken.None);

        Assert.Equal(ApprovalState.Approved, decided.State);
        Assert.Equal("selim", decided.DecidedBy);
        Assert.NotNull(decided.DecidedAt);
    }

    /// Reddetmek run'ı İPTAL ediyor, BAŞARISIZ değil: bir insan "bu
    /// yayınlanmasın" dedi ve bu sistemin doğru çalıştığının kanıtı.
    /// `Failed` işaretlemek, hata panellerini insan kararlarıyla
    /// doldurup gerçek arızaları görünmez kılardı.
    [Fact]
    public async Task Ret_RunuIptalEdiyor_BasarisizDegil()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var harness = await StartAsync(db, "approval");
        var approval = await db.Approvals.SingleAsync(a => a.RunId == harness.RunId, CancellationToken.None);

        await harness.Approvals.RejectAsync(approval.Id, "selim", "iddia desteksiz", CancellationToken.None);
        await DrainAsync(harness.Engine);

        var run = await db.Runs.AsNoTracking().SingleAsync(r => r.Id == harness.RunId, CancellationToken.None);

        Assert.Equal(RunState.Cancelled, run.State);
        Assert.NotEqual(RunState.Failed, run.State);
        Assert.Empty(harness.Publish.Calls);
    }

    /// İki kişi paneli aynı anda açıp ikisi de onaylarsa, ikinci karar
    /// sonraki node'ları BİR KEZ DAHA kuyruğa atardı: aynı video iki
    /// kez render edilir, iki kez yüklenirdi.
    [Fact]
    public async Task IkinciKarar_Reddediliyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var harness = await StartAsync(db, "approval");
        var approval = await db.Approvals.SingleAsync(a => a.RunId == harness.RunId, CancellationToken.None);

        await harness.Approvals.ApproveAsync(approval.Id, "selim", null, CancellationToken.None);

        var second = await harness.Approvals.ApproveAsync(approval.Id, "baska-kisi", null, CancellationToken.None);

        Assert.True(second.IsFailure);
        Assert.Equal("approval.already_decided", second.Error.Code);

        await DrainAsync(harness.Engine);

        // Yayın node'u YALNIZCA BİR KEZ çalıştı.
        Assert.Single(harness.Publish.Calls);
    }

    [Fact]
    public async Task BekleyenListesi_EnEskiOnce()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var first = await StartAsync(db, "approval");
        var second = await StartAsync(db, "approval");

        var pending = await first.Approvals.PendingAsync(null, 10, CancellationToken.None);

        Assert.Equal(2, pending.Count);

        // EN ESKİ ÖNCE: onay kuyruğu bir yığın değil sıra. En yeniyi
        // üstte göstermek, yoğun bir günde en eski videoların hiç
        // bakılmadan kalması demekti.
        Assert.Equal(first.RunId, pending[0].RunId);
        Assert.Equal(second.RunId, pending[1].RunId);
        Assert.All(pending, p => Assert.NotEmpty(p.Reason));
    }

    [Fact]
    public async Task KararaBaglananlar_BekleyenListesindeGorunmuyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var harness = await StartAsync(db, "approval");
        var approval = await db.Approvals.SingleAsync(a => a.RunId == harness.RunId, CancellationToken.None);

        await harness.Approvals.ApproveAsync(approval.Id, "selim", null, CancellationToken.None);

        Assert.Empty(await harness.Approvals.PendingAsync(null, 10, CancellationToken.None));
    }

    [Fact]
    public async Task OlmayanOnay_AcikHataVeriyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var harness = await StartAsync(db, "approval");

        var result = await harness.Approvals.ApproveAsync(
            Guid.CreateVersion7(), "selim", null, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("approval.not_found", result.Error.Code);
    }
}
