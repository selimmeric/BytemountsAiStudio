namespace BytemountsAiStudio.Core.Content;

/// Kapak metninin durduğu yer (P5-03).
public enum ThumbnailTextPosition
{
    /// Dikey ortada — varsayılan.
    Center = 0,

    /// Alt üçlükte.
    ///
    /// Ayrı bir seçenek, çünkü platform kapağın SAĞ ALT köşesine süre
    /// rozeti basıyor: alta yaslanan metin o rozetin altında kalabilir
    /// ve bunu ancak ölçerek öğrenilir.
    Lower = 1,
}
