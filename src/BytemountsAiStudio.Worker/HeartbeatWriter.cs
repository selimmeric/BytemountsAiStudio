using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BytemountsAiStudio.Worker;

/// Sağlık durumunu bir dosyaya yazar (P4-05).
///
/// NEDEN DOSYA, HTTP DEĞİL: Worker bir `Host`, web sunucusu değil.
/// Sağlık kontrolü için ASP.NET taşımak, bir port açmak ve o portu
/// korumak gerekirdi — kabın içinden okunacak tek bir satır için.
/// Docker'ın `healthcheck` komutu zaten kabın İÇİNDE çalışıyor.
///
/// İKİ ŞEY BİRDEN YAZILIYOR ve ikisi de gerekli:
///   - `at`: yazma zamanı. Süreç dondu ya da öldüyse dosya eskir.
///   - `healthy`: döngüler koşuyor mu. Süreç ayakta ama her tur
///     düşüyorsa `false`.
///
/// Yalnızca zaman damgası yazsaydık bugünkü arızayı kaçırırdık: süreç
/// canlıydı, dosya tazeydi, hiçbir video üretilmiyordu. Yalnızca
/// `healthy` yazsaydık donmuş bir süreç sonsuza kadar "sağlıklı"
/// kalırdı.
public sealed partial class HeartbeatWriter(
    WorkerHealth health,
    WorkerHostOptions options,
    TimeProvider time,
    ILogger<HeartbeatWriter> logger) : BackgroundService
{
    /// Dosyanın yazılma sıklığı.
    ///
    /// Sağlık kontrolü aralığından belirgin şekilde kısa olmalı: eşit
    /// olsaydı normal zamanlama sapması bile dosyayı "eski"
    /// gösterirdi ve sağlıklı kaplar durup dururken yeniden başlardı.
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    public static string PathFor(WorkerHostOptions options)
        => Environment.GetEnvironmentVariable("BMAI_HEARTBEAT")
           ?? Path.Combine(Path.GetTempPath(), $"bmai-worker-{options.WorkerId}.json");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var path = PathFor(options);

        // İLK YAZMA BEKLEMEDEN: kap ayağa kalkar kalkmaz dosya
        // olmalı, yoksa ilk sağlık kontrolü "dosya yok" deyip kabı
        // öldürürdü.
        Write(path);

        using var timer = new PeriodicTimer(Interval, time);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                Write(path);
            }
        }
        catch (OperationCanceledException)
        {
            // Beklenen kapanış yolu.
        }
    }

    private void Write(string path)
    {
        var failing = health.Failing();

        var payload = JsonSerializer.Serialize(new
        {
            at = time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture),
            worker = options.WorkerId,
            healthy = failing.Count == 0,

            // HANGİ KUYRUK VE NE KADAR SÜREDİR: "sağlıksız" tek başına
            // nereye bakılacağını söylemiyor. Kap yeniden başladıktan
            // sonra geriye kalan tek ipucu bu satır olabilir.
            failing = failing.Select(f => new
            {
                queue = f.Queue.ToString(),
                seconds = (int)f.For.TotalSeconds,
            }),
        });

        try
        {
            // ÖNCE GEÇİCİ DOSYA, SONRA TAŞIMA: sağlık kontrolü tam
            // yazma anında okursa yarım bir JSON görürdü ve sağlıklı
            // bir worker'ı hasta sanardı.
            var temporary = path + ".tmp";

            File.WriteAllText(temporary, payload);
            File.Move(temporary, path, overwrite: true);
        }
#pragma warning disable CA1031 // Kalp atışı yazılamıyorsa worker durmamalı.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Yazamamak worker'ı durdurmuyor ama SESSİZ de kalmıyor:
            // dosya eskir, sağlık kontrolü düşer ve kap yeniden
            // başlar. O yeniden başlamanın sebebi burada yazılı olmalı.
            LogWriteFailed(logger, path, ex);
        }
    }

    [LoggerMessage(EventId = 1200, Level = LogLevel.Error,
        Message = "Kalp atışı yazılamadı: {Path}")]
    private static partial void LogWriteFailed(ILogger logger, string path, Exception exception);
}
