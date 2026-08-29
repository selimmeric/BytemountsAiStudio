using System.Globalization;
using System.Text.Json;
using BytemountsAiStudio.Core.Errors;

namespace BytemountsAiStudio.Core.Learning;

/// İstem sürümü A/B varyantı (P5-05).
///
/// Deneyin değiştirdiği şey İSTEMİN SÜRÜMÜ: `script.generate@2` mi
/// `script.generate@3` mü daha iyi senaryo yazıyor. Metin istem
/// dosyasında duruyor, kodda değil — yani kollar sürümlü, gözle
/// karşılaştırılabilir ve geçmişi git'te.
public static class PromptVariant
{
    /// Deneyin hangi isteme dokunduğu.
    public const string KeyField = "istem";

    /// Hangi sürümü kullandığı.
    public const string VersionField = "surum";

    public static IReadOnlyDictionary<string, string[]> Allowed { get; }
        = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            // Değer kümesi boş: istem anahtarları ve sürüm numaraları
            // sabit bir listeye sığmıyor. Doğrulama bir adım sonra,
            // KAYITTA yapılıyor — var olmayan bir sürüme işaret eden
            // kol, sessizce varsayılan sürüme düşer ve iki kol aynı
            // istemi kullanırdı.
            [KeyField] = [],
            [VersionField] = [],
        };

    /// Varyant ayarından istem seçimi.
    public static Result<(string Key, int Version)> Parse(string? configJson)
    {
        var parsed = VariantConfig.Parse(configJson);

        if (parsed.IsFailure)
        {
            return Result.Failure<(string, int)>(parsed.Error);
        }

        var valid = VariantConfig.Validate(parsed.Value, Allowed);

        if (valid.IsFailure)
        {
            return Result.Failure<(string, int)>(valid.Error);
        }

        var key = parsed.Value.GetValueOrDefault(KeyField);

        if (string.IsNullOrWhiteSpace(key))
        {
            return Error.Permanent("variant.no_prompt_key",
                $"İstem deneyinde `{KeyField}` yok; hangi isteme dokunulduğu belirsiz.");
        }

        var raw = parsed.Value.GetValueOrDefault(VersionField);

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version)
            || version <= 0)
        {
            return Error.Permanent("variant.bad_prompt_version",
                $"`{VersionField}` pozitif bir tam sayı olmalı: '{raw}'");
        }

        return Result.Success((key, version));
    }
}

/// Bir run'ın bir istem için kullanacağı sürüm (P5-05).
///
/// KÖPRÜ: deney atamasını node'a taşıyan tek yol. Olmasaydı istem
/// deneyi atama tablosuna yazılır, hiçbir node okumaz ve iki kol aynı
/// istemi kullanırdı.
public static class PromptSelection
{
    /// Bu run bu istem için hangi sürümü kullanmalı — deney yoksa `null`.
    ///
    /// `null` "varsayılan sürüm" demek ve normal işleyiş: videoların
    /// ezici çoğunluğu hiçbir istem deneyine girmiyor.
    public static int? Version(JsonElement runContext, string promptKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(promptKey);

        var config = ExperimentContext.ConfigFor(runContext, "prompt");

        if (config is null)
        {
            return null;
        }

        var parsed = PromptVariant.Parse(config);

        if (parsed.IsFailure)
        {
            // BOZUK AYAR BURADA SESSİZ: kayıt aşamasında zaten
            // reddediliyor ve deney kapatılıyor. Burada hata döndürmek,
            // her node'a aynı doğrulamayı tekrar ettirmek olurdu.
            return null;
        }

        // BAŞKA BİR İSTEMİN DENEYİ BU NODE'U ETKİLEMİYOR.
        //
        // Anahtar kontrolü olmasaydı, `script.generate` üzerinde açılan
        // bir deney `seo.generate` node'unu da o sürüme zorlar ve
        // muhtemelen "sürüm yok" hatasıyla run'ı düşürürdü.
        return string.Equals(parsed.Value.Key, promptKey, StringComparison.Ordinal)
            ? parsed.Value.Version
            : null;
    }
}
