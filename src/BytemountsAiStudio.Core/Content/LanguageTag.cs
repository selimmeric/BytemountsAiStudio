using System.Globalization;

namespace BytemountsAiStudio.Core.Content;

/// BCP-47 dil etiketi ("tr-TR", "en-US"). Deger nesnesi.
///
/// ADR-013: dil bu sistemde birinci sinif boyut. Ham string tasimak, dilin
/// yanlislikla bos ya da tutarsiz ("tr", "TR", "tr_TR") gecmesine izin verirdi;
/// tekillik kontrolu ve font zinciri secimi buna bagli oldugu icin sessiz
/// hataya donusurdu.
public readonly record struct LanguageTag
{
    private LanguageTag(string value, CultureInfo culture)
    {
        Value = value;
        Culture = culture;
    }

    public string Value { get; }

    /// Sayi/tarih bicimleri ve buyuk-kucuk harf donusumleri icin.
    /// Turkce i/I donusumu bunun uzerinden yapilir - Invariant kullanmak yanlistir.
    public CultureInfo Culture { get; }

    /// Ana dil alt etiketi: "tr-TR" -> "tr". Ses ve font secimi genelde buna bakar.
    public string Primary => Value.Split('-')[0];

    /// Sagdan sola yazilan diller. Altyazi hizalamasi ve kutu yonu buna bagli.
    public bool IsRightToLeft => Culture.TextInfo.IsRightToLeft;

    public static Result<LanguageTag> TryCreate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Errors.Error.Permanent("language.empty", "Dil etiketi bos olamaz.");
        }

        var trimmed = value.Trim();

        try
        {
            // GetCultureInfo bilinmeyen etiketlerde de nesne uretebiliyor;
            // predefinedOnly ile gercekten taninan bir kulture zorluyoruz.
            var culture = CultureInfo.GetCultureInfo(trimmed, predefinedOnly: true);
            return new LanguageTag(culture.Name, culture);
        }
        catch (CultureNotFoundException)
        {
            return Errors.Error.Permanent(
                "language.unknown", $"Taninmayan dil etiketi: '{trimmed}'.");
        }
    }

    /// Konfigurasyondan gelen, gecerliligi zaten dogrulanmis degerler icin.
    public static LanguageTag Create(string value)
    {
        var result = TryCreate(value);
        return result.IsSuccess
            ? result.Value
            : throw new ArgumentException(result.Error.Message, nameof(value));
    }

    public override string ToString() => Value;
}
