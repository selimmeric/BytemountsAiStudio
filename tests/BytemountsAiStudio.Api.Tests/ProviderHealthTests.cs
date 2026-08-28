using BytemountsAiStudio.Persistence;
using BytemountsAiStudio.Persistence.Entities;
using BytemountsAiStudio.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Api.Tests;

/// Sağlayıcı sağlığı görünümü (P2-04).
///
/// Devre kesicinin süreç içi durumu DEĞİL, filonun gözlemi. Bir
/// worker'ın özel sayacını göstermek yanıltıcı olurdu: üç worker'lı
/// bir kurulumda üçünün sayacı farklı ve panel hangisini gösterirse
/// göstersin diğer ikisi hakkında yalan söylerdi.
[Collection(DatabaseCollection.Name)]
public sealed class ProviderHealthTests(DatabaseFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (!fixture.Available)
        {
            return;
        }

        await using var db = fixture.CreateContext();

        await db.Database.ExecuteSqlRawAsync("DELETE FROM provider_calls");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private void RequireDatabase()
        => Assert.True(fixture.Available, $"PostgreSQL erişilemiyor ({fixture.UnavailableReason}).");

    private static void Add(
        StudioDbContext db, string provider, bool succeeded, int minutesAgo, int latencyMs = 100)
        => db.ProviderCalls.Add(new ProviderCall
        {
            ProviderKey = provider,
            Operation = "test",
            Succeeded = succeeded,
            LatencyMs = latencyMs,
            Cost = 0.01m,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
        });

    /// ART ARDA HATA, toplam hata oranından AYRI.
    ///
    /// Sabah beş hata alıp düzelmiş bir sağlayıcı ile şu an art arda
    /// beş hata veren sağlayıcı aynı orana sahip olabiliyor — ama biri
    /// sağlıklı, diğeri ölü. Panelin cevaplaması gereken soru
    /// ikincisi.
    [Fact]
    public async Task ArtArdaHata_SonduranHatalarSayiliyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        // Önce beş hata, sonra düzeldi: SAĞLIKLI.
        for (var i = 0; i < 5; i++)
        {
            Add(db, "duzeldi", succeeded: false, minutesAgo: 20 - i);
        }

        Add(db, "duzeldi", succeeded: true, minutesAgo: 10);

        // Art arda beş hata, hâlâ sürüyor: SAĞLIKSIZ.
        Add(db, "olu", succeeded: true, minutesAgo: 20);

        for (var i = 0; i < 5; i++)
        {
            Add(db, "olu", succeeded: false, minutesAgo: 15 - i);
        }

        await db.SaveChangesAsync(CancellationToken.None);

        var health = await RunQueries.ProviderHealthAsync(
            db, TimeSpan.FromMinutes(30), 5, CancellationToken.None);

        var duzeldi = health.Single(p => p.ProviderKey == "duzeldi");
        var olu = health.Single(p => p.ProviderKey == "olu");

        // İkisinin de beş hatası var...
        Assert.Equal(5, duzeldi.Failures);
        Assert.Equal(5, olu.Failures);

        // ...ama yalnızca biri sağlıksız.
        Assert.Equal(0, duzeldi.ConsecutiveFailures);
        Assert.False(duzeldi.Unhealthy);
        Assert.Equal(5, olu.ConsecutiveFailures);
        Assert.True(olu.Unhealthy);
    }

    /// SAĞLIKSIZ OLANLAR ÖNCE: panelde ilk görülmesi gereken satır,
    /// sorunu olan satır.
    [Fact]
    public async Task Siralama_SaglıksizlariOneAliyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        Add(db, "aaa-saglikli", succeeded: true, minutesAgo: 5);

        for (var i = 0; i < 5; i++)
        {
            Add(db, "zzz-olu", succeeded: false, minutesAgo: 10 - i);
        }

        await db.SaveChangesAsync(CancellationToken.None);

        var health = await RunQueries.ProviderHealthAsync(
            db, TimeSpan.FromMinutes(30), 5, CancellationToken.None);

        Assert.Equal("zzz-olu", health[0].ProviderKey);
    }

    /// PENCERE ŞART: pencere olmasaydı aylar önce bir kez bozulmuş
    /// bir sağlayıcı sonsuza kadar "hatalı" görünürdü ve panel bir
    /// süre sonra hiçbir şey söylemez olurdu.
    [Fact]
    public async Task EskiCagrilar_PencereDisindaKaliyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        Add(db, "eski", succeeded: false, minutesAgo: 120);
        Add(db, "yeni", succeeded: true, minutesAgo: 5);
        await db.SaveChangesAsync(CancellationToken.None);

        var health = await RunQueries.ProviderHealthAsync(
            db, TimeSpan.FromMinutes(30), 5, CancellationToken.None);

        Assert.Single(health);
        Assert.Equal("yeni", health[0].ProviderKey);
    }

    /// Son BAŞARI ayrı raporlanıyor: son çağrı bir hataysa, "en son ne
    /// zaman çalışıyordu" sorusunun cevabı hâlâ gerekiyor.
    [Fact]
    public async Task SonBasari_SonCagridanAyri()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        Add(db, "p", succeeded: true, minutesAgo: 20);
        Add(db, "p", succeeded: false, minutesAgo: 2);
        await db.SaveChangesAsync(CancellationToken.None);

        var health = await RunQueries.ProviderHealthAsync(
            db, TimeSpan.FromMinutes(30), 5, CancellationToken.None);

        Assert.NotNull(health[0].LastSuccessAt);
        Assert.True(health[0].LastSuccessAt < health[0].LastCallAt);
    }

    /// Hiç başarısı olmayan sağlayıcıda son başarı YOK — sıfır ya da
    /// "şimdi" göstermek, hiç çalışmamış bir sağlayıcıyı çalışmış
    /// gibi gösterirdi.
    [Fact]
    public async Task HicBasariYok_SonBasariBos()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        Add(db, "hicbir-zaman", succeeded: false, minutesAgo: 3);
        await db.SaveChangesAsync(CancellationToken.None);

        var health = await RunQueries.ProviderHealthAsync(
            db, TimeSpan.FromMinutes(30), 5, CancellationToken.None);

        Assert.Null(health[0].LastSuccessAt);
    }

    [Fact]
    public async Task HicCagriYok_BosListe()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        Assert.Empty(await RunQueries.ProviderHealthAsync(
            db, TimeSpan.FromMinutes(30), 5, CancellationToken.None));
    }
}
