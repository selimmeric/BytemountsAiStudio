using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BytemountsAiStudio.Core.Content;

/// Türkçe konuşma normalizasyonu.
///
/// Türkçe sayı okunuşu düzenli: birler, onlar, yüzler ve binler tekrarlanan
/// bir kalıp izliyor. Bu yüzden tablo değil ALGORİTMA yazılabiliyor —
/// İngilizce'deki "fourteen fifty-three" gibi istisnalar yok.
public sealed partial class TurkishSpeechNormalizer : ISpeechNormalizer
{
    public string Language => "tr";

    private static readonly string[] Ones =
        ["", "bir", "iki", "üç", "dört", "beş", "altı", "yedi", "sekiz", "dokuz"];

    private static readonly string[] Tens =
        ["", "on", "yirmi", "otuz", "kırk", "elli", "altmış", "yetmiş", "seksen", "doksan"];

    private static readonly (long Value, string Name)[] Scales =
        [(1_000_000_000, "milyar"), (1_000_000, "milyon"), (1_000, "bin")];

    /// Kısaltmalar: TTS bunları harf harf okumaya çalışır ya da kelime sanır.
    private static readonly Dictionary<string, string> Abbreviations = new(StringComparer.Ordinal)
    {
        ["M.Ö."] = "milattan önce",
        ["M.S."] = "milattan sonra",
        ["MÖ"] = "milattan önce",
        ["MS"] = "milattan sonra",
        ["vb."] = "ve benzeri",
        ["vs."] = "vesaire",
        ["yy."] = "yüzyıl",
        ["yy"] = "yüzyıl",
        ["km"] = "kilometre",
        ["cm"] = "santimetre",
        ["mm"] = "milimetre",
        ["kg"] = "kilogram",
        ["m²"] = "metrekare",
        ["°C"] = "santigrat derece",
    };

    public string Normalize(string displayText)
    {
        if (string.IsNullOrWhiteSpace(displayText))
        {
            return string.Empty;
        }

        var text = displayText;

        // Sıra önemli: yüzde ve para birimleri sayıdan ÖNCE işlenmeli,
        // yoksa "%12" içindeki 12 tek başına çevrilir ve "%" ortada kalır.
        text = PercentPattern().Replace(text, m => $"yüzde {SpellNumber(m.Groups[1].Value)}");
        text = DollarPattern().Replace(text, m => $"{SpellNumber(m.Groups[1].Value)} dolar");
        text = LiraPattern().Replace(text, m => $"{SpellNumber(m.Groups[1].Value)} lira");

        foreach (var (abbreviation, expansion) in Abbreviations)
        {
            text = text.Replace(abbreviation, expansion, StringComparison.Ordinal);
        }

        // Binlik ayırıcı önce temizleniyor: "1.453" tek sayıdır, iki değil.
        text = ThousandsPattern().Replace(text, m => m.Value.Replace(".", string.Empty, StringComparison.Ordinal));

        text = NumberPattern().Replace(text, m => SpellNumber(m.Value));

        return WhitespacePattern().Replace(text, " ").Trim();
    }

    /// Sayıyı Türkçe okunuşuna çevirir.
    ///
    /// "bir bin" DEĞİL "bin": Türkçe'de bin'in önündeki tek "bir" düşer.
    /// Bu istisnayı atlamak en sık yapılan hata.
    internal static string SpellNumber(string digits)
    {
        var trimmed = digits.Trim();

        if (!long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return trimmed;
        }

        return Spell(value);
    }

    internal static string Spell(long value)
    {
        if (value == 0)
        {
            return "sıfır";
        }

        var builder = new StringBuilder();

        if (value < 0)
        {
            builder.Append("eksi ");
            value = -value;
        }

        foreach (var (scale, name) in Scales)
        {
            if (value < scale)
            {
                continue;
            }

            var count = value / scale;

            // "bir bin" olmaz, "bin" olur. Milyon ve milyarda ise "bir milyon"
            // doğru — istisna yalnızca binde.
            if (!(count == 1 && scale == 1_000))
            {
                builder.Append(SpellUnderThousand(count)).Append(' ');
            }

            builder.Append(name).Append(' ');
            value %= scale;
        }

        if (value > 0)
        {
            builder.Append(SpellUnderThousand(value));
        }

        return builder.ToString().Trim();
    }

    private static string SpellUnderThousand(long value)
    {
        var builder = new StringBuilder();
        var hundreds = value / 100;

        if (hundreds > 0)
        {
            // "bir yüz" değil "yüz" — binle aynı kural.
            if (hundreds > 1)
            {
                builder.Append(Ones[hundreds]).Append(' ');
            }

            builder.Append("yüz ");
        }

        var remainder = value % 100;
        var tens = remainder / 10;

        if (tens > 0)
        {
            builder.Append(Tens[tens]).Append(' ');
        }

        var ones = remainder % 10;

        if (ones > 0)
        {
            builder.Append(Ones[ones]);
        }

        return builder.ToString().Trim();
    }

    [GeneratedRegex(@"%\s*(\d+)")]
    private static partial Regex PercentPattern();

    [GeneratedRegex(@"\$\s*(\d+)")]
    private static partial Regex DollarPattern();

    [GeneratedRegex(@"(\d+)\s*(?:TL|₺)")]
    private static partial Regex LiraPattern();

    [GeneratedRegex(@"\b\d{1,3}(?:\.\d{3})+\b")]
    private static partial Regex ThousandsPattern();

    [GeneratedRegex(@"\d+")]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex WhitespacePattern();
}

/// İngilizce konuşma normalizasyonu.
///
/// Türkçe'den farkı yıl okunuşu: 1453 "one thousand four hundred fifty-three"
/// değil "fourteen fifty-three" diye okunur. Bu istisna olmadan tarih
/// anlatan bir video kulağa yapay gelir.
public sealed partial class EnglishSpeechNormalizer : ISpeechNormalizer
{
    public string Language => "en";

    private static readonly string[] Ones =
    [
        "", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine",
        "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen",
        "sixteen", "seventeen", "eighteen", "nineteen",
    ];

    private static readonly string[] Tens =
        ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];

    private static readonly Dictionary<string, string> Abbreviations = new(StringComparer.Ordinal)
    {
        ["B.C."] = "before Christ",
        ["A.D."] = "anno Domini",
        ["etc."] = "et cetera",
        ["km"] = "kilometers",
        ["kg"] = "kilograms",
        ["°C"] = "degrees Celsius",
    };

    public string Normalize(string displayText)
    {
        if (string.IsNullOrWhiteSpace(displayText))
        {
            return string.Empty;
        }

        var text = displayText;

        text = PercentPattern().Replace(text, m => $"{Spell(long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))} percent");
        text = DollarPattern().Replace(text, m => $"{Spell(long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))} dollars");

        foreach (var (abbreviation, expansion) in Abbreviations)
        {
            text = text.Replace(abbreviation, expansion, StringComparison.Ordinal);
        }

        text = ThousandsPattern().Replace(text, m => m.Value.Replace(",", string.Empty, StringComparison.Ordinal));

        // Yıl gibi görünen dört haneliler ayrı ele alınıyor.
        text = YearPattern().Replace(text, m => SpellYear(long.Parse(m.Value, CultureInfo.InvariantCulture)));
        text = NumberPattern().Replace(text, m => Spell(long.Parse(m.Value, CultureInfo.InvariantCulture)));

        return WhitespacePattern().Replace(text, " ").Trim();
    }

    /// 1453 → "fourteen fifty-three". 2000 ve 1900 gibi yuvarlak yıllar
    /// bu kalıba uymuyor, onlar normal okunuyor.
    internal static string SpellYear(long year)
    {
        if (year is < 1100 or > 2099)
        {
            return Spell(year);
        }

        var high = year / 100;
        var low = year % 100;

        if (low == 0)
        {
            return $"{Spell(high)} hundred";
        }

        return low < 10
            ? $"{Spell(high)} oh {Spell(low)}"
            : $"{Spell(high)} {Spell(low)}";
    }

    internal static string Spell(long value)
    {
        if (value == 0)
        {
            return "zero";
        }

        if (value < 0)
        {
            return "minus " + Spell(-value);
        }

        if (value < 20)
        {
            return Ones[value];
        }

        if (value < 100)
        {
            var tens = Tens[value / 10];
            var ones = value % 10;
            return ones == 0 ? tens : $"{tens}-{Ones[ones]}";
        }

        if (value < 1_000)
        {
            var hundreds = $"{Ones[value / 100]} hundred";
            var remainder = value % 100;
            return remainder == 0 ? hundreds : $"{hundreds} {Spell(remainder)}";
        }

        foreach (var (scale, name) in new[] { (1_000_000_000L, "billion"), (1_000_000L, "million"), (1_000L, "thousand") })
        {
            if (value >= scale)
            {
                var count = Spell(value / scale);
                var remainder = value % scale;
                return remainder == 0 ? $"{count} {name}" : $"{count} {name} {Spell(remainder)}";
            }
        }

        return value.ToString(CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"(\d+)\s*%")]
    private static partial Regex PercentPattern();

    [GeneratedRegex(@"\$\s*(\d+)")]
    private static partial Regex DollarPattern();

    [GeneratedRegex(@"\b\d{1,3}(?:,\d{3})+\b")]
    private static partial Regex ThousandsPattern();

    [GeneratedRegex(@"\b(?:1[1-9]\d{2}|20\d{2})\b")]
    private static partial Regex YearPattern();

    [GeneratedRegex(@"\d+")]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex WhitespacePattern();
}

/// Dile göre normalizer seçer.
public sealed class SpeechNormalizerRegistry
{
    private readonly Dictionary<string, ISpeechNormalizer> _normalizers;

    public SpeechNormalizerRegistry(params ISpeechNormalizer[] normalizers)
        => _normalizers = normalizers.ToDictionary(n => n.Language, StringComparer.OrdinalIgnoreCase);

    public static SpeechNormalizerRegistry Default()
        => new(new TurkishSpeechNormalizer(), new EnglishSpeechNormalizer());

    /// Desteklenmeyen dilde metin OLDUĞU GİBİ dönüyor.
    ///
    /// Yeni bir dil eklemek bir normalizer yazmak demek; o gelene kadar
    /// içerik üretilebilmeli. Hata döndürmek üçüncü dili engellerdi.
    public string Normalize(LanguageTag language, string displayText)
        => _normalizers.TryGetValue(language.Primary, out var normalizer)
            ? normalizer.Normalize(displayText)
            : displayText;

    public bool Supports(LanguageTag language) => _normalizers.ContainsKey(language.Primary);
}
