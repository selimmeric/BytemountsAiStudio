using BytemountsAiStudio.Core.Execution;

namespace BytemountsAiStudio.Worker;

/// Kuyruk sınıfı başına eşzamanlılık (mimari §8.1).
///
/// Sayılar keyfi değil, kaynağın kendisinden geliyor:
///   - `Render` 1: FFmpeg zaten tüm çekirdekleri kullanıyor. İki render
///     paralel koşarsa ikisi de yavaşlar ve bellek iki katına çıkar.
///   - `Upload` 1: YouTube kotası zaten sıralı tüketiliyor; paralel yükleme
///     kotayı daha hızlı bitirmekten başka bir şey yapmaz.
///   - `Llm` 8: ağ beklemesi, CPU değil. Yüksek eşzamanlılık serbest.
///   - `Align` 2: ASR CPU/GPU yiyor, render kadar olmasa da ağır.
public sealed record WorkerHostOptions
{
    public string WorkerId { get; init; } =
        $"{Environment.MachineName}-{Environment.ProcessId}";

    public IReadOnlyDictionary<QueueClass, int> Concurrency { get; init; } =
        new Dictionary<QueueClass, int>
        {
            [QueueClass.Llm] = 8,
            [QueueClass.Search] = 4,
            [QueueClass.Asset] = 8,
            [QueueClass.ImageGeneration] = 2,
            [QueueClass.Tts] = 3,
            [QueueClass.Align] = 2,
            [QueueClass.Render] = 1,
            [QueueClass.Upload] = 1,
        };

    /// Kuyruk boşken bekleme süresi. Çok kısa olursa veritabanını gereksiz
    /// yere yorar; çok uzun olursa iş gecikmeli başlar.
    public TimeSpan IdleDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// Süresi dolmuş kiralamaların ne sıklıkla toplanacağı.
    public TimeSpan ReclaimInterval { get; init; } = TimeSpan.FromSeconds(30);
}
