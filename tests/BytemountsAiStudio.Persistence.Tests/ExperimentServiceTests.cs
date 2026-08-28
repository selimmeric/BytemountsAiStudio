using BytemountsAiStudio.Core.Learning;
using BytemountsAiStudio.Persistence.Entities;
using BytemountsAiStudio.Persistence.Providers;
using BytemountsAiStudio.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Persistence.Tests;

/// Deney atama ve değerlendirme (P5-02).
[Collection(DatabaseCollection.Name)]
public sealed class ExperimentServiceTests(DatabaseFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (!fixture.Available)
        {
            return;
        }

        await using var db = fixture.CreateContext();

        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM publication_metrics; DELETE FROM experiment_assignments; "
            + "DELETE FROM experiment_variants; DELETE FROM experiments; "
            + "DELETE FROM runs; DELETE FROM workflow_versions; DELETE FROM workflows");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private void RequireDatabase()
        => Assert.True(fixture.Available, $"PostgreSQL erişilemiyor ({fixture.UnavailableReason}).");

    /* ---- atama ---- */

    /// ATAMA DETERMİNİSTİK.
    ///
    /// Rastgele sayı üreteci kullanmak, aynı run'ın yeniden
    /// değerlendirilmesinde farklı varyanta düşmesi demekti — ve
    /// hedefli yeniden koşma (P2-07) tam olarak bunu yapıyor: aynı
    /// run'ı ikinci kez çalıştırıyor. O run iki varyantta birden
    /// sayılırdı.
    [Fact]
    public void Atama_Deterministik()
    {
        var run = Guid.CreateVersion7();
        var experiment = Guid.CreateVersion7();

        var first = ExperimentService.Bucket(run, experiment, 2);

        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(first, ExperimentService.Bucket(run, experiment, 2));
        }
    }

    /// FARKLI RUN'LAR FARKLI VARYANTLARA DÜŞÜYOR.
    ///
    /// Deterministik olmak, hepsinin aynı varyanta düşmesi demek
    /// olsaydı deney hiçbir şey ölçmezdi. Bin run'da dağılımın
    /// makul olduğu ölçülüyor.
    [Fact]
    public void FarkliRunlar_Dagiliyor()
    {
        var experiment = Guid.CreateVersion7();
        var counts = new int[2];

        for (var i = 0; i < 1000; i++)
        {
            counts[ExperimentService.Bucket(Guid.CreateVersion7(), experiment, 2)]++;
        }

        // %45–%55 aralığı: rastgele bir bölmede beklenen sapma bu
        // büyüklükte. Dışına çıkması, özetin bir tarafa yattığını
        // söylerdi.
        Assert.InRange(counts[0], 450, 550);
    }

    /// AYNI RUN FARKLI DENEYLERDE FARKLI VARYANTA DÜŞEBİLİYOR.
    ///
    /// Deney kimliği özete giriyor. Girmeseydi, bir run'ın bütün
    /// deneylerde hep aynı tarafa düşmesi ve deneylerin birbirine
    /// karışması mümkündü.
    [Fact]
    public void AyniRun_FarkliDeneylerdeBagimsiz()
    {
        var run = Guid.CreateVersion7();

        var buckets = Enumerable.Range(0, 50)
            .Select(_ => ExperimentService.Bucket(run, Guid.CreateVersion7(), 2))
            .Distinct()
            .Count();

        Assert.Equal(2, buckets);
    }

    /// AÇIK DENEYE RUN ATANIYOR.
    [Fact]
    public async Task AcikDeney_RunAtaniyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();
        var (experimentId, _, _) = await SeedExperimentAsync(db);
        var runId = await SeedRunAsync(db);

        var service = new ExperimentService(db);
        var assigned = await service.AssignAsync(runId, null, CancellationToken.None);

        Assert.True(assigned.IsSuccess);
        Assert.Single(assigned.Value);
        Assert.Equal("thumbnail", assigned.Value[0].Dimension);

        Assert.True(await db.ExperimentAssignments
            .AnyAsync(a => a.ExperimentId == experimentId && a.RunId == runId, CancellationToken.None));
    }

    /// AYNI RUN İKİNCİ KEZ ATANMIYOR.
    ///
    /// İkinci bir atama, aynı videonun iki varyantta birden sayılması
    /// demek — ve o video her iki tarafın da ortalamasını kaydırır.
    /// Hedefli yeniden koşma aynı run'ı tekrar buraya getirebiliyor.
    [Fact]
    public async Task AyniRun_IkinciKezAtanmiyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();
        await SeedExperimentAsync(db);
        var runId = await SeedRunAsync(db);

        var service = new ExperimentService(db);

        await service.AssignAsync(runId, null, CancellationToken.None);
        await service.AssignAsync(runId, null, CancellationToken.None);

        Assert.Equal(1, await db.ExperimentAssignments.CountAsync(CancellationToken.None));
    }

    /// TEK VARYANTLI DENEYE ATAMA YAPILMIYOR.
    ///
    /// Karşılaştıracak bir şey yok; atama yapmak, hiçbir şey ölçmeyen
    /// bir deneyin veri topluyormuş gibi görünmesi olurdu.
    [Fact]
    public async Task TekVaryantliDeney_AtamaYok()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();

        var experiment = new Experiment { Dimension = "thumbnail", Name = "tek", RequiredPerVariant = 100 };
        db.Experiments.Add(experiment);
        db.ExperimentVariants.Add(new ExperimentVariant
        {
            Experiment = experiment, Name = "kontrol", IsControl = true,
        });
        await db.SaveChangesAsync(CancellationToken.None);

        var runId = await SeedRunAsync(db);
        var assigned = await new ExperimentService(db).AssignAsync(runId, null, CancellationToken.None);

        Assert.Empty(assigned.Value);

        // VE DENEY GÖRÜNÜR BİÇİMDE KAPANIYOR: sessizce atlanan bir
        // deney haftalarca "koşuyor" görünür ve hiçbir şey ölçmez.
        Assert.Equal("Invalid", await db.Experiments.Select(e => e.State).FirstAsync(CancellationToken.None));
    }

    /* ---- değerlendirme ---- */

    /// ATAMASIZ DENEY DEĞERLENDİRİLMİYOR.
    [Fact]
    public async Task AtamasizDeney_Reddediliyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();
        var (experimentId, _, _) = await SeedExperimentAsync(db);

        var verdict = await new ExperimentService(db).EvaluateAsync(experimentId, CancellationToken.None);

        Assert.True(verdict.IsFailure);
        Assert.Equal("experiment.no_assignments", verdict.Error.Code);
    }

    /// ÖLÇÜM YOKSA "YETERLİ VERİ YOK" — "fark yok" DEĞİL.
    ///
    /// Ölçüm henüz gelmemiş bir deneyi "fark yok" diye kapatmak,
    /// hiçbir şey öğrenmeden denemeyi bırakmak demek.
    [Fact]
    public async Task OlcumYok_YeterliVeriYok()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();
        await SeedExperimentAsync(db);

        var runId = await SeedRunAsync(db);
        await new ExperimentService(db).AssignAsync(runId, null, CancellationToken.None);

        var verdict = await new ExperimentService(db).EvaluateAsync(
            await db.Experiments.Select(e => e.Id).FirstAsync(CancellationToken.None),
            CancellationToken.None);

        Assert.True(verdict.IsSuccess);
        Assert.Equal(ExperimentOutcome.NotEnoughData, verdict.Value.Outcome);
    }

    /// GERÇEK VERİYLE KARAR VERİLİYOR.
    ///
    /// Yeterli gösterim ve gerçek bir fark: varyant kazanmalı. Bu
    /// test, zincirin tamamını (atama → ölçüm → karar) gerçek
    /// veritabanı üzerinden koşturuyor.
    [Fact]
    public async Task YeterliOlcum_KararVeriliyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();
        var (experimentId, controlId, variantId) = await SeedExperimentAsync(db);

        // Her varyant için beş run; her run'a yedinci gün ölçümü.
        // Kontrol %4, varyant %6 tıklanma.
        foreach (var (variant, clicks) in new[] { (controlId, 400), (variantId, 600) })
        {
            for (var i = 0; i < 5; i++)
            {
                var runId = await SeedRunAsync(db);

                db.ExperimentAssignments.Add(new ExperimentAssignment
                {
                    ExperimentId = experimentId, VariantId = variant, RunId = runId,
                });

                db.PublicationMetrics.Add(new PublicationMetric
                {
                    RunId = runId,
                    DayOffset = ExperimentService.MetricDay,
                    Impressions = 2_000,
                    Clicks = clicks,
                });

                await db.SaveChangesAsync(CancellationToken.None);
            }
        }

        var verdict = await new ExperimentService(db).EvaluateAsync(experimentId, CancellationToken.None);

        Assert.True(verdict.IsSuccess, verdict.IsFailure ? verdict.Error.Message : string.Empty);
        Assert.Equal(ExperimentOutcome.VariantWins, verdict.Value.Outcome);
        Assert.True(verdict.Value.PValue < 0.05);
    }

    /// AYNI GÜN İKİ KEZ ÖLÇÜLEMİYOR.
    ///
    /// Günlük çekim iki kez koşarsa (yeniden deneme, iki worker) aynı
    /// günün sayıları iki kez toplanır ve BÜTÜN oranlar bozulur.
    /// Kısıt veritabanında, çünkü uygulama katmanındaki kontrol yarış
    /// koşulunda kaçırır.
    [Fact]
    public async Task AyniGunIkinciOlcum_Reddediliyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();
        var runId = await SeedRunAsync(db);

        db.PublicationMetrics.Add(new PublicationMetric
        {
            RunId = runId, DayOffset = 7, Impressions = 100, Clicks = 4,
        });
        await db.SaveChangesAsync(CancellationToken.None);

        db.PublicationMetrics.Add(new PublicationMetric
        {
            RunId = runId, DayOffset = 7, Impressions = 100, Clicks = 4,
        });

        await Assert.ThrowsAsync<DbUpdateException>(
            () => db.SaveChangesAsync(CancellationToken.None));
    }

    /* ---- yardımcılar ---- */

    private static async Task<(Guid Experiment, Guid Control, Guid Variant)> SeedExperimentAsync(
        StudioDbContext db)
    {
        var experiment = new Experiment
        {
            Dimension = "thumbnail",
            Name = "kapak denemesi",
            MinimumDetectableEffect = 0.02,
            RequiredPerVariant = 1_500,
        };

        // KOLLAR GERÇEKTEN AYRIŞIYOR. Aynı ayarla iki kol açmak,
        // hiçbir şey ölçmeyen bir deney demek — ve `AssignAsync` artık
        // bunu reddediyor.
        var control = new ExperimentVariant
        {
            Experiment = experiment, Name = "a-kontrol", IsControl = true, ConfigJson = "{}",
        };

        var variant = new ExperimentVariant
        {
            Experiment = experiment, Name = "b-varyant", IsControl = false,
            ConfigJson = """{"konum":"alt"}""",
        };

        db.Experiments.Add(experiment);
        db.ExperimentVariants.AddRange(control, variant);
        await db.SaveChangesAsync(CancellationToken.None);

        return (experiment.Id, control.Id, variant.Id);
    }

    private static async Task<Guid> SeedRunAsync(StudioDbContext db)
    {
        var workflow = new Workflow
        {
            Key = "deney-" + Guid.NewGuid().ToString("N")[..8],
            Name = "deney",
            CurrentVersion = 1,
        };

        var version = new WorkflowVersion
        {
            Workflow = workflow,
            Version = 1,
            GraphJson = """{"schema_version":1,"key":"k","name":"n","nodes":[],"edges":[]}""",
        };

        var run = new Run { WorkflowVersion = version, State = Core.Execution.RunState.Completed };

        db.Workflows.Add(workflow);
        db.WorkflowVersions.Add(version);
        db.Runs.Add(run);
        await db.SaveChangesAsync(CancellationToken.None);

        return run.Id;
    }
}
