using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Persistence.Entities;
using BytemountsAiStudio.Persistence.Providers;
using BytemountsAiStudio.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Persistence.Tests;

/// Kota havuzu — veritabanı tarafı (P4-04).
///
/// YouTube günlük 10.000 birim veriyor ve bir yükleme 1.600 birim:
/// PROJE BAŞINA GÜNDE ALTI VİDEO. Faz 4'ün hedefi günde 100 video ve
/// tek proje bunun on altıda birini bile karşılamıyor.
///
/// ***BU DOSYANIN ASIL SINADIĞI ŞEY YARIŞ.*** İki worker aynı anda
/// rezervasyon isterse, okuyup-yazan bir uygulama ikisine de "yer var"
/// der ve kota aşılır. P4-03'te Redis'te Lua betiğiyle çözülen sorunun
/// aynısı; Postgres'teki karşılığı tek ifadelik `ON CONFLICT`.
[Collection(DatabaseCollection.Name)]
public sealed class QuotaPoolServiceTests(DatabaseFixture fixture) : IAsyncLifetime
{
    private const string Provider = "youtube";

    public Task InitializeAsync() => CleanAsync();

    public Task DisposeAsync() => CleanAsync();

    private async Task CleanAsync()
    {
        if (!fixture.Available)
        {
            return;
        }

        await using var db = fixture.CreateContext();

        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM quota_ledger; DELETE FROM credentials WHERE provider_key = 'youtube'");
    }

    private void RequireDatabase()
        => Assert.True(fixture.Available, $"PostgreSQL erişilemiyor ({fixture.UnavailableReason}).");

    /// Sabit "şimdi": gün anahtarı Pasifik tarihinden türüyor ve
    /// gerçek saate bağlı bir test, gün dönümünde farklı sonuç
    /// verirdi.
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 20, 0, 0, TimeSpan.Zero);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static QuotaPoolService Service(StudioDbContext db, DateTimeOffset? now = null)
        => new(db, new FixedTime(now ?? Now));

    private static async Task AccountsAsync(StudioDbContext db, params string[] names)
    {
        foreach (var name in names)
        {
            db.Credentials.Add(new Credential
            {
                ProviderKey = Provider,
                Account = name,
                CipherText = "sifreli",
                Masked = "****1234",
            });
        }

        await db.SaveChangesAsync(CancellationToken.None);
    }

    /* ---- hesaplar ---- */

    /// HESAPLAR KİMLİK KAYITLARINDAN GELİYOR.
    ///
    /// Ayrı bir "havuz" tablosu olsaydı hesap eklemek iki yere yazmak
    /// olurdu ve biri unutulurdu — bu depoda defalarca ödenmiş hata.
    [Fact]
    public async Task Hesaplar_KimlikKayitlarindanGeliyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();
        await AccountsAsync(db, "proje-01", "proje-02", "proje-03");

        var accounts = await Service(db).AccountsAsync(Provider, null, CancellationToken.None);

        Assert.Equal(3, accounts.Count);
        Assert.All(accounts, a => Assert.Equal(QuotaLedger.DailyUnits, a.Remaining));
    }

    /// HESAP YOKSA BOŞ LİSTE — SIFIR KOTALI HESAP DEĞİL.
    [Fact]
    public async Task HesapYok_BosListe()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();

        Assert.Empty(await Service(db).AccountsAsync(Provider, null, CancellationToken.None));
    }

    /* ---- rezervasyon ---- */

    /// REZERVASYON DEFTERE YAZILIYOR VE KALAN DÜŞÜYOR.
    [Fact]
    public async Task Rezervasyon_DeftereYaziliyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();
        await AccountsAsync(db, "proje-01");

        var decision = await Service(db).ReserveAsync(
            Provider, null, QuotaLedger.UploadCost, CancellationToken.None);

        Assert.True(decision.IsSuccess);
        Assert.True(decision.Value.Granted);
        Assert.Equal("proje-01", decision.Value.Account);
        Assert.Equal(QuotaLedger.DailyUnits - QuotaLedger.UploadCost, decision.Value.RemainingAfter);

        var entry = await db.QuotaLedger.AsNoTracking().FirstAsync(CancellationToken.None);

        Assert.Equal(QuotaLedger.UploadCost, entry.ReservedUnits);

        // GÜN ANAHTARI PASİFİK TARİHİ: 29 Ağustos 20:00 UTC =
        // 29 Ağustos 13:00 Pasifik.
        Assert.Equal("2026-08-29", entry.DayKey);
    }

    /// DOLAN HESAPTAN SONRA DİĞERİNE GEÇİLİYOR.
    [Fact]
    public async Task DolanHesap_DigerineGeciliyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();
        await AccountsAsync(db, "proje-01", "proje-02");

        var service = Service(db);
        var used = new List<string>();

        // Altı yayın bir hesabı dolduruyor (6 x 1.600 = 9.600).
        for (var i = 0; i < 7; i++)
        {
            var decision = await service.ReserveAsync(
                Provider, null, QuotaLedger.UploadCost, CancellationToken.None);

            Assert.True(decision.Value.Granted, $"{i}. rezervasyon: {decision.Value.Reason}");
            used.Add(decision.Value.Account!);
        }

        // İKİ HESAP DA KULLANILDI: tek hesapta kalsaydı yedinci
        // rezervasyon reddedilirdi.
        Assert.Equal(2, used.Distinct(StringComparer.Ordinal).Count());
    }

    /// HAVUZ TÜKENİYOR VE SEBEBİ SAYIYLA YAZILIYOR.
    [Fact]
    public async Task HavuzTukendi_SebepSayili()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();
        await AccountsAsync(db, "proje-01");

        var service = Service(db);

        for (var i = 0; i < 6; i++)
        {
            await service.ReserveAsync(Provider, null, QuotaLedger.UploadCost, CancellationToken.None);
        }

        var decision = await service.ReserveAsync(
            Provider, null, QuotaLedger.UploadCost, CancellationToken.None);

        Assert.Equal(PoolOutcome.Exhausted, decision.Value.Outcome);
        Assert.Null(decision.Value.Account);
    }

    /// GÜNLÜK HAVUZDAN BÜYÜK İŞ, KOTA SORUNU DEĞİL.
    ///
    /// Beklemek çözmüyor: yarın da sığmayacak. Erteleme saymak,
    /// hiç koşamayacak bir işi her gün yeniden denemek olurdu.
    [Fact]
    public async Task GunlukHavuzdanBuyuk_KaliciHata()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();
        await AccountsAsync(db, "proje-01");

        var decision = await Service(db).ReserveAsync(
            Provider, null, QuotaLedger.DailyUnits + 1, CancellationToken.None);

        Assert.True(decision.IsFailure);
        Assert.Equal("quota.cost_exceeds_daily", decision.Error.Code);
    }

    /* ---- yarış ---- */

    /// ***EŞZAMANLI REZERVASYONLAR KOTAYI AŞMIYOR.***
    ///
    /// BU DOSYANIN EN ÖNEMLİ TESTİ ve tesadüf değil ki AYRI
    /// BAĞLANTILAR kullanıyor: tek bağlantı üzerinde geçen bir test,
    /// süreç içi bir kilitle de geçerdi ve dağıtıklığı hiç sınamazdı.
    /// İki bağlantı = iki worker.
    ///
    /// Okuyup-yazan bir uygulama burada 10 rezervasyonun HEPSİNİ
    /// kabul ederdi: on worker aynı sayıyı okur, hepsi "yer var"
    /// görür. Doğru cevap tam ALTI (6 x 1.600 = 9.600 ≤ 10.000).
    [Fact]
    public async Task EszamanliRezervasyon_KotayiAsmiyor()
    {
        RequireDatabase();

        await using (var setup = fixture.CreateContext())
        {
            await AccountsAsync(setup, "proje-01");
        }

        var granted = 0;

        var attempts = Enumerable.Range(0, 10).Select(async _ =>
        {
            await using var connection = fixture.CreateContext();

            var decision = await Service(connection).ReserveAsync(
                Provider, null, QuotaLedger.UploadCost, CancellationToken.None);

            if (decision.IsSuccess && decision.Value.Granted)
            {
                Interlocked.Increment(ref granted);
            }
        });

        await Task.WhenAll(attempts);

        Assert.Equal(6, granted);

        await using var check = fixture.CreateContext();
        var entry = await check.QuotaLedger.AsNoTracking().FirstAsync(CancellationToken.None);

        Assert.Equal(6 * QuotaLedger.UploadCost, entry.ReservedUnits);
        Assert.True(entry.ReservedUnits <= QuotaLedger.DailyUnits);
    }

    /* ---- gün sınırı ---- */

    /// FARKLI GÜN, FARKLI DEFTER SATIRI.
    ///
    /// Gün anahtarı olmasaydı kota hiç sıfırlanmaz ve fabrika ikinci
    /// günün sabahında "kota bitti" derdi.
    [Fact]
    public async Task FarkliGun_FarkliSatir()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();
        await AccountsAsync(db, "proje-01");

        await Service(db).ReserveAsync(Provider, null, QuotaLedger.UploadCost, CancellationToken.None);

        var tomorrow = Service(db, Now.AddDays(1));
        var decision = await tomorrow.ReserveAsync(
            Provider, null, QuotaLedger.UploadCost, CancellationToken.None);

        Assert.True(decision.Value.Granted);

        // YARININ HESABI SIFIRDAN BAŞLIYOR.
        Assert.Equal(QuotaLedger.DailyUnits - QuotaLedger.UploadCost, decision.Value.RemainingAfter);
        Assert.Equal(2, await db.QuotaLedger.CountAsync(CancellationToken.None));
    }

    /* ---- kapasite ---- */

    /// KAPASİTE PANODA GÖRÜNÜYOR.
    ///
    /// "Bugün kaç video yayınlanabilir" sorusunun cevabı, kota
    /// bittikten SONRA öğrenilecek bir şey olmamalı.
    [Fact]
    public async Task Kapasite_Hesaplaniyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();
        await AccountsAsync(db, "proje-01", "proje-02", "proje-03");

        var capacity = await Service(db).CapacityAsync(
            Provider, null, QuotaLedger.UploadCost, CancellationToken.None);

        Assert.Equal(18, capacity);
    }
}
