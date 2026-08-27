using System.Collections.Concurrent;

namespace BytemountsAiStudio.Core.Observability;

/// Loglara sızan gizli değerleri temizler (P1-01).
///
/// Neden gerekli: bir API anahtarı yalnızca "gizli anahtar" diye yazdığınız
/// yerden sızmaz. Sağlayıcı istisnası isteğin URL'sini mesaja koyar, HTTP
/// istemcisi başlığı yazar, bir hata gövdesi anahtarı aynen geri döndürür.
/// Bu yolların hepsini tek tek kapatmak mümkün değil; çıkışta süzmek mümkün.
///
/// Bilinen değerleri süzüyoruz, kalıba bakmıyoruz. Kalıp eşleştirme
/// (`sk-...` gibi) yeni bir sağlayıcı formatında sessizce ıskalar; oysa
/// depodan okunan her anahtar buraya kaydediliyor ve tam eşleşme kaçırmaz.
///
/// Kayıt süreç ömrü boyunca duruyor. Anahtar sayısı onlarla ölçülüyor,
/// dolayısıyla süzme maliyeti log satırı başına birkaç `Contains` çağrısı.
public static class SecretRedactor
{
    /// Bu uzunluğun altındaki değerler KAYDEDİLMİYOR.
    ///
    /// Kısa bir gizli değer ("test", "abc") log metinlerinde tesadüfen
    /// geçer ve süzgeç bütün satırları hurdaya çevirir. Gerçek bir API
    /// anahtarı zaten bunun çok üstünde; bu eşiğin altındaki bir değer
    /// üretim anahtarı değildir.
    private const int MinimumLength = 12;

    private const string Mask = "***";

    private static readonly ConcurrentDictionary<string, byte> Secrets = new(StringComparer.Ordinal);

    /// Bir gizli değeri süzgece ekler. Anahtar okunduğu anda çağrılıyor.
    public static void Register(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length < MinimumLength)
        {
            return;
        }

        Secrets.TryAdd(secret, 0);
    }

    /// Metindeki bilinen gizli değerleri maskeler.
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text) || Secrets.IsEmpty)
        {
            return text ?? string.Empty;
        }

        var result = text;

        foreach (var secret in Secrets.Keys)
        {
            if (result.Contains(secret, StringComparison.Ordinal))
            {
                result = result.Replace(secret, Mask, StringComparison.Ordinal);
            }
        }

        return result;
    }

    /// Kullanıcıya gösterilebilir maskeli hâl: son dört karakter açık.
    ///
    /// "Doğru anahtarı mı koydum" sorusunu anahtarı açığa çıkarmadan
    /// cevaplıyor. Dört karakter, iki anahtarı birbirinden ayırmaya yetiyor
    /// ama kaba kuvvetle tamamlanmaya yetmiyor.
    public static string Mask4(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return Mask;
        }

        return secret.Length <= 4
            ? Mask
            : string.Concat(Mask, secret.AsSpan(secret.Length - 4));
    }

    /// Yalnızca testler için: süzgeci boşaltır.
    /// Testler arasında sızıntı olmaması gerekiyor.
    public static void Clear() => Secrets.Clear();
}
