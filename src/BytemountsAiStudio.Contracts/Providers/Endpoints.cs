namespace BytemountsAiStudio.Contracts.Providers;

/// Dış servis adreslerinin çözümlendiği tek yer.
///
/// HİÇBİR ADRES KODA GÖMÜLÜ DEĞİL. Her adresin bir ortam değişkeni
/// karşılığı var ve varsayılanı `config/providers.json` ile AYNI
/// olmak zorunda — `ProviderEndpointTests` bunu sınıyor.
///
/// Neden: bu bir sistem, bir betik değil. Servis adresi değişince
/// (kendi kopyasını çalıştıran biri, bölgesel bir uç nokta, bir vekil
/// sunucu, testte sahte bir sunucu) yapılacak şey ortam değişkeni
/// tanımlamak olmalı — yeniden derleme değil.
///
/// GEÇERSİZ ADRES SESSİZCE YOK SAYILMIYOR: `BMAI_PEXELS_URL=htp://...`
/// yazan biri, sistemin hâlâ varsayılana gittiğini fark etmezdi ve
/// "ayarım neden çalışmıyor" sorusunun cevabı hiçbir yerde olmazdı.
public static class Endpoints
{
    /// Ortam değişkeni adının ön eki.
    public const string Prefix = "BMAI_";

    /// YER TUTUCULU adresi çözümler.
    ///
    /// Wikipedia gibi dile göre alan adı değişen servisler için:
    /// `https://{language}.wikipedia.org/w/api.php`. Şablonu `Uri`
    /// olarak tutmak mümkün değil (yer tutucu geçerli bir adres
    /// bileşeni değil), o yüzden metin.
    ///
    /// YER TUTUCUNUN VARLIĞI DOĞRULANIYOR: onu düşüren bir ayar,
    /// bütün dilleri tek bir dile bağlardı ve Türkçe kanal İngilizce
    /// Wikipedia'dan okurdu.
    public static string ResolveTemplate(
        string environmentVariable,
        string fallback,
        string requiredPlaceholder,
        Func<string, string?>? read = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentVariable);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallback);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredPlaceholder);

        var raw = (read ?? Environment.GetEnvironmentVariable)(environmentVariable);
        var template = string.IsNullOrWhiteSpace(raw) ? fallback : raw;

        if (!template.Contains(requiredPlaceholder, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{environmentVariable} '{requiredPlaceholder}' yer tutucusunu içermeli: '{template}'",
                environmentVariable);
        }

        return template;
    }

    /// Adresi çözümler: ortam değişkeni → varsayılan.
    ///
    /// `read` dışarıdan veriliyor: yapılandırma mantığı süreç geneli
    /// ortam değişkenlerine DOKUNMADAN sınanabilsin. Testte
    /// `Environment.SetEnvironmentVariable` çağırmak, aynı süreçte
    /// koşan komşu testleri kırmanın sessiz bir yolu.
    public static Uri Resolve(
        string environmentVariable, string fallback, Func<string, string?>? read = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentVariable);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallback);

        var raw = (read ?? Environment.GetEnvironmentVariable)(environmentVariable);

        if (string.IsNullOrWhiteSpace(raw))
        {
            return new Uri(fallback);
        }

        if (Uri.TryCreate(raw, UriKind.Absolute, out var address))
        {
            return address;
        }

        // BOZUK AYAR GÖRÜLÜR OLUYOR. Sessizce varsayılana düşmek,
        // "ayarımı neden uygulamıyor" sorusunu cevapsız bırakırdı.
        throw new ArgumentException(
            $"{environmentVariable} geçerli bir mutlak adres değil: '{raw}'", environmentVariable);
    }
}
