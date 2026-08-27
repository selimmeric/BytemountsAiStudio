namespace BytemountsAiStudio.Media.Rendering;

/// Bu derlemenin islevi:
///   Render motorunun yan etkili katmani: FFmpeg surec yonetimi, Skia metin kompozisyonu, ffprobe dogrulama, atomik tasima.
///
/// Bagimlilik kurali:
///   Media + Contracts. Saf katmani tuketir, tersi olmaz.
///
/// Isaretleyici tip, testlerde ve DI taramasinda derlemeye tutamak olarak kullanilir.
public static class AssemblyMarker
{
    public static readonly System.Reflection.Assembly Assembly = typeof(AssemblyMarker).Assembly;
}
