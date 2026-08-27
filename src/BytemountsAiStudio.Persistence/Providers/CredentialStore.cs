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
        string providerKey, Guid? channelId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);

        // 1. Kanala özel kayıt — en dar kapsam kazanıyor.
        if (channelId is { } id)
        {
            var scoped = await FindAsync(providerKey, id, cancellationToken).ConfigureAwait(false);

            if (scoped is not null)
            {
                return await UnprotectAsync(scoped, cancellationToken).ConfigureAwait(false);
            }
        }

        // 2. Genel kayıt.
        var global = await FindAsync(providerKey, null, cancellationToken).ConfigureAwait(false);

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
        string providerKey, Guid? channelId, string secret, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);

        if (string.IsNullOrWhiteSpace(secret))
        {
            return Error.Permanent("credential.empty", "Bos anahtar saklanamaz.");
        }

        var existing = await FindAsync(providerKey, channelId, cancellationToken).ConfigureAwait(false);
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
        string providerKey, Guid? channelId, CancellationToken cancellationToken)
    {
        var existing = await FindAsync(providerKey, channelId, cancellationToken).ConfigureAwait(false);

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
            ChannelId = c.ChannelId,
            Source = "db",
            Masked = c.Masked,
            UpdatedAt = c.UpdatedAt,
            LastUsedAt = c.LastUsedAt,
        })];
    }

    private Task<Credential?> FindAsync(string providerKey, Guid? channelId, CancellationToken cancellationToken)
        => _db.Credentials.FirstOrDefaultAsync(
            c => c.ProviderKey == providerKey && c.ChannelId == channelId, cancellationToken);

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
