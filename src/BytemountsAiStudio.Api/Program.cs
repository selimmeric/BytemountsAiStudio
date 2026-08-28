using System.Reflection;
using BytemountsAiStudio.Api;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Nodes;
using BytemountsAiStudio.Persistence;
using BytemountsAiStudio.Persistence.Providers;
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
builder.Services.AddScoped<DeadLetterTriage>();
builder.Services.AddScoped<SystemControl>();
builder.Services.AddSingleton(TimeProvider.System);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Panel API ile AYNI sunucudan geliyor (P1-29).
//
// Ayri bir sunucu, CORS ayari ve ikinci bir dagitim adimi getirirdi;
// panelin tek isi bu API'yi gostermek ve baska bir yerde durmasinin
// hicbir faydasi yok.
app.UseDefaultFiles();
app.UseStaticFiles();

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

// ---- Olu mektup kuyrugu ----

app.MapGet("/dlq", async (StudioDbContext db, CancellationToken cancellationToken, int limit = 50) =>
    Results.Ok(await RunQueries.DeadLettersAsync(db, limit, cancellationToken)));

// DLQ triyaji (P2-10): uc eylem, uc farkli soruya cevap.
//
//   retry  : "gecici bir arizaydi, artik duzeldi"
//   skip   : "bu adim bu kosuda calismayacak ama video kurtarilabilir"
//   cancel : "bu video kurtarilamaz"
app.MapPost("/dlq/{id:guid}/retry", async (
    Guid id, ApprovalDecisionRequest request, DeadLetterTriage triage, CancellationToken cancellationToken) =>
    Triage(await triage.RetryAsync(id, request.DecidedBy, cancellationToken)));

app.MapPost("/dlq/{id:guid}/skip", async (
    Guid id, ApprovalDecisionRequest request, DeadLetterTriage triage, CancellationToken cancellationToken) =>
    Triage(await triage.SkipNodeAsync(id, request.DecidedBy, cancellationToken)));

app.MapPost("/dlq/{id:guid}/cancel", async (
    Guid id, ApprovalDecisionRequest request, DeadLetterTriage triage, CancellationToken cancellationToken) =>
    Triage(await triage.CancelRunAsync(id, request.DecidedBy, cancellationToken)));

// ---- Kontroller (P2-04) ----
//
// ACIL DURDURMA ile KANAL DURAKLATMA ayri kavramlar: biri her seyi,
// digeri yalnizca o kanalin yeni islerini durduruyor. Tek dugmeye
// indirmek, bir kanali susturmak icin butun sistemi durdurmak
// demekti.
app.MapGet("/control", async (SystemControl control, StudioDbContext db, CancellationToken cancellationToken) =>
{
    var kill = await control.KillSwitchAsync(cancellationToken);

    var channels = await db.Channels.AsNoTracking()
        .OrderBy(c => c.Name)
        .Select(c => new ChannelControl(c.Id, c.Name, c.Language, c.IsPaused, c.Mode.ToString()))
        .ToListAsync(cancellationToken);

    return Results.Ok(new ControlState(kill.Engaged, kill.By, kill.Reason, kill.Since, channels));
});

app.MapPost("/control/kill-switch", async (
    KillSwitchRequest request, SystemControl control, CancellationToken cancellationToken) =>
{
    await control.SetKillSwitchAsync(request.Engaged, request.DecidedBy, request.Reason, cancellationToken);

    return Results.NoContent();
});

app.MapPost("/control/channels/{id:guid}/pause", async (
    Guid id, ChannelPauseRequest request, SystemControl control,
    StudioDbContext db, CancellationToken cancellationToken) =>
{
    if (!await db.Channels.AsNoTracking().AnyAsync(c => c.Id == id, cancellationToken))
    {
        return Results.NotFound(new { error = $"Kanal bulunamadı: {id}" });
    }

    await control.SetChannelPausedAsync(id, request.Paused, cancellationToken);

    return Results.NoContent();
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

// Kaç art arda hata "sağlıksız" sayılıyor.
//
// Devre kesicinin eşiğiyle AYNI (5): panelde kırmızı görünen satır
// ile devrenin gerçekten açılacağı an aynı olmalı, yoksa panel
// "sağlıklı" derken çağrılar reddedilir ve kimse sebebini anlamaz.
const int ProviderFailureThreshold = 5;

// ---- Sağlayıcı sağlığı (P2-04) ----
//
// Devre kesicinin süreç içi durumu DEĞİL, filonun gözlemi. Bayrağı
// her çağrıda veritabanına yazmak, para harcamayan bir kontrolü
// hattın en sık sorgusuna çevirirdi; oysa `provider_calls` zaten
// yazılıyor ve "bu sağlayıcı şu an sağlıklı mı" sorusuna asıl cevap
// veren de o.
app.MapGet("/providers", async (
    StudioDbContext db, CancellationToken cancellationToken, int windowMinutes = 30) =>
{
    // Pencere makul sınırlar içinde: sıfır ya da negatif bir değer
    // hiçbir çağrıyı kapsamaz ve panel boş görünürdü.
    var minutes = Math.Clamp(windowMinutes, 1, 24 * 60);

    var providers = await RunQueries.ProviderHealthAsync(
        db, TimeSpan.FromMinutes(minutes), ProviderFailureThreshold, cancellationToken);

    return Results.Ok(new ProviderHealthSummary(minutes, ProviderFailureThreshold, providers));
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

/// DLQ triyaj sonucunu HTTP'ye çevirir.
///
/// "Bu iş ölü mektup kuyruğunda değil" 409: istek geçerli ama
/// durum uygun değil. 400 yapmak, istemcinin isteği düzeltmesi
/// gerektiğini ima ederdi — oysa düzeltilecek bir şey yok, iş başka
/// birinin eylemiyle zaten çözülmüş olabilir.
static IResult Triage(BytemountsAiStudio.Core.Result result)
{
    if (result.IsSuccess)
    {
        return Results.NoContent();
    }

    return result.Error.Code switch
    {
        "dlq.not_found" or "dlq.no_run" or "dlq.no_graph" => Results.NotFound(new { error = result.Error.Message }),
        "dlq.not_dead_lettered" => Results.Conflict(new { error = result.Error.Message }),
        _ => Results.BadRequest(new { error = result.Error.Message }),
    };
}

internal sealed record HealthResponse(string Status, string Version, string Environment);
