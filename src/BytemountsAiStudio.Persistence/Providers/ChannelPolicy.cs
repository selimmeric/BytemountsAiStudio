using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core.Execution;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Persistence.Providers;

/// Kanal politikasını veritabanından okur.
///
/// Node'lar veritabanına doğrudan bağlanmıyor (§6.1); bu sınıf o
/// köprü. Ayrı olmasının pratik faydası: onay kapısı testleri
/// veritabanı olmadan koşuyor.
public sealed class ChannelPolicy(StudioDbContext db) : IChannelPolicy
{
    public async Task<ChannelMode?> ModeAsync(Guid channelId, CancellationToken cancellationToken)
        => await db.Channels.AsNoTracking()
            .Where(c => c.Id == channelId)
            .Select(c => (ChannelMode?)c.Mode)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
}
