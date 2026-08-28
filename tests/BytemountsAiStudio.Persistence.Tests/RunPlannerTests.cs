using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Persistence.Entities;
using BytemountsAiStudio.Persistence.Providers;
using BytemountsAiStudio.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Persistence.Tests;

/// Zamanlayıcının karar merkezi (P2-01/02/03/12).
///
/// Kabul kriteri: **gece boyunca kendi başına çalışsın.** O da şuna
/// bağlı — her "hayır" doğru sebeple verilsin. Yanlış bir hayır sabaha
/// hiç video olmaması, yanlış bir evet bütçenin gece boyunca
/// tükenmesi demek.
[Collection(DatabaseCollection.Name)]
public sealed class RunPlannerTests(DatabaseFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Noon = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    public async Task InitializeAsync()
    {
        if (!fixture.Available)
        {
            return;
        }

        await using var db = fixture.CreateContext();

        await db.Database.ExecuteSqlRawAsync("DELETE FROM provider_calls");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM node_executions");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM run_events");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM jobs");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM runs");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM topics");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM channels");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM workflows");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM settings");

        SystemControl.Invalidate();
    }

    public Task DisposeAsync()
    {
        SystemControl.Invalidate();
        return Task.CompletedTask;
    }

    private void RequireDatabase()
        => Assert.True(fixture.Available, $"PostgreSQL erişilemiyor ({fixture.UnavailableReason}).");

    private static RunPlanner Planner(StudioDbContext db, TimeProvider time)
        => new(db, new SystemControl(db, time), new CostLedger(db, time), new TopicPool(db), time);

    /// GERÇEK bir iş akışı sürümü: `runs.workflow_version_id` yabancı
    /// anahtar taşıyor ve uydurma bir kimlik kısıtı ihlal ediyor.
    private static async Task<Guid> VersionAsync(StudioDbContext db)
    {
        var workflow = new Persistence.Entities.Workflow
        {
            Key = "planner-" + Guid.NewGuid().ToString("N")[..8],
            Name = "Planlayici testi",
            CurrentVersion = 1,
        };

        var version = new WorkflowVersion { Version = 1, GraphJson = "{}" };
        workflow.Versions.Add(version);
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync(CancellationToken.None);

        return version.Id;
    }

    private static async Task<Channel> ChannelAsync(
        StudioDbContext db, string settings = """{"daily_target":3}""", bool withTopic = true)
    {
        var channel = new Channel
        {
            Name = "Test kanalı",
            Language = "tr-TR",
            SettingsJson = settings,
        };

        db.Channels.Add(channel);

        if (withTopic)
        {
            db.Topics.Add(new Topic
            {
                ChannelId = channel.Id,
                Title = "Hazır konu",
                Language = "tr-TR",
                State = TopicState.Queued,
                OverallScore = 80,
            });
        }

        await db.SaveChangesAsync(CancellationToken.None);

        return channel;
    }

    [Fact]
    public async Task HerSeyYolunda_Baslatiyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var channel = await ChannelAsync(db);
        var verdict = await Planner(db, new FakeTimeProvider(Noon)).DecideAsync(channel, CancellationToken.None);

        Assert.True(verdict.ShouldStart, verdict.Reason);
    }

    /// ACİL DURDURMA HER ŞEYDEN ÖNCE: gerekçesi kimin bastığını da
    /// söylüyor, çünkü bu düğmede ilk sorulacak soru bu.
    [Fact]
    public async Task AcilDurdurma_HicbirKanaliBaslatmiyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var channel = await ChannelAsync(db);
        var time = new FakeTimeProvider(Noon);

        await new SystemControl(db, time)
            .SetKillSwitchAsync(true, "selim", "maliyet", CancellationToken.None);

        var verdict = await Planner(db, time).DecideAsync(channel, CancellationToken.None);

        Assert.False(verdict.ShouldStart);
        Assert.Contains("selim", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuraklatilmisKanal_Baslatilmiyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var channel = await ChannelAsync(db);
        channel.IsPaused = true;
        await db.SaveChangesAsync(CancellationToken.None);

        var verdict = await Planner(db, new FakeTimeProvider(Noon)).DecideAsync(channel, CancellationToken.None);

        Assert.False(verdict.ShouldStart);
        Assert.Contains("duraklat", verdict.Reason, StringComparison.Ordinal);
    }

    /// SÜREN BİR RUN VARKEN İKİNCİSİ BAŞLAMIYOR.
    ///
    /// Paralel run'lar bütçeyi QC'nin sorunu yakalamasından daha hızlı
    /// harcıyor: aynı kusuru taşıyan beş video aynı anda üretilirdi ve
    /// hedefli retry hiçbir şey öğretemezdi.
    [Fact]
    public async Task SurenRun_IkincisiniEngelliyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var channel = await ChannelAsync(db);

        db.Runs.Add(new Run
        {
            ChannelId = channel.Id,
            State = RunState.Running,
            WorkflowVersionId = await VersionAsync(db),
        });
        await db.SaveChangesAsync(CancellationToken.None);

        var verdict = await Planner(db, new FakeTimeProvider(Noon)).DecideAsync(channel, CancellationToken.None);

        Assert.False(verdict.ShouldStart);
        Assert.Contains("sürüyor", verdict.Reason, StringComparison.Ordinal);
    }

    /// ONAY BEKLEYEN RUN DA SÜRÜYOR SAYILIYOR.
    ///
    /// Saymasaydık, insan uyurken onay kuyruğu dolardı: her tur yeni
    /// bir video başlar, hiçbiri onaylanmaz ve sabaha kırk video
    /// birikirdi.
    [Fact]
    public async Task OnayBekleyenRun_YeniBaslatmayiEngelliyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var channel = await ChannelAsync(db);

        db.Runs.Add(new Run
        {
            ChannelId = channel.Id,
            State = RunState.WaitingApproval,
            WorkflowVersionId = await VersionAsync(db),
        });

        await db.SaveChangesAsync(CancellationToken.None);

        var verdict = await Planner(db, new FakeTimeProvider(Noon)).DecideAsync(channel, CancellationToken.None);

        Assert.False(verdict.ShouldStart);
    }

    /// BİTMİŞ RUN ENGEL DEĞİL: tamamlanan bir video, bir sonrakinin
    /// önünde durmamalı.
    [Fact]
    public async Task TamamlanmisRun_EngelDegil()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var channel = await ChannelAsync(db);

        db.Runs.Add(new Run
        {
            ChannelId = channel.Id,
            State = RunState.Completed,
            WorkflowVersionId = await VersionAsync(db),
            CreatedAt = Noon.AddHours(-5),
        });

        await db.SaveChangesAsync(CancellationToken.None);

        var verdict = await Planner(db, new FakeTimeProvider(Noon)).DecideAsync(channel, CancellationToken.None);

        Assert.True(verdict.ShouldStart, verdict.Reason);
    }

    /// Günlük hedef dolunca duruyor ve YARINA kadar bekliyor.
    [Fact]
    public async Task GunlukHedefDoldu_YarinaKadarBekliyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var channel = await ChannelAsync(db, """{"daily_target":2,"minimum_gap_minutes":0}""");

        for (var i = 0; i < 2; i++)
        {
            db.Runs.Add(new Run
            {
                ChannelId = channel.Id,
                State = RunState.Completed,
                WorkflowVersionId = await VersionAsync(db),
                CreatedAt = Noon.AddHours(-6 + i),
            });
        }

        await db.SaveChangesAsync(CancellationToken.None);

        var verdict = await Planner(db, new FakeTimeProvider(Noon)).DecideAsync(channel, CancellationToken.None);

        Assert.False(verdict.ShouldStart);
        Assert.Contains("hedef doldu", verdict.Reason, StringComparison.Ordinal);
        Assert.True(verdict.RetryAfter > TimeSpan.FromHours(6));
    }

    /// TOPLU ÜRETİM ENGELİ: son run'ın üstünden yeterli süre geçmeli.
    [Fact]
    public async Task AralikDolmadan_Baslatmiyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var channel = await ChannelAsync(db, """{"daily_target":5,"minimum_gap_minutes":180}""");

        db.Runs.Add(new Run
        {
            ChannelId = channel.Id,
            State = RunState.Completed,
            WorkflowVersionId = await VersionAsync(db),
            CreatedAt = Noon.AddMinutes(-30),
        });

        await db.SaveChangesAsync(CancellationToken.None);

        var verdict = await Planner(db, new FakeTimeProvider(Noon)).DecideAsync(channel, CancellationToken.None);

        Assert.False(verdict.ShouldStart);
        Assert.InRange(verdict.RetryAfter, TimeSpan.FromMinutes(149), TimeSpan.FromMinutes(151));
    }

    /// BOŞ HAVUZ BAŞLATMIYOR AMA DOLDURMA PLANI DÖNÜYOR.
    ///
    /// "Başlatamadım" ile "ne yapmalı" aynı cevapta olmalı; ayrı
    /// sorulsaydı unutulacak yer tam da burasıydı ve havuz sabaha
    /// kadar boş kalırdı.
    [Fact]
    public async Task BosHavuz_BaslatmiyorAmaDoldurmaIstiyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var channel = await ChannelAsync(db, withTopic: false);
        var verdict = await Planner(db, new FakeTimeProvider(Noon)).DecideAsync(channel, CancellationToken.None);

        Assert.False(verdict.ShouldStart);
        Assert.Contains("havuz", verdict.Reason, StringComparison.Ordinal);
        Assert.NotNull(verdict.Refill);
        Assert.True(verdict.Refill.ShouldRefill);
    }

    /// KANAL GÜNLÜK BÜTÇESİ: aşılacaksa yeni run başlamıyor.
    [Fact]
    public async Task KanalButcesiDolu_Baslatmiyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var channel = await ChannelAsync(db);
        channel.DailyBudget = 0.10m;
        channel.MaxCostPerVideo = 0.50m;
        await db.SaveChangesAsync(CancellationToken.None);

        var verdict = await Planner(db, new FakeTimeProvider(Noon)).DecideAsync(channel, CancellationToken.None);

        Assert.False(verdict.ShouldStart);
        Assert.Contains("kanal günlük", verdict.Reason, StringComparison.Ordinal);
    }

    /// GLOBAL AYLIK LİMİT `settings` tablosundan okunuyor ve kanal
    /// limitinden bağımsız çalışıyor: üç kanalın her biri kendi
    /// limitinde kalıp toplamda üç katını harcayamasın.
    [Fact]
    public async Task AylikLimitDolu_Baslatmiyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var channel = await ChannelAsync(db);
        channel.MaxCostPerVideo = 5.00m;

        db.Settings.Add(new Setting { Key = RunPlanner.MonthlyBudgetKey, Value = "1.00" });

        db.ProviderCalls.Add(new ProviderCall
        {
            ProviderKey = "test",
            Operation = "test",
            Cost = 0.90m,
            CreatedAt = Noon.AddDays(-1),
            Succeeded = true,
        });

        await db.SaveChangesAsync(CancellationToken.None);

        var verdict = await Planner(db, new FakeTimeProvider(Noon)).DecideAsync(channel, CancellationToken.None);

        Assert.False(verdict.ShouldStart);
        Assert.Contains("global aylık", verdict.Reason, StringComparison.Ordinal);
    }

    /// LİMİT YOKSA SINIRSIZ, SIFIR DEĞİL.
    ///
    /// Sıfır saysaydık, aylık limit tanımlamamış bir kurulum hiç video
    /// üretemezdi — ve sebebi hiçbir yerde yazmazdı.
    [Fact]
    public async Task AylikLimitYok_Baslatiyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var channel = await ChannelAsync(db);
        var verdict = await Planner(db, new FakeTimeProvider(Noon)).DecideAsync(channel, CancellationToken.None);

        Assert.True(verdict.ShouldStart, verdict.Reason);
    }

    /// Günlük hedefi olmayan kanal hiç değerlendirilmiyor: kanalı
    /// duraklatmadan üretimi durdurmanın yolu bu.
    [Fact]
    public async Task HedefiOlmayanKanal_Baslatmiyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var channel = await ChannelAsync(db, """{"daily_target":0}""");
        var verdict = await Planner(db, new FakeTimeProvider(Noon)).DecideAsync(channel, CancellationToken.None);

        Assert.False(verdict.ShouldStart);
        Assert.Contains("hedefi yok", verdict.Reason, StringComparison.Ordinal);
    }

    /// AYAR UYARILARI KARARLA BİRLİKTE DÖNÜYOR: yanlış yazılmış bir
    /// alan sessizce varsayılana düşmemeli.
    [Fact]
    public async Task BozukAyar_UyariylaDonuyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var channel = await ChannelAsync(db, """{"dailyTarget":5}""");
        var verdict = await Planner(db, new FakeTimeProvider(Noon)).DecideAsync(channel, CancellationToken.None);

        Assert.NotNull(verdict.Warnings);
        Assert.Contains(verdict.Warnings, w => w.Contains("daily_target", StringComparison.Ordinal));
    }

    /// SÜREKLİ MOD: tür karışımı tanımlıysa sıradaki tür seçiliyor ve
    /// en çok geride kalan kazanıyor.
    [Fact]
    public async Task SurekliMod_EnGerideTuruSeciyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var channel = await ChannelAsync(db,
            """
            {"daily_target":4,"minimum_gap_minutes":0,
             "genres":[{"name":"tarih","share":0.5},{"name":"bilim","share":0.5}]}
            """);

        db.Runs.Add(new Run
        {
            ChannelId = channel.Id,
            State = RunState.Completed,
            WorkflowVersionId = await VersionAsync(db),
            CreatedAt = Noon.AddHours(-2),
            ContextJson = """{"genre":"tarih"}""",
        });

        await db.SaveChangesAsync(CancellationToken.None);

        var verdict = await Planner(db, new FakeTimeProvider(Noon)).DecideAsync(channel, CancellationToken.None);

        Assert.True(verdict.ShouldStart, verdict.Reason);
        Assert.Equal("bilim", verdict.Genre);
    }

    /// Tür tanımsızsa tür seçilmiyor — boş bir tür adı, bağlamda
    /// anlamsız bir alan bırakırdı.
    [Fact]
    public async Task TurTanimsiz_TurSecilmiyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var channel = await ChannelAsync(db);
        var verdict = await Planner(db, new FakeTimeProvider(Noon)).DecideAsync(channel, CancellationToken.None);

        Assert.Null(verdict.Genre);
    }

    /// Okunamayan bir run bağlamı tür sayımını bozmuyor: o run
    /// sayılmıyor, diğerleri sayılmaya devam ediyor.
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("bozuk json", null)]
    [InlineData("""{"genre":"tarih"}""", "tarih")]
    [InlineData("""{"genre":42}""", null)]
    [InlineData("{}", null)]
    public void BaglamdanTur_GuvenliOkunuyor(string? json, string? expected)
        => Assert.Equal(expected, RunPlanner.GenreOf(json));
}
