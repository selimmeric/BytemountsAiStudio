using System.Text.Json;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Persistence;
using BytemountsAiStudio.Persistence.Entities;
using BytemountsAiStudio.Queue;
using BytemountsAiStudio.TestSupport;
using BytemountsAiStudio.Workflow.Definition;
using BytemountsAiStudio.Workflow.Engine;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Workflow.Tests;

/// Paralel dalların çıktısı birbirini silmemeli.
///
/// GERÇEK BİR KAYIPTAN DOĞDU. Faz 3 kabul koşusunda `music.select`
/// başarıyla bitti, lisanslı bir parça seçti ve çıktısı
/// `node_executions` içinde duruyordu. Ama `runs.context_json`
/// içinde `music` anahtarı YOKTU: timeline sessizce müziksiz
/// derlendi ve video müziksiz çıktı.
///
/// Ölçülen zamanlar sebebi söyledi:
///   music   14:06:06.097 → 14:06:06.119
///   visuals 14:06:06.117 → 14:06:09.063
///
/// `visuals`, müziğin commit'inden **2 ms önce** başladı; bağlamı o
/// anki hâliyle okudu, üç saniye sonra kendi birleştirmesini yazdı ve
/// müziği sildi. Birleştirme bellekte yapılıp KOLONUN TAMAMI geri
/// yazılıyordu.
///
/// CLI'da hiç görülmedi: orada node'lar tek döngüde sırayla koşuyor.
/// Worker sekiz kuyruk sınıfını paralel dinliyor. Bütün doğrulama
/// CLI üzerinden yapılmıştı.
///
/// KAYBIN SESSİZLİĞİ ASIL MESELE: müzik görülebilir bir alan olduğu
/// için fark edildi; aynı yarış her paralel dalın çıktısını
/// kaybedebilirdi ve hiçbiri iz bırakmazdı.
[Collection(DatabaseCollection.Name)]
public sealed class ParallelContextTests(DatabaseFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (!fixture.Available)
        {
            return;
        }

        await using var db = fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM jobs");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM runs");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private void RequireDatabase()
        => Assert.True(fixture.Available, $"PostgreSQL erişilemiyor ({fixture.UnavailableReason}).");

    /// Bir kökten iki dal: ikisi de AYRI kuyruk sınıfında, yani
    /// Worker'da paralel koşuyorlar.
    private static WorkflowGraph ForkGraph() => new()
    {
        Key = "catal",
        Name = "Catallanan hat",
        Nodes =
        [
            new() { Id = "kok", Type = "test.root", Config = ScriptedHandler.Json("{}") },
            new() { Id = "muzik", Type = "test.slow", Config = ScriptedHandler.Json("{}") },
            new() { Id = "gorsel", Type = "test.fast", Config = ScriptedHandler.Json("{}") },
        ],
        Edges =
        [
            new() { From = "kok", To = "muzik" },
            new() { From = "kok", To = "gorsel" },
        ],
    };

    /// İKİ DALIN ÇIKTISI DA BAĞLAMDA KALIYOR.
    ///
    /// Test iki AYRI `DbContext` ve iki ayrı motor kullanıyor —
    /// Worker'ın yaptığı da tam olarak bu. Tek bağlam paylaşmak,
    /// sınanmak istenen yarışı ortadan kaldırırdı: EF'in kimlik
    /// haritası iki okumayı aynı nesneye bağlar ve kayıp hiç
    /// yaşanmazdı.
    [Fact]
    public async Task ParalelIkiDal_IkisininCiktisiDaKaliyor()
    {
        RequireDatabase();

        await using var setup = fixture.CreateContext();
        var versionId = await CreateWorkflowAsync(setup, ForkGraph());

        var root = new ScriptedHandler("test.root", QueueClass.Llm,
            _ => ScriptedHandler.Json("""{"ok":true}"""));

        // Başlatan motorun kaydı graftaki BÜTÜN tipleri tanımak
        // zorunda: `StartRunAsync` grafı doğruluyor ve tanımadığı bir
        // tip run'ı hiç başlatmıyor. Bu iki dalı burada çalıştırmıyor,
        // yalnızca tanıyor.
        var starterRegistry = new NodeRegistry()
            .Register(root)
            .Register(new ScriptedHandler("test.slow", QueueClass.Asset,
                _ => ScriptedHandler.Json("{}")))
            .Register(new ScriptedHandler("test.fast", QueueClass.Search,
                _ => ScriptedHandler.Json("{}")));

        var starter = new WorkflowEngine(setup, new JobQueue(setup), starterRegistry);
        var runId = await starter.StartRunAsync(versionId, null, null, CancellationToken.None);

        Assert.True(runId.IsSuccess, runId.IsFailure ? runId.Error.Message : "");

        // Kök çalışsın; iki dal kuyruğa girsin.
        await starter.ExecuteNextAsync("kok-worker", QueueClass.Llm, CancellationToken.None);

        // YARIŞ BURADA KURULUYOR.
        //
        // `muzik` önce başlayıp HIZLI bitiyor; `gorsel` onun
        // bitişinden hemen önce başlayıp YAVAŞ bitiyor. Yani `gorsel`
        // bağlamı müziksiz okuyor ve sonra yazıyor — üretimde ölçülen
        // sıralamanın aynısı.
        var musicWrote = new TaskCompletionSource();
        var visualsRead = new TaskCompletionSource();

        await using var musicDb = fixture.CreateContext();
        await using var visualsDb = fixture.CreateContext();

        var musicHandler = new ScriptedHandler("test.slow", QueueClass.Asset,
            _ => ScriptedHandler.Json("""{"asset":"sha256:aa","title":"parca"}"""));

        var visualsHandler = new ScriptedHandler("test.fast", QueueClass.Search, _ =>
        {
            // Görsel node'u bağlamı okumuş sayılıyor; müzik yazsın.
            visualsRead.SetResult();
            musicWrote.Task.Wait(TimeSpan.FromSeconds(10));

            return ScriptedHandler.Json("""{"images":3}""");
        });

        var musicEngine = new WorkflowEngine(
            musicDb, new JobQueue(musicDb), new NodeRegistry().Register(musicHandler));

        var visualsEngine = new WorkflowEngine(
            visualsDb, new JobQueue(visualsDb), new NodeRegistry().Register(visualsHandler));

        var visualsTask = Task.Run(() =>
            visualsEngine.ExecuteNextAsync("gorsel-worker", QueueClass.Search, CancellationToken.None));

        await visualsRead.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var musicTask = musicEngine.ExecuteNextAsync("muzik-worker", QueueClass.Asset, CancellationToken.None);
        await musicTask;

        musicWrote.SetResult();
        await visualsTask;

        await using var check = fixture.CreateContext();
        var context = await check.Runs.AsNoTracking()
            .Where(r => r.Id == runId.Value)
            .Select(r => r.ContextJson)
            .SingleAsync(CancellationToken.None);

        using var document = JsonDocument.Parse(context);

        // ASIL İDDİA: İKİSİ DE BURADA.
        //
        // Düzeltmeden önce `muzik` yoktu — sonra yazan `gorsel` onu
        // siliyordu.
        Assert.True(document.RootElement.TryGetProperty("muzik", out var music),
            $"'muzik' bağlamdan silinmiş; paralel dalın çıktısı kayboldu. Bağlam: {context}");

        Assert.True(document.RootElement.TryGetProperty("gorsel", out var visuals),
            $"'gorsel' bağlamda yok. Bağlam: {context}");

        Assert.Equal("sha256:aa", music.GetProperty("asset").GetString());
        Assert.Equal(3, visuals.GetProperty("images").GetInt32());
    }

    private static async Task<Guid> CreateWorkflowAsync(StudioDbContext db, WorkflowGraph graph)
    {
        var workflow = new Persistence.Entities.Workflow
        {
            Key = graph.Key + "-" + Guid.NewGuid().ToString("N")[..6],
            Name = graph.Name,
            CurrentVersion = 1,
        };

        var version = new WorkflowVersion { Version = 1, GraphJson = graph.ToJson() };
        workflow.Versions.Add(version);
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync(CancellationToken.None);

        return version.Id;
    }
}
