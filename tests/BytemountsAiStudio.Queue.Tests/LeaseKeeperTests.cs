using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Persistence;
using BytemountsAiStudio.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Queue.Tests;

/// Kiralamayı uzatan atışın testleri (§8.1).
///
/// ***BU DOSYANIN VAR OLMA SEBEBİ:*** `JobQueue.HeartbeatAsync` yazılmış,
/// testlenmiş ve **hiçbir yerden çağrılmıyordu**. Kiralama süreleri iş
/// sınıfına göre ayarlanmıştı ve `ReclaimExpiredAsync` süresi dolanı geri
/// alıyordu — ama uzatan kimse yoktu. 60 dakikayı aşan bir render hâlâ
/// koşarken geri alınıyor ve ikinci bir worker aynı işi paralel
/// başlatıyordu.
[Collection(DatabaseCollection.Name)]
public sealed class LeaseKeeperTests(DatabaseFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (!fixture.Available)
        {
            return;
        }

        await using var db = fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM jobs");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private void RequireDatabase()
        => Assert.True(fixture.Available, $"PostgreSQL erişilemiyor ({fixture.UnavailableReason}).");

    private static EnqueueRequest Request()
        => new() { Queue = QueueClass.Render, Priority = 0, PayloadJson = """{"x":1}""" };

    /* ---- aralık ---- */

    /// ***ARALIK KİRALAMANIN ÜÇTE BİRİ, YARISI DEĞİL.***
    ///
    /// Yarısı da çalışırdı ama TEK BİR kaçırılan atışta iş geri
    /// alınırdı: ağ yavaşladığında ya da veritabanı bir saniye
    /// takıldığında sistem işi kaybederdi. Üçte birde iki atış üst üste
    /// kaçırılmadan kayıp olmuyor.
    [Fact]
    public void Aralik_KiralamaninUcteBiri()
        => Assert.Equal(TimeSpan.FromMinutes(20), LeaseKeeper.IntervalFor(TimeSpan.FromMinutes(60)));

    /// KISA KİRALAMADA BİLE BEŞ SANİYENİN ALTINA İNMİYOR.
    ///
    /// Saniyede bir `UPDATE`, kısa işlerde kazandırdığından fazlasını
    /// götürürdü: on worker'lık bir filo veritabanına yalnızca atış
    /// için saniyede on yazma yapardı.
    [Fact]
    public void KisaKiralama_BesSaniyeTaban()
        => Assert.Equal(TimeSpan.FromSeconds(5), LeaseKeeper.IntervalFor(TimeSpan.FromSeconds(6)));

    /* ---- uzatma ---- */

    /// ***ATIŞ KİRALAMAYI GERÇEKTEN UZATIYOR.***
    ///
    /// BU DOSYANIN EN ÖNEMLİ TESTİ. Bir saniyelik kiralama veriliyor
    /// (atış aralığı beş saniyeye çıkmasın diye taban zaten devrede,
    /// o yüzden kısa bir bekleme yeterli) ve `lease_expires_at` alanının
    /// İLERİ GİTTİĞİ ölçülüyor. Uzatılmasaydı sabit kalırdı.
    [Fact]
    public async Task Atis_KiralamayiUzatiyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();
        var queue = new JobQueue(db);

        var job = await queue.EnqueueAsync(Request(), CancellationToken.None);
        var lease = await queue.LeaseAsync(
            QueueClass.Render, "worker-1", TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.NotNull(lease);
        Assert.Equal(job, lease.Id);

        var before = lease.LeaseExpiresAt;

        // ATIŞ ARALIĞI TABAN BEŞ SANİYE: yedi saniye beklemek en az bir
        // atış demek. Sabit bir sayı yerine `IntervalFor` üzerinden
        // türetiliyor ki taban değiştiğinde test de değişsin.
        var interval = LeaseKeeper.IntervalFor(TimeSpan.FromMinutes(1));

        await using (LeaseKeeper.Start(
            fixture.ConnectionString, job, "worker-1", TimeSpan.FromMinutes(1),
            CancellationToken.None))
        {
            await Task.Delay(interval + TimeSpan.FromSeconds(2));
        }

        await using var check = fixture.CreateContext();

        var after = await check.Jobs.AsNoTracking()
            .Where(j => j.Id == job)
            .Select(j => j.LeaseExpiresAt)
            .FirstAsync();

        Assert.NotNull(after);
        Assert.True(after > before, $"Kiralama uzatılmadı: {before} -> {after}");
    }

    /* ---- kayıp ---- */

    /// ***KİRALAMA BAŞKASINA GEÇERSE İŞ İPTAL EDİLİYOR.***
    ///
    /// Kaybedilmiş bir kiralamayla devam etmek, atışın önlemeye
    /// çalıştığı şeyin ta kendisi: iki worker aynı işi koşuyor.
    [Fact]
    public async Task KiralamaKaybi_BelirtecIptalEdiliyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();
        var queue = new JobQueue(db);

        var job = await queue.EnqueueAsync(Request(), CancellationToken.None);

        await queue.LeaseAsync(
            QueueClass.Render, "worker-1", TimeSpan.FromMinutes(1), CancellationToken.None);

        // İŞ BAŞKA BİR WORKER'A GEÇİYOR: gerçek hayatta bunu
        // `ReclaimExpiredAsync` yapıyor; burada doğrudan yazılıyor ki
        // test kiralama süresinin dolmasını beklemek zorunda kalmasın.
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE jobs SET leased_by = 'worker-2' WHERE id = {0}", job);

        var messages = new List<string>();

        await using var keeper = LeaseKeeper.Start(
            fixture.ConnectionString, job, "worker-1", TimeSpan.FromMinutes(1),
            CancellationToken.None, onLost: messages.Add);

        var interval = LeaseKeeper.IntervalFor(TimeSpan.FromMinutes(1));

        await Task.Delay(interval + TimeSpan.FromSeconds(2));

        Assert.True(keeper.LeaseLost, "Kiralama kaybı fark edilmedi.");
        Assert.True(keeper.Token.IsCancellationRequested, "Belirteç iptal edilmedi.");

        // SEBEP YAZILIYOR: "node neden iptal oldu" sorusunun cevabı
        // yalnızca iptal edilmiş bir belirteç olmamalı.
        Assert.NotEmpty(messages);
    }

    /* ---- bağlantısız kurulum ---- */

    /// BAĞLANTI DİZGESİ YOKSA ATIŞ DA YOK — AMA BELİRTEÇ ÇALIŞIYOR.
    ///
    /// Çağıran taraf iki hâli ayrı ayrı ele almak zorunda kalmasın:
    /// atışsız kurulumda da `Token` geçerli ve iptal edilebilir.
    [Fact]
    public async Task BaglantiYok_BelirtecCalisiyor()
    {
        using var cts = new CancellationTokenSource();

        await using var keeper = LeaseKeeper.Start(
            null, Guid.CreateVersion7(), "worker-1", TimeSpan.FromMinutes(1), cts.Token);

        Assert.False(keeper.Token.IsCancellationRequested);
        Assert.False(keeper.LeaseLost);

        await cts.CancelAsync();

        Assert.True(keeper.Token.IsCancellationRequested);
    }
}
