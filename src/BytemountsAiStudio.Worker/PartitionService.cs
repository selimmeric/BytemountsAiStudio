using BytemountsAiStudio.Persistence;
using BytemountsAiStudio.Persistence.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BytemountsAiStudio.Worker;

/// Bölüm bakımı (P4-06).
///
/// EN TEHLİKELİ TUZAK: PostgreSQL'de kapsayan bir bölüm yoksa INSERT
/// DÜŞÜYOR. Varsayılan bölüm bunu yakalıyor — sistem durmuyor — ama
/// orada satır birikmesi bölümlemenin sessizce işlevsizleşmesi demek:
/// bütün veri tek bir bölüme yığılıyor, bölüm budama çalışmıyor ve
/// eski veriyi ucuza silmek imkânsız hale geliyor.
///
/// AÇILIŞTA VE GÜNDE BİR: açılışta, çünkü aylarca kapalı kalmış bir
/// kurulum açıldığında bölümleri eksik olur. Günde bir, çünkü ay
/// dönümü kimsenin ayakta olmadığı bir saatte geliyor.
public sealed partial class PartitionService(
    IServiceScopeFactory scopeFactory,
    TimeProvider time,
    ILogger<PartitionService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunOnceAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(Interval, time);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Beklenen kapanış yolu.
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();

            var created = await PartitionMaintenance
                .EnsureAsync(db, time.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false);

            if (created.IsSuccess && created.Value > 0)
            {
                LogCreated(logger, created.Value);
            }

            // VARSAYILAN BÖLÜMDE SATIR VARSA BU BİR ARIZA.
            //
            // Sistem çalışıyor ama bölümleme işini yapmıyor. Sessiz
            // kalsaydı, aylar sonra "neden eski veriyi silemiyoruz"
            // diye sorulduğunda cevap aranırdı.
            var stray = await PartitionMaintenance
                .DefaultRowsAsync(db, cancellationToken)
                .ConfigureAwait(false);

            if (stray > 0)
            {
                LogStrayRows(logger, stray);
            }
        }
#pragma warning disable CA1031 // Bakım hatası worker'ı durdurmamalı.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Bakım düşerse üretim devam ediyor: varsayılan bölüm
            // INSERT'leri karşılıyor. Ama sebep yazılı olmalı, yoksa
            // bölümler sessizce eskimeye başlar.
            LogFailed(logger, ex);
        }
    }

    [LoggerMessage(EventId = 1220, Level = LogLevel.Information,
        Message = "{Count} yeni tablo bölümü açıldı.")]
    private static partial void LogCreated(ILogger logger, int count);

    [LoggerMessage(EventId = 1221, Level = LogLevel.Warning,
        Message = "Varsayılan bölümde {Rows} satır var: bölüm bakımı geri kalmış. "
                  + "Veri tek bölümde birikiyor, bölüm budama ve ucuz silme çalışmıyor.")]
    private static partial void LogStrayRows(ILogger logger, int rows);

    [LoggerMessage(EventId = 1222, Level = LogLevel.Error,
        Message = "Bölüm bakımı başarısız; üretim devam ediyor (varsayılan bölüm karşılıyor).")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
