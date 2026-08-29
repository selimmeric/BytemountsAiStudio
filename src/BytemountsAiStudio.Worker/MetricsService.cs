using BytemountsAiStudio.Persistence;
using BytemountsAiStudio.Persistence.Providers;
using BytemountsAiStudio.Providers.Open;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BytemountsAiStudio.Worker;

/// Yayın sonrası ölçümleri günde bir çeken servis (P5-01).
///
/// ***BU SERVİS YOKTU VE YOKLUĞU ÖĞRENME DÖNGÜSÜNÜ ELLE ÇEVİRİLEN BİR
/// KOLA ÇEVİRİYORDU.***
///
/// `MetricsCollector` yazılmış, testlenmiş ve **yalnızca CLI'den**
/// çağrılabiliyordu (`bmai ogrenme cek`). Worker'ın altı arka plan
/// servisinin hiçbiri ölçüm toplamıyordu. Planda P5-01'in adı
/// "YouTube Analytics **günlük çekim**" ve günlük çekimin tetiği hiç
/// yoktu: biri her gün elle komut çalıştırmadıkça deney sonuçları
/// asla gelmiyordu — yani öğrenme döngüsü kapanmıyordu.
///
/// ***ANAHTAR YOKKEN GÜRÜLTÜ YAPMIYOR.*** Toplayıcı önce
/// "ölçülecek yayın var mı" diye veritabanına bakıyor; yoksa hiçbir
/// API çağrısı yapılmıyor. Anahtarsız bir kurulumda bu servis günde
/// bir kez boş dönüyor ve tek satır bile log yazmıyor.
///
/// ***YEDİNCİ GÜN KURALI TOPLAYICIDA, BURADA DEĞİL.*** Bu servis
/// yalnızca "günde bir kez sor" diyor; hangi yayının ölçülmeye hazır
/// olduğuna `MetricsCollector` karar veriyor. İki yerde ayrı kural
/// olsaydı, biri güncellenip diğeri unutulduğunda ölçümler ya hiç
/// gelmez ya iki kez gelirdi.
public sealed partial class MetricsService(
    IServiceScopeFactory scopeFactory,
    TimeProvider time,
    ILogger<MetricsService> logger) : BackgroundService
{
    /// GÜNDE BİR: Analytics verisi günlük tanecikli, daha sık sormak
    /// aynı sayıyı yeniden çekmek ve kota harcamak demek.
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // AÇILIŞTA HEMEN BİR KEZ: worker günde bir yeniden başlatılan
        // bir kurulumda, yalnızca zamanlayıcıya güvenmek hiç ölçüm
        // toplanmaması demekti.
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

            using var http = new HttpClient();

            var collector = new MetricsCollector(
                db,
                // KİMLİK ŞİFRELİ DEPODAN: `bmai credential set
                // youtube-analytics` ile girilen anahtar buradan
                // ulaşıyor. Verilmediğinde sağlayıcı ortam
                // değişkenine düşüyor.
                new YouTubeAnalyticsProvider(http, credentials: Credentials(db)),
                time);

            var summary = await collector.CollectAsync(cancellationToken).ConfigureAwait(false);

            if (summary.IsFailure)
            {
                LogFailed(logger, summary.Error.ToString());
                return;
            }

            // ***HİÇBİR ŞEY OLMADIYSA SESSİZ.*** Anahtarsız bir
            // kurulumda bu servis her gün "0 ölçüm" yazsaydı, gerçek
            // bir sorun çıktığında o satır görünmez olurdu.
            if (summary.Value.Collected > 0 || summary.Value.NoData > 0)
            {
                LogCollected(logger, summary.Value.Collected, summary.Value.NoData);
            }
        }
#pragma warning disable CA1031 // Ölçüm hatası worker'ı durdurmamalı.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Ölçüm toplamak ÜRETİM YOLUNDA DEĞİL: düşmesi video
            // üretimini etkilemiyor. Ama sebep yazılı olmalı, yoksa
            // deney sonuçları sessizce hiç gelmez.
            LogFailed(logger, ex.Message);
        }
    }

    private static DatabaseCredentialSource? Credentials(StudioDbContext db)
    {
        var path = PipelineSelection.CatalogPath();
        var catalog = Contracts.Providers.ProviderCatalog.Load(path);

        return catalog.IsSuccess
            ? DatabaseCredentialSource.Load(
                new CredentialStore(db, KeyRing.Create()), catalog.Value, null)
            : null;
    }

    [LoggerMessage(EventId = 1230, Level = LogLevel.Information,
        Message = "Ölçüm çekimi: {Collected} yayın ölçüldü, {NoData} yayında veri yok.")]
    private static partial void LogCollected(ILogger logger, int collected, int noData);

    [LoggerMessage(EventId = 1231, Level = LogLevel.Warning,
        Message = "Ölçüm çekimi başarısız; üretim etkilenmiyor: {Sebep}")]
    private static partial void LogFailed(ILogger logger, string sebep);
}
