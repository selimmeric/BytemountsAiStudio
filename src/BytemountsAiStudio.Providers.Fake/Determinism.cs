using System.Globalization;
using System.Text;

namespace BytemountsAiStudio.Providers.Fake;

/// Sahte sağlayıcıların üreteci.
///
/// ADR-009: fake'lerin tek işi ucuz olmak değil, DETERMİNİST olmak. Aynı girdi
/// her zaman aynı çıktıyı vermeli ki boru hattı testi "bazen geçen" bir test
/// olmasın. Bu yüzden burada <see cref="Random"/> ve <see cref="DateTime.Now"/>
/// yok — her şey girdinin kararlı hash'inden türetilir.
internal static class Determinism
{
    /// Sabit referans an. Fake'ler saate bakmaz; zaman damgası gerektiğinde
    /// bunu kullanır. Testlerin dünkü çıktıyla bugünkü çıktıyı karşılaştırabilmesi
    /// buna bağlı.
    public static readonly DateTimeOffset Epoch =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// FNV-1a 64 bit. Kriptografik değil — amaç güvenlik değil, süreçler ve
    /// çalıştırmalar arasında AYNI sonucu vermek. `string.GetHashCode()` bunu
    /// garanti etmez (randomized hashing), o yüzden kullanılamaz.
    public static ulong Hash(params ReadOnlySpan<string?> parts)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        var hash = offsetBasis;

        foreach (var part in parts)
        {
            foreach (var b in Encoding.UTF8.GetBytes(part ?? string.Empty))
            {
                hash ^= b;
                hash *= prime;
            }

            // Parça sınırı: ("ab","c") ile ("a","bc") farklı hash üretmeli.
            hash ^= 0xFF;
            hash *= prime;
        }

        return hash;
    }

    /// Hash'ten [min, max) aralığında kararlı bir sayı.
    public static int Range(ulong hash, int min, int max)
        => min + (int)(hash % (ulong)Math.Max(1, max - min));

    /// Hash'ten okunabilir kısa kimlik. Sahte URL'lerde ve dış kimliklerde kullanılır.
    public static string Token(ulong hash, int length = 11)
    {
        const string alphabet = "abcdefghijkmnpqrstuvwxyz23456789";
        var builder = new StringBuilder(length);

        for (var i = 0; i < length; i++)
        {
            builder.Append(alphabet[(int)(hash % (ulong)alphabet.Length)]);
            hash /= (ulong)alphabet.Length;
            if (hash == 0)
            {
                hash = Hash(builder.ToString());
            }
        }

        return builder.ToString();
    }

    /// Hash'ten okunaklı bir RGB rengi. Doygunluğu ve parlaklığı sınırlı
    /// tutuyoruz: sahte görseller üst üste bindiğinde ayırt edilebilsin ama
    /// göz almasın.
    public static (byte R, byte G, byte B) Color(ulong hash)
    {
        var hue = (int)(hash % 360);
        return HsvToRgb(hue, 0.45, 0.72);
    }

    private static (byte R, byte G, byte B) HsvToRgb(int hue, double saturation, double value)
    {
        var c = value * saturation;
        var x = c * (1 - Math.Abs((hue / 60.0 % 2) - 1));
        var m = value - c;

        var (r, g, b) = hue switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return ((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    /// Kültürden bağımsız biçimlendirme.
    ///
    /// `string.Create(CultureInfo, ...)` kullanılamaz: o aşırı yükleme yalnızca
    /// çağrı yerinde yazılmış bir enterpolasyon dizgisiyle çalışır, değişkene
    /// alınmış bir <see cref="FormattableString"/> ile değil.
    public static string Format(FormattableString text)
        => FormattableString.Invariant(text);
}
