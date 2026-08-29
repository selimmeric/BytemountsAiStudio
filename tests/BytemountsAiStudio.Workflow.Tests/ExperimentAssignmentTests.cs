using System.Text.Json;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Core.Learning;
using BytemountsAiStudio.Persistence;
using BytemountsAiStudio.Persistence.Entities;
using BytemountsAiStudio.Queue;
using BytemountsAiStudio.TestSupport;
using BytemountsAiStudio.Workflow.Definition;
using BytemountsAiStudio.Workflow.Engine;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Workflow.Tests;

/// Deney kolunun run bağlamına ULAŞTIĞI (P5-03).
///
/// Bu dosya bir boşluğu kapatıyor: atama tablosuna satır yazmak
/// deneyi KAYDEDİYOR ama UYGULAMIYOR. Kapak ve başlık node'ları atama
/// tablosunu okumuyor — run bağlamını okuyor. Köprü kopuksa deney
/// kusursuz görünür (satırlar var, kollar dengeli) ve iki kolda da
/// AYNI videoyu üretir.
///
/// Depodaki en pahalı hata sınıfı tam olarak bu: "yazıldı ama
/// bağlanmadı". `DatabaseSeeder`'ın başındaki not, aynı hatanın müzik
/// ve kapak node'larında nasıl sessizce geçtiğini anlatıyor.
[Collection(DatabaseCollection.Name)]
public sealed class ExperimentAssignmentTests(DatabaseFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (!fixture.Available)
        {
            return;
        }

        await using var db = fixture.CreateContext();

        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM jobs; DELETE FROM experiment_assignments; "
            + "DELETE FROM experiment_variants; DELETE FROM experiments; "
            + "DELETE FROM runs; DELETE FROM workflow_versions; DELETE FROM workflows");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private void RequireDatabase()
        => Assert.True(fixture.Available, $"PostgreSQL erişilemiyor ({fixture.UnavailableReason}).");

    /// ATANAN KOL RUN BAĞLAMINA YAZILIYOR.
    ///
    /// Ölçülen şey atama satırı değil, `runs.context_json` — çünkü
    /// node'ların okuduğu yer orası.
    [Fact]
    public async Task Deney_KolRunBaglaminaYaziliyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();
        var versionId = await CreateWorkflowAsync(db);
        await CreateExperimentAsync(db, "thumbnail", """{"harf":"buyuk"}""");

        var runId = await StartAsync(db, versionId);

        var context = await ContextOfAsync(db, runId);
        var config = ExperimentContext.ConfigFor(context.RootElement, "thumbnail");

        Assert.NotNull(config);

        // Ayar GERÇEKTEN ayrıştırılabiliyor: bağlama yazılan şeyin
        // node'un okuyabildiği biçimde olması ayrı bir iddia.
        var parsed = ThumbnailVariant.Parse(config);
        Assert.True(parsed.IsSuccess, parsed.IsFailure ? parsed.Error.Message : string.Empty);

        Assert.NotNull(ExperimentContext.VariantName(context.RootElement, "thumbnail"));
        Assert.Equal(1, await db.ExperimentAssignments.CountAsync(CancellationToken.None));
    }

    /// DENEY YOKSA BAĞLAM KİRLENMİYOR.
    ///
    /// Boş bir `experiments` bloğu yazmak zararsız görünür ama
    /// "deneye girdi mi" sorusunu bağlamdan cevaplanamaz kılardı.
    [Fact]
    public async Task DeneyYok_BaglamdaBlokYok()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();
        var versionId = await CreateWorkflowAsync(db);

        var runId = await StartAsync(db, versionId);
        var context = await ContextOfAsync(db, runId);

        Assert.False(context.RootElement.TryGetProperty(ExperimentContext.Key, out _));
    }

    /// BOZUK DENEY RUN'I DÜŞÜRMÜYOR — ama sessizce de koşmuyor.
    ///
    /// Bir ölçüm hatasına üretimi feda etmek yanlış olurdu; ama
    /// atlanan deneyi "koşuyor" bırakmak daha yanlış: haftalarca veri
    /// toplar ve sonunda "fark yok" der.
    [Fact]
    public async Task TaninmayanAyar_DeneyKapaniyorRunKosuyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();
        var versionId = await CreateWorkflowAsync(db);
        await CreateExperimentAsync(db, "thumbnail", """{"konumu":"alt"}""");

        var runId = await StartAsync(db, versionId);

        var context = await ContextOfAsync(db, runId);
        Assert.False(context.RootElement.TryGetProperty(ExperimentContext.Key, out _));

        var experiment = await db.Experiments.AsNoTracking().FirstAsync(CancellationToken.None);

        Assert.Equal("Invalid", experiment.State);
        Assert.Contains("konumu", experiment.Reason ?? string.Empty, StringComparison.Ordinal);
        Assert.Empty(await db.ExperimentAssignments.ToListAsync(CancellationToken.None));
    }

    /// İKİ BOYUTTA AYRIŞAN DENEY KOŞMUYOR.
    ///
    /// `ExperimentEvaluator.SingleChangedDimension` P5-02'de yazılmış
    /// ama HİÇBİR YERDEN ÇAĞRILMIYORDU. Bu test onu hatta tutuyor:
    /// hem kapağı hem puntoyu değiştiren bir deney kazandığında
    /// hangisinin kazandırdığı bilinemezdi.
    [Fact]
    public async Task IkiBoyutDegisen_DeneyKapaniyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();
        var versionId = await CreateWorkflowAsync(db);
        await CreateExperimentAsync(db, "thumbnail", """{"harf":"buyuk","punto":"buyuk"}""");

        await StartAsync(db, versionId);

        var experiment = await db.Experiments.AsNoTracking().FirstAsync(CancellationToken.None);

        Assert.Equal("Invalid", experiment.State);
        Assert.Contains("2 boyutta", experiment.Reason ?? string.Empty, StringComparison.Ordinal);
    }

    /// AYNI BOYUTTA İKİ AÇIK DENEY: ikisi de kapanıyor.
    ///
    /// Veritabanı kısıtı kanal+boyut ikilisini tekil tutuyor, ama
    /// KANALSIZ bir deney kanala özel bir deneyle aynı boyutta
    /// çakışabiliyor. Birini seçmek keyfî olurdu; ikisini birden
    /// uygulamak tek değişken kuralını deneylerin ARASINDA kırardı.
    [Fact]
    public async Task AyniBoyuttaIkiDeney_IkisiDeKapaniyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();
        var versionId = await CreateWorkflowAsync(db);

        await CreateExperimentAsync(db, "thumbnail", """{"harf":"buyuk"}""");
        await CreateExperimentAsync(db, "thumbnail", """{"punto":"buyuk"}""");

        var runId = await StartAsync(db, versionId);

        var context = await ContextOfAsync(db, runId);
        Assert.False(context.RootElement.TryGetProperty(ExperimentContext.Key, out _));

        var states = await db.Experiments.AsNoTracking()
            .Select(e => e.State).ToListAsync(CancellationToken.None);

        Assert.Equal(["Invalid", "Invalid"], states);
    }

    /// FARKLI BOYUTLARDA İKİ DENEY AYNI ANDA KOŞABİLİYOR.
    ///
    /// Kapak deneyi ile başlık deneyi birbirinin değişkeni değil; aynı
    /// videoda ikisini birden ölçmek, deney başına gereken örneklemi
    /// ikiye katlamamak demek.
    [Fact]
    public async Task FarkliBoyutlar_IkisiDeUygulaniyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();
        var versionId = await CreateWorkflowAsync(db);

        await CreateExperimentAsync(db, "thumbnail", """{"harf":"buyuk"}""");
        await CreateExperimentAsync(db, "title", """{"stil":"soru"}""");

        var runId = await StartAsync(db, versionId);
        var context = await ContextOfAsync(db, runId);

        Assert.NotNull(ExperimentContext.ConfigFor(context.RootElement, "thumbnail"));
        Assert.NotNull(ExperimentContext.ConfigFor(context.RootElement, "title"));
        Assert.Equal(2, await db.ExperimentAssignments.CountAsync(CancellationToken.None));
    }

    /* ---- istem deneyi (P5-05) ---- */

    /// VAR OLAN İSTEM SÜRÜMÜ ATANIYOR.
    [Fact]
    public async Task IstemDeneyi_Atantyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();
        var versionId = await CreateWorkflowAsync(db);

        await CreateExperimentAsync(db, "prompt",
            """{"istem":"seo.generate","surum":"2"}""",
            controlConfig: """{"istem":"seo.generate","surum":"1"}""");

        var runId = await StartAsync(db, versionId);
        var context = await ContextOfAsync(db, runId);

        var config = ExperimentContext.ConfigFor(context.RootElement, "prompt");

        Assert.NotNull(config);

        var parsed = PromptVariant.Parse(config);
        Assert.True(parsed.IsSuccess, parsed.IsFailure ? parsed.Error.Message : string.Empty);
        Assert.Equal("seo.generate", parsed.Value.Key);
    }

    /// OLMAYAN İSTEM SÜRÜMÜ İLK VİDEODAN ÖNCE YAKALANIYOR.
    ///
    /// Bu kontrol olmasaydı iki sonuçtan biri olurdu: ya `Get`
    /// hatasıyla her run düşerdi, ya da bir yerde varsayılana düşülüp
    /// iki kol AYNI istemi kullanırdı. İkincisi daha kötü — deney
    /// haftalarca koşup "fark yok" derdi.
    [Fact]
    public async Task OlmayanIstemSurumu_DeneyKapaniyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();
        var versionId = await CreateWorkflowAsync(db);

        await CreateExperimentAsync(db, "prompt",
            """{"istem":"seo.generate","surum":"99"}""",
            controlConfig: """{"istem":"seo.generate","surum":"1"}""");

        var runId = await StartAsync(db, versionId);
        var context = await ContextOfAsync(db, runId);

        Assert.False(context.RootElement.TryGetProperty(ExperimentContext.Key, out _));

        var experiment = await db.Experiments.AsNoTracking().FirstAsync(CancellationToken.None);

        Assert.Equal("Invalid", experiment.State);
        Assert.Contains("99", experiment.Reason ?? string.Empty, StringComparison.Ordinal);
    }

    /// OLMAYAN İSTEM ANAHTARI DA YAKALANIYOR.
    [Fact]
    public async Task OlmayanIstemAnahtari_DeneyKapaniyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();
        var versionId = await CreateWorkflowAsync(db);

        await CreateExperimentAsync(db, "prompt",
            """{"istem":"olmayan.istem","surum":"2"}""",
            controlConfig: """{"istem":"olmayan.istem","surum":"1"}""");

        await StartAsync(db, versionId);

        var experiment = await db.Experiments.AsNoTracking().FirstAsync(CancellationToken.None);

        Assert.Equal("Invalid", experiment.State);
    }

    /* ---- yardımcılar ---- */

    private static async Task<Guid> StartAsync(StudioDbContext db, Guid versionId)
    {
        var engine = new WorkflowEngine(
            db,
            new JobQueue(db),
            new NodeRegistry().Register(new ScriptedHandler(
                "test.tek", QueueClass.Llm, _ => ScriptedHandler.Json("{}"))));

        var run = await engine.StartRunAsync(versionId, null, null, CancellationToken.None);

        Assert.True(run.IsSuccess, run.IsFailure ? run.Error.Message : string.Empty);

        return run.Value;
    }

    private static async Task<JsonDocument> ContextOfAsync(StudioDbContext db, Guid runId)
        => JsonDocument.Parse(await db.Runs.AsNoTracking()
            .Where(r => r.Id == runId)
            .Select(r => r.ContextJson)
            .FirstAsync(CancellationToken.None));

    private static async Task<Guid> CreateWorkflowAsync(StudioDbContext db)
    {
        var graph = WorkflowGraph.Parse("""
            {
              "schema_version": 1,
              "key": "deney",
              "name": "Deney grafı",
              "nodes": [ { "id": "tek", "type": "test.tek", "config": {} } ],
              "edges": []
            }
            """);

        Assert.NotNull(graph);

        var workflow = new Persistence.Entities.Workflow
        {
            Key = "deney-" + Guid.NewGuid().ToString("N")[..6],
            Name = graph.Name,
            CurrentVersion = 1,
        };

        var version = new WorkflowVersion { Version = 1, GraphJson = graph.ToJson() };
        workflow.Versions.Add(version);
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync(CancellationToken.None);

        return version.Id;
    }

    private static async Task CreateExperimentAsync(
        StudioDbContext db, string dimension, string variantConfig, string controlConfig = "{}")
    {
        var experiment = new Experiment
        {
            Dimension = dimension,
            Name = dimension + " denemesi",
            RequiredPerVariant = 1_500,
        };

        db.Experiments.Add(experiment);

        db.ExperimentVariants.AddRange(
            new ExperimentVariant
            {
                Experiment = experiment, Name = "a-kontrol", IsControl = true, ConfigJson = controlConfig,
            },
            new ExperimentVariant
            {
                Experiment = experiment, Name = "b-varyant", IsControl = false, ConfigJson = variantConfig,
            });

        await db.SaveChangesAsync(CancellationToken.None);
    }
}
