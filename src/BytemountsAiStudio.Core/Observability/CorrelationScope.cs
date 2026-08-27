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

    /// Yeni bir kapsam açar. `using` ile kullanılır; kapsam bittiğinde
    /// önceki değer geri gelir.
    public static IDisposable Begin(string runId, string? nodeId = null)
    {
        var previous = Current.Value;
        Current.Value = new CorrelationState(runId, nodeId);

        // OpenTelemetry etiketleri: aynı bilgi hem log hem izleme tarafında.
        Activity.Current?.SetTag("run.id", runId);

        if (nodeId is not null)
        {
            Activity.Current?.SetTag("node.id", nodeId);
        }

        return new Scope(previous);
    }

    private sealed record CorrelationState(string RunId, string? NodeId);

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
