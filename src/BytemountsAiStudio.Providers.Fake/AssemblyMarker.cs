namespace BytemountsAiStudio.Providers.Fake;

/// Bu derlemenin islevi:
///   Tum provider arayuzlerinin deterministik sahte implementasyonlari. Testler ve yerel gelistirme icin.
///
/// Bagimlilik kurali:
///   Core + Contracts. Uretimde yuklenmez.
///
/// Isaretleyici tip, testlerde ve DI taramasinda derlemeye tutamak olarak kullanilir.
public static class AssemblyMarker
{
    public static readonly System.Reflection.Assembly Assembly = typeof(AssemblyMarker).Assembly;
}
