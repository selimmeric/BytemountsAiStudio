// BytemountsAiStudio CLI
//
// Faz 0'in kabul kriteri bu arac uzerinden dogrulaniyor: tek komutla,
// sahte saglayicilarla, aga cikmadan ve para harcamadan gercek bir mp4.

using System.Diagnostics;
using System.Globalization;
using BytemountsAiStudio.Cli;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Nodes;
using BytemountsAiStudio.Workflow.Engine;
using BytemountsAiStudio.Queue;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Persistence;
using BytemountsAiStudio.Persistence.Storage;
using Microsoft.EntityFrameworkCore;

var command = args.Length > 0 ? args[0] : "help";

return command switch
{
    "version" => Version(),
    "pipeline" => await RunPipelineAsync(args).ConfigureAwait(false),
    "run" => await RunWorkflowAsync(args).ConfigureAwait(false),
    "db" => await RunDatabaseAsync(args).ConfigureAwait(false),
    "help" or "--help" or "-h" => Help(),
    _ => Unknown(command),
};

static int Version()
{
    var version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0";
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"bytemounts-ai-studio {version}"));
    return 0;
}

static int Help()
{
    Console.WriteLine("""
        BytemountsAiStudio CLI

        Kullanim:
          bmai pipeline [--topic "<konu>"] [--out <dosya.mp4>] [--lang tr-TR] [--dot <graf.dot>]
                                        sahte boru hatti: konu -> mp4
          bmai run [--topic "<konu>"] [--lang tr-TR]
                                        workflow engine uzerinden kosar
          bmai db migrate               semayi guncelle
          bmai db seed                  baslangic verisini yukle
          bmai version                  surum
          bmai help                     bu yardim

        Ortam degiskenleri:
          BMAI_CONNECTION               PostgreSQL baglantisi
          BMAI_STORAGE                  varlik deposu kok dizini
        """);
    return 0;
}

static int Unknown(string command)
{
    Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"Bilinmeyen komut: {command}. 'bmai help' deneyin."));
    return 2;
}

static string Option(string[] args, string name, string fallback)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
}

static string StorageRoot()
    => Environment.GetEnvironmentVariable("BMAI_STORAGE")
       ?? Path.Combine(Directory.GetCurrentDirectory(), "storage");

static StudioDbContext CreateContext()
{
    var connectionString = Environment.GetEnvironmentVariable("BMAI_CONNECTION")
                           ?? StudioDbContextFactory.DefaultConnectionString;

    return new StudioDbContext(StudioDbContextFactory.Build(connectionString).Options);
}

static async Task<int> RunDatabaseAsync(string[] args)
{
    var subcommand = args.Length > 1 ? args[1] : "migrate";
    await using var db = CreateContext();

    switch (subcommand)
    {
        case "migrate":
            await db.Database.MigrateAsync().ConfigureAwait(false);
            Console.WriteLine("Sema guncel.");
            return 0;

        case "seed":
            var added = await DatabaseSeeder.SeedAsync(db).ConfigureAwait(false);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Seed tamam ({added} yeni kayit)."));
            return 0;

        default:
            Console.Error.WriteLine($"Bilinmeyen db komutu: {subcommand}");
            return 2;
    }
}

static async Task<int> RunPipelineAsync(string[] args)
{
    var topic = Option(args, "--topic", "Dunyanin En Tehlikeli 10 Yeri");
    var output = Option(args, "--out", Path.Combine("output", "fake-short.mp4"));
    var languageTag = Option(args, "--lang", "tr-TR");
    var dot = Array.IndexOf(args, "--dot") >= 0 ? Option(args, "--dot", "graph.dot") : null;

    var language = LanguageTag.TryCreate(languageTag);
    if (language.IsFailure)
    {
        Console.Error.WriteLine(language.Error.Message);
        return 2;
    }

    await using var db = CreateContext();

    try
    {
        await db.Database.MigrateAsync().ConfigureAwait(false);
    }
    catch (Npgsql.NpgsqlException ex)
    {
        Console.Error.WriteLine($"Veritabanina baglanilamadi: {ex.Message}");
        Console.Error.WriteLine("`docker compose up -d` calistirin.");
        return 3;
    }

    var storage = new FileSystemAssetStore(db, StorageRoot());
    var pipeline = new FakeShortsPipeline(storage);

    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"konu      : {topic}"));
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"dil       : {language.Value}"));

    var stopwatch = Stopwatch.StartNew();
    var result = await pipeline
        .RunAsync(topic, output, language.Value, Console.WriteLine, dot)
        .ConfigureAwait(false);

    if (result.IsFailure)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"BASARISIZ: {result.Error}");
        if (result.Error.Detail is { } detail)
        {
            Console.Error.WriteLine(detail);
        }

        return 1;
    }

    var outcome = result.Value;
    var probe = outcome.Probe;

    Console.WriteLine();
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"cikti     : {outcome.OutputPath}"));
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"olculen   : {probe.Width}x{probe.Height} {probe.VideoCodec}/{probe.AudioCodec}, "
        + $"{probe.DurationSeconds:0.###} sn, {probe.SizeBytes / 1024} KB"));
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"sure      : render {outcome.Duration.TotalSeconds:0.#} sn, toplam {stopwatch.Elapsed.TotalSeconds:0.#} sn"));

    return 0;
}


/// Workflow engine uzerinden kosum (P0-27).
///
/// `pipeline` komutundan farki: adimlar dogrudan cagrilmiyor, kuyruga
/// atiliyor ve engine tarafindan surukleniyor. Ayni is, gercek uretimdeki
/// yoldan geciyor - kuyruk, node kaydi, idempotency, hata siniflandirmasi.
static async Task<int> RunWorkflowAsync(string[] args)
{
    var topic = Option(args, "--topic", "Dunyanin En Tehlikeli 10 Yeri");
    var languageTag = Option(args, "--lang", "tr-TR");

    await using var db = CreateContext();

    try
    {
        await db.Database.MigrateAsync().ConfigureAwait(false);
        await DatabaseSeeder.SeedAsync(db).ConfigureAwait(false);
    }
    catch (Npgsql.NpgsqlException ex)
    {
        Console.Error.WriteLine($"Veritabanina baglanilamadi: {ex.Message}");
        return 3;
    }

    var storage = new FileSystemAssetStore(db, StorageRoot());
    var registry = NodeHandlerRegistration.BuildFakeRegistry(
        storage, Path.Combine(Directory.GetCurrentDirectory(), "output"));

    var queue = new JobQueue(db);
    var engine = new WorkflowEngine(db, queue, registry);

    var version = await db.WorkflowVersions
        .Where(v => v.Workflow!.Key == DatabaseSeeder.FakeWorkflowKey)
        .OrderByDescending(v => v.Version)
        .FirstOrDefaultAsync()
        .ConfigureAwait(false);

    if (version is null)
    {
        Console.Error.WriteLine("shorts-fake workflow bulunamadi.");
        return 4;
    }

    var input = System.Text.Json.JsonSerializer.Serialize(new
    {
        input = new { topic, language = languageTag },
    });

    var started = await engine.StartRunAsync(version.Id, null, null, CancellationToken.None, input)
        .ConfigureAwait(false);

    if (started.IsFailure)
    {
        Console.Error.WriteLine($"Run baslatilamadi: {started.Error}");
        return 1;
    }

    var runId = started.Value;
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"run       : {runId}"));
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"konu      : {topic}"));

    var stopwatch = Stopwatch.StartNew();
    var seen = new HashSet<string>(StringComparer.Ordinal);

    // Tek surecli worker dongusu: uretimde bunu Worker host yapiyor,
    // burada CLI kendi kuyrugunu tuketiyor.
    for (var i = 0; i < 400; i++)
    {
        foreach (var queueClass in Enum.GetValues<QueueClass>())
        {
            await engine.ExecuteNextAsync("cli", queueClass, CancellationToken.None).ConfigureAwait(false);
        }

        db.ChangeTracker.Clear();

        var executions = await db.NodeExecutions.AsNoTracking()
            .Where(e => e.RunId == runId)
            .OrderBy(e => e.CreatedAt)
            .Select(e => new { e.NodeId, e.State, e.DurationMs })
            .ToListAsync().ConfigureAwait(false);

        foreach (var execution in executions.Where(e => seen.Add(e.NodeId)))
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {execution.NodeId,-10}: {execution.State} ({execution.DurationMs} ms)"));
        }

        var run = await db.Runs.AsNoTracking().FirstAsync(r => r.Id == runId).ConfigureAwait(false);

        if (run.State is RunState.Completed or RunState.Failed or RunState.Cancelled)
        {
            Console.WriteLine();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"durum     : {run.State}"));

            if (run.ErrorJson is { } error)
            {
                Console.Error.WriteLine(error);
            }

            var output = System.Text.Json.JsonDocument.Parse(run.ContextJson).RootElement;

            if (output.TryGetProperty("render", out var render)
                && render.TryGetProperty("output_path", out var path))
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"cikti     : {path.GetString()}"));
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"olculen   : {render.GetProperty("width").GetInt32()}x{render.GetProperty("height").GetInt32()}, "
                    + $"{render.GetProperty("duration_seconds").GetDouble():0.###} sn, "
                    + $"{render.GetProperty("size_bytes").GetInt64() / 1024} KB"));
            }

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"sure      : {stopwatch.Elapsed.TotalSeconds:0.#} sn"));

            return run.State == RunState.Completed ? 0 : 1;
        }
    }

    Console.Error.WriteLine("Run zaman asimina ugradi.");
    return 5;
}
