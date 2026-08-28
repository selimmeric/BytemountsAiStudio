using System.Text.Json;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Core.Learning;
using BytemountsAiStudio.Persistence.Entities;
using BytemountsAiStudio.Persistence.Providers;
using BytemountsAiStudio.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Persistence.Tests;

/// Ağırlıkların kanaldan gelmesi ve kalibrasyonun veriyi doğru
/// toplaması (P5-04).
[Collection(DatabaseCollection.Name)]
public sealed class TopicWeightCalibratorTests(DatabaseFixture fixture) : IAsyncLifetime
{
    /// Bu testin kurduğu kanalların adı.
    ///
    /// KANAL TABLOSU TOPTAN SİLİNMİYOR: seeder'ın kurduğu iki kanalı
    /// silmek, aynı koleksiyondaki `SchemaTests`'i düşürüyordu (o test
    /// "tam iki kanal" bekliyor). Kendi verisini temizleyen bir test,
    /// başkasının verisini silmeyen testtir.
    private const string ChannelName = "kalibrasyon test kanalı";

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
            + "DELETE FROM runs; DELETE FROM topics; "
            + "DELETE FROM workflow_versions; DELETE FROM workflows; "
            + "DELETE FROM channels WHERE name = '" + ChannelName + "'");
    }

    private void RequireDatabase()
        => Assert.True(fixture.Available, $"PostgreSQL erişilemiyor ({fixture.UnavailableReason}).");

    /* ---- ağırlık kanaldan geliyor ---- */

    /// KANAL AĞIRLIĞI KONUNUN PUANINA GİRİYOR.
    ///
    /// Bu depodaki en pahalı hata sınıfının testi: ayar kaydediliyor
    /// ama okunmuyor. Ses ve yazı tipi ayarlarında bir kez böyle oldu
    /// (P3-01) — kanalı değiştirmek hiçbir şeyi değiştirmiyordu.
    [Fact]
    public async Task KanalAgirligi_KonuPuaninaGiriyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();

        // Kaynak boyutu neredeyse tek belirleyici olan bir kanal.
        var channelId = await CreateChannelAsync(db,
            """
            {"score_weights":{
              "demand":0.05,"fit":0.05,"sourceability":0.80,
              "visualizability":0.05,"freshness":0.05}}
            """);

        var score = new TopicScore
        {
            Demand = 90, Fit = 90, Sourceability = 20,
            Visualizability = 90, Freshness = 90, Risk = 0,
        };

        await new TopicPool(db).AdmitAsync(
            channelId, "tr-TR", "kaynağı zayıf konu", score, null, CancellationToken.None);

        var topic = await db.Topics.AsNoTracking()
            .FirstAsync(t => t.Title == "kaynağı zayıf konu", CancellationToken.None);

        // Varsayılan ağırlıklarla bu konu 80 puan alır ve KABUL
        // edilirdi; kanalın ağırlıklarıyla 34 alıyor ve edilmiyor.
        Assert.True(topic.OverallScore < TopicPolicy.AcceptThreshold,
            $"Kanal ağırlıkları uygulanmadı: {topic.OverallScore:0.#}");

        Assert.NotEqual(TopicState.Queued, topic.State);
    }

    /// KANALSIZ KONU VARSAYILANLA SKORLANIYOR.
    [Fact]
    public async Task KanalsizKonu_VarsayilanAgirlik()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();

        var score = new TopicScore
        {
            Demand = 90, Fit = 90, Sourceability = 20,
            Visualizability = 90, Freshness = 90, Risk = 0,
        };

        await new TopicPool(db).AdmitAsync(
            null, "tr-TR", "kanalsız konu", score, null, CancellationToken.None);

        var topic = await db.Topics.AsNoTracking()
            .FirstAsync(t => t.Title == "kanalsız konu", CancellationToken.None);

        Assert.Equal(score.Weighted(ScoreWeights.Default), topic.OverallScore, 6);
    }

    /* ---- örneklem toplama ---- */

    /// AZ İZLENEN VİDEO ÖRNEKLEME GİRMİYOR.
    ///
    /// Üç izlenmeyle %90 tutma oranı bir şey söylemiyor; o üç kişi
    /// tesadüfen sonuna kadar izlemiş olabilir.
    [Fact]
    public async Task AzIzlenenVideo_Disarida()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();
        var channelId = await CreateChannelAsync(db, "{}");

        await CreateMeasuredRunAsync(db, channelId, views: 5, watchSeconds: 100);
        await CreateMeasuredRunAsync(db, channelId, views: 500, watchSeconds: 10_000);

        var samples = await new TopicWeightCalibrator(db).SamplesAsync(channelId, CancellationToken.None);

        Assert.Single(samples);
    }

    /// PAKETLEME DENEYİNE GİREN VİDEO ÖRNEKLEME GİRMİYOR.
    ///
    /// O videonun kapağı ya da başlığı KASTEN değiştirildi;
    /// performansı konunun değil, denenen kolun sonucu. İçeride
    /// bırakmak, kapak deneyinin (P5-03) etkisini konu ağırlıklarına
    /// sızdırmak olurdu.
    [Fact]
    public async Task DeneyeGirenVideo_Disarida()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();
        var channelId = await CreateChannelAsync(db, "{}");

        var inExperiment = await CreateMeasuredRunAsync(db, channelId, 500, 10_000);
        await CreateMeasuredRunAsync(db, channelId, 500, 10_000);

        await AssignToExperimentAsync(db, inExperiment);

        var samples = await new TopicWeightCalibrator(db).SamplesAsync(channelId, CancellationToken.None);

        Assert.Single(samples);
        Assert.NotEqual(inExperiment, samples[0].RunId);
    }

    /// SONUÇ İZLENME BAŞINA SANİYE.
    ///
    /// Tıklanma oranı DEĞİL: CTR kapağı ve başlığı ölçüyor, konuyu
    /// değil. Ağırlıkları CTR'ye göre ayarlamak, kapak deneyinin
    /// etkisini konu skoruna yazmak olurdu.
    [Fact]
    public async Task Sonuc_IzlenmeBasinaSaniye()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();
        var channelId = await CreateChannelAsync(db, "{}");

        await CreateMeasuredRunAsync(db, channelId, views: 200, watchSeconds: 5_000);

        var samples = await new TopicWeightCalibrator(db).SamplesAsync(channelId, CancellationToken.None);

        Assert.Equal(25.0, samples[0].Outcome, 6);
    }

    /* ---- karar ve uygulama ---- */

    /// VERİ YOKKEN AĞIRLIK DEĞİŞMİYOR.
    [Fact]
    public async Task VeriYok_AgirlikDegismiyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();
        var channelId = await CreateChannelAsync(db, """{"daily_target":3}""");

        var verdict = await new TopicWeightCalibrator(db)
            .CalibrateAsync(channelId, apply: true, CancellationToken.None);

        Assert.True(verdict.IsSuccess);
        Assert.Equal(CalibrationOutcome.NotEnoughData, verdict.Value.Outcome);

        var settings = await db.Channels.AsNoTracking()
            .Where(c => c.Id == channelId).Select(c => c.SettingsJson)
            .FirstAsync(CancellationToken.None);

        Assert.DoesNotContain("score_weights", settings, StringComparison.Ordinal);
    }

    /// UYGULAMA AYARIN GERİ KALANINI KORUYOR.
    ///
    /// Ayarın tamamını yeniden yazmak, ses ve tempo ayarlarını
    /// sessizce silmek olurdu.
    [Fact]
    public async Task AgirlikYazimi_DigerAyarlariKoruyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();

        var channelId = await CreateChannelAsync(db,
            """{"daily_target":7,"voice_id":"ses-1"}""");

        var channel = await db.Channels.FirstAsync(c => c.Id == channelId, CancellationToken.None);

        // Uygulama yolu doğrudan sınanıyor: kalibrasyonun "benimse"
        // demesi için altmış ölçülmüş video kurmak, testi ölçtüğü
        // şeyden uzaklaştırırdı.
        var written = TopicWeightCalibrator.Write(channel, ScoreWeights.Default with
        {
            Demand = 0.10, Fit = 0.10, Sourceability = 0.60,
            Visualizability = 0.10, Freshness = 0.10,
        });

        Assert.True(written.IsSuccess, written.IsFailure ? written.Error.Message : string.Empty);

        await db.SaveChangesAsync(CancellationToken.None);

        var settings = ChannelSettings.Parse(channel.SettingsJson);

        Assert.Equal(0.60, settings.ScoreWeights.Sourceability, 6);
        Assert.Equal(7, settings.Pacing.DailyTarget);
        Assert.Equal("ses-1", settings.VoiceId);
        Assert.Empty(settings.Warnings);
    }

    /// OLMAYAN KANAL SESSİZCE GEÇİLMİYOR.
    [Fact]
    public async Task OlmayanKanal_Reddediliyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();

        var verdict = await new TopicWeightCalibrator(db)
            .CalibrateAsync(Guid.CreateVersion7(), apply: false, CancellationToken.None);

        Assert.True(verdict.IsFailure);
        Assert.Equal("calibration.no_channel", verdict.Error.Code);
    }

    /* ---- yardımcılar ---- */

    private static async Task<Guid> CreateChannelAsync(StudioDbContext db, string settingsJson)
    {
        var channel = new Channel
        {
            Name = ChannelName,
            Language = "tr-TR",
            SettingsJson = settingsJson,
        };

        db.Channels.Add(channel);
        await db.SaveChangesAsync(CancellationToken.None);

        return channel.Id;
    }

    private static async Task<Guid> CreateMeasuredRunAsync(
        StudioDbContext db, Guid channelId, int views, long watchSeconds)
    {
        var workflow = new Persistence.Entities.Workflow
        {
            Key = "kalibrasyon-" + Guid.NewGuid().ToString("N")[..8],
            Name = "kalibrasyon",
            CurrentVersion = 1,
        };

        var version = new WorkflowVersion
        {
            Workflow = workflow,
            Version = 1,
            GraphJson = """{"schema_version":1,"key":"k","name":"n","nodes":[],"edges":[]}""",
        };

        var topic = new Topic
        {
            ChannelId = channelId,
            Title = "konu " + Guid.NewGuid().ToString("N")[..6],
            Language = "tr-TR",
            ScoresJson = JsonSerializer.Serialize(new
            {
                demand = 60, fit = 60, sourceability = 60,
                visualizability = 60, freshness = 60, risk = 0,
            }),
            OverallScore = 60,
        };

        db.Workflows.Add(workflow);
        db.WorkflowVersions.Add(version);
        db.Topics.Add(topic);
        await db.SaveChangesAsync(CancellationToken.None);

        var run = new Run
        {
            WorkflowVersionId = version.Id,
            ChannelId = channelId,
            TopicId = topic.Id,
            State = RunState.Completed,
        };

        db.Runs.Add(run);

        db.PublicationMetrics.Add(new PublicationMetric
        {
            Run = run,
            DayOffset = ExperimentService.MetricDay,
            Impressions = views * 10,
            Clicks = views,
            Views = views,
            WatchSeconds = watchSeconds,
        });

        await db.SaveChangesAsync(CancellationToken.None);

        return run.Id;
    }

    private static async Task AssignToExperimentAsync(StudioDbContext db, Guid runId)
    {
        var experiment = new Experiment
        {
            Dimension = "thumbnail",
            Name = "kapak denemesi",
            RequiredPerVariant = 1_500,
        };

        var variant = new ExperimentVariant
        {
            Experiment = experiment, Name = "b-varyant", ConfigJson = """{"harf":"buyuk"}""",
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
