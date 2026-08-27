using BytemountsAiStudio.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Persistence.Providers;

/// Acil durdurmanın durumu (P2-04).
public sealed record KillSwitchState(bool Engaged, string? By, string? Reason, DateTimeOffset? Since);

/// Sistem geneli kontroller: acil durdurma ve kanal duraklatma
/// (P2-04, §13.3).
///
/// DURUM VERİTABANINDA. Önceki hâli statik bir alandı ve yalnızca o
/// süreci durduruyordu: filodaki diğer worker'lar hiçbir şey
/// görmüyor, yeniden başlatmada bayrak kayboluyordu. "Tek tıkla her
/// şey dursun" sözünün karşılığı yoktu.
///
/// KISA ÖMÜRLÜ ÖNBELLEK var ve gerekli: bayrak her sağlayıcı
/// çağrısında okunuyor ve her seferinde veritabanına gitmek, para
/// harcamayan bir kontrolü hattın en sık sorgusuna çevirirdi.
/// Karşılığında durdurma en fazla birkaç saniye gecikmeyle yayılıyor
/// — kabul edilebilir, çünkü o birkaç saniyede başlayan çağrı zaten
/// başlamış çağrılarla aynı durumda.
public sealed class SystemControl(StudioDbContext db, TimeProvider? timeProvider = null)
{
    public const string KillSwitchKey = "kill_switch";

    /// Önbellek ömrü.
    ///
    /// Beş saniye: durdurmayı basan kişi "hemen" beklemiyor, bir
    /// nefes bekliyor. Sıfır yapmak her çağrıda bir sorgu, uzun tutmak
    /// ise acil durdurmanın acil olmaması demek.
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    private static KillSwitchState? _cached;
    private static DateTimeOffset _cachedAt;
    private static readonly Lock Gate = new();

    /// Acil durdurma etkin mi.
    public async Task<KillSwitchState> KillSwitchAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();

        lock (Gate)
        {
            if (_cached is { } cached && now - _cachedAt < CacheLifetime)
            {
                return cached;
            }
        }

        var setting = await db.Settings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == KillSwitchKey, cancellationToken)
            .ConfigureAwait(false);

        var state = setting is null || !string.Equals(setting.Value, "on", StringComparison.OrdinalIgnoreCase)
            ? new KillSwitchState(false, null, null, null)
            : new KillSwitchState(true, setting.UpdatedBy, setting.Reason, setting.UpdatedAt);

        lock (Gate)
        {
            _cached = state;
            _cachedAt = now;
        }

        return state;
    }

    /// Acil durdurmayı açar ya da kapatır.
    public async Task SetKillSwitchAsync(
        bool engaged, string by, string? reason, CancellationToken cancellationToken)
    {
        var setting = await db.Settings
            .FirstOrDefaultAsync(s => s.Key == KillSwitchKey, cancellationToken)
            .ConfigureAwait(false);

        if (setting is null)
        {
            setting = new Setting { Key = KillSwitchKey, Value = "off" };
            db.Settings.Add(setting);
        }

        setting.Value = engaged ? "on" : "off";
        setting.UpdatedBy = by;
        setting.Reason = reason;
        setting.UpdatedAt = _time.GetUtcNow();

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // ÖNBELLEK HEMEN GEÇERSİZ: durdurmayı basan kişinin kendi
        // ekranında beş saniye "kapalı" görmesi, düğmenin çalışmadığı
        // izlenimi verirdi.
        Invalidate();
    }

    /// Bir kanal duraklatılmış mı.
    ///
    /// Kanal duraklatma ayrı bir kavram: acil durdurma HER ŞEYİ
    /// durduruyor, kanal duraklatma yalnızca o kanalın yeni işlerini.
    /// İkisini tek bayrağa indirmek, bir kanalı susturmak için bütün
    /// sistemi durdurmak demekti.
    public async Task<bool> IsChannelPausedAsync(Guid channelId, CancellationToken cancellationToken)
        => await db.Channels.AsNoTracking()
            .Where(c => c.Id == channelId)
            .Select(c => c.IsPaused)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task SetChannelPausedAsync(
        Guid channelId, bool paused, CancellationToken cancellationToken)
    {
        var channel = await db.Channels
            .FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken)
            .ConfigureAwait(false);

        if (channel is null)
        {
            return;
        }

        channel.IsPaused = paused;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// Önbelleği elle boşaltır.
    ///
    /// Testler için de gerekli: süreç geneli bir önbellek, iki testin
    /// birbirinin durumunu görmesi demek ve bu depoda tam olarak bu
    /// sınıftan hatalar CI'ı iki kez kırdı.
    public static void Invalidate()
    {
        lock (Gate)
        {
            _cached = null;
        }
    }
}
