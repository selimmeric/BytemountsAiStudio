namespace BytemountsAiStudio.Contracts;

/// Bu derlemenin islevi:
///   Provider arayuzleri (ILlm, ISearch, ITts, ...), node sozlesmeleri ve DTO'lar.
///
/// Bagimlilik kurali:
///   Yalnizca Core'a bagimli. Hicbir implementasyona bagimli olamaz.
///
/// Isaretleyici tip, testlerde ve DI taramasinda derlemeye tutamak olarak kullanilir.
public static class AssemblyMarker
{
    public static readonly System.Reflection.Assembly Assembly = typeof(AssemblyMarker).Assembly;
}
