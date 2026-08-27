using System.Reflection;
using BytemountsAiStudio.Api;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Nodes;
using BytemountsAiStudio.Persistence;
using BytemountsAiStudio.Persistence.Storage;
using BytemountsAiStudio.Queue;
using BytemountsAiStudio.Workflow.Engine;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

// Bağlantı dizesi ortamdan; CLI ile AYNI değişken. İki ayrı isim
// olsaydı biri ayarlanır diğeri unutulur ve API sessizce başka bir
// veritabanına bakardı.
var connectionString = Environment.GetEnvironmentVariable("BMAI_CONNECTION")
    ?? builder.Configuration.GetConnectionString("Studio")
    ?? "Host=localhost;Port=5432;Database=bmai;Username=bmai;Password=bmai_dev";

builder.Services.AddDbContext<StudioDbContext>(options =>
    options.UseNpgsql(connectionString, npgsql => npgsql.UseVector()).UseSnakeCaseNamingConvention());

builder.Services.AddScoped<JobQueue>();
builder.Services.AddHttpClient();

// KAYIT GERÇEK OLMAK ZORUNDA.
//
// Boş bir `NodeRegistry` de derleniyor ve API de ayağa kalkıyor — ama
// onay verildiğinde motor sonraki node'un tipini tanımıyor ve
// SESSİZCE hiçbir şey kuyruğa atmıyor. Run "Running" görünür,
// kuyrukta iş yoktur, ve kimse bir şeyin durduğunu fark etmez.
builder.Services.AddScoped<NodeRegistry>(services =>
{
    var db = services.GetRequiredService<StudioDbContext>();
    var http = services.GetRequiredService<IHttpClientFactory>().CreateClient("nodes");

    var storageRoot = Environment.GetEnvironmentVariable("BMAI_STORAGE")
        ?? Path.Combine(Directory.GetCurrentDirectory(), "storage");

    var outputDirectory = Environment.GetEnvironmentVariable("BMAI_OUTPUT")
        ?? Path.Combine(Directory.GetCurrentDirectory(), "output");

    return NodeHandlerRegistration.BuildOpenRegistry(
        new FileSystemAssetStore(db, storageRoot), http, outputDirectory);
});

builder.Services.AddScoped<WorkflowEngine>();
builder.Services.AddScoped<ApprovalService>();
builder.Services.AddSingleton(TimeProvider.System);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Saglik ucu: hem insan hem izleme sistemi icin ilk temas noktasi.
app.MapGet("/health", () => Results.Ok(new HealthResponse(
    Status: "ok",
    Version: Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0",
    Environment: app.Environment.EnvironmentName)));

// ---- Run'lar ----

app.MapGet("/runs", async (
    StudioDbContext db, CancellationToken cancellationToken,
    string? state = null, Guid? channelId = null, int limit = 50) =>
{
    // Tanınmayan bir durum adı SESSİZCE yok sayılmıyor: filtre
    // çalışmadığında panelde "hepsi" görünür ve kullanıcı yanlış
    // sonuca bakar.
    if (state is not null && !Enum.TryParse<RunState>(state, ignoreCase: true, out _))
    {
        return Results.BadRequest(new { error = $"Bilinmeyen durum: {state}" });
    }

    var parsed = state is null ? (RunState?)null : Enum.Parse<RunState>(state, ignoreCase: true);

    return Results.Ok(await RunQueries.ListAsync(db, parsed, channelId, limit, cancellationToken));
});

app.MapGet("/runs/{id:guid}", async (Guid id, StudioDbContext db, CancellationToken cancellationToken) =>
{
    var detail = await RunQueries.DetailAsync(db, id, cancellationToken);

    return detail is null ? Results.NotFound() : Results.Ok(detail);
});

/// Canlı ilerleme. Pano yenilemeden bunu dinliyor.
app.MapGet("/runs/{id:guid}/stream", async (
    Guid id, HttpContext http, StudioDbContext db, TimeProvider time, CancellationToken cancellationToken) =>
{
    await ProgressStream.WriteAsync(http, db, id, time, cancellationToken);
});

// ---- Konu havuzu ----

app.MapGet("/topics", async (
    StudioDbContext db, CancellationToken cancellationToken,
    Guid? channelId = null, string? language = null, int limit = 50) =>
{
    var query = db.Topics.AsNoTracking();

    if (channelId is { } channel)
    {
        query = query.Where(t => t.ChannelId == channel);
    }

    if (!string.IsNullOrWhiteSpace(language))
    {
        query = query.Where(t => t.Language == language);
    }

    var topics = await query
        // EN YÜKSEK SKOR ÖNCE: konu havuzunun tek işi "sırada ne var"
        // sorusuna cevap vermek ve o cevap skora göre.
        .OrderByDescending(t => t.OverallScore)
        .Take(Math.Clamp(limit, 1, 200))
        .Select(t => new TopicSummary(
            t.Id, t.Title, t.Language, t.OverallScore, t.State.ToString(), t.ChannelId, t.CreatedAt))
        .ToListAsync(cancellationToken);

    return Results.Ok(topics);
});

// ---- Onay kuyruğu ----

app.MapGet("/approvals", async (
    ApprovalService approvals, CancellationToken cancellationToken,
    Guid? channelId = null, int limit = 50) =>
{
    var pending = await approvals.PendingAsync(channelId, limit, cancellationToken);

    return Results.Ok(pending.Select(p => new ApprovalSummary(
        p.Id, p.RunId, p.NodeId, p.Reason, p.RequestedAt, p.ChannelId)));
});

app.MapPost("/approvals/{id:guid}/approve", async (
    Guid id, ApprovalDecisionRequest request, ApprovalService approvals, CancellationToken cancellationToken) =>
{
    var result = await approvals.ApproveAsync(id, request.DecidedBy, request.Note, cancellationToken);

    return Decision(result);
});

app.MapPost("/approvals/{id:guid}/reject", async (
    Guid id, ApprovalDecisionRequest request, ApprovalService approvals, CancellationToken cancellationToken) =>
{
    var result = await approvals.RejectAsync(id, request.DecidedBy, request.Note, cancellationToken);

    return Decision(result);
});

// ---- Maliyet ----

app.MapGet("/cost", async (StudioDbContext db, CancellationToken cancellationToken, Guid? runId = null) =>
{
    var byProvider = await RunQueries.CostsAsync(db, runId, cancellationToken);
    var total = byProvider.Sum(c => c.Cost);

    var runCount = runId is null
        ? await db.Runs.AsNoTracking().CountAsync(cancellationToken)
        : 1;

    return Results.Ok(new CostSummary(
        total,
        runCount,
        // SIFIRA BÖLME YOK: hiç run yokken ortalama da yok.
        runCount == 0 ? 0 : total / runCount,
        byProvider));
});

app.Run();

/// Karar sonucunu HTTP'ye çevirir.
///
/// Hata SINIFI korunuyor: "zaten karara bağlanmış" bir istek 409,
/// "kayıt yok" 404. Hepsini 400 yapmak, istemcinin yeniden deneyip
/// denememesi gerektiğini bilememesi demekti.
static IResult Decision(BytemountsAiStudio.Core.Result result)
{
    if (result.IsSuccess)
    {
        return Results.NoContent();
    }

    return result.Error.Code switch
    {
        "approval.not_found" or "approval.no_run" => Results.NotFound(new { error = result.Error.Message }),
        "approval.already_decided" => Results.Conflict(new { error = result.Error.Message }),
        _ => Results.BadRequest(new { error = result.Error.Message }),
    };
}

internal sealed record HealthResponse(string Status, string Version, string Environment);
