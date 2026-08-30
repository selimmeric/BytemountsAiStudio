using BytemountsAiStudio.Api;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Persistence;
using BytemountsAiStudio.Persistence.Entities;
using BytemountsAiStudio.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Api.Tests;

/// API sorgularının testleri (P1-28).
///
/// Gerçek veritabanına koşuyor: sorguların çoğu gruplama, birleştirme
/// ve sıralama yapıyor ve bunların doğruluğu ancak SQL üretilip
/// çalıştırıldığında görülüyor. Bellek içi bir sağlayıcı, üretilen
/// sorgunun geçerli olduğuna dair hiçbir şey söylemez.
[Collection(DatabaseCollection.Name)]
public sealed class RunQueriesTests(DatabaseFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (!fixture.Available)
        {
            return;
        }

        await using var db = fixture.CreateContext();

        // Kendi actigimizi temizliyoruz: bu depoda iki kez, testlerin
        // birbirinin verisini bozmasi yuzunden CI kirmizi yandi.
        await db.Database.ExecuteSqlRawAsync("DELETE FROM provider_calls");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM run_events");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM approvals");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM node_executions");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM jobs");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM runs");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private void RequireDatabase()
        => Assert.True(fixture.Available, $"PostgreSQL erişilemiyor ({fixture.UnavailableReason}).");

    private static async Task<Guid> SeedRunAsync(
        StudioDbContext db, RunState state, decimal cost = 0, Guid? channelId = null)
    {
        var workflow = new Persistence.Entities.Workflow
        {
            Key = "api-" + Guid.NewGuid().ToString("N")[..8],
            Name = "API testi",
            CurrentVersion = 1,
        };

        var version = new WorkflowVersion { Version = 1, GraphJson = """{"key":"t","name":"t","nodes":[],"edges":[]}""" };
        workflow.Versions.Add(version);
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync(CancellationToken.None);

        var run = new Run
        {
            WorkflowVersionId = version.Id,
            ChannelId = channelId,
            State = state,
            ActualCost = cost,
            StartedAt = DateTimeOffset.UtcNow,
        };

        db.Runs.Add(run);
        await db.SaveChangesAsync(CancellationToken.None);

        return run.Id;
    }

    [Fact]
    public async Task Liste_DurumaGoreSuzuluyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        await SeedRunAsync(db, RunState.Completed);
        await SeedRunAsync(db, RunState.Failed);
        await SeedRunAsync(db, RunState.Failed);

        var failed = await RunQueries.ListAsync(db, RunState.Failed, null, 50, CancellationToken.None);
        var all = await RunQueries.ListAsync(db, null, null, 50, CancellationToken.None);

        Assert.Equal(2, failed.Count);
        Assert.Equal(3, all.Count);
    }

    /// Sınırsız bir liste, bir yıllık koşu birikince hem paneli hem
    /// veritabanını kilitlerdi.
    [Fact]
    public async Task Liste_SinirUygulaniyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        for (var i = 0; i < 5; i++)
        {
            await SeedRunAsync(db, RunState.Completed);
        }

        Assert.Equal(2, (await RunQueries.ListAsync(db, null, null, 2, CancellationToken.None)).Count);

        // Sıfır ya da negatif bir sınır, boş liste değil EN AZ BİR
        // kayıt vermeli: "sınır hatalı" ile "hiç run yok" farklı
        // sorunlar ve ikisi panelde aynı görünmemeli.
        Assert.NotEmpty(await RunQueries.ListAsync(db, null, null, 0, CancellationToken.None));
    }

    [Fact]
    public async Task Detay_ZamanCizelgesiVeLoglariIceriyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var runId = await SeedRunAsync(db, RunState.Failed);

        db.NodeExecutions.Add(new NodeExecution
        {
            RunId = runId,
            NodeId = "yaz",
            NodeType = "script.generate",
            State = NodeState.Succeeded,
            Attempt = 1,
            DurationMs = 1200,
            IdempotencyKey = "k1",
        });

        db.NodeExecutions.Add(new NodeExecution
        {
            RunId = runId,
            NodeId = "ses",
            NodeType = "tts.synthesize",
            State = NodeState.Failed,
            Attempt = 2,
            DurationMs = 300,
            IdempotencyKey = "k2",
            ErrorJson = """{"code":"tts.no_voice","message":"ses yok"}""",
        });

        db.RunEvents.Add(new RunEvent { RunId = runId, Level = "warn", Message = "ertelendi" });

        await db.SaveChangesAsync(CancellationToken.None);

        var detail = await RunQueries.DetailAsync(db, runId, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal(2, detail.Timeline.Count);
        Assert.Equal(1, detail.Run.FailedNodes);
        Assert.Single(detail.Events);

        var failedNode = detail.Timeline.Single(t => t.State == NodeState.Failed);

        // "Bu video neden böyle oldu" sorusunun cevabı: hangi node,
        // kaçıncı denemede, hangi hatayla.
        Assert.Equal("tts.no_voice", failedNode.ErrorCode);
        Assert.Equal(2, failedNode.Attempt);
    }

    [Fact]
    public async Task OlmayanRun_NullDoner()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        Assert.Null(await RunQueries.DetailAsync(db, Guid.CreateVersion7(), CancellationToken.None));
    }

    /// Maliyet kırılımı `provider_calls`'tan geliyor: `runs.actual_cost`
    /// tek bir sayı ve "neden bu kadar" sorusuna cevap vermiyor.
    [Fact]
    public async Task Maliyet_SaglayiciBazindaKirilıyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var runId = await SeedRunAsync(db, RunState.Completed, cost: 0.30m);

        db.ProviderCalls.AddRange(
            new ProviderCall
            {
                RunId = runId, ProviderKey = "ollama", Operation = "complete",
                Cost = 0m, LatencyMs = 900, Succeeded = true,
            },
            new ProviderCall
            {
                RunId = runId, ProviderKey = "elevenlabs", Operation = "tts",
                Cost = 0.20m, LatencyMs = 1500, Succeeded = true,
            },
            new ProviderCall
            {
                RunId = runId, ProviderKey = "elevenlabs", Operation = "tts",
                Cost = 0.10m, LatencyMs = 400, Succeeded = false,
            });

        await db.SaveChangesAsync(CancellationToken.None);

        var costs = await RunQueries.CostsAsync(db, runId, CancellationToken.None);

        // En pahalı önce.
        Assert.Equal("elevenlabs", costs[0].ProviderKey);
        Assert.Equal(0.30m, costs[0].Cost);
        Assert.Equal(2, costs[0].Calls);

        // BAŞARISIZ çağrı da sayılıyor: para harcamış olabilir ve
        // "maliyet yüksek ama video yok" durumunun tek açıklaması bu.
        Assert.Equal(1, costs[0].Failures);
    }

    [Fact]
    public async Task Ilerleme_TamamlananVeBekleyeniSayiyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var runId = await SeedRunAsync(db, RunState.Running);

        db.NodeExecutions.Add(new NodeExecution
        {
            RunId = runId, NodeId = "a", NodeType = "test.a",
            State = NodeState.Succeeded, Attempt = 1, DurationMs = 10, IdempotencyKey = "p1",
        });

        db.Jobs.Add(new Job
        {
            Queue = QueueClass.Llm, RunId = runId, NodeId = "b",
            State = JobState.Pending, MaxAttempts = 3,
        });

        await db.SaveChangesAsync(CancellationToken.None);

        var progress = await RunQueries.ProgressAsync(db, runId, CancellationToken.None);

        Assert.NotNull(progress);
        Assert.Equal(1, progress.Completed);
        Assert.Equal(0, progress.Failed);
        Assert.Equal(1, progress.Pending);
        Assert.Equal("a", progress.CurrentNode);
    }

    /* ---- insan karari ---- */

    /// ***ONAY GEREKÇESİ KOŞU DETAYINDA GÖRÜNÜYOR.***
    ///
    /// `approvals.note` uzun süre YAZILIP hiçbir yerde
    /// GÖSTERİLMİYORDU: bir insan "bu videoyu şu yüzden reddettim"
    /// yazıyor ve o cümle bir daha kimsenin karşısına çıkmıyordu.
    /// Öğrenen sistemin (Faz 5) besleneceği veri budur ve okunmayan
    /// veri, olmayan veriyle aynı şey.
    ///
    /// Bekleyen onaylar ekranında DEĞİL burada: bekleyen bir onayın
    /// henüz notu yok — not karar anında yazılıyor.
    [Fact]
    public async Task Detay_OnayGerekcesiniDonduruyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var runId = await SeedRunAsync(db, RunState.Failed);

        db.Approvals.Add(new Approval
        {
            RunId = runId,
            NodeId = "onay",
            Reason = "Kanal onay modunda",
            State = ApprovalState.Rejected,
            DecidedBy = "selim",
            Note = "Üçüncü sahnedeki iddia kaynaksız.",
            DecidedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync(CancellationToken.None);

        var detail = await RunQueries.DetailAsync(db, runId, CancellationToken.None);

        Assert.NotNull(detail);

        var approval = Assert.Single(detail.Approvals);

        Assert.Equal("onay", approval.NodeId);
        Assert.Equal("Rejected", approval.State);
        Assert.Equal("selim", approval.DecidedBy);
        Assert.Equal("Üçüncü sahnedeki iddia kaynaksız.", approval.Note);
        Assert.NotNull(approval.DecidedAt);
    }

    /// ONAYSIZ KOŞUDA LİSTE BOŞ — ve bu bir hata değil.
    ///
    /// Otomatik modda hiç onay kaydı oluşmuyor; boş liste "onay
    /// istenmedi" demek. Null dönseydi panel bunu hata sanardı.
    [Fact]
    public async Task Detay_OnayYoksa_BosListe()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var runId = await SeedRunAsync(db, RunState.Completed);

        var detail = await RunQueries.DetailAsync(db, runId, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Empty(detail.Approvals);
    }

    [Fact]
    public async Task Ilerleme_OlmayanRunIcinNull()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        Assert.Null(await RunQueries.ProgressAsync(db, Guid.CreateVersion7(), CancellationToken.None));
    }
}
