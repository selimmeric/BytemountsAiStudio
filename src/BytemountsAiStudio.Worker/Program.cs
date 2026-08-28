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
builder.Services.AddScoped<IStorageProvider>(sp =>
    new FileSystemAssetStore(sp.GetRequiredService<StudioDbContext>(), storageRoot));

builder.Services.AddScoped(sp =>
    NodeHandlerRegistration.BuildFakeRegistry(
        sp.GetRequiredService<IStorageProvider>(), outputRoot));

builder.Services.AddScoped<IWorkflowEngine, WorkflowEngine>();
builder.Services.AddSingleton(new WorkerHostOptions());
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
