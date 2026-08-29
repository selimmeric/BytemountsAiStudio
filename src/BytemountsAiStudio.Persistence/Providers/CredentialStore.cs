using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Core.Observability;
using BytemountsAiStudio.Persistence.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Persistence.Providers;

/// Şifreli kimlik deposu (P1-01).
///
/// Şifreleme ASP.NET Data Protection ile: kendi şifrelememizi yazmak,
/// anahtar döndürme ve algoritma sürümleme gibi doğru yapması zor işleri
/// baştan üstlenmek olurdu. Data Protection ikisini de kendi hâllediyor —
/// şifreli metnin başındaki sürüm bilgisi sayesinde anahtar döndükten sonra
/// eski kayıtlar da okunmaya devam ediyor.
///
/// `Purpose` dizgesi kritik: aynı anahtar halkasıyla şifrelenmiş başka bir
/// veriyi (örneğin bir çerez) buraya yapıştırıp çözdürmek mümkün olmasın.
public sealed class CredentialStore : ICredentialStore
{
    /// Bu dizge DEĞİŞTİRİLEMEZ. Değişirse mevcut bütün kayıtlar çözülemez
    /// hâle gelir ve anahtarların yeniden girilmesi gerekir.
    private const string Purpose = "BytemountsAiStudio.Credentials.v1";

    private readonly StudioDbContext _db;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _time;

    public CredentialStore(
        StudioDbContext db,
        IDataProtectionProvider protection,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(protection);

        _db = db;
        _protector = protection.CreateProtector(Purpose);
        _time = timeProvider ?? TimeProvider.System;
    }

    /// Ortam değişkeni adını çözen işlev.
    ///
    /// Varsayılan olarak `config/providers.json` içindeki `key_env` ile aynı
    /// kuralı uyguluyor. Kataloğa bağımlılık kurmak yerine işlev olarak
    /// veriliyor: depo katmanının katalog dosyasını okuması gerekmiyor.
    public Func<string, string> EnvironmentVariableName { get; init; } =
        key => key.Replace('-', '_').ToUpperInvariant() + "_API_KEY";

    public async Task<Result<string>> GetAsync(
        string providerKey, Guid? channelId, CancellationToken cancellationToken,
        string account = Credentials.DefaultAccount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);

        // 1. Kanala özel kayıt — en dar kapsam kazanıyor.
        if (channelId is { } id)
        {
            var scoped = await FindAsync(providerKey, id, account, cancellationToken).ConfigureAwait(false);

            if (scoped is not null)
            {
                return await UnprotectAsync(scoped, cancellationToken).ConfigureAwait(false);
            }
        }

        // 2. Genel kayıt.
        var global = await FindAsync(providerKey, null, account, cancellationToken).ConfigureAwait(false);

        if (global is not null)
        {
            return await UnprotectAsync(global, cancellationToken).ConfigureAwait(false);
        }

        // 3. Ortam değişkeni — geliştirme kolaylığı, üretimde son çare.
        var name = EnvironmentVariableName(providerKey);
        var fromEnvironment = Environment.GetEnvironmentVariable(name);

        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            SecretRedactor.Register(fromEnvironment);
            return Result.Success(fromEnvironment);
        }

        // Kalıcı hata: yeniden denemek anahtarı var etmez. Mesaj ne
        // yapılacağını söylüyor, çünkü bu hatayı gören kişi genellikle
        // anahtarı koymayı unutmuş olan kişi.
        return Error.Permanent(
            "credential.missing",
            $"'{providerKey}' icin anahtar yok.",
            $"Ya `bmai credential set {providerKey}` calistirin ya da {name} ortam degiskenini tanimlayin.");
    }

    public async Task<Result> SetAsync(
        string providerKey, Guid? channelId, string secret, CancellationToken cancellationToken,
        string account = Credentials.DefaultAccount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);

        if (string.IsNullOrWhiteSpace(secret))
        {
            return Error.Permanent("credential.empty", "Bos anahtar saklanamaz.");
        }

        var existing = await FindAsync(providerKey, channelId, account, cancellationToken).ConfigureAwait(false);
        var now = _time.GetUtcNow();

        if (existing is null)
        {
            _db.Credentials.Add(new Credential
            {
                ChannelId = channelId,
                ProviderKey = providerKey,
                CipherText = _protector.Protect(secret),
                Masked = SecretRedactor.Mask4(secret),
                UpdatedAt = now,
            });
        }
        else
        {
            existing.CipherText = _protector.Protect(secret);
            existing.Masked = SecretRedactor.Mask4(secret);
            existing.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        SecretRedactor.Register(secret);

        return Result.Success();
    }

    public async Task<Result> DeleteAsync(
        string providerKey, Guid? channelId, CancellationToken cancellationToken,
        string account = Credentials.DefaultAccount)
    {
        var existing = await FindAsync(providerKey, channelId, account, cancellationToken).ConfigureAwait(false);

        if (existing is null)
        {
            return Error.Permanent("credential.missing", $"'{providerKey}' icin kayit yok.");
        }

        _db.Credentials.Remove(existing);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }

    /// Kayıtların üst bilgisi. Bu yolda şifre ÇÖZÜLMÜYOR — maskeli hâl
    /// kayıtla birlikte saklandığı için buna gerek yok.
    public async Task<IReadOnlyList<CredentialInfo>> ListAsync(
        Guid? channelId, CancellationToken cancellationToken)
    {
        var rows = await _db.Credentials
            .AsNoTracking()
            .Where(c => c.ChannelId == null || c.ChannelId == channelId)
            .OrderBy(c => c.ProviderKey)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(c => new CredentialInfo
        {
            ProviderKey = c.ProviderKey,
            Account = c.Account,
            ChannelId = c.ChannelId,
            Source = "db",
            Masked = c.Masked,
            UpdatedAt = c.UpdatedAt,
            LastUsedAt = c.LastUsedAt,
        })];
    }

    /// Senkron listeleme — DI fabrikasindan cagriliyor.
    ///
    /// ***`.Result` DEGIL, GERCEK SENKRON SORGU.*** Kayit bir DI
    /// fabrikasindan kuruluyor ve o fabrika senkron; asenkron bir
    /// cagriyi beklemek worker is parcacigini bloke eder ve klasik
    /// kilitlenmeyi uretir. EF'in kendi senkron sorgusu oyle bir risk
    /// tasimiyor -- bekleyen bir `Task` yok.
    ///
    /// Asenkron esi (`ListAsync`) duruyor ve CLI onu kullaniyor: orada
    /// senkron olmasi icin bir sebep yok.
    public IReadOnlyList<CredentialInfo> List(Guid? channelId)
        => [.. _db.Credentials
            .AsNoTracking()
            .Where(c => c.ChannelId == null || c.ChannelId == channelId)
            .OrderBy(c => c.ProviderKey)
            .ToList()
            .Select(c => new CredentialInfo
            {
                ProviderKey = c.ProviderKey,
                Account = c.Account,
                ChannelId = c.ChannelId,
                Source = "db",
                Masked = c.Masked,
                UpdatedAt = c.UpdatedAt,
                LastUsedAt = c.LastUsedAt,
            })];

    /// Senkron okuma — ayni gerekce.
    ///
    /// KAPSAM SIRASI ASENKRON ESIYLE AYNI: once kanala ozel, sonra
    /// genel. Iki ayri oncelik kurali olsaydi ayni anahtar iki yoldan
    /// farkli deger verirdi.
    public Result<string> Get(string providerKey, Guid? channelId, string account)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);

        var credential = channelId is { } id
            ? Find(providerKey, id, account) ?? Find(providerKey, null, account)
            : Find(providerKey, null, account);

        return credential is null
            ? Error.Permanent("credential.not_found",
                $"'{providerKey}' icin kimlik yok (hesap: {account}).")
            : Unprotect(credential);
    }

    private Credential? Find(string providerKey, Guid? channelId, string account)
        => _db.Credentials.AsNoTracking().FirstOrDefault(
            c => c.ProviderKey == providerKey && c.ChannelId == channelId && c.Account == account);

    /// Senkron cozme.
    ///
    /// SON KULLANIM DAMGASI YAZILMIYOR: bu yol kayit kurulurken
    /// KOSUYOR, anahtar gercekten kullanilirken degil. Damgayi burada
    /// atmak, hic cagrilmayan bir saglayicinin anahtarini
    /// "kullanildi" gostermekti -- ve o damga "hangi anahtar olu"
    /// sorusunun tek cevabi.
    private Result<string> Unprotect(Credential credential)
    {
        try
        {
            var plain = _protector.Unprotect(credential.CipherText);

            SecretRedactor.Register(plain);

            return Result.Success(plain);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            return Error.Permanent(
                "credential.undecryptable",
                $"'{credential.ProviderKey}' anahtari cozulemedi.",
                "Data Protection anahtar halkasi degismis olabilir; anahtari yeniden girin. " + ex.Message);
        }
    }

    private Task<Credential?> FindAsync(
        string providerKey, Guid? channelId, string account, CancellationToken cancellationToken)
        => _db.Credentials.FirstOrDefaultAsync(
            c => c.ProviderKey == providerKey && c.ChannelId == channelId && c.Account == account,
            cancellationToken);

    private async Task<Result<string>> UnprotectAsync(Credential credential, CancellationToken cancellationToken)
    {
        string plain;

        try
        {
            plain = _protector.Unprotect(credential.CipherText);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            // Anahtar halkası kaybolmuş ya da değişmiş. Yeniden denemek
            // düzeltmez; anahtarın yeniden girilmesi gerekiyor.
            return Error.Permanent(
                "credential.undecryptable",
                $"'{credential.ProviderKey}' anahtari cozulemedi.",
                "Data Protection anahtar halkasi degismis olabilir; anahtari yeniden girin. " + ex.Message);
        }

        // Anahtar okunduğu anda süzgece giriyor: bundan sonra bir istisna
        // mesajında ya da HTTP izinde geçerse maskelenecek.
        SecretRedactor.Register(plain);

        credential.LastUsedAt = _time.GetUtcNow();
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success(plain);
    }
}
