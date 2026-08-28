using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Errors;

namespace BytemountsAiStudio.Core.Learning;

/// Kapak varyantının ayarları (P5-03).
public readonly record struct ThumbnailVariantSettings(
    ThumbnailTextPosition Position,
    bool Uppercase,
    byte ScrimAlpha,
    float FontSize)
{
    /// Kontrol kolu: bugünkü davranış.
    ///
    /// Varsayılanı burada TEK BİR YERDE tutmak şart: kontrol kolunun
    /// ayarları başka bir yerden gelseydi, "kontrol" ile "hiç deney
    /// yok" iki farklı kapak üretirdi ve deneyin tabanı kayardı.
    public static ThumbnailVariantSettings Default { get; }
        = new(ThumbnailTextPosition.Center, Uppercase: false, ScrimAlpha: 130, FontSize: 96f);
}

/// Kapak A/B varyantı (P5-03).
///
/// SADECE GÖRÜNÜR ŞEYLER: her ayarın kapakta gözle görülür bir
/// karşılığı var. Görünmeyen bir ayarı denemek (dosya adı, JPEG
/// kalitesi 88 → 87) izleyicinin göremediği bir şeyi ölçmek olurdu.
public static class ThumbnailVariant
{
    /// KAPALI SÖZLÜK. Buraya yazılmayan hiçbir anahtar geçmiyor.
    public static IReadOnlyDictionary<string, string[]> Allowed { get; }
        = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["konum"] = ["orta", "alt"],
            ["harf"] = ["normal", "buyuk"],
            ["karartma"] = ["hafif", "normal", "agir"],
            ["punto"] = ["normal", "buyuk"],
        };

    public static Result<ThumbnailVariantSettings> Parse(string? configJson)
    {
        var parsed = VariantConfig.Parse(configJson);

        if (parsed.IsFailure)
        {
            return Result.Failure<ThumbnailVariantSettings>(parsed.Error);
        }

        var valid = VariantConfig.Validate(parsed.Value, Allowed);

        if (valid.IsFailure)
        {
            return Result.Failure<ThumbnailVariantSettings>(valid.Error);
        }

        var values = parsed.Value;

        return Result.Success(new ThumbnailVariantSettings(
            values.GetValueOrDefault("konum") == "alt"
                ? ThumbnailTextPosition.Lower
                : ThumbnailTextPosition.Center,
            Uppercase: values.GetValueOrDefault("harf") == "buyuk",
            ScrimAlpha: values.GetValueOrDefault("karartma") switch
            {
                "hafif" => 90,
                "agir" => 180,
                _ => ThumbnailVariantSettings.Default.ScrimAlpha,
            },
            FontSize: values.GetValueOrDefault("punto") == "buyuk"
                ? 120f
                : ThumbnailVariantSettings.Default.FontSize));
    }

    /// Kapak metnini varyanta göre biçimler.
    ///
    /// BÜYÜK HARF DİLE DUYARLI. `ToUpperInvariant` Türkçe'de "istanbul"
    /// kelimesini "ISTANBUL" yapıyor; doğrusu "İSTANBUL". Kapak,
    /// kanalın en çok görülen tek görseli ve oradaki noktasız İ, o
    /// kanalın Türkçe yazamadığını söylüyor.
    public static string ApplyCase(string title, LanguageTag language, bool uppercase)
    {
        ArgumentNullException.ThrowIfNull(title);

        return uppercase ? title.ToUpper(language.Culture) : title;
    }
}

/// Başlık A/B varyantı (P5-03).
///
/// Başlığın KENDİSİ değil, başlığın YAZILMA BİÇİMİ deneniyor: metni
/// koda gömmek her videoya aynı başlığı verirdi. Değişen şey isteme
/// giren stil adı; stilin ne demek olduğu istem dosyasında yazıyor
/// (`prompts/seo.generate/v2.md`) — böylece sürümlü ve gözle
/// karşılaştırılabilir kalıyor.
public static class TitleVariant
{
    /// İstem dosyasındaki yer tutucu.
    public const string Placeholder = "baslik_stili";

    public const string DefaultStyle = "duz";

    public static IReadOnlyDictionary<string, string[]> Allowed { get; }
        = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["stil"] = ["duz", "soru", "sayi"],
        };

    public static Result<string> Parse(string? configJson)
    {
        var parsed = VariantConfig.Parse(configJson);

        if (parsed.IsFailure)
        {
            return Result.Failure<string>(parsed.Error);
        }

        var valid = VariantConfig.Validate(parsed.Value, Allowed);

        return valid.IsFailure
            ? Result.Failure<string>(valid.Error)
            : Result.Success(parsed.Value.GetValueOrDefault("stil") ?? DefaultStyle);
    }

    /// Stilin isteme GERÇEKTEN girdiğini doğrular.
    ///
    /// İstem şablonu, kendisinde olmayan yer tutuculara verilen
    /// değerleri SESSİZCE YUTUYOR. Kontrol olmadan, `{{baslik_stili}}`
    /// içermeyen eski bir istem sürümüyle koşan bir başlık deneyi iki
    /// kolda da aynı istemi kullanır ve hiçbir şey ölçmez.
    public static Result Verify(string templateText)
    {
        ArgumentNullException.ThrowIfNull(templateText);

        return templateText.Contains("{{" + Placeholder + "}}", StringComparison.Ordinal)
            ? Result.Success()
            : Error.Permanent("variant.placeholder_missing",
                "Başlık deneyi koşuyor ama istemde '{{" + Placeholder + "}}' yer tutucusu yok; "
                + "stil isteme hiç girmezdi.");
    }
}
