namespace BytemountsAiStudio.Core;

/// Bu derlemenin islevi:
///   Domain modelleri, deger tipleri, enum'lar, Result. Is mantigi burada YASAMAZ; burada yalnizca 'ne oldugu' tanimlanir.
///
/// Bagimlilik kurali:
///   Hicbir seye bagimli degil. Bu kural bozulursa katman modeli coker.
///
/// Isaretleyici tip, testlerde ve DI taramasinda derlemeye tutamak olarak kullanilir.
public static class AssemblyMarker
{
    public static readonly System.Reflection.Assembly Assembly = typeof(AssemblyMarker).Assembly;
}
