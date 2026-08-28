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
        await db.Database.ExecuteSqlRawAsync("DELETE FROM settings");

        // Surec geneli onbellek: bosaltilmazsa bir onceki testin
        // durdurma bayragi bu testte de gorunur. Bu depoda paylasilan
        // durumun komsu testleri kirmasi CI'i iki kez kirmizi yakti.
        SystemControl.Invalidate();
    }

    public Task DisposeAsync()
    {
        SystemControl.Invalidate();
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

        // YARIM KALMIŞ RUN GÜNLÜK BÜTÇEYİ AŞABİLİYOR (P2-03).
        //
        // Bu kapı bir node çalışırken soruluyor; yani her çağrı
        // tanımı gereği "yarım kalmış". Durdurmak, o ana kadar
        // harcanan her kuruşu çöpe atmak ve ertesi gün aynı adımları
        // İKİNCİ KEZ ödemek olurdu. Yeni run'ları durdurma kararı
        // zamanlayıcıda (`RunPlanner`) veriliyor.
        Assert.True((await gate.AuthorizeAsync(channel.Id, 0.005m, CancellationToken.None)).IsSuccess);
        Assert.True((await gate.AuthorizeAsync(channel.Id, 0.05m, CancellationToken.None)).IsSuccess);

        // ...AMA `StopEverything` seçilmişse aşamıyor: bütçeyi kesin
        // sınır olarak isteyenin de bir yolu olmalı.
        channel.SettingsJson = """{"action_on_exceed":"stop"}""";
        await db.SaveChangesAsync(CancellationToken.None);

        var denied = await gate.AuthorizeAsync(channel.Id, 0.05m, CancellationToken.None);

        Assert.True(denied.IsFailure);
        Assert.Equal(ErrorKind.Resource, denied.Error.Kind);
        Assert.Equal("budget.exceeded", denied.Error.Code);
        Assert.Contains("günlük", denied.Error.Message, StringComparison.Ordinal);
    }

    /// VİDEO BAŞINA TAVAN AŞILAMIYOR.
    ///
    /// "Yarım kalanı bitir" kuralının olmazsa olmaz karşılığı bu:
    /// tavan olmasaydı bir kez başlamış bir run günlük bütçeyi
    /// sınırsız aşabilirdi — sürekli çağrı yapan bir node, "zaten
    /// başlamıştı" gerekçesiyle ayın tamamını harcayabilirdi.
    [Fact]
    public async Task VideoTavani_YarimRunuDaDurduruyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var channel = new Channel
        {
            Name = "Tavan " + Guid.NewGuid().ToString("N")[..6],
            Language = "tr-TR",
            DailyBudget = 100m,
            MaxCostPerVideo = 0.10m,
        };

        db.Channels.Add(channel);
        await db.SaveChangesAsync(CancellationToken.None);

        var run = await CreateRunAsync(db, channel.Id);
        var ledger = new CostLedger(db) { RunId = run };
        var gate = new BudgetGate(db, ledger);

        await ledger.RecordAsync(Record(0.09m), CancellationToken.None);

        Assert.True((await gate.AuthorizeAsync(channel.Id, 0.005m, CancellationToken.None)).IsSuccess);

        var denied = await gate.AuthorizeAsync(channel.Id, 0.05m, CancellationToken.None);

        Assert.True(denied.IsFailure);
        Assert.Equal("budget.video_cap", denied.Error.Code);

        // KAYNAK hatası: insan tavanı büyütüp devam ettirebilsin.
        // Kalıcı olsaydı yarım video doğrudan çöpe giderdi.
        Assert.Equal(ErrorKind.Resource, denied.Error.Kind);
    }

    /// GLOBAL AYLIK LİMİT eskiden hiç bakılmıyordu: kanal
    /// limitlerinin toplamı aylık limiti aşabiliyor ve aşınca kimse
    /// durdurmuyordu.
    [Fact]
    public async Task AylikLimit_StopIleDurduruyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var channel = new Channel
        {
            Name = "Aylik " + Guid.NewGuid().ToString("N")[..6],
            Language = "tr-TR",
            SettingsJson = """{"action_on_exceed":"stop"}""",
        };

        db.Channels.Add(channel);
        db.Settings.RemoveRange(db.Settings.Where(s => s.Key == RunPlanner.MonthlyBudgetKey));
        db.Settings.Add(new Setting { Key = RunPlanner.MonthlyBudgetKey, Value = "0.05" });
        await db.SaveChangesAsync(CancellationToken.None);

        var run = await CreateRunAsync(db, channel.Id);
        var ledger = new CostLedger(db) { RunId = run };
        var gate = new BudgetGate(db, ledger);

        await ledger.RecordAsync(Record(0.04m), CancellationToken.None);

        var denied = await gate.AuthorizeAsync(channel.Id, 0.05m, CancellationToken.None);

        Assert.True(denied.IsFailure);
        Assert.Contains("aylık", denied.Error.Message, StringComparison.Ordinal);

        db.Settings.RemoveRange(db.Settings.Where(s => s.Key == RunPlanner.MonthlyBudgetKey));
        await db.SaveChangesAsync(CancellationToken.None);
    }

    [Fact]
    public async Task KillSwitch_TumUcretliCagrilariDurdurur()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();
        var control = new SystemControl(db);
        var gate = new BudgetGate(db, new CostLedger(db), control);

        await control.SetKillSwitchAsync(true, "selim", "test", CancellationToken.None);

        var result = await gate.AuthorizeAsync(null, 0.001m, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("budget.kill_switch", result.Error.Code);

        // KIM BASTI ve NEDEN, hata mesajinda: acil durdurma gibi bir
        // dugmede ilk sorulacak sey bu.
        Assert.Contains("selim", result.Error.Message, StringComparison.Ordinal);
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
