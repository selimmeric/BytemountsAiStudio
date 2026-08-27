using SkiaSharp;

namespace BytemountsAiStudio.Media.Rendering.Text;

/// Font zincirinden yazı tipi seçer (P1-23).
///
/// İKİ AYRI HATAYI kapatmak için ortak bir yere alındı; ikisi de canlı
/// çıktıya bakılınca görüldü, testlerden geçiyordu.
///
/// 1. `SKTypeface.FromFamilyName` KURULU OLMAYAN bir aile için NULL
///    DÖNMÜYOR. Sistem varsayılanını döndürüyor ve istenen kalınlığı
///    sessizce düşürüyor. Yani `FromFamilyName("Inter", Bold)` bu
///    makinede "Segoe UI regular" veriyor. `null` kontrolü yapan kod
///    bunu yakalayamıyor; dönen yüzün ADINA bakmak gerekiyor.
///
/// 2. Altyazı çizimi kalınlığı hiç istemiyordu. `TextStyle.Bold`
///    timeline'da `true` olduğu hâlde çizim ince yüzle yapılıyordu —
///    yani ayar vardı, etkisi yoktu.
public static class FontResolver
{
    /// Metnin TAMAMINI çizebilen ilk yazı tipi.
    ///
    /// Tek karakter için fallback yapmak satır ortasında yazı tipi
    /// değiştiriyor ve metin dağınık görünüyor — Türkçe'de bu 'ğ' ya da
    /// 'ş' harfinde oluyor, yani neredeyse her cümlede.
    public static SKTypeface Resolve(
        IReadOnlyList<string> fontStack, string sample, bool bold, SKFontManager fonts)
    {
        ArgumentNullException.ThrowIfNull(fontStack);
        ArgumentNullException.ThrowIfNull(fonts);

        var weight = bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal;

        foreach (var family in fontStack)
        {
            var candidate = SKTypeface.FromFamilyName(
                family, weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);

            if (candidate is null)
            {
                continue;
            }

            // ADI TUTUYOR MU — asıl kontrol bu. Tutmuyorsa Skia bize
            // istemediğimiz bir yüzü vermiş demektir ve o yüz istenen
            // kalınlıkta da değil.
            if (Matches(candidate, family) && CanRender(candidate, sample))
            {
                return candidate;
            }

            candidate.Dispose();
        }

        // Zincirdeki hiçbir aile kurulu değil. Sistemin varsayılanını
        // İSTENEN KALINLIKTA almaya çalışıyoruz: kalınlığı da kaybetmek
        // iki kayıp olurdu.
        var fallback = fonts.MatchCharacter(
            null, weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright, null,
            sample.FirstOrDefault(char.IsLetter));

        if (fallback is not null)
        {
            return fallback;
        }

        // Son çare: varsayılan yüz. Yanlış fontla çizmek, hiç çizmemekten iyi.
        return SKTypeface.FromFamilyName(null, weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
               ?? SKTypeface.Default;
    }

    /// Dönen yüzün ailesi istenen aile mi.
    ///
    /// Skia bazı yüzleri "Segoe UI" yerine "Segoe UI Variable" gibi
    /// döndürebiliyor; ön ek eşleşmesi bunu kabul ediyor ama tamamen
    /// başka bir aileyi kabul etmiyor.
    private static bool Matches(SKTypeface typeface, string family)
        => typeface.FamilyName.StartsWith(family, StringComparison.OrdinalIgnoreCase)
           || family.StartsWith(typeface.FamilyName, StringComparison.OrdinalIgnoreCase);

    public static bool CanRender(SKTypeface typeface, string sample)
    {
        ArgumentNullException.ThrowIfNull(typeface);
        ArgumentNullException.ThrowIfNull(sample);

        using var font = new SKFont(typeface);

        foreach (var rune in sample.EnumerateRunes())
        {
            if (rune.Value is ' ' or '\n' or '\t' or '\r')
            {
                continue;
            }

            if (font.GetGlyph(rune.Value) == 0)
            {
                return false;
            }
        }

        return true;
    }
}
