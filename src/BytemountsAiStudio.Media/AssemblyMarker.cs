namespace BytemountsAiStudio.Media;

/// Bu derlemenin islevi:
///   Render motorunun SAF katmani: Timeline semasi, Planner, Filter Graph IR, Validator, Emitter.
///
/// Bagimlilik kurali:
///   Yalnizca Core. Bu projede dosya sistemi, surec baslatma veya ag erisimi BULUNMAZ - saflik testin temelidir.
///
/// Isaretleyici tip, testlerde ve DI taramasinda derlemeye tutamak olarak kullanilir.
public static class AssemblyMarker
{
    public static readonly System.Reflection.Assembly Assembly = typeof(AssemblyMarker).Assembly;
}
