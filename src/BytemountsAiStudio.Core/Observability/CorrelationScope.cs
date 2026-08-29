using System.Diagnostics;

namespace BytemountsAiStudio.Core.Observability;

/// Bir run'ın tüm adımlarını birbirine bağlayan kimlik (§2.4/22).
///
/// Otonom bir sistemde "bu video neden böyle oldu" sorusunun cevabı, o
/// videoyla ilgili tüm log satırlarını tek sorguyla toplayabilmekten geçiyor.
/// Correlation id olmadan log'lar birbirine karışmış binlerce satır olur.
///
/// `AsyncLocal` kullanılıyor: değer asenkron çağrı zinciri boyunca kendiliğinden
/// taşınıyor, her metoda parametre olarak geçirmek gerekmiyor.
public static class CorrelationScope
{
    private static readonly AsyncLocal<CorrelationState?> Current = new();

    public static string? RunId => Current.Value?.RunId;

    public static string? NodeId => Current.Value?.NodeId;

    /// Çalışan node'un kanalı.
    ///
    /// ***KANAL BURAYA SONRADAN EKLENDİ ve sebebi maliyet defteri:***
    /// bütçe kapısı "bu KANAL bugün ne kadar harcadı" sorusunu soruyor
    /// ve cevabı verebilmek için harcamanın kanalını bilmek gerekiyor.
    /// Kanalı motordan sağlayıcı çağrısına kadar elle taşımak, yolun
    /// üstündeki her imzaya bir parametre eklemek demekti — ve bu
    /// depoda opsiyonel parametrelerin unutulduğu defalarca görüldü
    /// (`uniqueness`, `channels`). Kapsam zaten node çalışmadan hemen
    /// önce açılıyor; kanalın yeri burası.
    public static Guid? ChannelId => Current.Value?.ChannelId;

    /// Koşu kimliğinin Guid hâli.
    ///
    /// Kapsam metin taşıyor çünkü loglara metin yazılıyor; deftere ise
    /// yabancı anahtar yazılıyor. Ayrı bir alan tutmak yerine geri
    /// ayrıştırılıyor: motor `ToString("N")` ile yazıyor ve bu biçim
    /// tam olarak geri dönüyor. İki alan tutmak, birinin diğerinden
    /// farklı bir koşuyu göstermesi riskini açardı.
    public static Guid? RunGuid
        => Current.Value?.RunId is { } id && Guid.TryParseExact(id, "N", out var parsed)
            ? parsed
            : null;

    /// Yeni bir kapsam açar. `using` ile kullanılır; kapsam bittiğinde
    /// önceki değer geri gelir.
    public static IDisposable Begin(string runId, string? nodeId = null, Guid? channelId = null)
    {
        var previous = Current.Value;
        Current.Value = new CorrelationState(runId, nodeId, channelId);

        // OpenTelemetry etiketleri: aynı bilgi hem log hem izleme tarafında.
        Activity.Current?.SetTag("run.id", runId);

        if (nodeId is not null)
        {
            Activity.Current?.SetTag("node.id", nodeId);
        }

        return new Scope(previous);
    }

    private sealed record CorrelationState(string RunId, string? NodeId, Guid? ChannelId);

    private sealed class Scope(CorrelationState? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Current.Value = previous;
        }
    }
}
