using System.Text.Json;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Persistence.Providers;

/// Maliyet defteri (mimari §13).
///
/// Her para harcayan çağrı buraya yazılır — BAŞARISIZ olanlar dahil.
/// Başarısız çağrı da sağlayıcı tarafında ücretlendirilmiş olabilir; onu
/// saymamak defterin gerçeği söylememesi demek.
public sealed class CostLedger(StudioDbContext db, TimeProvider? timeProvider = null) : ICostLedger
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public Guid? RunId { get; set; }

    public string? NodeId { get; set; }

    public Guid? ChannelId { get; set; }

    public async Task RecordAsync(ProviderCallRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        db.ProviderCalls.Add(new ProviderCall
        {
            RunId = record.RunId ?? RunId,
            NodeId = record.NodeId ?? NodeId,
            ProviderKey = record.ProviderKey,
            Operation = record.Operation,
            UnitsJson = JsonSerializer.Serialize(record.Units),
            Cost = record.Cost,
            LatencyMs = record.LatencyMs,
            Succeeded = record.Succeeded,
        });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// Bugünkü harcama. Bütçe kapısı buna bakıyor.
    ///
    /// Kanal filtresi run üzerinden yapılıyor: `provider_calls` doğrudan
    /// kanala bağlı değil, çünkü bir çağrının kanalı ancak bir run'a aitse
    /// bellidir — kanal dışı çağrılar (bakım, deneme) da var.
    public async Task<decimal> SpentTodayAsync(Guid? channelId, CancellationToken cancellationToken)
    {
        // Gün başlangıcı AÇIKÇA UTC. `.Date` kullanmak Kind=Unspecified bir
        // DateTime üretiyor, karşılaştırmada yerel saat dilimi uygulanıyor ve
        // Npgsql "yalnızca UTC offset destekleniyor" diyerek patlıyor.
        // Ayrıca "bugün" tanımı sunucunun saat dilimine göre kaymamalı.
        var now = _time.GetUtcNow();
        var since = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);

        var query = db.ProviderCalls.AsNoTracking().Where(c => c.CreatedAt >= since);

        if (channelId is { } id)
        {
            var runIds = db.Runs.AsNoTracking().Where(r => r.ChannelId == id).Select(r => r.Id);
            query = query.Where(c => c.RunId != null && runIds.Contains(c.RunId.Value));
        }

        return await query.SumAsync(c => c.Cost, cancellationToken).ConfigureAwait(false);
    }
}

/// Bütçe kapısı: kanal günlük ve global aylık limitler + kill-switch.
///
/// §13.2: sistemin parayı kendi başına harcamasını durduran son nokta.
/// Limit aşımında dönen hata KAYNAK sınıfında — iş başarısız olmaz,
/// ertelenir. Kalıcı hata olsaydı bütçe dolduğu gün tüm run'lar ölürdü.
public sealed class BudgetGate(StudioDbContext db, ICostLedger ledger, SystemControl? control = null) : IBudgetGate
{
    private readonly SystemControl _control = control ?? new SystemControl(db);

    public async Task<Core.Result> AuthorizeAsync(
        Guid? channelId, decimal estimatedCost, CancellationToken cancellationToken)
    {
        // ACİL DURDURMA VERİTABANINDAN okunuyor (P2-04).
        //
        // Önceki hâli statik bir alandı ve yalnızca o süreci
        // durduruyordu: filodaki diğer worker'lar hiçbir şey görmüyor,
        // yeniden başlatmada bayrak kayboluyordu.
        var kill = await _control.KillSwitchAsync(cancellationToken).ConfigureAwait(false);

        if (kill.Engaged)
        {
            return Core.Errors.Error.Resource(
                "budget.kill_switch",
                $"Acil durdurma etkin ({kill.By ?? "bilinmiyor"}): {kill.Reason ?? "gerekçe yok"}",
                TimeSpan.FromHours(1));
        }

        if (channelId is not { } id)
        {
            return Core.Result.Success();
        }

        var channel = await db.Channels.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (channel is null)
        {
            return Core.Result.Success();
        }

        // KANAL DURAKLATMA acil durdurmadan AYRI: biri her şeyi,
        // diğeri yalnızca o kanalın yeni işlerini durduruyor. Tek
        // bayrağa indirmek, bir kanalı susturmak için bütün sistemi
        // durdurmak demekti.
        if (channel.IsPaused)
        {
            return Core.Errors.Error.Resource(
                "budget.channel_paused",
                $"'{channel.Name}' kanalı duraklatılmış.",
                TimeSpan.FromHours(1));
        }

        if (channel.DailyBudget is not { } dailyBudget)
        {
            return Core.Result.Success();
        }

        var spent = await ledger.SpentTodayAsync(id, cancellationToken).ConfigureAwait(false);

        if (spent + estimatedCost <= dailyBudget)
        {
            return Core.Result.Success();
        }

        // Ertesi günün başına kadar ertele: bütçe gün başında sıfırlanıyor.
        var untilMidnight = TimeSpan.FromHours(24) - DateTimeOffset.UtcNow.TimeOfDay;

        return Core.Errors.Error.Resource(
            "budget.daily_exceeded",
            $"'{channel.Name}' kanalının günlük bütçesi aşılacaktı: " +
            $"{spent:0.####} + {estimatedCost:0.####} > {dailyBudget:0.####}",
            untilMidnight);
    }
}

/// Sağlayıcı sonuç önbelleği — idempotency'nin deposu.
///
/// `node_executions.idempotency_key` üzerinden değil ayrı bir tablo olmadan,
/// mevcut `provider_calls` da değil: bu bir ÖNBELLEK, defter değil. Faz 0'da
/// süreç içi; Faz 4'te Redis'e taşınacak (arayüz aynı kalır).
public sealed class InMemoryResultCache : IProviderResultCache
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _entries =
        new(StringComparer.Ordinal);

    public int Count => _entries.Count;

    public Task<string?> TryGetAsync(string idempotencyKey, string operation, CancellationToken cancellationToken)
        => Task.FromResult(_entries.GetValueOrDefault(Key(idempotencyKey, operation)));

    public Task SetAsync(string idempotencyKey, string operation, string payload, CancellationToken cancellationToken)
    {
        _entries[Key(idempotencyKey, operation)] = payload;
        return Task.CompletedTask;
    }

    /// İşlem adı anahtara dahil: aynı node farklı sağlayıcıları çağırabilir
    /// ve ikisinin sonucu birbirine karışmamalı.
    private static string Key(string idempotencyKey, string operation)
        => $"{idempotencyKey}:{operation}";
}
