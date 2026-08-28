using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Persistence;
using BytemountsAiStudio.Persistence.Entities;
using BytemountsAiStudio.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Queue.Tests;

/// Kanal adaletinin kuyruğa bağlanması (P2-05).
///
/// KABUL KRİTERİ: **3 kanallı yükte hiçbiri aç kalmıyor.** Saf karar
/// ayrıca sınandı; burada sınanan şey kuyruğun o kararı gerçekten
/// KULLANIP kullanmadığı. Kullanmasaydı: yirmi videoluk bir kampanya
/// başlatan kanal, günde bir video üreten kanalın işini saatlerce
/// bekletir ve ikincisi hiçbir zaman "hata" vermez — sadece hiç sıra
/// alamazdı.
[Collection(DatabaseCollection.Name)]
public sealed class FairLeaseTests(DatabaseFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    public async Task InitializeAsync()
    {
        if (!fixture.Available)
        {
            return;
        }

        await using var db = fixture.CreateContext();

        await db.Database.ExecuteSqlRawAsync("DELETE FROM jobs");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM channels");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private void RequireDatabase()
        => Assert.True(fixture.Available, $"PostgreSQL erişilemiyor ({fixture.UnavailableReason}).");

    private static async Task<Channel> ChannelAsync(StudioDbContext db, string name)
    {
        var channel = new Channel { Name = name, Language = "tr-TR" };

        db.Channels.Add(channel);
        await db.SaveChangesAsync(CancellationToken.None);

        return channel;
    }

    private static async Task EnqueueAsync(JobQueue queue, Guid? channelId, int count, int priority = 0)
    {
        for (var i = 0; i < count; i++)
        {
            await queue.EnqueueAsync(new EnqueueRequest
            {
                Queue = QueueClass.Llm,
                ChannelId = channelId,
                Priority = priority,
            }, CancellationToken.None);
        }
    }

    /// KABUL KRİTERİ: bir kanalın yığını diğerlerini aç bırakmıyor.
    ///
    /// Adaletsiz sırada (öncelik → yaş) ilk üç kiralamanın ÜÇÜ DE
    /// yığını olan kanaldan gelirdi; onun yirmi işi de daha eski.
    [Fact]
    public async Task BuyukYigin_DigerKanallariAcBirakmiyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var time = new FakeTimeProvider(Now);
        var queue = new JobQueue(db, time);

        var yigin = await ChannelAsync(db, "Kampanya");
        var kucuk1 = await ChannelAsync(db, "Kucuk 1");
        var kucuk2 = await ChannelAsync(db, "Kucuk 2");

        // Yığın ÖNCE kuyruğa giriyor: işleri daha eski.
        await EnqueueAsync(queue, yigin.Id, 20);
        await EnqueueAsync(queue, kucuk1.Id, 1);
        await EnqueueAsync(queue, kucuk2.Id, 1);

        var served = new List<Guid?>();

        for (var i = 0; i < 3; i++)
        {
            var job = await queue.LeaseAsync(QueueClass.Llm, $"w{i}", TimeSpan.FromMinutes(5));

            Assert.NotNull(job);

            var channelId = await db.Jobs.AsNoTracking()
                .Where(j => j.Id == job.Id).Select(j => j.ChannelId)
                .SingleAsync(CancellationToken.None);

            served.Add(channelId);
        }

        // ÜÇ KANALIN ÜÇÜ DE sıra aldı.
        Assert.Contains(kucuk1.Id, served);
        Assert.Contains(kucuk2.Id, served);
        Assert.Equal(3, served.Distinct().Count());
    }

    /// KANAL BAŞINA TAVAN: bir kanal bütün worker'ları kaplayamıyor.
    [Fact]
    public async Task TekKanal_TumWorkerlariKaplayamiyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var queue = new JobQueue(db, new FakeTimeProvider(Now));

        var yigin = await ChannelAsync(db, "Kampanya");
        var digeri = await ChannelAsync(db, "Digeri");

        await EnqueueAsync(queue, yigin.Id, 10);
        await EnqueueAsync(queue, digeri.Id, 10);

        var leased = new List<Guid?>();

        for (var i = 0; i < 4; i++)
        {
            var job = await queue.LeaseAsync(QueueClass.Llm, $"w{i}", TimeSpan.FromMinutes(5));

            Assert.NotNull(job);

            leased.Add(await db.Jobs.AsNoTracking()
                .Where(j => j.Id == job.Id).Select(j => j.ChannelId)
                .SingleAsync(CancellationToken.None));
        }

        // Dört kiralamada hiçbir kanal tavanı (2) aşmıyor.
        Assert.Equal(2, leased.Count(c => c == yigin.Id));
        Assert.Equal(2, leased.Count(c => c == digeri.Id));
    }

    /// ADALET CANLILIĞI ENGELLEMİYOR.
    ///
    /// Kanala bağlı olmayan işler (bakım, deneme) hiçbir kanalın
    /// payına girmiyor. İkinci deneme olmasaydı worker eli boş
    /// dönerdi: adalet uğruna hiç iş yapmamak, adaletsizlikten kötü.
    [Fact]
    public async Task KanalsizIs_YineDeKiralaniyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var queue = new JobQueue(db, new FakeTimeProvider(Now));

        var a = await ChannelAsync(db, "A");
        var b = await ChannelAsync(db, "B");

        // İki kanalın işleri BİTMİŞ; yalnızca kanalsız iş kaldı.
        await EnqueueAsync(queue, a.Id, 0);
        await EnqueueAsync(queue, b.Id, 0);
        await EnqueueAsync(queue, null, 1);

        var job = await queue.LeaseAsync(QueueClass.Llm, "w", TimeSpan.FromMinutes(5));

        Assert.NotNull(job);
    }

    /// Tek kanal varsa adalet sorusu sorulmuyor ama iş yine de
    /// çıkıyor: tek kanallı bir kurulum bozulmamalı.
    [Fact]
    public async Task TekKanal_NormalCalisiyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var queue = new JobQueue(db, new FakeTimeProvider(Now));
        var channel = await ChannelAsync(db, "Tek");

        await EnqueueAsync(queue, channel.Id, 3);

        Assert.NotNull(await queue.LeaseAsync(QueueClass.Llm, "w", TimeSpan.FromMinutes(5)));
    }

    /// GEÇMİŞ PAY GERÇEKTEN SAYILIYOR.
    ///
    /// Bu ölçüt olmadan, işler hızlı bittiğinde koşan sayısı hep sıfır
    /// kalıyor, ilk ölçüt hiçbir şey ayırt etmiyor ve seçim kimlik
    /// sırasına düşüyor — en küçük kimlikli kanal her turu kazanıp
    /// diğerini aç bırakıyor. Burada az önce sıra almış kanal
    /// bekliyor.
    [Fact]
    public async Task YakinGecmisteSiraAlan_SonrayaKaliyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var time = new FakeTimeProvider(Now);
        var queue = new JobQueue(db, time);

        var doymus = await ChannelAsync(db, "Doymus");
        var ac = await ChannelAsync(db, "Ac");

        await EnqueueAsync(queue, doymus.Id, 1);
        await EnqueueAsync(queue, ac.Id, 1);

        // "Doymuş" kanal az önce beş iş bitirdi.
        //
        // Geçmiş DOĞRUDAN yazılıyor, kiralama döngüsüyle üretilmiyor:
        // ilk yazımda öyleydi ve test kırıldı, çünkü döngü içindeki
        // kiralama her zaman az önce eklenen işi almıyor — adalet
        // bazen diğer kanalı seçiyor ve o kanalın tek işi kiralanmış
        // hâlde kalıyordu. Kurulum, sınanan şeyin kendisine bağlı
        // olmamalı.
        for (var i = 0; i < 5; i++)
        {
            db.Jobs.Add(new Job
            {
                Queue = QueueClass.Llm,
                ChannelId = doymus.Id,
                State = JobState.Succeeded,
                CompletedAt = Now.AddMinutes(-1),
            });
        }

        await db.SaveChangesAsync(CancellationToken.None);

        // Kuyrukta ikisinin de birer işi var; sıra aç olanın.
        var job = await queue.LeaseAsync(QueueClass.Llm, "w", TimeSpan.FromMinutes(5));

        Assert.NotNull(job);

        var channelId = await db.Jobs.AsNoTracking()
            .Where(j => j.Id == job.Id).Select(j => j.ChannelId)
            .SingleAsync(CancellationToken.None);

        Assert.Equal(ac.Id, channelId);
    }

    /// Duraklatılmış kanalın işi hiç kiralanmıyor — adalet seçimi
    /// onu seçse bile.
    [Fact]
    public async Task DuraklatilmisKanal_SiraAlmiyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var queue = new JobQueue(db, new FakeTimeProvider(Now));

        var duraklatilmis = await ChannelAsync(db, "Duraklatilmis");
        duraklatilmis.IsPaused = true;

        var calisan = await ChannelAsync(db, "Calisan");
        await db.SaveChangesAsync(CancellationToken.None);

        await EnqueueAsync(queue, duraklatilmis.Id, 5);
        await EnqueueAsync(queue, calisan.Id, 1);

        var job = await queue.LeaseAsync(QueueClass.Llm, "w", TimeSpan.FromMinutes(5));

        Assert.NotNull(job);

        var channelId = await db.Jobs.AsNoTracking()
            .Where(j => j.Id == job.Id).Select(j => j.ChannelId)
            .SingleAsync(CancellationToken.None);

        Assert.Equal(calisan.Id, channelId);

        // İkinci kiralama: duraklatılmış kanalın işi hâlâ alınmıyor.
        Assert.Null(await queue.LeaseAsync(QueueClass.Llm, "w", TimeSpan.FromMinutes(5)));
    }

    /// Tamamlanan iş bitiş anını KAYDEDİYOR: geçmiş pay ölçütü buna
    /// bakıyor ve kayıt olmadan ölçüt hep sıfır kalırdı.
    [Fact]
    public async Task TamamlananIs_BitisAniniKaydediyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var time = new FakeTimeProvider(Now);
        var queue = new JobQueue(db, time);
        var channel = await ChannelAsync(db, "Kayit");

        var id = await queue.EnqueueAsync(new EnqueueRequest
        {
            Queue = QueueClass.Llm,
            ChannelId = channel.Id,
        }, CancellationToken.None);

        await queue.LeaseAsync(QueueClass.Llm, "w", TimeSpan.FromMinutes(5));
        await queue.CompleteAsync(id, CancellationToken.None);

        var completed = await db.Jobs.AsNoTracking()
            .Where(j => j.Id == id).Select(j => j.CompletedAt)
            .SingleAsync(CancellationToken.None);

        Assert.Equal(Now, completed);
    }
}
