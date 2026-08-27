using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BytemountsAiStudio.Worker;

/// Kuyruk tuketen arka plan servisi.
///
/// Su an yalnizca iskelet: baslar, iptal edilene kadar bekler, temiz kapanir.
/// Gercek tuketim dongusu P0-12'de baglanacak (kuyruk sinifi basina esZamanlilik,
/// lease alma, heartbeat, graceful shutdown'da lease birakma).
public sealed partial class QueueWorker(ILogger<QueueWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Beklenen kapanis yolu; hata degil.
        }

        LogStopped(logger);
    }

    // LoggerMessage kaynak ureteci: her log cagrisinda string bicimlendirme
    // maliyeti odenmez. Ev standardi bu - dogrudan LogInformation cagrilmaz.
    [LoggerMessage(EventId = 1000, Level = LogLevel.Information,
        Message = "Worker basladi. Kuyruk tuketimi henuz bagli degil (P0-12).")]
    private static partial void LogStarted(ILogger logger);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information,
        Message = "Worker durdu.")]
    private static partial void LogStopped(ILogger logger);
}
