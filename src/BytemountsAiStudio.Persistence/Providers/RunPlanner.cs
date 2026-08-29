using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Persistence.Providers;

/// Bir kanal için "şimdi yeni bir run başlatmalı mıyız" kararı.
public sealed record StartVerdict(
    bool ShouldStart,
    string Reason,
    TimeSpan RetryAfter,
    DateTimeOffset? PublishAt = null,
    string? Genre = null,
    RefillPlan? Refill = null,
    IReadOnlyList<string>? Warnings = null)
{
    public static StartVerdict No(string reason, TimeSpan retryAfter)
        => new(false, reason, retryAfter);
}

/// Zamanlayıcının karar merkezi (P2-01/02/03/05/12).
///
/// SAF POLİTİKALARI VERİTABANINA BAĞLAYAN KATMAN. Tempo, bütçe, kota,
/// havuz ve tür karışımı ayrı ayrı yazıldı ve test edildi; burada
/// hiçbir yeni kural yok, yalnızca sıraya konuyorlar. Kuralı burada
/// tekrar yazmak, aynı kararın iki yerde yaşaması ve zamanla
/// ayrışması demekti.
///
/// SIRA MALİYETE GÖRE: önce bir alan okuması gerektiren kontroller,
/// sonra sorgu gerektirenler. Bütçe sorgusunu, kanal zaten
/// duraklatılmışken çalıştırmanın anlamı yok.
///
/// HER "HAYIR" GEREKÇELİ ve bir bekleme süresi taşıyor. Gerekçesiz
/// bir hayır, sabah "neden hiç video üretilmemiş" sorusunun cevapsız
/// kalması demek — otonom bir sistemde en sık sorulan soru bu.
public sealed class RunPlanner(
    StudioDbContext db,
    SystemControl control,
    CostLedger ledger,
    TopicPool topics,
    TimeProvider? timeProvider = null)
{
    /// Bir kanalda aynı anda kaç run.
    ///
    /// VARSAYILAN BİR: paralel run'lar bütçeyi QC'nin sorunu
    /// yakalamasından daha hızlı harcıyor. Aynı kusuru taşıyan beş
    /// videoyu aynı anda üretmek yerine, birincisi QC'den geçsin diye
    /// beklemek — hedefli retry (P2-07) da ancak böyle bir şey
    /// öğretiyor.
    ///
    /// ***AMA SABİT DEĞİL, ÇÜNKÜ HEDEF SABİT DEĞİL.*** Günde 10 video
    /// hedefleyen bir kanal videoları yalnızca SERİ üretebiliyordu: bir
    /// run bitmeden ikincisi başlamıyordu ve `WaitingApproval`
    /// durumundaki bir run bile sayılıyordu — yani insan onayını
    /// bekleyen TEK video bütün kanalı durduruyordu. On altı çekirdekli
    /// bir makinede `BMAI_CONCURRENCY_RENDER=4` verilse bile kanal
    /// başına tek run olduğu için eşzamanlılık kullanılamıyordu.
    ///
    /// Bir kanalın günlük hedefi ile eşzamanlılığı ayrı ayarlar:
    /// hedefi büyütmek riski otomatik büyütmemeli.
    public const int DefaultMaxConcurrentRunsPerChannel = 1;

    public const string ConcurrencyVariable = "BMAI_RUNS_PER_CHANNEL";

    public static int MaxConcurrentRunsPerChannel
        => int.TryParse(Environment.GetEnvironmentVariable(ConcurrencyVariable),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0
                ? value
                : DefaultMaxConcurrentRunsPerChannel;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<StartVerdict> DecideAsync(Channel channel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var now = _time.GetUtcNow();

        // 1. ACİL DURDURMA: en ucuz ve en kesin kontrol.
        var kill = await control.KillSwitchAsync(cancellationToken).ConfigureAwait(false);

        if (kill.Engaged)
        {
            return StartVerdict.No(
                $"acil durdurma etkin ({kill.By}: {kill.Reason})", TimeSpan.FromMinutes(1));
        }

        if (channel.IsPaused)
        {
            return StartVerdict.No("kanal duraklatılmış", TimeSpan.FromMinutes(5));
        }

        var settings = ChannelSettings.Parse(channel.SettingsJson);

        if (settings.Pacing.DailyTarget <= 0)
        {
            return StartVerdict.No("kanalın günlük hedefi yok", TimeSpan.FromHours(1))
                with { Warnings = settings.Warnings };
        }

        // 2. EŞ ZAMANLI RUN SINIRI.
        var inFlight = await db.Runs.AsNoTracking()
            .CountAsync(r => r.ChannelId == channel.Id
                             && (r.State == RunState.Pending
                                 || r.State == RunState.Running
                                 || r.State == RunState.WaitingResource
                                 || r.State == RunState.WaitingApproval),
                cancellationToken)
            .ConfigureAwait(false);

        if (inFlight >= MaxConcurrentRunsPerChannel)
        {
            return StartVerdict.No(
                $"{inFlight} run zaten sürüyor", TimeSpan.FromMinutes(2))
                with { Warnings = settings.Warnings };
        }

        // 3. TEMPO: günlük hedef, aralık ve kota.
        //
        // YAYIN HENÜZ BAĞLI DEĞİL (P1-24/25 anahtar bekliyor), bu
        // yüzden tempo run BAŞLANGIÇLARINDAN ölçülüyor. Ölçüyü
        // uydurmak yerine mevcut sinyali kullanmak doğru: yayın
        // bağlanınca kaynak değişecek, kural değişmeyecek. Fark şu —
        // üretilip yayınlanmayan bir video da hedefe sayılıyor; bu,
        // hedefi aşmaktansa altında kalmayı tercih eden yön.
        var since = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);

        var todaysRuns = await db.Runs.AsNoTracking()
            .Where(r => r.ChannelId == channel.Id && r.CreatedAt >= since)
            .Select(r => r.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // ***KOTA GERÇEK DEFTERDEN OKUNUYOR, KOŞU SAYISINDAN TAHMİN
        // EDİLMİYOR.***
        //
        // Önceki hâli harcamayı `todaysRuns.Count * maliyet` diye
        // TAHMİN ediyordu ve üç yerden birden yanlıştı:
        //
        //   1. QC'de düşen bir koşu kota HARCAMIYOR ama sayılıyordu.
        //   2. Yeniden denenen bir yükleme iki kez harcıyor ama tek
        //      koşu görünüyordu.
        //   3. Gün sınırı UTC'ydi; YouTube kotayı PASİFİK gece
        //      yarısında sıfırlıyor. Günün yedi-sekiz saatinde yanlış
        //      güne bakılıyordu.
        //
        // Havuz aynı soruyu gerçek rezervasyon defterinden ve doğru gün
        // anahtarıyla cevaplıyor. Kapasite sıfırsa üretime hiç
        // başlanmıyor: videoyu üretip yükleyememek, harcanan her şeyi
        // ertesi güne taşımak ve o gün yeniden ödemek demek.
        var cost = QuotaLedger.CostOf(withThumbnail: true, withPlaylist: false);

        var capacity = await new QuotaPoolService(db, _time)
            .CapacityAsync("youtube", channel.Id, cost, cancellationToken)
            .ConfigureAwait(false);

        // KAPASİTE YAYIN SAYISI, BİRİM DEĞİL: `Reserve` birim
        // bekliyor, o yüzden geri çevriliyor. Havuzun toplamını
        // doğrudan vermek, parçalanmış bir havuzda olmayan bir
        // kapasiteyi raporlamak olurdu (`QuotaPool.Capacity`).
        var quota = QuotaLedger.Reserve(
            spentToday: 0, cost, now, dailyLimit: capacity * cost);

        var schedule = PublishSchedule.Decide(
            settings.Pacing,
            todaysRuns.Count,
            todaysRuns.Count == 0 ? null : todaysRuns.Max(),
            quota,
            now);

        if (!schedule.ShouldStart)
        {
            return StartVerdict.No(schedule.Reason, schedule.RetryAfter)
                with { Warnings = settings.Warnings };
        }

        // 4. BÜTÇE.
        //
        // `runAlreadyStarted: false` — bu YENİ bir run. Yarım kalmış
        // bir run'ın devamıyla aynı muameleyi görseydi, bütçe dolmuşken
        // yeni videolar başlatılmaya devam ederdi.
        var budget = await DecideBudgetAsync(channel, settings, cancellationToken).ConfigureAwait(false);

        if (!budget.Allowed)
        {
            return StartVerdict.No(budget.Reason, budget.RetryAfter)
                with { Warnings = settings.Warnings };
        }

        // 5. KONU HAVUZU.
        var pool = await topics
            .StatusAsync(channel.Id, channel.Language, settings.Pacing.DailyTarget, cancellationToken)
            .ConfigureAwait(false);

        var refill = TopicPoolPolicy.Decide(pool);

        if (TopicPoolPolicy.IsStarved(pool))
        {
            // AÇ HAVUZ BAŞLATMAYI ENGELLİYOR ama doldurma planı yine
            // de dönüyor: "başlatamadım" ile "ne yapmalı" aynı cevapta
            // olmalı, yoksa çağıran havuzu doldurmayı ayrıca sormak
            // zorunda kalır ve unutulan yer tam da burası olurdu.
            return new StartVerdict(false, "konu havuzu boş", TimeSpan.FromMinutes(10),
                Refill: refill, Warnings: settings.Warnings);
        }

        // 6. TÜR SEÇİMİ (sürekli mod).
        var genre = settings.Genres.Count == 0
            ? null
            : ContinuousStrategy.Next(settings.Genres, await ProducedByGenreAsync(
                channel.Id, since, cancellationToken).ConfigureAwait(false));

        return new StartVerdict(true, "başlatılabilir", TimeSpan.Zero,
            schedule.PublishAt, genre, refill, settings.Warnings);
    }

    private async Task<BudgetVerdict> DecideBudgetAsync(
        Channel channel, ChannelSettings settings, CancellationToken cancellationToken)
    {
        var estimate = channel.MaxCostPerVideo ?? BudgetPolicy.EstimateRun(
            sentenceCount: 12, paidTts: false, paidLlm: false, paidImages: false);

        var windows = new List<BudgetWindow>();

        if (channel.DailyBudget is { } daily)
        {
            var spentToday = await ledger.SpentTodayAsync(channel.Id, cancellationToken).ConfigureAwait(false);

            windows.Add(new BudgetWindow("kanal günlük", spentToday, daily,
                BudgetPolicy.UntilTomorrow(_time.GetUtcNow())));
        }

        var monthlyLimit = await MonthlyLimitAsync(cancellationToken).ConfigureAwait(false);

        if (monthlyLimit is { } limit)
        {
            var spentMonth = await ledger.SpentThisMonthAsync(cancellationToken).ConfigureAwait(false);

            windows.Add(new BudgetWindow("global aylık", spentMonth, limit,
                BudgetPolicy.UntilNextMonth(_time.GetUtcNow())));
        }

        return BudgetPolicy.Decide(windows, estimate, runAlreadyStarted: false, settings.BudgetAction);
    }

    /// Global aylık limit `settings` tablosundan okunuyor.
    ///
    /// Kanal tablosunda değil, çünkü tek bir kanala ait değil. Yoksa
    /// limit YOK demek — sıfır demek değil; sıfır saysaydık limit
    /// tanımlamamış bir kurulum hiç video üretemezdi.
    public const string MonthlyBudgetKey = "monthly_budget";

    private async Task<decimal?> MonthlyLimitAsync(CancellationToken cancellationToken)
    {
        var setting = await db.Settings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == MonthlyBudgetKey, cancellationToken)
            .ConfigureAwait(false);

        return decimal.TryParse(setting?.Value, System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    /// Bugün hangi türden kaç video üretildi.
    ///
    /// Tür run bağlamında duruyor; ayrı bir kolon açmak, tür kavramı
    /// değiştiğinde şema göçü gerektirirdi.
    private async Task<Dictionary<string, int>> ProducedByGenreAsync(
        Guid channelId, DateTimeOffset since, CancellationToken cancellationToken)
    {
        var contexts = await db.Runs.AsNoTracking()
            .Where(r => r.ChannelId == channelId && r.CreatedAt >= since)
            .Select(r => r.ContextJson)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var json in contexts)
        {
            var genre = GenreOf(json);

            if (genre is null)
            {
                continue;
            }

            counts[genre] = counts.GetValueOrDefault(genre) + 1;
        }

        return counts;
    }

    internal static string? GenreOf(string? contextJson)
    {
        if (string.IsNullOrWhiteSpace(contextJson))
        {
            return null;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(contextJson);

            return document.RootElement.TryGetProperty("genre", out var value)
                   && value.ValueKind == System.Text.Json.JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            // Okunamayan bir bağlam tür sayımını bozmamalı: o run
            // sayılmıyor, diğerleri sayılmaya devam ediyor.
            return null;
        }
    }
}
