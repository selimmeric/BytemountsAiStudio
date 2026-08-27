using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;

namespace BytemountsAiStudio.Contracts.Prompts;

/// Dosyadan okunmuş, sürümlenmiş bir istem şablonu (P1-07).
///
/// Bir istem KODDUR. Kod gibi sürümlenmesi, kod gibi karşılaştırılabilmesi
/// ve bir çıktı bozulduğunda "hangi istem sürümüyle üretildi" sorusunun
/// cevaplanabilmesi gerekiyor. Kaynak dosyada gömülü bir dizge bunların
/// hiçbirini vermiyordu: değiştiren kişi belli olsa bile hangi videonun
/// hangi metinle üretildiği kayıtta yoktu.
public sealed record PromptTemplate
{
    public required string Key { get; init; }

    public required int Version { get; init; }

    /// Dosyanın ham içeriğinin SHA-256'sı, ilk 16 karakter.
    ///
    /// Sürüm numarası yetmiyor: birisi sürüm numarasını artırmadan metni
    /// düzeltebilir ve o zaman iki farklı istem aynı kimliği taşır. Özet,
    /// numaraya güvenmeden gerçek metni damgalıyor.
    public required string Hash { get; init; }

    public string? Description { get; init; }

    public string? System { get; init; }

    public required string User { get; init; }

    /// Kayıtlarda ve loglarda geçen tek satırlık kimlik: `script.generate@3#a1b2c3d4`.
    public string Stamp => string.Create(CultureInfo.InvariantCulture, $"{Key}@{Version}#{Hash}");

    /// Yer tutucuları doldurur.
    ///
    /// Eksik yer tutucu HATA — boş bırakmak değil. Boş bırakmak, modele
    /// "'' konusunda senaryo yaz" diyen sessizce bozuk bir istem üretir;
    /// bunun teşhisi, çıktıya bakarak saatler alır. Fazladan değer ise
    /// serbest: aynı sözlük birden çok isteme verilebilsin.
    public Result<RenderedPrompt> Render(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var missing = new List<string>();
        var system = System is null ? null : Substitute(System, values, missing);
        var user = Substitute(User, values, missing);

        if (missing.Count > 0)
        {
            return Error.Permanent(
                "prompt.missing_value",
                $"'{Stamp}' istemi icin deger verilmemis yer tutucu var: {string.Join(", ", missing.Distinct(StringComparer.Ordinal))}");
        }

        return Result.Success(new RenderedPrompt
        {
            Stamp = Stamp,
            Key = Key,
            Version = Version,
            System = system,
            User = user,
        });
    }

    /// `{{ad}}` yer tutucularını değiştirir.
    ///
    /// Elle yazılmış bir tarayıcı; hazır bir şablon motoru getirmedik.
    /// Şablon motorları döngü, koşul ve metot çağrısı getiriyor — istem
    /// dosyalarına mantık girmesini istemiyoruz. İstemde mantık olursa
    /// istem artık okunabilir bir metin olmaktan çıkar ve gözle
    /// karşılaştırılamaz hâle gelir (ifade dili için verdiğimiz kararın
    /// aynısı, §7.3).
    private static string Substitute(
        string text, IReadOnlyDictionary<string, string> values, List<string> missing)
    {
        var result = new StringBuilder(text.Length);
        var index = 0;

        while (index < text.Length)
        {
            var open = text.IndexOf("{{", index, StringComparison.Ordinal);

            if (open < 0)
            {
                result.Append(text, index, text.Length - index);
                break;
            }

            var close = text.IndexOf("}}", open + 2, StringComparison.Ordinal);

            if (close < 0)
            {
                result.Append(text, index, text.Length - index);
                break;
            }

            result.Append(text, index, open - index);

            var name = text[(open + 2)..close].Trim();

            if (values.TryGetValue(name, out var value))
            {
                result.Append(value);
            }
            else
            {
                missing.Add(name);
            }

            index = close + 2;
        }

        return result.ToString();
    }

    internal static string ComputeHash(string content)
    {
        // Satır sonu normalize ediliyor: aynı dosya Windows ve Linux'ta
        // farklı özet vermemeli, yoksa CI'daki damga geliştirme
        // makinesindekiyle tutmaz.
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));

        return Convert.ToHexStringLower(bytes)[..16];
    }
}

/// Doldurulmuş istem. Sağlayıcıya bu gidiyor, kayda `Stamp` yazılıyor.
public sealed record RenderedPrompt
{
    public required string Stamp { get; init; }

    public required string Key { get; init; }

    public required int Version { get; init; }

    public string? System { get; init; }

    public required string User { get; init; }
}
