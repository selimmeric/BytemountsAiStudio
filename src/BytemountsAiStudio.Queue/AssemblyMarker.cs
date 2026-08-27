namespace BytemountsAiStudio.Queue;

/// Bu derlemenin islevi:
///   Is kuyrugu: lease, heartbeat, retry politikalari, DLQ, kanal adaleti.
///
/// Bagimlilik kurali:
///   Core + Persistence. Workflow'u bilmez - kuyruk genel amaclidir.
///
/// Isaretleyici tip, testlerde ve DI taramasinda derlemeye tutamak olarak kullanilir.
public static class AssemblyMarker
{
    public static readonly System.Reflection.Assembly Assembly = typeof(AssemblyMarker).Assembly;
}
