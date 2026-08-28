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

    /// TEK BİR RUN'a harcanan.
    ///
    /// Video başına tavan (`max_cost_per_video`) buna bakıyor.
    /// Günlük bütçeden ayrı bir kavram: günlük bütçe gün sonunda
    /// sıfırlanan bir HIZ sınırı, video tavanı ise tek bir eserin
    /// ne kadara mal olabileceğine dair MUTLAK bir sınır.
    public Task<decimal> SpentOnRunAsync(Guid runId, CancellationToken cancellationToken)
        => db.ProviderCalls.AsNoTracking()
            .Where(c => c.RunId == runId)
            .SumAsync(c => c.Cost, cancellationToken);

    /// Bu AY harcanan (P2-03 global aylık pencere).
    ///
    /// Kanal filtresi YOK ve olmamalı: aylık limit sistemin tamamına
    /// ait. Kanal başına aylık limit tanımlamak, üç kanalın hepsinin
    /// kendi limitinde kalıp toplamda üç katını harcaması demekti.
    public async Task<decimal> SpentThisMonthAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var since = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);

        return await db.ProviderCalls.AsNoTracking()
            .Where(c => c.CreatedAt >= since)
            .SumAsync(c => c.Cost, cancellationToken)
            .ConfigureAwait(false);
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

        // ---- BÜTÇE KARARI TEK YERDE (P2-03) ----
        //
        // Buradaki kural elle yazılmıştı ve üç şeyi kaçırıyordu:
        // global aylık limit hiç bakılmıyordu, `action_on_exceed`
        // yok sayılıyordu ve en önemlisi YARIM KALMIŞ run ile YENİ
        // run aynı muameleyi görüyordu.
        //
        // Sonuncusu gerçek bir kusurdu: bütçe dolduğu anda üretilmekte
        // olan video ortasından kesiliyordu — senaryo yazılmış, ses
        // üretilmiş, görseller indirilmiş ve hiçbiri kullanılmayacak;
        // üstelik ertesi gün devam edilse o adımlar İKİNCİ KEZ para
        // harcayacaktı.
        //
        // BURASI HER ZAMAN "YARIM KALMIŞ": bu kapı bir node çalışırken,
        // sağlayıcı çağrısının hemen öncesinde soruluyor. Yeni run
        // kararı zamanlayıcıda (`RunPlanner`) veriliyor ve orası
        // `runAlreadyStarted: false` diyor. İkisi böyle tamamlanıyor:
        // bütçe dolunca yeni video BAŞLAMIYOR, başlamış olan BİTİYOR.
        //
        // Aşımın sınırı bu yüzden ölçülü: kanal başına aynı anda tek
        // run olduğu için (`RunPlanner.MaxConcurrentRunsPerChannel`)
        // en fazla bir videonun kalan maliyeti kadar aşılabiliyor.
        // ---- VİDEO BAŞINA TAVAN: AŞILAMAZ ----
        //
        // "Yarım kalanı bitir" kuralının olmazsa olmaz karşılığı bu.
        // Tavan olmasaydı, bir kez başlamış bir run günlük bütçeyi
        // sınırsız aşabilirdi: bir retry döngüsü ya da sürekli çağrı
        // yapan bir node, "zaten başlamıştı" gerekçesiyle ayın
        // tamamını harcayabilirdi.
        //
        // Günlük bütçeden FARKLI bir kavram: günlük bütçe gün sonunda
        // sıfırlanan bir hız sınırı, video tavanı tek bir eserin
        // maliyetine dair mutlak bir sınır. Bu yüzden
        // `action_on_exceed`'e de tabi değil.
        if (channel.MaxCostPerVideo is { } videoCap
            && ledger is CostLedger { RunId: { } runId } runLedger)
        {
            var spentOnRun = await runLedger.SpentOnRunAsync(runId, cancellationToken).ConfigureAwait(false);

            if (spentOnRun + estimatedCost > videoCap)
            {
                // KALICI DEĞİL, KAYNAK: run insana gidiyor ve o
                // isterse tavanı büyütüp devam ettiriyor. Kalıcı
                // saysaydık yarım video doğrudan çöpe giderdi.
                return Core.Errors.Error.Resource(
                    "budget.video_cap",
                    $"Video başına tavan aşılacaktı: {spentOnRun:0.####} + {estimatedCost:0.####} > {videoCap:0.####}",
                    TimeSpan.FromHours(1));
            }
        }

        var settings = Core.Execution.ChannelSettings.Parse(channel.SettingsJson);
        var windows = new List<Core.Execution.BudgetWindow>();

        var now = DateTimeOffset.UtcNow;

        if (channel.DailyBudget is { } dailyBudget)
        {
            var spent = await ledger.SpentTodayAsync(id, cancellationToken).ConfigureAwait(false);

            windows.Add(new Core.Execution.BudgetWindow(
                $"'{channel.Name}' günlük", spent, dailyBudget,
                Core.Execution.BudgetPolicy.UntilTomorrow(now)));
        }

        // GLOBAL AYLIK LİMİT önceden hiç bakılmıyordu: kanal
        // limitlerinin toplamı aylık limiti aşabiliyordu ve aşınca
        // kimse durdurmuyordu.
        var monthly = await db.Settings.AsNoTracking()
            .Where(s => s.Key == RunPlanner.MonthlyBudgetKey)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (decimal.TryParse(monthly, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var monthlyLimit)
            && ledger is CostLedger costLedger)
        {
            var spentMonth = await costLedger.SpentThisMonthAsync(cancellationToken).ConfigureAwait(false);

            windows.Add(new Core.Execution.BudgetWindow(
                "global aylık", spentMonth, monthlyLimit,
                Core.Execution.BudgetPolicy.UntilNextMonth(now)));
        }

        var verdict = Core.Execution.BudgetPolicy.Decide(
            windows, estimatedCost, runAlreadyStarted: true, settings.BudgetAction);

        return verdict.Allowed
            ? Core.Result.Success()
            : Core.Errors.Error.Resource("budget.exceeded", verdict.Reason, verdict.RetryAfter);
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
