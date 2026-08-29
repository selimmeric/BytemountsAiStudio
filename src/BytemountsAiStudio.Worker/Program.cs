using Microsoft.Extensions.DependencyInjection;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Nodes;
using BytemountsAiStudio.Persistence;
using BytemountsAiStudio.Providers.Llm;
using BytemountsAiStudio.Persistence.Providers;
using BytemountsAiStudio.Persistence.Storage;
using BytemountsAiStudio.Queue;
using BytemountsAiStudio.Worker;
using BytemountsAiStudio.Workflow.Engine;
using Serilog;
using Serilog.Events;

var builder = Host.CreateApplicationBuilder(args);

var connectionString = Environment.GetEnvironmentVariable("BMAI_CONNECTION")
                       ?? StudioDbContextFactory.DefaultConnectionString;

var seqUrl = Environment.GetEnvironmentVariable("BMAI_SEQ") ?? "http://localhost:5341";
var storageRoot = Environment.GetEnvironmentVariable("BMAI_STORAGE")
                  ?? Path.Combine(Directory.GetCurrentDirectory(), "storage");
var outputRoot = Environment.GetEnvironmentVariable("BMAI_OUTPUT")
                 ?? Path.Combine(Directory.GetCurrentDirectory(), "output");

// Yapilandirilmis log: bir run'in tum satirlari RunId ile filtrelenebilsin.
// Duz metin logda bu sorgu yazilamaz; otonom sistemde tek teshis araci bu.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("service", "worker")
    .Enrich.With<CorrelationEnricher>()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.Seq(seqUrl)
    .CreateLogger();

builder.Logging.ClearProviders();
builder.Services.AddSerilog();

builder.Services.AddStudioPersistence(connectionString);
builder.Services.AddScoped<JobQueue>();

// Depolama ve node kaydi SCOPED: ikisi de DbContext'e bagli. Singleton
// yapilsaydi tum worker donguleri ayni change tracker uzerinde yarisirdi.
// DEPO SECIMI TEK YERDE (P4-02): `BMAI_S3_ENDPOINT` doluysa nesne
// deposu, bossa dosya sistemi. Uc host'ta ayri `if` yazmak, birinin
// S3'e digerinin dosya sistemine bakmasi demekti -- ve bu depoda tam
// olarak o hata (CLI ile Worker'in farkli kurulmasi) bir gunu goturdu.
builder.Services.AddScoped<IStorageProvider>(sp =>
    StorageSelection.Build(sp.GetRequiredService<StudioDbContext>(), storageRoot));

// TEKILLIK VE KANAL POLITIKASI BURADA DA VERILIYOR.
//
// Verilmiyordu ve sonucu sessizdi: tekillik olculmuyordu, yani QC her
// videoyu "olculmedi" deyip insana gonderiyordu -- otonomi bitiyordu.
// Kanal politikasi da yoktu, yani ses, yazi tipi, en-boy orani ve onay
// modu varsayilana dusuyordu: uc ayri kanal tek tip video uretiyordu.
//
// Yalnizca CLI ikisini de veriyordu ve butun dogrulama CLI uzerinden
// yapilmisti. Parametreler artik zorunlu; unutmak derlenmiyor.
// ***HAT SECIMI ARTIK BURADA SABIT DEGIL.***
//
// Worker `BuildFakeRegistry`'yi SABIT cagiriyordu ve secenek yoktu:
// zamanlayici kosu baslatiyor, isler kuyruga giriyor ve bu worker
// onlari calistiriyor -- yani OTONOM FABRIKA, tasarlandigi gibi
// kostugunda bastan sona SAHTE video uretiyordu. Gercek icerik
// yalnizca elle `bmai run --acik` cagirarak uretilebiliyordu, ki o da
// fabrikanin var olus sebebinin tersi.
//
// Karar tek yerde (`RegistrySelection`) ve Api ile Cli ayni yeri
// cagiriyor: uc host'un uc ayri karar vermesi, ayrismanin kendisiydi.
builder.Services.AddScoped(sp =>
    RegistrySelection.BuildFromStore(
        sp.GetRequiredService<IStorageProvider>(),
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("nodes"),
        outputRoot,
        new TitleUniqueness(sp.GetRequiredService<StudioDbContext>()),
        new ChannelPolicy(sp.GetRequiredService<StudioDbContext>()),
        // SAGLAYICI ZINCIRI (P0-14): olcum, butce, hiz siniri, devre
        // kesici. Sahte hatta da takili -- para harcamayan bir hatta
        // zincirin dogru kuruldugunu sinamak, ucret odemeden dogrulama
        // yapabilmenin tek yolu.
        PipelineSelection.BuildFrom(
            sp.GetRequiredService<StudioDbContext>(),
            reason => Log.Warning("Saglayici zinciri eksik kuruldu: {Sebep}", reason)),
        new QuotaPoolService(sp.GetRequiredService<StudioDbContext>()),
        // SIFRELI KIMLIK DEPOSU: `bmai credential set` ile girilen
        // anahtarlar buradan saglayicilara ulasiyor. Kopru yoktu ve
        // her anahtar ortam degiskeninden geliyordu.
        new CredentialStore(sp.GetRequiredService<StudioDbContext>(), KeyRing.Create()),
        onWarning: mesaj => Log.Information("{Mesaj}", mesaj)));

builder.Services.AddScoped<IWorkflowEngine, WorkflowEngine>();
// WORKER ROLU (P4-01): `BMAI_ROLE` -- all | render | light.
//
// Render bir makinenin butun cekirdeklerini ve gigabaytlarca
// bellegini yiyor; LLM ve varlik isleri ag bekliyor. Ikisini ayni
// surecte tutmak, ag bekleyen isleri render'in bitmesini bekleyen bir
// makineye hapsetmek demek.
//
// Ayiran sey KOD DEGIL, YAPILANDIRMA: kuyruk zaten kiralama tabanli
// (`FOR UPDATE SKIP LOCKED`), iki worker ayni veritabanina bakiyor ve
// hicbiri digerinin isini almiyor.
var role = WorkerRoles.Parse(Environment.GetEnvironmentVariable("BMAI_ROLE"));

if (role.Warning is not null)
{
    Log.Warning("{Uyari}", role.Warning);
}

Log.Information("Worker rolu: {Rol} ({Kuyruk} kuyruk)",
    role.Role, WorkerRoles.ConcurrencyFor(role.Role).Count);

builder.Services.AddSingleton(new WorkerHostOptions
{
    Concurrency = WorkerRoles.ConcurrencyFor(role.Role),
});
builder.Services.AddSingleton(TimeProvider.System);

// SAGLIK SINYALI (P4-05).
//
// `restart: unless-stopped` yalnizca COKEN kabi yeniden baslatiyor.
// Bugun yasanan ariza ise suydu: surec ayaktaydi, butun kuyruk
// donguleri her turda istisna atiyordu ve hicbir video
// uretilmiyordu. Kap saglikli gorunuyordu.
builder.Services.AddSingleton<WorkerHealth>();
builder.Services.AddHostedService<HeartbeatWriter>();

// KALICI OLARAK SAGLIKSIZ WORKER KENDINI KAPATIYOR.
//
// Docker'in `restart: unless-stopped` politikasi yalnizca CIKAN kabi
// yeniden baslatiyor; `unhealthy` isaretlenmis ama calismaya devam
// eden bir kabi Compose kendi basina yeniden baslatmiyor. Yani saglik
// kontrolu tek basina "otomatik yeniden baslatma" demek degildi.
builder.Services.AddHostedService<SelfRestartService>();

// DEPO ACILISTA HAZIRLANIYOR (P4-02): kova yoksa olusturuluyor.
// Ilk yazmaya birakmak, ayni hatayi her kanalda ayri ayri ve uretimin
// ortasinda gormek demekti.
builder.Services.AddHostedService<StorageReadyService>();

// BOLUM BAKIMI (P4-06): kapsayan bir bolum yoksa INSERT DUSUYOR.
// Varsayilan bolum bunu yakaliyor ama orada satir birikmesi
// bolumlemenin sessizce islevsizlesmesi demek.
builder.Services.AddHostedService<PartitionService>();

// ***OLCUM CEKIMI GUNDE BIR (P5-01).***
//
// `MetricsCollector` yazilmis ve YALNIZCA CLI'den cagrilabiliyordu:
// worker'in alti arka plan servisinin hicbiri olcum toplamiyordu.
// Planda P5-01'in adi "YouTube Analytics GUNLUK CEKIM" ve gunluk
// cekimin tetigi hic yoktu -- biri her gun elle komut calistirmadikca
// deney sonuclari asla gelmiyordu, yani ogrenme dongusu kapanmiyordu.
//
// ZAMANLAYICI GIBI KAPATILABILIR DEGIL ve olmasi da gerekmiyor:
// olculecek yayin yoksa hicbir API cagrisi yapilmiyor. Anahtarsiz bir
// kurulumda bu servis gunde bir kez bos donuyor ve tek satir bile log
// yazmiyor.

builder.Services.AddHostedService<MetricsService>();

builder.Services.AddHostedService<QueueWorker>();

// ---- ZAMANLAYICI (P2-01/02/12) ----
//
// VARSAYILAN KAPALI: `dotnet run` yapan biri farkinda olmadan uretim
// baslatmamali. Acmak bilincli bir hareket olmali cunku bu dongu
// gercek para harcayabiliyor.
builder.Services.AddScoped<SystemControl>();
builder.Services.AddScoped<CostLedger>();
builder.Services.AddScoped<TopicPool>();
builder.Services.AddScoped<RunPlanner>();

builder.Services.AddSingleton(new OrchestratorOptions
{
    Enabled = string.Equals(
        Environment.GetEnvironmentVariable("BMAI_ORCHESTRATOR"), "on", StringComparison.OrdinalIgnoreCase),
    Interval = TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("BMAI_ORCHESTRATOR_INTERVAL"),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? seconds
            : 60),
});

// KONU URETICISI YALNIZCA MODEL VARSA: Ollama yoksa kaydetmemek,
// "uretici kayitli degil" diye acikca loglanmasini sagliyor. Kayitli
// olup her turda baglanti hatasi vermek, ayni bilgiyi gurultuyle
// vermekti.
if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BMAI_OLLAMA")))
{
    // Ayni desen `BuildOpenRegistry` ile: yerel LLM TEK YERDE
    // kuruluyor ve ortam degiskenini okuyan tek satir bu.
    builder.Services.AddSingleton<ILlmProvider>(
        new OllamaLlmProvider(new HttpClient(), OllamaOptions.FromEnvironment()));

    builder.Services.AddScoped<TopicGenerator>();
}

builder.Services.AddHostedService<OrchestratorService>();

// DI GRAFI ACILISTA DOGRULANIYOR.
//
// Dogrulanmasaydi, cozulemeyen bir bagimlilik ancak o servis ilk kez
// istendiginde patlardi — ve zamanlayicida bu, dakikada bir "kanal
// degerlendirilirken hata" satiri demek: dongu donuyor, hicbir sey
// uretilmiyor ve sebep bir istisna yiginin icinde kaliyor.
builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions
{
    ValidateOnBuild = true,
    ValidateScopes = true,
}));

var host = builder.Build();

try
{
    host.Run();
}
finally
{
    Log.CloseAndFlush();
}
