using BytemountsAiStudio.Core.Observability;
using Serilog.Core;
using Serilog.Events;

namespace BytemountsAiStudio.Worker;

/// Her log satırına run ve node kimliğini ekler.
///
/// §2.4/22: "bu video neden böyle oldu" sorusunun cevabı, o videoyla ilgili
/// tüm satırları tek sorguyla toplayabilmekten geçiyor. Her log çağrısına
/// elle parametre eklemek er geç bir yerde unutulurdu.
public sealed class CorrelationEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(propertyFactory);

        if (CorrelationScope.RunId is { } runId)
        {
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("RunId", runId));
        }

        if (CorrelationScope.NodeId is { } nodeId)
        {
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("NodeId", nodeId));
        }
    }
}
