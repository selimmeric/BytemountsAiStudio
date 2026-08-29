using BytemountsAiStudio.Core;

namespace BytemountsAiStudio.Contracts.Providers;

/// API anahtarlarının saklandığı yer (mimari §16, P1-01).
///
/// Anahtar üç yerde aranıyor, bu sırayla:
///   1. Kanala özel kayıt  — her kanal kendi hesabını kullanabilsin
///   2. Genel kayıt        — tek hesabı bütün kanallar paylaşıyorsa
///   3. Ortam değişkeni    — `config/providers.json` içindeki `key_env`
///
/// Ortam değişkeninin EN SONDA olması bilinçli: geliştirme makinesinde
/// ortamdan okumak pratik, ama üretimde kanal başına ayrı hesap gerekiyor
/// ve o zaman veritabanındaki kayıt ortamı ezmeli. Ters sırada olsaydı
/// sunucuda unutulmuş bir ortam değişkeni bütün kanalları sessizce aynı
/// hesaba bağlardı.
/// Kimlik kayıtlarının ortak sabitleri.
public static class Credentials
{
    /// Hesap adı verilmediğinde kullanılan ad (P4-04).
    ///
    /// TEK YERDE: kod bir yerde `default`, göç dosyası başka bir yerde
    /// boş dizge yazsaydı, mevcut kayıtlar havuzda GÖRÜNMEZ olurdu.
    public const string DefaultAccount = "default";
}

public interface ICredentialStore
{
    /// Anahtarın açık hâlini döndürür. Bulunamazsa kalıcı hata —
    /// yeniden denemek anahtarı var etmez.
    /// `account`: kota havuzundaki hesap adı (P4-04).
    ///
    /// VARSAYILANI OLAN BİR PARAMETRE, atlanabilir bir bağımlılık
    /// değil: tek hesaplı kurulumda `default` DOĞRU cevap, çünkü
    /// kayıtlar da o adla yazılıyor. Boş bırakmak bir şeyi sessizce
    /// kapatmıyor.
    Task<Result<string>> GetAsync(
        string providerKey, Guid? channelId, CancellationToken cancellationToken,
        string account = Credentials.DefaultAccount);

    Task<Result> SetAsync(
        string providerKey, Guid? channelId, string secret, CancellationToken cancellationToken,
        string account = Credentials.DefaultAccount);

    Task<Result> DeleteAsync(
        string providerKey, Guid? channelId, CancellationToken cancellationToken,
        string account = Credentials.DefaultAccount);

    /// Kayıtların ÜST BİLGİSİ — gizli değer dönmüyor.
    ///
    /// Ayrı bir metot olması gerekiyordu: "hangi anahtarlar tanımlı"
    /// sorusunun cevabı arayüzde ve loglarda gösteriliyor, ve o yol
    /// üzerinde gizli değerin hiç bulunmaması gerekiyor.
    Task<IReadOnlyList<CredentialInfo>> ListAsync(Guid? channelId, CancellationToken cancellationToken);
}

/// Bir kimlik kaydının gizli olmayan tarafı.
public sealed record CredentialInfo
{
    public required string ProviderKey { get; init; }

    /// Havuzdaki hesap adı (P4-04).
    public string Account { get; init; } = Credentials.DefaultAccount;

    /// null = genel kayıt, bütün kanallar için.
    public Guid? ChannelId { get; init; }

    /// Kaynak: `db` veya `env`. Hangi anahtarın kullanıldığı sorusunun
    /// cevabı sorun giderirken en çok işe yarayan bilgi.
    public required string Source { get; init; }

    /// Değerin son dört karakteri, öncesi maskeli — "doğru anahtarı mı
    /// koydum" sorusunu, anahtarı açığa çıkarmadan cevaplıyor.
    public required string Masked { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }

    public DateTimeOffset? LastUsedAt { get; init; }
}

/// Anahtarın nereden okunacağı.
///
/// Ortam değişkeni VARSAYILAN ama tek yol değil: anahtarlar şifreli
/// depoda duruyor (P1-01) ve orası da bu arayüzü gerçekliyor.
/// Sağlayıcı hangisinin kullanıldığını bilmiyor.
///
/// `ICredentialStore`'dan AYRI ve daha dar: depo yazma, silme ve
/// maskeleme de yapıyor; sağlayıcının tek ihtiyacı okumak. Geniş
/// arayüzü sağlayıcıya vermek, bir sağlayıcı hatasının kimlik
/// bilgilerini silebilmesi demekti.
public interface ICredentialSource
{
    string? Get(string name);
}
