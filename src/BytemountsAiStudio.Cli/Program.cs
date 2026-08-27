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
using BytemountsAiStudio.Contracts.Prompts;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Providers.Open;
using BytemountsAiStudio.Persistence.Providers;
using BytemountsAiStudio.Persistence.Storage;
using BytemountsAiStudio.Core.Observability;
using Microsoft.EntityFrameworkCore;

var command = args.Length > 0 ? args[0] : "help";

return command switch
{
    "version" => Version(),
    "pipeline" => await RunPipelineAsync(args).ConfigureAwait(false),
    "run" => await RunWorkflowAsync(args, open: false).ConfigureAwait(false),
    "real" => await RunWorkflowAsync(args, open: true).ConfigureAwait(false),
    "providers" => ShowProviders(),
    "credential" => await RunCredentialAsync(args).ConfigureAwait(false),
    "prompt" => RunPrompt(args),
    "fetch" => await RunFetchAsync(args).ConfigureAwait(false),
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
                                        sahte saglayicilarla kosar
          bmai real [--topic "<konu>"] [--lang tr-TR]
                                        ANAHTARSIZ gercek saglayicilar:
                                        Ollama + Wikipedia + Pollinations +
                                        Windows TTS
          bmai providers                saglayici katalogunu goster
          bmai credential list [--channel <id>]
          bmai credential set <saglayici> [--channel <id>]
                                        anahtari sifreleyerek saklar; deger
                                        stdin'den okunur, komut satirina
                                        yazilmaz
          bmai credential rm <saglayici> [--channel <id>]
          bmai prompt list              istem surumlerini goster
          bmai prompt eval              fixture'lari kosar (model cagirmaz)
          bmai fetch <url>              sayfayi ceker (robots.txt kontrollu)
          bmai db migrate               semayi guncelle
          bmai db seed                  baslangic verisini yukle
          bmai version                  surum
          bmai help                     bu yardim

        Ortam degiskenleri:
          BMAI_CONNECTION               PostgreSQL baglantisi
          BMAI_STORAGE                  varlik deposu kok dizini
          BMAI_KEYRING_PATH             sifreleme anahtar halkasinin yeri
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
static async Task<int> RunWorkflowAsync(string[] args, bool open)
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
    var outputDirectory = Path.Combine(Directory.GetCurrentDirectory(), "output");

    // `run` sahte saglayicilarla, `real` anahtarsiz gercek saglayicilarla.
    // Ikisi de AYNI graf ve AYNI engine uzerinden geciyor; degisen tek sey
    // node kaydi. Provider soyutlamasinin kanti bu.
    using var http = new HttpClient();
    var registry = open
        ? NodeHandlerRegistration.BuildOpenRegistry(storage, http, outputDirectory)
        : NodeHandlerRegistration.BuildFakeRegistry(storage, outputDirectory);

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
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"saglayici : {(open ? "GERCEK (Ollama + Wikipedia + Pollinations + Windows TTS)" : "sahte")}"));

    var stopwatch = Stopwatch.StartNew();
    var seen = new HashSet<string>(StringComparer.Ordinal);

    // Tek surecli worker dongusu: uretimde bunu Worker host yapiyor,
    // burada CLI kendi kuyrugunu tuketiyor.
    // Dongu backoff ve erteleme surelerini beklemek zorunda: is `run_after`
    // ile ileri tarihe atildiginda hemen alinamaz. Gecikmesiz dongu bosuna
    // donup zaman asimina ugruyordu.
    var deadline = DateTimeOffset.UtcNow.AddMinutes(10);

    while (DateTimeOffset.UtcNow < deadline)
    {
        foreach (var queueClass in Enum.GetValues<QueueClass>())
        {
            await engine.ExecuteNextAsync("cli", queueClass, CancellationToken.None).ConfigureAwait(false);
        }

        await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);

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



/// Kimlik yonetimi (P1-01).
///
/// Anahtar KOMUT SATIRINDAN alinmiyor, stdin'den okunuyor. Komut satirina
/// yazilan bir deger kabuk gecmisine, islem listesine ve ekran goruntusune
/// girer; uc yerde birden sizdirmanin gerekcesi yok.
static async Task<int> RunCredentialAsync(string[] args)
{
    var subcommand = args.Length > 1 ? args[1] : "list";
    var channelText = Option(args, "--channel", string.Empty);
    Guid? channel = Guid.TryParse(channelText, out var parsed) ? parsed : null;

    if (channelText.Length > 0 && channel is null)
    {
        Console.Error.WriteLine("--channel gecerli bir GUID olmali.");
        return 2;
    }

    await using var db = CreateContext();
    var store = new CredentialStore(db, KeyRing.Create());

    switch (subcommand)
    {
        case "list":
            var rows = await store.ListAsync(channel, CancellationToken.None).ConfigureAwait(false);

            if (rows.Count == 0)
            {
                Console.WriteLine("Kayitli anahtar yok. 'bmai providers' hangilerinin beklendigini gosteriyor.");
                return 0;
            }

            Console.WriteLine();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {"SAGLAYICI",-20} {"DEGER",-10} {"KAPSAM",-10} SON KULLANIM"));
            Console.WriteLine("  " + new string('-', 66));

            foreach (var row in rows)
            {
                var scope = row.ChannelId is null ? "genel" : "kanal";
                var used = row.LastUsedAt?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "-";

                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  {row.ProviderKey,-20} {row.Masked,-10} {scope,-10} {used}"));
            }

            Console.WriteLine();
            return 0;

        case "set":
            if (args.Length < 3 || args[2].StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("Kullanim: bmai credential set <saglayici>");
                return 2;
            }

            Console.Error.WriteLine($"'{args[2]}' anahtarini yapistirip Enter'a basin (ekranda gorunur):");
            var secret = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(secret))
            {
                Console.Error.WriteLine("Bos deger okundu; hicbir sey saklanmadi.");
                return 2;
            }

            var saved = await store.SetAsync(args[2], channel, secret.Trim(), CancellationToken.None)
                .ConfigureAwait(false);

            if (saved.IsFailure)
            {
                Console.Error.WriteLine(saved.Error.ToString());
                return 1;
            }

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"'{args[2]}' saklandi ({SecretRedactor.Mask4(secret.Trim())}). Anahtar halkasi: {KeyRing.Default.FullName}"));
            return 0;

        case "rm":
            if (args.Length < 3)
            {
                Console.Error.WriteLine("Kullanim: bmai credential rm <saglayici>");
                return 2;
            }

            var removed = await store.DeleteAsync(args[2], channel, CancellationToken.None).ConfigureAwait(false);

            if (removed.IsFailure)
            {
                Console.Error.WriteLine(removed.Error.ToString());
                return 1;
            }

            Console.WriteLine($"'{args[2]}' silindi.");
            return 0;

        default:
            Console.Error.WriteLine($"Bilinmeyen credential komutu: {subcommand}");
            return 2;
    }
}

/// Saglayici katalogunu gosterir (config/providers.json).
///
/// "Su an ne ile calisabiliyorum" ve "anahtar gelirse ne acilir" sorularinin
/// tek cevap noktasi.
/// Tek bir sayfayi ceker ve ne cikardigini gosterir (P1-06).
///
/// Arastirma sonuclari bozuk geldiginde ilk bakilacak yer burasi:
/// sorun aramada mi, cekimde mi, yoksa metin cikariminda mi.
static async Task<int> RunFetchAsync(string[] args)
{
    if (args.Length < 2 || !Uri.TryCreate(args[1], UriKind.Absolute, out var url))
    {
        Console.Error.WriteLine("Kullanim: bmai fetch <url>");
        return 2;
    }

    using var http = new HttpClient();
    var provider = new WebFetchProvider(http);

    var result = await provider
        .FetchAsync(url, ProviderContext.ForTest("cli-fetch"), CancellationToken.None)
        .ConfigureAwait(false);

    if (result.IsFailure)
    {
        Console.Error.WriteLine($"Cekilemedi: {result.Error}");
        return 1;
    }

    var document = result.Value.Value;

    Console.WriteLine();
    Console.WriteLine($"  adres   : {document.Url}");
    Console.WriteLine($"  baslik  : {document.Title}");
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"  metin   : {document.MainText.Length} karakter"));
    Console.WriteLine($"  ozet    : {document.ContentHash[..16]}");
    Console.WriteLine($"  duvar   : {(document.IsPaywalled ? "ODEME DUVARI SUPHESI" : "yok")}");
    Console.WriteLine();
    Console.WriteLine("  --- ilk 600 karakter ---");
    Console.WriteLine(document.MainText.Length > 600 ? document.MainText[..600] : document.MainText);
    Console.WriteLine();

    return 0;
}

/// Istem kayit defteri (P1-07).
///
/// `bmai prompt eval` CI'da kosuyor: bir istem duzenlendiginde
/// fixture'lar kiriliyor. Model CAGRILMIYOR - dogrulanan sey
/// doldurulmus istemin kendisi (bkz. PromptEvaluator).
static int RunPrompt(string[] args)
{
    var subcommand = args.Length > 1 ? args[1] : "list";

    // Diskteki dizin varsa o kazaniyor: istem duzenleyip yeniden
    // derlemeden denemek mumkun kalsin.
    var directory = Path.Combine(Directory.GetCurrentDirectory(), "prompts");
    var onDisk = Directory.Exists(directory);
    var registry = onDisk ? PromptRegistry.Load(directory) : PromptRegistry.Embedded;

    if (registry.IsFailure)
    {
        Console.Error.WriteLine($"Istemler okunamadi: {registry.Error}");
        return 1;
    }

    switch (subcommand)
    {
        case "list":
            Console.WriteLine();
            Console.WriteLine(onDisk ? $"  Kaynak: {directory}" : "  Kaynak: derlemeye gomulu");
            Console.WriteLine("  " + new string('-', 74));

            foreach (var key in registry.Value.Keys.Order(StringComparer.Ordinal))
            {
                foreach (var template in registry.Value.Versions(key))
                {
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"  {template.Stamp,-46} {template.Description}"));
                }
            }

            Console.WriteLine();
            return 0;

        case "eval":
            if (!onDisk)
            {
                Console.Error.WriteLine($"Fixture'lar icin '{directory}' dizini gerekiyor.");
                return 2;
            }

            var report = PromptEvaluator.RunAll(registry.Value, directory);

            if (report.IsFailure)
            {
                Console.Error.WriteLine(report.Error.ToString());
                return 1;
            }

            Console.WriteLine();

            foreach (var result in report.Value.Results)
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  {(result.Passed ? "GECTI" : "KALDI")}  {result.Name,-28} {result.Stamp,-46} {result.RenderedChars} krk"));

                foreach (var failure in result.Failures)
                {
                    Console.WriteLine($"         - {failure}");
                }
            }

            Console.WriteLine();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {report.Value.Passed} gecti, {report.Value.Failed} kaldi."));
            Console.WriteLine();

            return report.Value.AllPassed ? 0 : 1;

        default:
            Console.Error.WriteLine($"Bilinmeyen prompt komutu: {subcommand}");
            return 2;
    }
}

static int ShowProviders()
{
    var path = Path.Combine(Directory.GetCurrentDirectory(), "config", "providers.json");
    var catalog = BytemountsAiStudio.Contracts.Providers.ProviderCatalog.Load(path);

    if (catalog.IsFailure)
    {
        Console.Error.WriteLine($"Katalog okunamadi: {catalog.Error}");
        return 1;
    }

    var value = catalog.Value;

    Console.WriteLine();
    Console.WriteLine("  ANAHTARSIZ CALISANLAR");
    Console.WriteLine("  " + new string('-', 74));

    foreach (var role in new[] { "llm", "search", "image.stock", "image.generative", "tts", "asr", "publish" })
    {
        foreach (var provider in value.For(role))
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {role,-18} {provider.DisplayName,-34} {provider.Cost}"));
        }
    }

    Console.WriteLine();
    Console.WriteLine("  ANAHTAR BEKLEYENLER");
    Console.WriteLine("  " + new string('-', 74));

    foreach (var provider in value.AwaitingKeys())
    {
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  {provider.Role,-18} {provider.DisplayName,-34} {provider.KeyEnv}"));
    }

    var free = value.KeyFree().Count;
    Console.WriteLine();
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"  Toplam {value.Providers.Count} saglayici; {free} tanesi anahtarsiz."));
    Console.WriteLine();

    return 0;
}
