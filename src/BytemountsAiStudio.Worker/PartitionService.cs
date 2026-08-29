using BytemountsAiStudio.Persistence;
using BytemountsAiStudio.Contracts.Providers;
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

            // ***ESKİ BÖLÜMLER DÜŞÜRÜLÜYOR.***
            //
            // `DropOlderThanAsync` yazılmış, testlenmiş ve HİÇBİR
            // YERDEN ÇAĞRILMIYORDU: bölümleme yapılıyor ama eski
            // bölümler hiç düşürülmüyordu. `run_events` her koşuda
            // büyüyor ve sınırsız birikiyordu — bölümlemenin ASIL
            // faydası (eski veriyi ucuz silmek) hiç elde edilmiyordu.
            var dropped = await PartitionMaintenance
                .DropOlderThanAsync(db, time.GetUtcNow() - EventRetention(), cancellationToken)
                .ConfigureAwait(false);

            if (dropped.IsSuccess && dropped.Value > 0)
            {
                LogDropped(logger, dropped.Value);
            }

            // ***SAKLAMA SÜPÜRÜCÜSÜ (P4-02).***
            //
            // `RetentionPolicy` de yazılmış ve hiçbir yerden
            // çağrılmıyordu: hiçbir ara varlık silinmiyor, depo
            // sınırsız büyüyor ve maliyet üretimle değil GEÇMİŞLE
            // orantılı hâle geliyordu.
            //
            // BÖLÜM BAKIMIYLA AYNI DÖNGÜDE çünkü ikisi de günde bir
            // koşan, üretim yolunda olmayan bakım işleri. Ayrı bir
            // servis, ikinci bir zamanlayıcı ve ikinci bir hata
            // yolu demekti.
            var storage = scope.ServiceProvider.GetRequiredService<IStorageProvider>();

            var swept = await new RetentionSweeper(db, storage, time)
                .SweepAsync(cancellationToken)
                .ConfigureAwait(false);

            if (swept.Deleted > 0 || swept.Failed > 0)
            {
                LogSwept(logger, swept.Deleted, swept.BytesFreed / (1024 * 1024), swept.Failed);
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

    /// `run_events` saklama penceresi — `BMAI_EVENT_RETENTION_DAYS`.
    ///
    /// VARSAYILAN 90 GÜN: olay kaydı bir tanı aracı, bir arşiv değil.
    /// Üç ay, "geçen çeyrekte ne oldu" sorusunu cevaplamaya yetiyor;
    /// daha uzunu tabloyu tanı için kullanılamayacak kadar
    /// büyütüyor.
    ///
    /// SIFIR VE NEGATİF REDDEDİLİYOR: sıfır gün, bugünün bölümünü
    /// düşürmeye çalışmak demekti — yani koşan sistemin altından
    /// tabloyu çekmek.
    internal static TimeSpan EventRetention()
        => int.TryParse(Environment.GetEnvironmentVariable("BMAI_EVENT_RETENTION_DAYS"),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var days) && days > 0
                ? TimeSpan.FromDays(days)
                : TimeSpan.FromDays(90);

    [LoggerMessage(EventId = 1223, Level = LogLevel.Information,
        Message = "{Count} eski tablo bölümü düşürüldü.")]
    private static partial void LogDropped(ILogger logger, int count);

    [LoggerMessage(EventId = 1224, Level = LogLevel.Information,
        Message = "Saklama süpürücüsü: {Deleted} varlık silindi ({Megabytes} MB), "
                  + "{Failed} silinemedi.")]
    private static partial void LogSwept(ILogger logger, int deleted, long megabytes, int failed);

    [LoggerMessage(EventId = 1222, Level = LogLevel.Error,
        Message = "Bölüm bakımı başarısız; üretim devam ediyor (varsayılan bölüm karşılıyor).")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
