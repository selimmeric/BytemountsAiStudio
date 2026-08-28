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

        // ALT CIZGI TIREYE CEVRILIYOR ve bu bir kolaylik degil,
        // gercek bir hatanin duzeltmesi.
        //
        // .NET `tr_TR` etiketini KABUL ediyor ve adini `tr_tr` yapiyor
        // - yani `tr-TR` ile ESIT OLMAYAN ikinci bir dil nesnesi.
        // Sonuclari sessiz ve agir olurdu:
        //   - `Primary` degeri "tr" degil "tr_tr" cikiyor, yani ses ve
        //     yazi tipi secimi hicbir seyle eslesmiyor
        //   - tekillik sorgusu dile gore filtreliyor; `tr_tr` konulari
        //     `tr-TR` konularini hic gormuyor ve ayni video ikinci kez
        //     uretiliyor
        //
        // Bu sinifin belge yorumu tam olarak bu senaryoyu "onlendi"
        // diye anlatiyordu; onlenmemisti. Ucuncu dil testi yakaladi.
        var trimmed = value.Trim().Replace('_', '-');

        try
        {
            // GetCultureInfo bilinmeyen etiketlerde de nesne uretebiliyor;
            // predefinedOnly ile gercekten taninan bir kulture zorluyoruz.
            var culture = CultureInfo.GetCultureInfo(trimmed, predefinedOnly: true);

            // KULTURUN KENDI ADI kullaniliyor, girilen metin degil:
            // "tr-tr" girilse bile deger "tr-TR" oluyor ve ayni dil
            // her yerde ayni nesneye donusuyor.
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
