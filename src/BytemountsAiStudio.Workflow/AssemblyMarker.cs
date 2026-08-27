namespace BytemountsAiStudio.Workflow;

/// Bu derlemenin islevi:
///   DAG modeli, run durum makinesi, node kaydi, kisitli ifade degerlendirici.
///
/// Bagimlilik kurali:
///   Core + Contracts + Queue + Persistence. Node handler'lari domain servislerini cagirir, is mantigini kendi tasimaz.
///
/// Isaretleyici tip, testlerde ve DI taramasinda derlemeye tutamak olarak kullanilir.
public static class AssemblyMarker
{
    public static readonly System.Reflection.Assembly Assembly = typeof(AssemblyMarker).Assembly;
}
