using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Core.Observability;
using BytemountsAiStudio.Persistence;
using BytemountsAiStudio.Queue;
using BytemountsAiStudio.Workflow.Engine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BytemountsAiStudio.Worker;

/// Kuyruk tüketen arka plan servisi (mimari §8.5).
///
/// Kuyruk sınıfı başına ayrı bir döngü koşuyor; hepsi tek bir süreçte ama
/// birbirinden bağımsız. Render döngüsü bir video üzerinde 25 dakika
/// çalışırken LLM döngüsü kesintisiz iş almaya devam ediyor — tek bir
/// döngüde birleştirilse render tüm sistemi durdururdu.
///
/// Kapanışta çalışan işler yarıda kesilmiyor: iptal isteniyor ve tamamlanmaları
/// bekleniyor. Yarıda kesilseydi kiralama süresi dolana kadar (render için
/// 60 dakika) o işler kimseye verilmezdi.
public sealed partial class QueueWorker(
    IServiceScopeFactory scopeFactory,
    WorkerHostOptions options,
    WorkerHealth health,
    ILogger<QueueWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger, options.WorkerId, options.Concurrency.Count);

        var loops = new List<Task>();

        foreach (var (queue, concurrency) in options.Concurrency)
        {
            for (var slot = 0; slot < concurrency; slot++)
            {
                loops.Add(ConsumeAsync(queue, stoppingToken));
            }
        }

        loops.Add(ReclaimLoopAsync(stoppingToken));

        try
        {
            await Task.WhenAll(loops).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Beklenen kapanış yolu.
        }

        LogStopped(logger, options.WorkerId);
    }

    private async Task ConsumeAsync(QueueClass queue, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Her iş kendi kapsamında: DbContext paylaşılmıyor. Paylaşılsaydı
                // paralel döngüler aynı change tracker üzerinde yarışırdı.
                using var scope = scopeFactory.CreateScope();
                var engine = scope.ServiceProvider.GetRequiredService<IWorkflowEngine>();

                var before = DateTimeOffset.UtcNow;
                await engine.ExecuteNextAsync(options.WorkerId, queue, cancellationToken)
                    .ConfigureAwait(false);

                // TUR SORUNSUZ BİTTİ (P4-05). "İş buldu" demek değil:
                // boş kuyrukta da başarı, ölçülen şey döngünün
                // çalışabiliyor olması.
                health.RecordSuccess(queue);

                // İş yoksa hemen dönmüş demektir; veritabanını yormamak için bekle.
                if (DateTimeOffset.UtcNow - before < TimeSpan.FromMilliseconds(50))
                {
                    await Task.Delay(options.IdleDelay, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
#pragma warning disable CA1031 // Tek bir işin hatası döngüyü durdurmamalı.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                // Döngü ölürse o kuyruk sessizce durur ve kimse fark etmez.
                LogLoopError(logger, queue.ToString(), ex);

                // VE DIŞARIDAN GÖRÜLEBİLİR OLUYOR (P4-05).
                //
                // Hatayı yutmak doğru — tek bir işin hatası kuyruğu
                // durdurmamalı. Ama bugün olan şu oldu: bütün döngüler
                // HER turda düştü, süreç ayakta kaldı, saniyede bir
                // hata satırı bastı ve hiçbir video üretilmedi. Kap
                // sağlıklı görünüyordu. Artık ardışık hata sağlık
                // durumuna yansıyor.
                health.RecordFailure(queue);
                await Task.Delay(options.IdleDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// Süresi dolmuş kiralamaları toplayan döngü.
    ///
    /// Çöken worker'ın işleri buradan kurtarılıyor. Ayrı bir döngü olması
    /// kasıtlı: tüketici döngüler tıkanırsa bile kurtarma çalışmalı.
    private async Task ReclaimLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(options.ReclaimInterval, cancellationToken).ConfigureAwait(false);

                using var scope = scopeFactory.CreateScope();
                var queue = scope.ServiceProvider.GetRequiredService<JobQueue>();

                var reclaimed = await queue.ReclaimExpiredAsync(cancellationToken).ConfigureAwait(false);

                if (reclaimed > 0)
                {
                    LogReclaimed(logger, reclaimed);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
#pragma warning disable CA1031
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogLoopError(logger, "reclaim", ex);
            }
        }
    }

    [LoggerMessage(EventId = 1000, Level = LogLevel.Information,
        Message = "Worker {WorkerId} başladı, {QueueCount} kuyruk sınıfı dinleniyor.")]
    private static partial void LogStarted(ILogger logger, string workerId, int queueCount);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information,
        Message = "Worker {WorkerId} durdu.")]
    private static partial void LogStopped(ILogger logger, string workerId);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Error,
        Message = "'{Queue}' döngüsünde beklenmeyen hata; döngü devam ediyor.")]
    private static partial void LogLoopError(ILogger logger, string queue, Exception exception);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Warning,
        Message = "{Count} kiralaması dolmuş iş geri alındı — bir worker çökmüş olabilir.")]
    private static partial void LogReclaimed(ILogger logger, int count);
}
