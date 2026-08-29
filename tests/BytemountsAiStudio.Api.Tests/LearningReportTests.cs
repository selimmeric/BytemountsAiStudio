using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Persistence;
using BytemountsAiStudio.Persistence.Entities;
using BytemountsAiStudio.Persistence.Providers;
using BytemountsAiStudio.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Api.Tests;

/// "Ne işe yarıyor" ekranı (P5-06).
///
/// EKRANIN EN ÖNEMLİ İŞİ "VERİ YOK" İLE "ETKİ YOK"U AYIRMAK. Doğru
/// hesaplanan bir sonucu yanlış gösteren bir panel, yanlış hesaplayan
/// bir panelle aynı kararı verdiriyor — bu yüzden testlerin yarısı
/// sayıları değil, sayıların yanındaki CÜMLEYİ sınıyor.
[Collection(DatabaseCollection.Name)]
public sealed class LearningReportTests(DatabaseFixture fixture) : IAsyncLifetime
{
    private const string ChannelName = "öğrenme test kanalı";

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
            + "DELETE FROM node_executions; DELETE FROM run_events; DELETE FROM jobs; "
            + "DELETE FROM runs; DELETE FROM channels WHERE name = '" + ChannelName + "'");
    }

    private void RequireDatabase()
        => Assert.True(fixture.Available, $"PostgreSQL erişilemiyor ({fixture.UnavailableReason}).");

    /* ---- başlık cümlesi ---- */

    /// ÖLÇÜM YOKKEN "İŞE YARAMIYOR" DENMİYOR.
    ///
    /// BU DOSYANIN EN ÖNEMLİ TESTİ. Sıfırlarla dolu bir tablo, bakan
    /// kişiye "denediklerimiz işe yaramıyor" dedirtir. Doğru cümle
    /// "henüz bilmiyoruz" ve ekran bunu AÇIKÇA söylemek zorunda.
    [Fact]
    public void OlcumYok_HenuzBilmiyoruzDiyor()
    {
        var headline = LearningReport.Headline(published: 40, measured: 0, []);

        Assert.Contains("HİÇBİRİNİN performansı ölçülmedi", headline, StringComparison.Ordinal);

        // YANLIŞ SONUÇ ADIYLA ANILIP REDDEDİLİYOR. "Ölçüm yok" demek
        // yetmiyor; bakan kişinin aklına gelen cümle "işe yaramıyor"
        // ve ekran onu açıkça çürütmeli.
        Assert.Contains("Bu 'işe yaramıyor' değil, 'henüz bilmiyoruz'",
            headline, StringComparison.Ordinal);
    }

    /// HİÇ VİDEO YOKSA CÜMLE DE BAŞKA.
    ///
    /// "40 video yayınlandı, hiçbiri ölçülmedi" ile "hiç video yok"
    /// farklı iki durum ve farklı iki iş gerektiriyor.
    [Fact]
    public void VideoYok_AyriCumle()
        => Assert.Equal("Henüz yayınlanmış video yok.", LearningReport.Headline(0, 0, []));

    /// GEÇERSİZ DENEY SAYISI BAŞLIKTA GÖRÜNÜYOR.
    ///
    /// Kapanmış bir deney, kapatıldığı söylenmediği sürece hâlâ veri
    /// topluyor sanılır.
    [Fact]
    public void GecersizDeney_BaslikYaziyor()
    {
        var headline = LearningReport.Headline(
            published: 100,
            measured: 80,
            [Card("Invalid", "NotEnoughData"), Card("Running", "NotEnoughData")]);

        Assert.Contains("1 tanesi GEÇERSİZ", headline, StringComparison.Ordinal);
    }

    /* ---- veritabanı ---- */

    /// BOŞ SİSTEMDE EKRAN ÇALIŞIYOR VE "VERİ YOK" DİYOR.
    ///
    /// Boş veri en sık kırılan yol: ortalama alınacak satır yok,
    /// bölme sıfıra düşüyor. Ekranın ilk günden çalışması gerekiyor.
    [Fact]
    public async Task BosSistem_VeriYokDiyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();

        var summary = await LearningReport.BuildAsync(db, CancellationToken.None);

        Assert.False(summary.HasData);
        Assert.Equal(0, summary.MeasuredRuns);
        Assert.Empty(summary.Experiments);
    }

    /// GEÇERSİZ DENEY EN ÜSTTE.
    ///
    /// Bir deneyin bozuk olduğunu görmek, sonucunu görmekten acil:
    /// bozuk deney düzeltilene kadar hiçbir şey ölçmüyor.
    [Fact]
    public async Task GecersizDeney_EnUstte()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();

        await CreateExperimentAsync(db, "title", "a-kosan", "Running");
        await CreateExperimentAsync(db, "thumbnail", "b-gecersiz", "Invalid");

        var summary = await LearningReport.BuildAsync(db, CancellationToken.None);

        Assert.Equal("Invalid", summary.Experiments[0].State);
        Assert.Equal("b-gecersiz", summary.Experiments[0].Name);
    }

    /// KANAL AĞIRLIKLARI EKRANDA VE "ELLE KONDU" İŞARETLİ.
    ///
    /// Varsayılan ağırlıkların kalibre edilmiş gibi görünmesi,
    /// ölçülmemiş bir hipotezi ölçülmüş gibi göstermek olurdu.
    [Fact]
    public async Task VarsayilanAgirlik_EllKonduIsaretli()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();

        db.Channels.Add(new Channel { Name = ChannelName, Language = "tr-TR" });
        await db.SaveChangesAsync(CancellationToken.None);

        var summary = await LearningReport.BuildAsync(db, CancellationToken.None);
        var card = summary.Weights.Single(w => w.ChannelName == ChannelName);

        Assert.True(card.IsDefault);
        Assert.Equal(nameof(Core.Learning.CalibrationOutcome.NotEnoughData), card.CalibrationOutcome);
        Assert.Contains("henüz bilinmiyor", card.CalibrationReason, StringComparison.Ordinal);
    }

    /// EKRANA BAKMAK HİÇBİR ŞEYİ DEĞİŞTİRMİYOR.
    ///
    /// Kalibrasyon `apply: false` ile çağrılıyor: bir panele girmek
    /// kanalın ağırlıklarını değiştirmemeli.
    [Fact]
    public async Task EkranaBakmak_AyarDegistirmiyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();

        db.Channels.Add(new Channel
        {
            Name = ChannelName, Language = "tr-TR", SettingsJson = """{"daily_target":4}""",
        });

        await db.SaveChangesAsync(CancellationToken.None);

        await LearningReport.BuildAsync(db, CancellationToken.None);

        var settings = await db.Channels.AsNoTracking()
            .Where(c => c.Name == ChannelName).Select(c => c.SettingsJson)
            .FirstAsync(CancellationToken.None);

        // Dizge KARŞILAŞTIRILMIYOR: kolon `jsonb` ve PostgreSQL
        // belgeyi kendi biçiminde geri veriyor (boşluklar, anahtar
        // sırası). Ölçülen şey biçim değil, İÇERİK.
        Assert.DoesNotContain("score_weights", settings, StringComparison.Ordinal);
        Assert.Equal(4, ChannelSettings.Parse(settings).Pacing.DailyTarget);
    }

    /// KOŞAN DENEYİN KARARI HER BAKIŞTA HESAPLANIYOR.
    ///
    /// Saklanmış bir "yeterli veri yok" cevabı, veri geldikten sonra
    /// da ekranda öyle durur.
    [Fact]
    public async Task KosanDeney_KararTazeHesaplaniyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();

        var experimentId = await CreateExperimentAsync(db, "title", "kosan", "Running");
        var runId = await RunAsync(db);

        var variantId = await db.ExperimentVariants.AsNoTracking()
            .Where(v => v.ExperimentId == experimentId).Select(v => v.Id)
            .FirstAsync(CancellationToken.None);

        db.ExperimentAssignments.Add(new ExperimentAssignment
        {
            ExperimentId = experimentId, VariantId = variantId, RunId = runId,
        });

        await db.SaveChangesAsync(CancellationToken.None);

        var summary = await LearningReport.BuildAsync(db, CancellationToken.None);
        var card = summary.Experiments.Single(e => e.Name == "kosan");

        Assert.Equal(1, card.Assigned);
        Assert.Equal(0, card.Measured);
        Assert.Equal(nameof(Core.Learning.ExperimentOutcome.NotEnoughData), card.Outcome);
        Assert.Contains("henüz bilinmiyor", card.Reason, StringComparison.Ordinal);
    }

    /* ---- yardımcılar ---- */

    private static ExperimentCard Card(string state, string outcome)
        => new(Guid.CreateVersion7(), "title", "deney", state, outcome, "", 0, 0, 1500);

    private static async Task<Guid> CreateExperimentAsync(
        StudioDbContext db, string dimension, string name, string state)
    {
        var experiment = new Experiment
        {
            Dimension = dimension,
            Name = name,
            State = state,
            RequiredPerVariant = 1_500,
            Reason = state == "Invalid" ? "ayar tanınmadı" : null,
        };

        db.Experiments.Add(experiment);

        db.ExperimentVariants.AddRange(
            new ExperimentVariant
            {
                Experiment = experiment, Name = "a-kontrol", IsControl = true, ConfigJson = "{}",
            },
            new ExperimentVariant
            {
                Experiment = experiment, Name = "b-varyant", ConfigJson = """{"stil":"soru"}""",
            });

        await db.SaveChangesAsync(CancellationToken.None);

        return experiment.Id;
    }

    private static async Task<Guid> RunAsync(StudioDbContext db)
    {
        var workflow = new Persistence.Entities.Workflow
        {
            Key = "ogrenme-" + Guid.NewGuid().ToString("N")[..8],
            Name = "Öğrenme testi",
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
