namespace BytemountsAiStudio.Core.Content;

/// Uretilen icerigin turu.
///
/// §34: sistem yalnizca video uretmeyecek. Tur bu enum'da genisler; yeni tur
/// eklemek yeni bir tablo ya da yeni bir boru hatti gerektirmez, yalnizca
/// farkli bir workflow ve farkli bir rendition demektir.
public enum ContentKind
{
    Short = 0,
    Video = 1,
    Podcast = 2,
    Blog = 3,
    SocialPost = 4,
}
