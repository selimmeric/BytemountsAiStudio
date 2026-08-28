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
    public static OutputSpec ForCanvas(Canvas canvas)
        => canvas.IsPortrait
            ? new OutputSpec
            {
                Preset = Name(canvas),

                // KISA VİDEODA ANAHTAR KARE SINIRI YOK: kimse
                // atlamıyor ve daha uzun GOP daha iyi sıkıştırma.
                KeyframeIntervalSeconds = null,
            }
            : new OutputSpec
            {
                Preset = Name(canvas),
                KeyframeIntervalSeconds = LandscapeKeyframeSeconds,
            };

    /// Ön ayarın adı — TUVALDEN türüyor.
    ///
    /// Sabit bir metin yazmak, tam da düzeltilen hatayı geri
    /// getirirdi: tuval değişince ad değişmez ve kayıt yine yalan
    /// söylerdi.
    public static string Name(Canvas canvas)
        => System.FormattableString.Invariant(
            $"{(canvas.IsPortrait ? "shorts" : "video")}-{canvas.Width}x{canvas.Height}");
}
