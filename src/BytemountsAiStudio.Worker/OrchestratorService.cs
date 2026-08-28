using System.Text.Json;
using BytemountsAiStudio.Persistence;
using BytemountsAiStudio.Persistence.Entities;
using BytemountsAiStudio.Persistence.Providers;
using BytemountsAiStudio.Workflow.Engine;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BytemountsAiStudio.Worker;

/// Kanalları tarayıp yeni run başlatan döngü (P2-01/02/12, §15).
///
/// KUYRUK TÜKETİCİSİNDEN AYRI BİR DÖNGÜ ve bu ayrım kasıtlı:
/// `QueueWorker` var olan işleri yürütüyor, bu servis YENİ İŞ
/// DOĞURUYOR. İkisi tek döngüde olsaydı, render'la meşgul bir sistem
/// yeni video başlatmayı da durdururdu — oysa üretim hattının başı ve
/// sonu farklı hızlarda çalışmalı.
///
/// KARAR BURADA DEĞİL (`RunPlanner`), ÜRETİM DE DEĞİL
/// (`TopicGenerator`). Bu servis yalnızca zamanı yönetiyor: ne zaman
/// sorulacak, cevap ne olursa ne yapılacak. Karar mantığını buraya
/// koymak, onu veritabanı ve zamanlayıcı olmadan sınanamaz hâle
/// getirirdi.
public sealed partial class OrchestratorService(
    IServiceScopeFactory scopeFactory,
    OrchestratorOptions options,
    ILogger<OrchestratorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            LogDisabled(logger);
            return;
        }

        LogStarted(logger, options.Interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
#pragma warning disable CA1031 // Tek bir turun hatası döngüyü öldürmemeli.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                // DÖNGÜ ÖLÜRSE SİSTEM SESSİZCE DURUR: hiçbir şey
                // kırılmaz, yalnızca yeni video başlamaz ve bu sabaha
                // kadar fark edilmez.
                LogTickError(logger, ex);
            }

            try
            {
                await Task.Delay(options.Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// Bir tur: bütün kanalları sırayla değerlendir.
    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();

        var channels = await db.Channels.AsNoTracking()
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var channel in channels)
        {
            try
            {
                await EvaluateAsync(scope.ServiceProvider, channel, cancellationToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Bir kanalın hatası diğerlerini durdurmamalı.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                // BİR KANAL ÇÖKERSE DİĞERLERİ ÇALIŞMAYA DEVAM ETMELİ.
                // Aksi hâlde tek bir bozuk ayar belgesi bütün filoyu
                // durdururdu.
                LogChannelError(logger, channel.Name, ex);
            }
        }
    }

    private async Task EvaluateAsync(
        IServiceProvider services, Channel channel, CancellationToken cancellationToken)
    {
        var planner = services.GetRequiredService<RunPlanner>();
        var verdict = await planner.DecideAsync(channel, cancellationToken).ConfigureAwait(false);

        foreach (var warning in verdict.Warnings ?? [])
        {
            // AYAR UYARILARI HER TURDA DEĞİL, GÖRÜLDÜĞÜ GİBİ:
            // sessizce varsayılana düşen bir ayar, aylarca yanlış
            // tempoda çalışan bir kanal demek.
            LogSettingsWarning(logger, channel.Name, warning);
        }

        // DOLDURMA BAŞLATMADAN BAĞIMSIZ ÇALIŞIYOR.
        //
        // "Şimdi başlatamıyorum" ile "havuzu doldurmayalım" aynı şey
        // değil: bütçe ya da tempo yüzünden beklerken havuzu
        // doldurmak, beklemenin bittiği anda hazır olmak demek.
        if (verdict.Refill is { ShouldRefill: true } refill)
        {
            await RefillAsync(services, channel, refill.Count, refill.Reason, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!verdict.ShouldStart)
        {
            LogSkipped(logger, channel.Name, verdict.Reason);
            return;
        }

        await StartRunAsync(services, channel, verdict, cancellationToken).ConfigureAwait(false);
    }

    private async Task RefillAsync(
        IServiceProvider services, Channel channel, int count, string reason,
        CancellationToken cancellationToken)
    {
        var generator = services.GetService<TopicGenerator>();

        if (generator is null)
        {
            // Üretici kayıtlı değilse (model yok) bunu SÖYLEMEK
            // gerekiyor: havuz boş kalacak ve sebebi bilinmeli.
            LogNoGenerator(logger, channel.Name);
            return;
        }

        var result = await generator.RefillAsync(channel, count, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            LogRefillFailed(logger, channel.Name, result.Error.ToString());
            return;
        }

        LogRefilled(logger, channel.Name, result.Value.Accepted, result.Value.Total, reason);

        foreach (var note in result.Value.Notes)
        {
            LogRefillNote(logger, channel.Name, note);
        }
    }

    private async Task StartRunAsync(
        IServiceProvider services, Channel channel, StartVerdict verdict,
        CancellationToken cancellationToken)
    {
        var pool = services.GetRequiredService<TopicPool>();

        var topic = await pool.TakeNextAsync(channel.Id, channel.Language, cancellationToken)
            .ConfigureAwait(false);

        if (topic.IsFailure)
        {
            // Havuz karar ile alma arasında boşalabilir: başka bir
            // worker aynı konuyu almış olabilir. Hata değil, bir
            // sonraki turda tekrar denenecek.
            LogSkipped(logger, channel.Name, topic.Error.Message);
            return;
        }

        var version = await ResolveWorkflowAsync(services, channel, cancellationToken).ConfigureAwait(false);

        if (version is null)
        {
            LogNoWorkflow(logger, channel.Name);
            return;
        }

        var engine = services.GetRequiredService<IWorkflowEngine>();

        var context = JsonSerializer.Serialize(new
        {
            topic = new
            {
                topic = topic.Value.Title,
                language = topic.Value.Language,
                angle = topic.Value.Angle,
            },
            genre = verdict.Genre,
            // Yayın saati ÜRETİM BAŞLARKEN belirleniyor, bitince
            // değil: video gizli yüklenip bu saatte açılacak ve
            // üretim ne kadar sürerse sürsün hedef saat kaymamalı.
            publish_at = verdict.PublishAt,
        });

        var run = await engine.StartRunAsync(
            version.Value, channel.Id, topic.Value.Id, cancellationToken, context).ConfigureAwait(false);

        if (run.IsFailure)
        {
            LogStartFailed(logger, channel.Name, run.Error.ToString());
            return;
        }

        LogStarted(logger, channel.Name, topic.Value.Title, run.Value);
    }

    private static async Task<Guid?> ResolveWorkflowAsync(
        IServiceProvider services, Channel channel, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<StudioDbContext>();
        var key = Core.Execution.ChannelSettings.Parse(channel.SettingsJson).WorkflowKey;

        // KANALA ÖZEL İŞ AKIŞI ÖNCE, sonra kanalın kendi tanımladığı,
        // sonra genel varsayılan. Sıra önemli: aynı anahtarla hem
        // kanala özel hem genel bir kayıt varsa kastedilen özel olan.
        var query = db.Workflows.AsNoTracking()
            .Where(w => key == null || w.Key == key)
            .OrderByDescending(w => w.ChannelId == channel.Id)
            .ThenByDescending(w => w.ChannelId == null);

        var workflow = await query.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (workflow is null)
        {
            return null;
        }

        var version = await db.WorkflowVersions.AsNoTracking()
            .Where(v => v.WorkflowId == workflow.Id && v.Version == workflow.CurrentVersion)
            .Select(v => (Guid?)v.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return version;
    }

    [LoggerMessage(EventId = 1100, Level = LogLevel.Information,
        Message = "Zamanlayıcı başladı; her {Seconds} saniyede bir kanallar taranıyor.")]
    private static partial void LogStarted(ILogger logger, double seconds);

    [LoggerMessage(EventId = 1101, Level = LogLevel.Information,
        Message = "Zamanlayıcı kapalı; yeni run başlatılmayacak.")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(EventId = 1102, Level = LogLevel.Information,
        Message = "{Channel}: '{Topic}' için run başlatıldı ({RunId}).")]
    private static partial void LogStarted(ILogger logger, string channel, string topic, Guid runId);

    [LoggerMessage(EventId = 1103, Level = LogLevel.Debug,
        Message = "{Channel}: başlatılmadı — {Reason}")]
    private static partial void LogSkipped(ILogger logger, string channel, string reason);

    [LoggerMessage(EventId = 1104, Level = LogLevel.Information,
        Message = "{Channel}: havuz dolduruldu, {Accepted}/{Total} konu kabul edildi ({Reason}).")]
    private static partial void LogRefilled(
        ILogger logger, string channel, int accepted, int total, string reason);

    [LoggerMessage(EventId = 1105, Level = LogLevel.Warning,
        Message = "{Channel}: havuz doldurulamadı — {Error}")]
    private static partial void LogRefillFailed(ILogger logger, string channel, string error);

    [LoggerMessage(EventId = 1106, Level = LogLevel.Warning,
        Message = "{Channel}: konu üreticisi kayıtlı değil; havuz kendiliğinden dolmayacak.")]
    private static partial void LogNoGenerator(ILogger logger, string channel);

    [LoggerMessage(EventId = 1107, Level = LogLevel.Error,
        Message = "{Channel}: iş akışı bulunamadı; run başlatılamıyor.")]
    private static partial void LogNoWorkflow(ILogger logger, string channel);

    [LoggerMessage(EventId = 1108, Level = LogLevel.Error,
        Message = "{Channel}: run başlatılamadı — {Error}")]
    private static partial void LogStartFailed(ILogger logger, string channel, string error);

    [LoggerMessage(EventId = 1109, Level = LogLevel.Warning,
        Message = "{Channel}: ayar uyarısı — {Warning}")]
    private static partial void LogSettingsWarning(ILogger logger, string channel, string warning);

    [LoggerMessage(EventId = 1110, Level = LogLevel.Information,
        Message = "{Channel}: doldurma notu — {Note}")]
    private static partial void LogRefillNote(ILogger logger, string channel, string note);

    [LoggerMessage(EventId = 1111, Level = LogLevel.Error,
        Message = "Zamanlayıcı turunda beklenmeyen hata; döngü devam ediyor.")]
    private static partial void LogTickError(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1112, Level = LogLevel.Error,
        Message = "{Channel} değerlendirilirken hata; diğer kanallar devam ediyor.")]
    private static partial void LogChannelError(ILogger logger, string channel, Exception exception);
}

/// Zamanlayıcı ayarları.
public sealed record OrchestratorOptions
{
    /// VARSAYILAN KAPALI.
    ///
    /// Açık olsaydı, `dotnet run` yapan biri farkında olmadan üretim
    /// başlatırdı — ve bu üretim gerçek para harcayabilir. Otonom bir
    /// sistemin açılması bilinçli bir hareket olmalı.
    public bool Enabled { get; init; }

    /// İki tur arası.
    ///
    /// BİR DAKİKA: kararların çoğu "henüz değil" ve her tur birkaç
    /// sorgu demek. Daha sık taramak veritabanını meşgul eder, daha
    /// seyrek taramak yayın penceresini kaçırır.
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(1);
}
