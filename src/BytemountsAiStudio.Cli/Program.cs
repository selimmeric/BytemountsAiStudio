// BytemountsAiStudio CLI
//
// Faz 0'in kabul kriteri bu arac uzerinden dogrulaniyor: tek komutla,
// sahte saglayicilarla, aga cikmadan ve para harcamadan gercek bir mp4.

using System.Diagnostics;
using System.Globalization;
using BytemountsAiStudio.Cli;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Persistence;
using BytemountsAiStudio.Persistence.Storage;
using Microsoft.EntityFrameworkCore;

var command = args.Length > 0 ? args[0] : "help";

return command switch
{
    "version" => Version(),
    "pipeline" => await RunPipelineAsync(args).ConfigureAwait(false),
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
