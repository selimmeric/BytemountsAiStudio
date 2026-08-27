using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Persistence;
using BytemountsAiStudio.Persistence.Entities;
using BytemountsAiStudio.Queue;
using Microsoft.EntityFrameworkCore;
using BytemountsAiStudio.TestSupport;

namespace BytemountsAiStudio.Queue.Tests;

/// Kuyruk testleri GERÇEK PostgreSQL'e koşuyor.
///
/// Bellek içi sağlayıcıda `FOR UPDATE SKIP LOCKED` yok — yani kuyruğun
/// doğruluğunu taşıyan tek cümle orada hiç sınanmaz. Orada geçen bir test,
/// üretimde çalışacağına dair hiçbir şey söylemez.
[Collection(DatabaseCollection.Name)]
public sealed class JobQueueTests(DatabaseFixture fixture) : IAsyncLifetime
{
    /// Testler koleksiyon fixture'ini paylastigi icin ayni veritabanina
    /// yaziyorlar. Temizlik olmadan bir testin biraktigi is, sonrakinin
    /// kiraladigi is oluyor ve testler birbirinin sonucunu bozuyor.
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

    private static EnqueueRequest Request(QueueClass queue = QueueClass.Llm, int priority = 0)
        => new() { Queue = queue, Priority = priority, PayloadJson = """{"x":1}""" };

    [Fact]
    public async Task KuyrugaAtilanIs_Kiralanabilir()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();
        var queue = new JobQueue(db);

        var id = await queue.EnqueueAsync(Request(), CancellationToken.None);
        var leased = await queue.LeaseAsync(QueueClass.Llm, "w1", TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.NotNull(leased);
        Assert.Equal(id, leased.Id);
        Assert.Equal(1, leased.Attempt);
    }

    [Fact]
    public async Task KiralananIs_IkinciWorkeraVerilmez()
    {
        // SKIP LOCKED'ın ve durum geçişinin birlikte çalıştığının kanıtı.
        RequireDatabase();
        await using var db = fixture.CreateContext();
        var queue = new JobQueue(db);

        await queue.EnqueueAsync(Request(QueueClass.Search), CancellationToken.None);

        var first = await queue.LeaseAsync(QueueClass.Search, "w1", TimeSpan.FromMinutes(5), CancellationToken.None);
        var second = await queue.LeaseAsync(QueueClass.Search, "w2", TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public async Task FarkliKuyrukSiniflari_BirbirininIsiniAlmaz()
    {
        // §8.1: 2 saniyelik LLM çağrısı ile 25 dakikalık render aynı havuzda
        // olamaz. Sınıflar sızdırırsa render worker'ı LLM işi çeker.
        RequireDatabase();
        await using var db = fixture.CreateContext();
        var queue = new JobQueue(db);

        await queue.EnqueueAsync(Request(QueueClass.Render), CancellationToken.None);

        var wrongClass = await queue.LeaseAsync(QueueClass.Tts, "w1", TimeSpan.FromMinutes(5), CancellationToken.None);
        var rightClass = await queue.LeaseAsync(QueueClass.Render, "w1", TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.Null(wrongClass);
        Assert.NotNull(rightClass);
    }

    [Fact]
    public async Task YuksekOncelikliIs_OnceAlinir()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();
        var queue = new JobQueue(db);

        await queue.EnqueueAsync(Request(QueueClass.Asset, priority: 0), CancellationToken.None);
        var urgent = await queue.EnqueueAsync(Request(QueueClass.Asset, priority: 10), CancellationToken.None);

        var leased = await queue.LeaseAsync(QueueClass.Asset, "w1", TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.Equal(urgent, leased!.Id);
    }

    [Fact]
    public async Task SuresiDolmusKiralama_GeriAlinir()
    {
        // Worker çökme kurtarmasının tamamı bu. Ölen worker'ı tespit etmeye
        // çalışmıyoruz; kiralamanın süresinin dolmasını bekliyoruz.
        RequireDatabase();
        await using var db = fixture.CreateContext();
        var queue = new JobQueue(db);

        await queue.EnqueueAsync(Request(QueueClass.Align), CancellationToken.None);

        // Negatif süre = kiralama daha doğarken ölmüş; çöken worker'ı taklit ediyor.
        var leased = await queue.LeaseAsync(QueueClass.Align, "cokecek", TimeSpan.FromSeconds(-1), CancellationToken.None);
        Assert.NotNull(leased);

        var reclaimed = await queue.ReclaimExpiredAsync(CancellationToken.None);
        Assert.Equal(1, reclaimed);

        var again = await queue.LeaseAsync(QueueClass.Align, "w2", TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.NotNull(again);
        Assert.Equal(2, again.Attempt);   // deneme sayacı korunuyor: sonsuz döngü olmaz
    }

    [Fact]
    public async Task Heartbeat_KiralamayiUzatir()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();
        var queue = new JobQueue(db);

        await queue.EnqueueAsync(Request(QueueClass.Upload), CancellationToken.None);
        var leased = await queue.LeaseAsync(QueueClass.Upload, "w1", TimeSpan.FromSeconds(1), CancellationToken.None);

        var extended = await queue.HeartbeatAsync(leased!.Id, "w1", TimeSpan.FromMinutes(30), CancellationToken.None);
        var wrongWorker = await queue.HeartbeatAsync(leased.Id, "baskasi", TimeSpan.FromMinutes(30), CancellationToken.None);

        Assert.True(extended);
        Assert.False(wrongWorker);   // başka worker'ın kiralamasını uzatamaz

        // Uzatıldığı için süpürücü artık geri alamamalı.
        Assert.Equal(0, await queue.ReclaimExpiredAsync(CancellationToken.None));
    }

    [Fact]
    public async Task KaynakHatasi_ErtelerVeDenemeSayaciniArtirmaz()
    {
        // ADR-011: kota bitişi başarısızlık değil ERTELEMEDİR. Deneme sayacı
        // artsaydı kotası dolu bir kanal birkaç günde DLQ'ya düşerdi.
        RequireDatabase();
        await using var db = fixture.CreateContext();
        var queue = new JobQueue(db);

        await queue.EnqueueAsync(Request(QueueClass.Upload), CancellationToken.None);
        var leased = await queue.LeaseAsync(QueueClass.Upload, "w1", TimeSpan.FromMinutes(5), CancellationToken.None);

        var disposition = await queue.FailAsync(
            leased!,
            Error.Resource("quota.youtube", "Kota doldu", TimeSpan.FromMilliseconds(-1)),
            CancellationToken.None);

        Assert.Equal(JobDisposition.Deferred, disposition);

        var again = await queue.LeaseAsync(QueueClass.Upload, "w1", TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.NotNull(again);
        Assert.Equal(1, again.Attempt);   // 1'e düşürülüp tekrar 1'e çıktı: net artış yok
    }

    [Fact]
    public async Task KaliciHata_TekrarDenenmez()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();
        var queue = new JobQueue(db);

        await queue.EnqueueAsync(Request(QueueClass.ImageGeneration), CancellationToken.None);
        var leased = await queue.LeaseAsync(QueueClass.ImageGeneration, "w1", TimeSpan.FromMinutes(5), CancellationToken.None);

        var disposition = await queue.FailAsync(
            leased!, Error.Permanent("bad.request", "Gecersiz istek"), CancellationToken.None);

        Assert.Equal(JobDisposition.Failed, disposition);
        Assert.Null(await queue.LeaseAsync(QueueClass.ImageGeneration, "w1", TimeSpan.FromMinutes(5), CancellationToken.None));
    }

    [Fact]
    public async Task GeciciHata_YenidenDenenirSonDenemedeDLQyaDuser()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();
        var queue = new JobQueue(db);

        var id = await queue.EnqueueAsync(
            new EnqueueRequest { Queue = QueueClass.Llm, MaxAttempts = 2 }, CancellationToken.None);

        var first = await queue.LeaseAsync(QueueClass.Llm, "w1", TimeSpan.FromMinutes(5), CancellationToken.None);
        var firstDisposition = await queue.FailAsync(
            first!, Error.Transient("http.429", "Rate limit", TimeSpan.FromMilliseconds(-1)), CancellationToken.None);

        var second = await queue.LeaseAsync(QueueClass.Llm, "w1", TimeSpan.FromMinutes(5), CancellationToken.None);
        var secondDisposition = await queue.FailAsync(
            second!, Error.Transient("http.429", "Rate limit"), CancellationToken.None);

        Assert.Equal(JobDisposition.Retried, firstDisposition);
        Assert.Equal(JobDisposition.DeadLettered, secondDisposition);

        var state = await db.Jobs.AsNoTracking().Where(j => j.Id == id).Select(j => j.State).SingleAsync(CancellationToken.None);
        Assert.Equal(JobState.DeadLettered, state);
    }

    [Fact]
    public async Task ZehirliHata_IlkDenemedeDLQyaGider()
    {
        // Her denemede aynı şekilde çöken iş, denemeleri tüketmeyi hak etmiyor.
        RequireDatabase();
        await using var db = fixture.CreateContext();
        var queue = new JobQueue(db);

        await queue.EnqueueAsync(Request(QueueClass.Search), CancellationToken.None);
        var leased = await queue.LeaseAsync(QueueClass.Search, "w1", TimeSpan.FromMinutes(5), CancellationToken.None);

        var disposition = await queue.FailAsync(
            leased! with { }, new Error("poison", "Hep ayni sekilde cokuyor", ErrorKind.Poison), CancellationToken.None);

        Assert.Equal(JobDisposition.DeadLettered, disposition);
    }

    [Fact]
    public async Task DuraklatilmisKanalinIsi_Alinmaz()
    {
        // §8.2: kanal duraklatma, kill-switch'in kanal ölçeğindeki hâli.
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var channel = new Channel { Name = "Duraklatilmis " + Guid.NewGuid().ToString("N")[..6], Language = "tr-TR", IsPaused = true };
        db.Channels.Add(channel);
        await db.SaveChangesAsync(CancellationToken.None);

        var queue = new JobQueue(db);
        await queue.EnqueueAsync(
            new EnqueueRequest { Queue = QueueClass.Asset, ChannelId = channel.Id }, CancellationToken.None);

        Assert.Null(await queue.LeaseAsync(QueueClass.Asset, "w1", TimeSpan.FromMinutes(5), CancellationToken.None));
    }

    [Fact]
    public async Task ZamaniGelmemisIs_Alinmaz()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();
        var queue = new JobQueue(db);

        await queue.EnqueueAsync(
            new EnqueueRequest
            {
                Queue = QueueClass.Tts,
                RunAfter = DateTimeOffset.UtcNow.AddHours(1),
            },
            CancellationToken.None);

        Assert.Null(await queue.LeaseAsync(QueueClass.Tts, "w1", TimeSpan.FromMinutes(5), CancellationToken.None));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(20)]
    public void Backoff_UstelArtarAmaSinirlanir(int attempt)
    {
        var delay = JobQueue.Backoff(attempt, null);

        Assert.True(delay > TimeSpan.Zero);
        Assert.True(delay <= TimeSpan.FromSeconds(305),
            $"{attempt}. denemede geri çekilme çok uzun: {delay}");
    }
}
