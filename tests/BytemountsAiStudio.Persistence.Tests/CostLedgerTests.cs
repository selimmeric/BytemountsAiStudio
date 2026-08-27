using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Persistence.Entities;
using BytemountsAiStudio.Persistence.Providers;
using BytemountsAiStudio.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Persistence.Tests;

[Collection(DatabaseCollection.Name)]
public sealed class CostLedgerTests(DatabaseFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (!fixture.Available)
        {
            return;
        }

        await using var db = fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM provider_calls");
        BudgetGate.KillSwitchEngaged = false;
    }

    public Task DisposeAsync()
    {
        BudgetGate.KillSwitchEngaged = false;
        return Task.CompletedTask;
    }

    private void RequireDatabase()
        => Assert.True(fixture.Available, $"PostgreSQL erişilemiyor ({fixture.UnavailableReason}).");

    private static ProviderCallRecord Record(decimal cost, bool succeeded = true) => new()
    {
        ProviderKey = "test",
        Operation = "complete",
        Units = UsageUnits.Tokens(100, 50),
        Cost = cost,
        LatencyMs = 42,
        Succeeded = succeeded,
    };

    [Fact]
    public async Task BasarisizCagrilarDa_DeftereYazilir()
    {
        // Başarısız çağrı da sağlayıcı tarafında ücretlendirilmiş olabilir.
        RequireDatabase();
        await using var db = fixture.CreateContext();
        var ledger = new CostLedger(db);

        await ledger.RecordAsync(Record(0.01m), CancellationToken.None);
        await ledger.RecordAsync(Record(0.02m, succeeded: false), CancellationToken.None);

        var calls = await db.ProviderCalls.AsNoTracking().ToListAsync(CancellationToken.None);

        Assert.Equal(2, calls.Count);
        Assert.Contains(calls, c => !c.Succeeded);
        Assert.Equal(0.03m, calls.Sum(c => c.Cost));
    }

    [Fact]
    public async Task BirimlerJsonOlarakSaklanir()
    {
        // Maliyet fiyattan türetiliyor; birim sayısı ham hâliyle duruyor ki
        // fiyat değişince geçmiş yeniden hesaplanabilsin.
        RequireDatabase();
        await using var db = fixture.CreateContext();

        await new CostLedger(db).RecordAsync(Record(0.05m), CancellationToken.None);

        var stored = await db.ProviderCalls.AsNoTracking().FirstAsync(CancellationToken.None);

        Assert.Contains("InputTokens", stored.UnitsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GunlukButceAsilinca_KaynakHatasiDoner()
    {
        // ADR-011: bütçe bitişi başarısızlık değil erteleme. Kalıcı hata
        // olsaydı bütçe dolduğu gün tüm run'lar ölürdü.
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var channel = new Channel
        {
            Name = "Butce " + Guid.NewGuid().ToString("N")[..6],
            Language = "tr-TR",
            DailyBudget = 0.10m,
        };
        db.Channels.Add(channel);
        await db.SaveChangesAsync(CancellationToken.None);

        var run = await CreateRunAsync(db, channel.Id);
        var ledger = new CostLedger(db) { RunId = run };
        var gate = new BudgetGate(db, ledger);

        await ledger.RecordAsync(Record(0.09m), CancellationToken.None);

        var allowed = await gate.AuthorizeAsync(channel.Id, 0.005m, CancellationToken.None);
        var denied = await gate.AuthorizeAsync(channel.Id, 0.05m, CancellationToken.None);

        Assert.True(allowed.IsSuccess);
        Assert.True(denied.IsFailure);
        Assert.Equal(ErrorKind.Resource, denied.Error.Kind);
        Assert.Equal("budget.daily_exceeded", denied.Error.Code);
    }

    [Fact]
    public async Task KillSwitch_TumUcretliCagrilariDurdurur()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();
        var gate = new BudgetGate(db, new CostLedger(db));

        BudgetGate.KillSwitchEngaged = true;

        var result = await gate.AuthorizeAsync(null, 0.001m, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("budget.kill_switch", result.Error.Code);
    }

    [Fact]
    public async Task ButcesizKanal_Sinirsiz()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var channel = new Channel
        {
            Name = "Sinirsiz " + Guid.NewGuid().ToString("N")[..6],
            Language = "tr-TR",
        };
        db.Channels.Add(channel);
        await db.SaveChangesAsync(CancellationToken.None);

        var gate = new BudgetGate(db, new CostLedger(db));

        Assert.True((await gate.AuthorizeAsync(channel.Id, 999m, CancellationToken.None)).IsSuccess);
    }

    private static async Task<Guid> CreateRunAsync(StudioDbContext db, Guid channelId)
    {
        var workflow = new Entities.Workflow
        {
            Key = "c-" + Guid.NewGuid().ToString("N")[..8],
            Name = "maliyet",
            CurrentVersion = 1,
        };
        var version = new WorkflowVersion { Version = 1, GraphJson = "{}" };
        workflow.Versions.Add(version);
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync(CancellationToken.None);

        var run = new Run { WorkflowVersionId = version.Id, ChannelId = channelId };
        db.Runs.Add(run);
        await db.SaveChangesAsync(CancellationToken.None);

        return run.Id;
    }
}
