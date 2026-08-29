using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Core.Execution;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Persistence.Providers;

/// Kota havuzunun veritabanı tarafı (P4-04).
///
/// SEÇİM SAF (`QuotaPool`), REZERVASYON ATOMİK. İkisi ayrı çünkü seçim
/// bir POLİTİKA (en çok kalanı al) ve gerçek bir kota tüketilerek
/// öğrenilecek bir şey değil; rezervasyon ise bir YARIŞ ve tek bir
/// SQL ifadesiyle çözülmesi gerekiyor.
///
/// ***İKİ WORKER AYNI ANDA REZERVASYON İSTERSE.***
///
/// Önce okuyup sonra yazmak (`SELECT` sonra `UPDATE`) burada yanlış:
/// iki worker aynı sayıyı okur, ikisi de "yer var" görür ve ikisi de
/// yükler — kota aşılır ve ikinci yükleme API tarafında reddedilir.
/// Bu, Redis'te Lua betiğiyle çözülen sorunun aynısı (P4-03) ve
/// Postgres'teki karşılığı `ON CONFLICT ... DO UPDATE ... WHERE`:
/// artırma ve sınır kontrolü TEK ifadede, dolayısıyla bölünemez.
///
/// Sınır aşılırsa `DO UPDATE` hiçbir satır güncellemiyor ve
/// `RETURNING` boş dönüyor — "sığmadı" cevabı bu.
public sealed class QuotaPoolService(
    StudioDbContext db, TimeProvider? timeProvider = null, int? dailyLimit = null)
    : Contracts.Providers.IQuotaPool
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// ***GÜNLÜK SINIR KATALOGDAN OKUNUYOR, KODDAN DEĞİL.***
    ///
    /// `config/providers.json` içinde `quota_units_per_day: 10000`
    /// ZATEN yazılıydı ve hiçbir yerden tüketilmiyordu — düzeltilen
    /// `endpoint_env` ve `requests_per_minute` vakalarının aynısı.
    /// Sabit kalsaydı, Google kota artırımı verdiğinde (başvuruyla
    /// 10.000 → 1.000.000 mümkün) sistem yine günde altı videodan
    /// fazlasına izin vermez ve sebebi loglarda DOĞRU görünürdü:
    /// "kota tükendi".
    ///
    /// KATALOG OKUNAMAZSA KOD SABİTİNE DÜŞÜLÜYOR: dosyanın
    /// bulunamaması, havuzun tamamen durması için sebep değil.
    private readonly int _dailyLimit = dailyLimit ?? CatalogLimit();

    private static int CatalogLimit()
    {
        var catalog = Contracts.Providers.ProviderCatalog.Load(PipelineSelection.CatalogPath());

        return catalog.IsSuccess
               && catalog.Value.Limit("youtube", "quota_units_per_day") is { } units and > 0
            ? units
            : QuotaLedger.DailyUnits;
    }

    /// Havuzdaki hesapların bugünkü durumu.
    ///
    /// KANALA ÖZEL KAYITLAR VE GENEL KAYITLAR BİRLİKTE: bir kanalın
    /// kendi hesabı varsa o da havuza giriyor. Yalnızca birine bakmak,
    /// tanımlı bir hesabı görünmez kılardı.
    public async Task<IReadOnlyList<QuotaAccountState>> AccountsAsync(
        string providerKey, Guid? channelId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);

        var accounts = await db.Credentials.AsNoTracking()
            .Where(c => c.ProviderKey == providerKey)
            .Where(c => c.ChannelId == null || c.ChannelId == channelId)
            .Select(c => c.Account)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (accounts.Count == 0)
        {
            return [];
        }

        var day = QuotaPool.DayKey(_time.GetUtcNow());

        var reserved = await db.QuotaLedger.AsNoTracking()
            .Where(q => q.ProviderKey == providerKey && q.DayKey == day)
            .ToDictionaryAsync(q => q.Account, q => q.ReservedUnits, StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);

        return
        [
            .. accounts
                .OrderBy(a => a, StringComparer.Ordinal)
                .Select(a => new QuotaAccountState(
                    a, reserved.GetValueOrDefault(a), _dailyLimit)),
        ];
    }

    /// Havuzdan kota rezerve eder ve seçilen hesabı döner.
    public async Task<Result<PoolDecision>> ReserveAsync(
        string providerKey, Guid? channelId, int cost, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();

        if (cost > _dailyLimit)
        {
            // TEK GÜNE SIĞMAYAN İŞ, KOTA SORUNU DEĞİL YAPILANDIRMA
            // SORUNU: beklemek çözmüyor, yarın da sığmayacak.
            return Error.Permanent("quota.cost_exceeds_daily",
                $"{cost} birim, günlük havuzdan ({_dailyLimit}) büyük.");
        }

        var accounts = (await AccountsAsync(providerKey, channelId, cancellationToken)
            .ConfigureAwait(false)).ToList();

        var day = QuotaPool.DayKey(now);

        // DENEME SAYISI HESAP SAYISI KADAR.
        //
        // Yarışı kaybettiğimiz hesap "dolu" sayılıp bir sonrakine
        // geçiliyor. Sonsuz döngü yok: her tur bir hesabı eliyor.
        for (var attempt = 0; attempt < accounts.Count; attempt++)
        {
            var decision = QuotaPool.Select(accounts, cost, now);

            if (!decision.Granted)
            {
                return Result.Success(decision);
            }

            var written = await TryReserveAsync(
                providerKey, decision.Account!, day, cost, cancellationToken).ConfigureAwait(false);

            if (written is { } reservedNow)
            {
                return Result.Success(decision with
                {
                    // GERÇEKLEŞEN KALAN YAZILIYOR, TAHMİN EDİLEN DEĞİL:
                    // yarışta başka bir worker araya girmişse sayı
                    // bizim hesabımızdan farklı ve doğru olan onunki.
                    RemainingAfter = _dailyLimit - reservedNow,
                });
            }

            // YARIŞI KAYBETTİK: başka bir worker bu hesabı bizden önce
            // doldurdu. O hesabı dolu işaretleyip devam ediyoruz.
            var index = accounts.FindIndex(a => a.Account == decision.Account);

            if (index >= 0)
            {
                accounts[index] = accounts[index] with { SpentToday = _dailyLimit };
            }
        }

        return Result.Success(new PoolDecision(
            accounts.Count == 0 ? PoolOutcome.NoAccounts : PoolOutcome.Exhausted,
            null, cost, 0, 0,
            accounts.Count == 0
                ? "Havuzda hiç hesap yok; kota bitmedi, hesap tanımlanmamış."
                : $"{accounts.Count} hesabın hepsinde yer kalmadı (yarış sonrası)."));
    }

    /// Tek bir hesapta ATOMİK rezervasyon.
    ///
    /// Artırma ve sınır kontrolü TEK ifadede: `DO UPDATE`'in `WHERE`
    /// koşulu sağlanmazsa hiçbir satır güncellenmiyor ve `RETURNING`
    /// boş dönüyor. Okuyup-yazmak olsaydı iki worker aynı sayıyı okur
    /// ve ikisi de geçerdi.
    ///
    /// İLK EKLEMEDE `WHERE` ÇALIŞMIYOR (çakışma yok), o yüzden maliyet
    /// sınır kontrolü çağıran tarafta yapılıyor.
    private async Task<int?> TryReserveAsync(
        string providerKey, string account, string day, int cost, CancellationToken cancellationToken)
    {
        var id = Guid.CreateVersion7();
        var limit = _dailyLimit;
        var now = _time.GetUtcNow();

        var rows = await db.Database.SqlQuery<int>($"""
            INSERT INTO quota_ledger (id, provider_key, account, day_key, reserved_units, created_at)
            VALUES ({id}, {providerKey}, {account}, {day}, {cost}, {now})
            ON CONFLICT (provider_key, account, day_key)
            DO UPDATE SET reserved_units = quota_ledger.reserved_units + {cost}
            WHERE quota_ledger.reserved_units + {cost} <= {limit}
            RETURNING reserved_units AS "Value"
            """).ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows.Count > 0 ? rows[0] : null;
    }

    /// Havuzun bugün kaç yayın kaldırabileceği.
    ///
    /// Panoda görünüyor: "bugün kaç video yayınlanabilir" sorusunun
    /// cevabı, kota bittikten SONRA öğrenilecek bir şey olmamalı.
    public async Task<int> CapacityAsync(
        string providerKey, Guid? channelId, int costPerPublish, CancellationToken cancellationToken)
        => QuotaPool.Capacity(
            await AccountsAsync(providerKey, channelId, cancellationToken).ConfigureAwait(false),
            costPerPublish);
}
