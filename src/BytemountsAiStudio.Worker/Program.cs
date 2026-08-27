using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Nodes;
using BytemountsAiStudio.Persistence;
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

var host = builder.Build();

try
{
    host.Run();
}
finally
{
    Log.CloseAndFlush();
}
