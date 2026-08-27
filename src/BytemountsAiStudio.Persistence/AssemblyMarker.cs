namespace BytemountsAiStudio.Persistence;

/// Bu derlemenin islevi:
///   EF Core DbContext, konfigurasyonlar, migration'lar, repository'ler, icerik-adresli varlik deposu.
///
/// Bagimlilik kurali:
///   Core + Contracts.
///
/// Isaretleyici tip, testlerde ve DI taramasinda derlemeye tutamak olarak kullanilir.
public static class AssemblyMarker
{
    public static readonly System.Reflection.Assembly Assembly = typeof(AssemblyMarker).Assembly;
}
