using BytemountsAiStudio.Core.Content;

namespace BytemountsAiStudio.Media.Timeline;

/// Tuvale göre render ön ayarı (P3-02).
///
/// SORUN ADIN YALAN OLMASIYDI: `OutputSpec.Preset` her videoda
/// `"shorts-1080x1920"` yazıyordu — 1920×1080 çıkan on dakikalık uzun
/// videoda da. Ad hiçbir yerde okunmuyordu, o yüzden kimse fark
/// etmiyordu; ama çıktının yanında duran ve çıktıyı YANLIŞ ANLATAN
/// bir kayıt, hiç kayıt olmamasından kötü. "Bu video hangi ayarla
/// üretildi" sorusuna yanlış cevap veriyordu.
///
/// AYARLAR DA GERÇEKTEN FARKLI OLMALI, yalnızca ad değil — yoksa
/// "ön ayar" bir etiketten ibaret kalırdı.
public static class RenderPreset
{
    /// Uzun videoda anahtar kareler arası en çok kaç saniye.
    ///
    /// İki saniye: oynatıcı yalnızca anahtar kareye atlayabiliyor ve
    /// bölüm işareti üretip atlamanın nereye düşeceğini şansa
    /// bırakmak, işaretlerin yarısını vermekti. Daha küçük bir değer
    /// dosyayı gereksiz büyütürdü; daha büyüğü atlamayı hissedilir
    /// şekilde kaydırırdı.
    public const double LandscapeKeyframeSeconds = 2.0;

    /// Tuvale uygun çıktı ayarları.
    ///
    /// ANAHTAR KARE SINIRI YALNIZCA YATAYA: bölüm işaretleriyle
    /// atlanan şey uzun video ve uzun video yatay. Dikey ve KARE
    /// tuvalde kimse atlamıyor; sınır koymak dosyayı büyütür, karşılığı
    /// olmaz.
    ///
    /// Kare tuval P6-03'le geldi ve ilk hâlinde yataya sayılıyordu —
    /// `IsPortrait` yanlışsa "yatay" varsayan bir koşul, kareyi de
    /// yatay sanıyordu.
    public static OutputSpec ForCanvas(Canvas canvas)
        => new()
        {
            Preset = Name(canvas),
            KeyframeIntervalSeconds = IsLandscape(canvas) ? LandscapeKeyframeSeconds : null,
        };

    private static bool IsLandscape(Canvas canvas) => canvas.Width > canvas.Height;

    /// Ön ayarın adı — TUVALDEN türüyor.
    ///
    /// Sabit bir metin yazmak, tam da düzeltilen hatayı geri
    /// getirirdi: tuval değişince ad değişmez ve kayıt yine yalan
    /// söylerdi.
    ///
    /// KARE'NİN KENDİ ADI VAR: ilk hâlinde 1080x1080 çıktı
    /// `video-1080x1080` diye kaydediliyordu ve "video" burada yatayı
    /// anlatıyordu. Ad yine yalan söylüyordu, bu sefer daha sessizce.
    public static string Name(Canvas canvas)
        => System.FormattableString.Invariant(
            $"{Shape(canvas)}-{canvas.Width}x{canvas.Height}");

    private static string Shape(Canvas canvas)
        => canvas.IsPortrait ? "shorts" : IsLandscape(canvas) ? "video" : "kare";
}
