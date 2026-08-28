using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Persistence.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BytemountsAiStudio.Worker;

/// Depoyu açılışta hazırlar (P4-02).
///
/// GERÇEK BİR KOŞUDA ÖĞRENİLDİ: `EnsureBucketAsync` yazılmıştı ama
/// hiçbir yerden çağrılmıyordu. Worker sorunsuz başladı, iki run
/// başlattı, ilk seslendirme dosyasını yazmaya çalıştı ve "The
/// specified bucket does not exist" ile düştü — üstelik hata geçici
/// sayıldığı için aynı iş üç kez denendi.
///
/// AÇILIŞTA HAZIRLAMAK hatayı ilk videodan ÖNCE ve tek bir yerde
/// gösteriyor.
///
/// AMA WORKER'I DURDURMUYOR. Nesne deposu geçici olarak erişilemez
/// olabilir ve o zaman doğru davranış beklemek: kuyruk zaten geçici
/// hataları yeniden deniyor ve kalp atışı sağlıksızlığı bildiriyor.
/// Açılışta çökmek, deponun bir dakikalık kesintisinde bütün
/// worker'ların ölmesi demekti.
public sealed partial class StorageReadyService(
    IServiceScopeFactory scopeFactory,
    ILogger<StorageReadyService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IStorageProvider>();

        var ready = await StorageSelection.EnsureReadyAsync(storage, cancellationToken)
            .ConfigureAwait(false);

        if (ready.IsFailure)
        {
            LogNotReady(logger, storage.Key, ready.Error.ToString());
            return;
        }

        LogReady(logger, storage.Key);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 1210, Level = LogLevel.Information,
        Message = "Varlık deposu hazır: {Store}")]
    private static partial void LogReady(ILogger logger, string store);

    [LoggerMessage(EventId = 1211, Level = LogLevel.Error,
        Message = "Varlık deposu hazırlanamadı ({Store}) — {Error}. "
                  + "Üretim denenecek ama varlık yazımı düşebilir.")]
    private static partial void LogNotReady(ILogger logger, string store, string error);
}
