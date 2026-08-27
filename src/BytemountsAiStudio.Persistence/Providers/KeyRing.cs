using Microsoft.AspNetCore.DataProtection;

namespace BytemountsAiStudio.Persistence.Providers;

/// Şifreleme anahtar halkasının yeri (P1-01).
///
/// Tek yerde duruyor çünkü burası kaybedilirse veritabanındaki bütün API
/// anahtarları çözülemez hâle gelir. Konumu kod içinde dağılmış olsaydı
/// bir gün biri "geçici dizin daha temiz" diye değiştirir ve ilk yeniden
/// başlatmada anahtarlar giderdi.
///
/// Halka veritabanının DIŞINDA: ikisi aynı yerde durursa şifrelemenin bir
/// anlamı kalmaz — yedeği alan kişi hem şifreli metni hem anahtarı alır.
public static class KeyRing
{
    /// Varsayılan konum: kullanıcının uygulama verisi dizini.
    ///
    /// `LocalApplicationData` bilinçli — gezici profille sunucular arasında
    /// dolaşmasını istemiyoruz. Ortam değişkeni ile ezilebiliyor ki üretimde
    /// kalıcı bir birime (ya da bir sır yöneticisine) alınabilsin.
    public static DirectoryInfo Default
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("BMAI_KEYRING_PATH");

            var path = string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BytemountsAiStudio",
                    "keyring")
                : configured;

            return new DirectoryInfo(path);
        }
    }

    /// Konsol tarafı için sağlayıcı. Web tarafı DI ile `AddDataProtection()`
    /// kuracak; ikisi de aynı halkayı gösterdiği sürece aynı anahtarları okur.
    public static IDataProtectionProvider Create(DirectoryInfo? directory = null)
    {
        var target = directory ?? Default;
        target.Create();

        return DataProtectionProvider.Create(target);
    }
}
