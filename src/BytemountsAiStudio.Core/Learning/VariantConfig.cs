using System.Text.Json;
using BytemountsAiStudio.Core.Errors;

namespace BytemountsAiStudio.Core.Learning;

/// Bir deney varyantının node ayarlarına kattığı fark (P5-03).
///
/// SESSİZCE YOK SAYILAN AYAR, ÖLÇMEYEN BİR DENEY DEMEK.
///
/// Bu sınıfın tek işi bunu engellemek. Varyant ayarı yazım hatası
/// yüzünden düşerse iki kol AYNI videoyu üretir; deney haftalarca
/// koşar, binlerce gösterim toplar ve sonunda "fark yok" der. O
/// cümle doğru görünür — çünkü gerçekten fark yoktur. Ölçülen şey
/// varyant değil, hiçbir şeydir.
///
/// Bu depoda aynı hata sözleşme katmanında bir kez ödendi: istem
/// şablonuna verilen fazladan değerler sessizce yutuluyor. Burada
/// KAPALI SÖZLÜK var: tanınmayan anahtar da tanınmayan değer de
/// KALICI hata.
public static class VariantConfig
{
    /// Varyantın `config_json` alanını düz bir sözlüğe çevirir.
    ///
    /// Düz metin sözlüğü, iç içe nesne değil: iç içe yapı, "hangi
    /// alanın hangi node'a gittiği" sorusunu belirsizleştiriyor ve
    /// deneyin tek değişken kuralını denetlenemez kılıyordu.
    public static Result<IReadOnlyDictionary<string, string>> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Result.Success<IReadOnlyDictionary<string, string>>(
                new Dictionary<string, string>(StringComparer.Ordinal));
        }

        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Error.Permanent("variant.not_object",
                    "Varyant ayarı bir nesne olmalı.");
            }

            var values = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var property in document.RootElement.EnumerateObject())
            {
                // SAYI VE BOOL DA KABUL EDİLİYOR, ama metne çevrilerek.
                // Reddetmek, `{"punto": 2}` yazan birinin deneyini
                // çalıştırılamaz kılardı; sessizce atlamak ise ayarı
                // kaybederdi.
                var text = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
                        => property.Value.GetRawText(),
                    _ => null,
                };

                if (text is null)
                {
                    return Error.Permanent("variant.bad_value",
                        $"'{property.Name}' ayarı metin, sayı veya doğru/yanlış olmalı.");
                }

                values[property.Name] = text;
            }

            return Result.Success<IReadOnlyDictionary<string, string>>(values);
        }
        catch (JsonException ex)
        {
            return Error.Permanent("variant.bad_json", ex.Message);
        }
    }

    /// Ayarları KAPALI bir sözlüğe göre doğrular.
    ///
    /// `allowed`: anahtar → o anahtarın kabul ettiği değerler. Değer
    /// kümesi boşsa anahtar serbest metin alıyor demektir.
    public static Result Validate(
        IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, string[]> allowed)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(allowed);

        foreach (var (key, value) in values)
        {
            if (!allowed.TryGetValue(key, out var options))
            {
                // TANINMAYAN ANAHTAR HATA, UYARI DEĞİL. Uyarı log'a
                // düşer ve kimse okumaz; deney yine ölçmeden koşar.
                var known = string.Join(", ", allowed.Keys.Order(StringComparer.Ordinal));

                return Error.Permanent("variant.unknown_key",
                    $"'{key}' bilinmeyen bir varyant ayarı. Tanımlılar: {known}");
            }

            if (options.Length > 0 && !options.Contains(value, StringComparer.Ordinal))
            {
                return Error.Permanent("variant.unknown_value",
                    $"'{key}' için '{value}' tanımlı değil. Seçenekler: {string.Join(", ", options)}");
            }
        }

        return Result.Success();
    }
}
