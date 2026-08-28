using BytemountsAiStudio.Core.Execution;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BytemountsAiStudio.Worker;

/// Kalıcı olarak sağlıksız worker kendini kapatır (P4-05).
///
/// NEDEN GEREKLİ: Docker'ın `restart: unless-stopped` politikası
/// yalnızca ÇIKAN kabı yeniden başlatıyor. `unhealthy` işaretlenmiş
/// ama çalışmaya devam eden bir kabı Compose KENDİ BAŞINA yeniden
/// başlatmıyor — sağlık kontrolü yalnızca rapor veriyor.
///
/// Yani sağlık kontrolünü yazmak tek başına "otomatik yeniden
/// başlatma" demek değildi: kap sonsuza kadar kırmızı görünüp ayakta
/// kalırdı.
///
/// ALTERNATİFİ DAHA KÖTÜYDÜ. Sağlıksız kapları yeniden başlatan bir
/// yardımcı kap (autoheal deseni) Docker soketine erişmek zorunda ve
/// o soket makinede kök yetkisine denk. Üretim hattının yanında böyle
/// bir yetki taşımaktansa, sürecin KENDİSİ çıkıyor: fazladan yetki
/// yok, fazladan kap yok, ve `restart` politikası zaten çıkışı
/// karşılıyor.
///
/// EŞİK, RAPORLAMA EŞİĞİNDEN BELİRGİN ŞEKİLDE UZUN. Sıra kasıtlı:
/// önce bildir, sonra harekete geç. Bir dakikada sağlıksız
/// işaretlenen worker beş dakika boyunca hiç toparlayamazsa
/// kapanıyor. İkisi eşit olsaydı, geçici bir aksaklıkta iş yapan bir
/// süreç durup dururken öldürülürdü.
public sealed partial class SelfRestartService(
    WorkerHealth health,
    IHostApplicationLifetime lifetime,
    TimeProvider time,
    ILogger<SelfRestartService> logger) : BackgroundService
{
    /// Bu kadar süredir aralıksız düşen bir döngü varsa süreç
    /// kapanıyor.
    public static readonly TimeSpan Threshold = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval, time);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                var stuck = health.Failing().FirstOrDefault(f => f.For >= Threshold);

                if (stuck.Queue == default && stuck.For == default)
                {
                    continue;
                }

                // SEBEP ÖNCE LOGLANIYOR, SONRA ÇIKILIYOR.
                //
                // Kap yeniden başladığında bellekteki her şey gidiyor;
                // "neden yeniden başladı" sorusunun cevabı yalnızca bu
                // satırda kalıyor. Sessizce çıkan bir süreç, sonsuz
                // yeniden başlama döngüsünün sebebini de gizlerdi.
                var seconds = (int)stuck.For.TotalSeconds;

                LogRestarting(logger, stuck.Queue, seconds);

                lifetime.StopApplication();
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // Beklenen kapanış yolu.
        }
    }

    [LoggerMessage(EventId = 1201, Level = LogLevel.Critical,
        Message = "'{Queue}' döngüsü {Seconds} saniyedir aralıksız düşüyor; "
                  + "worker kapanıyor ve yeniden başlatılmayı bekliyor.")]
    private static partial void LogRestarting(ILogger logger, QueueClass queue, int seconds);
}
