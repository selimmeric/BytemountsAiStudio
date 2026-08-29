using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Core.Learning;
using BytemountsAiStudio.Persistence.Entities;
using BytemountsAiStudio.Persistence.Providers;
using BytemountsAiStudio.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Persistence.Tests;

/// Kararın UYGULANMASI (P5-07).
///
/// FAZ 5'İN KABUL KRİTERİ TAM OLARAK BU HALKA: bir deneyin kazanması,
/// kazananın uygulandığı anlamına gelmiyor. Karar verilip hiçbir şey
/// değişmezse sistem "soru başlıklar daha iyi" diye rapor yazar ve
/// ertesi gün yine düz başlık üretir — öğrenme döngüsü kapanmaz.
[Collection(DatabaseCollection.Name)]
public sealed class ExperimentConclusionTests(DatabaseFixture fixture) : IAsyncLifetime
{
    private const string ChannelPrefix = "karar test kanalı";

    public Task InitializeAsync() => CleanAsync();

    public Task DisposeAsync() => CleanAsync();

    private async Task CleanAsync()
    {
        if (!fixture.Available)
        {
            return;
        }

        await using var db = fixture.CreateContext();

        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM publication_metrics; DELETE FROM experiment_assignments; "
            + "DELETE FROM experiment_variants; DELETE FROM experiments; "
            + "DELETE FROM runs; DELETE FROM workflow_versions; DELETE FROM workflows; "
            + "DELETE FROM channels WHERE name LIKE 'karar test kanalı%'");
    }

    private void RequireDatabase()
        => Assert.True(fixture.Available, $"PostgreSQL erişilemiyor ({fixture.UnavailableReason}).");

    /// KARAR VERİLMEMİŞSE HİÇBİR ŞEY YAZILMIYOR.
    ///
    /// Saklanmış bir "yeterli veri yok" cevabı, veri geldikten sonra
    /// da orada durur ve deney sonsuza kadar kararsız görünür.
    [Fact]
    public async Task KararYok_HicbirSeyYazilmiyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();
        var channelId = await ChannelAsync(db);
        var (experimentId, _, variantId) = await ExperimentAsync(db, channelId);

        await MeasureAsync(db, experimentId, variantId, clicks: 6, impressions: 100, count: 1);

        var verdict = await new ExperimentService(db)
            .ConcludeAsync(experimentId, apply: true, CancellationToken.None);

        Assert.True(verdict.IsSuccess, verdict.IsFailure ? verdict.Error.Message : string.Empty);
        Assert.False(verdict.Value.IsDecided);

        var experiment = await db.Experiments.AsNoTracking()
            .FirstAsync(e => e.Id == experimentId, CancellationToken.None);

        Assert.Equal("Running", experiment.State);
        Assert.DoesNotContain("default_variants", await SettingsAsync(db, channelId),
            StringComparison.Ordinal);
    }

    /// KAZANAN KANAL VARSAYILANI OLUYOR.
    [Fact]
    public async Task VaryantKazandi_KanalVarsayilaniOluyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();

        var channelId = await ChannelAsync(db, """{"daily_target":5}""");
        var (experimentId, controlId, variantId) = await ExperimentAsync(db, channelId);

        await MeasureAsync(db, experimentId, controlId, clicks: 400);
        await MeasureAsync(db, experimentId, variantId, clicks: 600);

        var verdict = await new ExperimentService(db)
            .ConcludeAsync(experimentId, apply: true, CancellationToken.None);

        Assert.True(verdict.IsSuccess, verdict.IsFailure ? verdict.Error.Message : string.Empty);
        Assert.Equal(ExperimentOutcome.VariantWins, verdict.Value.Outcome);

        var settings = ChannelSettings.Parse(await SettingsAsync(db, channelId));

        Assert.Equal(
            ThumbnailTextPosition.Lower,
            ThumbnailVariant.Parse(settings.DefaultVariants["thumbnail"]).Value.Position);

        // AYARIN GERİ KALANI KORUNUYOR: belgenin tamamını yeniden
        // yazmak, tempo ayarını sessizce silmek olurdu.
        Assert.Equal(5, settings.Pacing.DailyTarget);

        var experiment = await db.Experiments.AsNoTracking()
            .FirstAsync(e => e.Id == experimentId, CancellationToken.None);

        Assert.Equal("Concluded", experiment.State);
        Assert.NotNull(experiment.DecidedAt);
    }

    /// UYGULAMADAN SORULABİLİYOR.
    ///
    /// Kararı görmek ile uygulamak ayrı iki işlem: "Ne işe yarıyor"
    /// ekranı her açılışta bu yolu kullanıyor ve bir panele girmek
    /// kanalın ayarlarını değiştirmemeli.
    [Fact]
    public async Task UygulamaKapali_AyarDegismiyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();

        var channelId = await ChannelAsync(db);
        var (experimentId, controlId, variantId) = await ExperimentAsync(db, channelId);

        await MeasureAsync(db, experimentId, controlId, clicks: 400);
        await MeasureAsync(db, experimentId, variantId, clicks: 600);

        var verdict = await new ExperimentService(db)
            .ConcludeAsync(experimentId, apply: false, CancellationToken.None);

        Assert.Equal(ExperimentOutcome.VariantWins, verdict.Value.Outcome);
        Assert.DoesNotContain("default_variants", await SettingsAsync(db, channelId),
            StringComparison.Ordinal);

        var experiment = await db.Experiments.AsNoTracking()
            .FirstAsync(e => e.Id == experimentId, CancellationToken.None);

        Assert.Equal("Running", experiment.State);
    }

    /// KONTROL KAZANDIYSA HİÇBİR ŞEY UYGULANMIYOR.
    ///
    /// Kontrol zaten yürürlükteki ayar; onu "kazanan" diye yeniden
    /// yazmak, hiçbir şeyin değişmediği bir değişiklik kaydı üretirdi.
    [Fact]
    public async Task KontrolKazandi_VarsayilanYazilmiyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();

        var channelId = await ChannelAsync(db);
        var (experimentId, controlId, variantId) = await ExperimentAsync(db, channelId);

        await MeasureAsync(db, experimentId, controlId, clicks: 600);
        await MeasureAsync(db, experimentId, variantId, clicks: 400);

        var verdict = await new ExperimentService(db)
            .ConcludeAsync(experimentId, apply: true, CancellationToken.None);

        Assert.Equal(ExperimentOutcome.ControlWins, verdict.Value.Outcome);
        Assert.DoesNotContain("default_variants", await SettingsAsync(db, channelId),
            StringComparison.Ordinal);
    }

    /// KANALSIZ DENEY KAZANIRSA SESSİZ GEÇİLMİYOR.
    ///
    /// Varsayılan yazılacak bir yer yok; "kazandı ama uygulanmadı"
    /// durumunu gizlemek, öğrenme döngüsünün kapandığı izlenimi
    /// verirdi.
    [Fact]
    public async Task KanalsizDeneyKazandi_HataDonuyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();
        var (experimentId, controlId, variantId) = await ExperimentAsync(db, channelId: null);

        await MeasureAsync(db, experimentId, controlId, clicks: 400);
        await MeasureAsync(db, experimentId, variantId, clicks: 600);

        var verdict = await new ExperimentService(db)
            .ConcludeAsync(experimentId, apply: true, CancellationToken.None);

        Assert.True(verdict.IsFailure);
        Assert.Equal("experiment.no_channel", verdict.Error.Code);
    }

    /* ---- yardımcılar ---- */

    private static async Task<string> SettingsAsync(StudioDbContext db, Guid channelId)
        => await db.Channels.AsNoTracking()
            .Where(c => c.Id == channelId).Select(c => c.SettingsJson)
            .FirstAsync(CancellationToken.None);

    private static async Task<Guid> ChannelAsync(StudioDbContext db, string settingsJson = "{}")
    {
        var channel = new Channel
        {
            Name = ChannelPrefix + " " + Guid.NewGuid().ToString("N")[..6],
            Language = "tr-TR",
            SettingsJson = settingsJson,
        };

        db.Channels.Add(channel);
        await db.SaveChangesAsync(CancellationToken.None);

        return channel.Id;
    }

    private static async Task<(Guid Experiment, Guid Control, Guid Variant)> ExperimentAsync(
        StudioDbContext db, Guid? channelId)
    {
        var experiment = new Experiment
        {
            ChannelId = channelId,
            Dimension = "thumbnail",
            Name = "kapak denemesi",
            MinimumDetectableEffect = 0.02,
            RequiredPerVariant = 1_500,
        };

        var control = new ExperimentVariant
        {
            Experiment = experiment, Name = "a-kontrol", IsControl = true, ConfigJson = "{}",
        };

        var variant = new ExperimentVariant
        {
            Experiment = experiment, Name = "b-varyant", ConfigJson = """{"konum":"alt"}""",
        };

        db.Experiments.Add(experiment);
        db.ExperimentVariants.AddRange(control, variant);
        await db.SaveChangesAsync(CancellationToken.None);

        return (experiment.Id, control.Id, variant.Id);
    }

    /// Bir kola run'lar ve yedinci gün ölçümleri.
    private static async Task MeasureAsync(
        StudioDbContext db, Guid experimentId, Guid variantId,
        int clicks, int impressions = 2_000, int count = 5)
    {
        for (var i = 0; i < count; i++)
        {
            var runId = await RunAsync(db);

            db.ExperimentAssignments.Add(new ExperimentAssignment
            {
                ExperimentId = experimentId, VariantId = variantId, RunId = runId,
            });

            db.PublicationMetrics.Add(new PublicationMetric
            {
                RunId = runId,
                DayOffset = ExperimentService.MetricDay,
                Impressions = impressions,
                Clicks = clicks,
            });

            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    private static async Task<Guid> RunAsync(StudioDbContext db)
    {
        var workflow = new Persistence.Entities.Workflow
        {
            Key = "karar-" + Guid.NewGuid().ToString("N")[..8],
            Name = "karar testi",
            CurrentVersion = 1,
        };

        var version = new WorkflowVersion
        {
            Workflow = workflow,
            Version = 1,
            GraphJson = """{"schema_version":1,"key":"k","name":"n","nodes":[],"edges":[]}""",
        };

        var run = new Run { WorkflowVersion = version, State = RunState.Completed };

        db.Workflows.Add(workflow);
        db.WorkflowVersions.Add(version);
        db.Runs.Add(run);
        await db.SaveChangesAsync(CancellationToken.None);

        return run.Id;
    }
}
