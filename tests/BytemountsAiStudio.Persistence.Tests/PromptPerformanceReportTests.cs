using System.Text.Json;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Persistence.Entities;
using BytemountsAiStudio.Persistence.Providers;
using BytemountsAiStudio.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Persistence.Tests;

/// İstem performans raporu (P5-05).
///
/// RAPORUN ASIL İŞİ YANLIŞ SONUÇ ÇIKARMAYI ENGELLEMEK. Sayılar tek
/// başına her zaman bir "kazanan" gösteriyor; bu testlerin yarısı
/// sayıları değil, sayıların yanındaki UYARIYI sınıyor.
[Collection(DatabaseCollection.Name)]
public sealed class PromptPerformanceReportTests(DatabaseFixture fixture) : IAsyncLifetime
{
    private const string ChannelName = "istem raporu test kanalı";

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
            "DELETE FROM publication_metrics; DELETE FROM node_executions; "
            + "DELETE FROM experiment_assignments; DELETE FROM experiment_variants; "
            + "DELETE FROM experiments; DELETE FROM runs; "
            + "DELETE FROM workflow_versions; DELETE FROM workflows; "
            + "DELETE FROM channels WHERE name = '" + ChannelName + "'");
    }

    private void RequireDatabase()
        => Assert.True(fixture.Available, $"PostgreSQL erişilemiyor ({fixture.UnavailableReason}).");

    /* ---- damga okuma ---- */

    /// İSTEM KULLANMAYAN NODE'LAR RAPORA GİRMİYOR.
    ///
    /// Render, TTS, kapak — çoğu node istem çağırmıyor. Onları
    /// "sürümü bilinmeyen" diye saymak, raporu anlamsız bir
    /// çoğunlukla doldururdu.
    [Fact]
    public void DamgasizCikti_Atlaniyor()
    {
        Assert.Null(PromptPerformanceReport.StampOf("""{"asset":"a://b"}"""));
        Assert.Null(PromptPerformanceReport.StampOf("bozuk json"));
        Assert.Null(PromptPerformanceReport.StampOf(null));

        Assert.Equal("seo.generate@2#abc",
            PromptPerformanceReport.StampOf("""{"prompt":"seo.generate@2#abc"}"""));
    }

    /* ---- uyarı metinleri ---- */

    /// TEK SÜRÜM: KARŞILAŞTIRILACAK BİR ŞEY YOK.
    [Fact]
    public void TekSurum_KarsilastirmaYok()
    {
        var notes = PromptPerformanceReport.Notes([Row("seo.generate", 1, runs: 50, randomized: 0)]);

        Assert.Contains("karşılaştırılacak bir şey yok",
            string.Join(" ", notes), StringComparison.Ordinal);
    }

    /// RASTGELE ATANMIŞ KARŞILAŞTIRMA NEDENSEL.
    [Fact]
    public void RastgeleAtama_Nedensel()
    {
        var notes = PromptPerformanceReport.Notes(
        [
            Row("seo.generate", 1, runs: 50, randomized: 50),
            Row("seo.generate", 2, runs: 50, randomized: 50),
        ]);

        Assert.Contains("NEDENSEL", string.Join(" ", notes), StringComparison.Ordinal);
        Assert.DoesNotContain("NEDENSEL DEĞİL", string.Join(" ", notes), StringComparison.Ordinal);
    }

    /// ARDIŞIK YAYINLANMIŞ SÜRÜMLER NEDENSEL DEĞİL.
    ///
    /// BU DOSYANIN EN ÖNEMLİ TESTİ. v1 haziranda, v2 temmuzda
    /// kullanıldıysa "v2 daha iyi" cümlesi aslında TEMMUZ'un
    /// hazirandan iyi olduğunu söylüyor: kanal büyüdü, konular
    /// değişti, platform sıralamayı değiştirdi.
    [Fact]
    public void ArdisikSurumler_NedenselDegil()
    {
        var haziran = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var temmuz = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

        var notes = PromptPerformanceReport.Notes(
        [
            Row("script.generate", 1, 50, 0, haziran, haziran.AddDays(20)),
            Row("script.generate", 2, 50, 0, temmuz, temmuz.AddDays(20)),
        ]);

        var text = string.Join(" ", notes);

        Assert.Contains("NEDENSEL DEĞİL", text, StringComparison.Ordinal);
        Assert.Contains("ÖRTÜŞMÜYOR", text, StringComparison.Ordinal);
        Assert.Contains("aradan geçen zaman", text, StringComparison.Ordinal);
    }

    /// ÖRTÜŞEN DÖNEMLER DE NEDENSEL DEĞİL — ama sebebi başka.
    ///
    /// Sürüm seçimi bir kurala bağlıysa (araştırma varsa v3) farkı
    /// yaratan o kural olabilir: v3 alan videolar zaten kaynaklı
    /// videolar.
    [Fact]
    public void OrtusenDonemler_YineNedenselDegil()
    {
        var basla = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        var notes = PromptPerformanceReport.Notes(
        [
            Row("script.generate", 1, 50, 0, basla, basla.AddDays(60)),
            Row("script.generate", 3, 50, 0, basla.AddDays(10), basla.AddDays(70)),
        ]);

        var text = string.Join(" ", notes);

        Assert.Contains("NEDENSEL DEĞİL", text, StringComparison.Ordinal);
        Assert.Contains("örtüşüyor", text, StringComparison.Ordinal);
        Assert.Contains("kurala bağlıysa", text, StringComparison.Ordinal);
    }

    /* ---- veritabanı ---- */

    /// RAPOR GERÇEK DAMGAYA GÖRE GRUPLUYOR.
    [Fact]
    public async Task Rapor_DamgayaGoreGrupluyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();
        var channelId = await CreateChannelAsync(db);

        await CreateRunAsync(db, channelId, "seo.generate@1#aaa", views: 200, watchSeconds: 4_000);
        await CreateRunAsync(db, channelId, "seo.generate@2#bbb", views: 200, watchSeconds: 6_000);

        var report = await new PromptPerformanceReport(db).BuildAsync(channelId, CancellationToken.None);

        Assert.True(report.IsSuccess);
        Assert.Equal(2, report.Value.Versions.Count);
        Assert.Equal([1, 2], report.Value.Versions.Select(v => v.Version));
        Assert.Equal(20.0, report.Value.Versions[0].MeanRetentionSeconds!.Value, 6);
        Assert.Equal(30.0, report.Value.Versions[1].MeanRetentionSeconds!.Value, 6);

        // VE UYARI RAPORLA BİRLİKTE GELİYOR: sayıları uyarısız
        // döndürmek, "v2 kazandı" denmesini kolaylaştırırdı.
        Assert.Contains("NEDENSEL DEĞİL",
            string.Join(" ", report.Value.Notes), StringComparison.Ordinal);
    }

    /// AZ İZLENEN RUN RAPORA GİRMİYOR.
    [Fact]
    public async Task AzIzlenenRun_Disarida()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();
        var channelId = await CreateChannelAsync(db);

        await CreateRunAsync(db, channelId, "seo.generate@1#aaa", views: 5, watchSeconds: 100);

        var report = await new PromptPerformanceReport(db).BuildAsync(channelId, CancellationToken.None);

        Assert.Empty(report.Value.Versions);
    }

    /// İSTEM DENEYİNE GİREN RUN "RASTGELE" SAYILIYOR.
    [Fact]
    public async Task DeneyeGirenRun_RastgeleSayiliyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();
        var channelId = await CreateChannelAsync(db);

        var runId = await CreateRunAsync(db, channelId, "seo.generate@1#aaa", 200, 4_000);
        await AssignToPromptExperimentAsync(db, runId);

        var report = await new PromptPerformanceReport(db).BuildAsync(channelId, CancellationToken.None);

        Assert.Equal(1, report.Value.Versions[0].RandomizedRuns);
    }

    /* ---- yardımcılar ---- */

    private static PromptVersionRow Row(
        string key, int version, int runs, int randomized,
        DateTimeOffset? first = null, DateTimeOffset? last = null)
        => new(key, version, runs, randomized, 10, 0.05, first, last);

    private static async Task<Guid> CreateChannelAsync(StudioDbContext db)
    {
        var channel = new Channel { Name = ChannelName, Language = "tr-TR" };

        db.Channels.Add(channel);
        await db.SaveChangesAsync(CancellationToken.None);

        return channel.Id;
    }

    private static async Task<Guid> CreateRunAsync(
        StudioDbContext db, Guid channelId, string stamp, int views, long watchSeconds)
    {
        var workflow = new Persistence.Entities.Workflow
        {
            Key = "istem-" + Guid.NewGuid().ToString("N")[..8],
            Name = "istem raporu",
            CurrentVersion = 1,
        };

        var version = new WorkflowVersion
        {
            Workflow = workflow,
            Version = 1,
            GraphJson = """{"schema_version":1,"key":"k","name":"n","nodes":[],"edges":[]}""",
        };

        db.Workflows.Add(workflow);
        db.WorkflowVersions.Add(version);
        await db.SaveChangesAsync(CancellationToken.None);

        var run = new Run
        {
            WorkflowVersionId = version.Id,
            ChannelId = channelId,
            State = RunState.Completed,
        };

        db.Runs.Add(run);
        await db.SaveChangesAsync(CancellationToken.None);

        db.NodeExecutions.Add(new NodeExecution
        {
            RunId = run.Id,
            NodeId = "seo",
            NodeType = "seo.generate",
            Attempt = 1,
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            State = NodeState.Succeeded,
            OutputJson = JsonSerializer.Serialize(new { prompt = stamp, title = "başlık" }),
            FinishedAt = DateTimeOffset.UtcNow,
        });

        // İSTEM KULLANMAYAN BİR NODE DA EKLENİYOR: raporun onu
        // eleyip elemediği ancak varken sınanabilir.
        db.NodeExecutions.Add(new NodeExecution
        {
            RunId = run.Id,
            NodeId = "render",
            NodeType = "media.render",
            Attempt = 1,
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            State = NodeState.Succeeded,
            OutputJson = """{"asset":"a://video.mp4"}""",
            FinishedAt = DateTimeOffset.UtcNow,
        });

        db.PublicationMetrics.Add(new PublicationMetric
        {
            RunId = run.Id,
            DayOffset = ExperimentService.MetricDay,
            Impressions = views * 10,
            Clicks = views,
            Views = views,
            WatchSeconds = watchSeconds,
        });

        await db.SaveChangesAsync(CancellationToken.None);

        return run.Id;
    }

    private static async Task AssignToPromptExperimentAsync(StudioDbContext db, Guid runId)
    {
        var experiment = new Experiment
        {
            Dimension = "prompt",
            Name = "istem denemesi",
            RequiredPerVariant = 1_500,
        };

        var variant = new ExperimentVariant
        {
            Experiment = experiment,
            Name = "b-varyant",
            ConfigJson = """{"istem":"seo.generate","surum":"2"}""",
        };

        db.Experiments.Add(experiment);
        db.ExperimentVariants.Add(variant);

        db.ExperimentAssignments.Add(new ExperimentAssignment
        {
            Experiment = experiment, Variant = variant, RunId = runId,
        });

        await db.SaveChangesAsync(CancellationToken.None);
    }
}
