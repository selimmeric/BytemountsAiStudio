using System.Collections.Concurrent;
using BytemountsAiStudio.Core.Execution;

namespace BytemountsAiStudio.Worker;

/// Worker'ın sağlık durumu (P4-05).
///
/// NEDEN SÜREÇ CANLILIĞI YETMİYOR — bugün yaşandı. `restart:
/// unless-stopped` yalnızca ÇÖKEN kabı yeniden başlatıyor. Bu depoda
/// gerçekleşen arıza ise şuydu: süreç ayaktaydı, bütün kuyruk
/// döngüleri her turda istisna atıyordu (EF yürütme stratejisi ile
/// açık transaction çakışması), saniyede bir hata satırı basılıyordu
/// ve HİÇBİR VİDEO ÜRETİLMİYORDU. Kap sağlıklı görünüyordu.
///
/// `QueueWorker.ConsumeAsync` hatayı bilerek yutuyor — tek bir işin
/// hatası o kuyruğu durdurmamalı. Doğru karar, ama bedeli şu: dışarıdan
/// bakan hiçbir şey "bu döngü hiç iş bitiremiyor" diyemiyordu.
///
/// ÖLÇÜLEN ŞEY "İŞ YAPILDI MI" DEĞİL, "SÜREKLİ DÜŞÜYOR MU". Kuyruğu boş
/// bir worker hiç iş yapmıyor ve tamamen sağlıklı; ayıran şey ARDIŞIK
/// HATA. Bir turda düşüp sonrakinde toparlayan bir döngü de sağlıklı:
/// geçici hata beklenen şey.
public sealed class WorkerHealth(TimeProvider time)
{
    /// Bir döngü bu kadar süredir ARALIKSIZ düşüyorsa worker hasta.
    ///
    /// Tek bir hata yetmiyor: geçici veritabanı hatası, kilit
    /// çakışması ve ağ kesintisi normal. Bir dakika boyunca hiç
    /// toparlayamamak normal değil — ve bugünkü arıza saniyeler
    /// içinde bu eşiği geçerdi.
    public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<QueueClass, DateTimeOffset> _failingSince = new();

    /// Bir döngü turu SORUNSUZ bitti.
    ///
    /// "İş buldu" demek değil — boş kuyrukta da başarı. Ölçülen şey
    /// döngünün çalışabiliyor olması.
    public void RecordSuccess(QueueClass queue) => _failingSince.TryRemove(queue, out _);

    /// Bir döngü turu istisnayla bitti.
    ///
    /// İlk hatanın zamanı saklanıyor, sayısı değil: hızlı dönen bir
    /// döngü dakikada yüzlerce hata üretir, yavaş dönen biri üç tane.
    /// Sayıya bakan bir eşik, döngü hızına göre farklı davranırdı.
    public void RecordFailure(QueueClass queue)
        => _failingSince.TryAdd(queue, time.GetUtcNow());

    /// Aralıksız düşen döngüler ve ne zamandır düştükleri.
    public IReadOnlyList<(QueueClass Queue, TimeSpan For)> Failing()
    {
        var now = time.GetUtcNow();

        return
        [
            .. _failingSince
                .Select(entry => (Queue: entry.Key, For: now - entry.Value))
                .Where(entry => entry.For >= FailureWindow)
                .OrderByDescending(entry => entry.For)
        ];
    }

    public bool IsHealthy => Failing().Count == 0;
}
