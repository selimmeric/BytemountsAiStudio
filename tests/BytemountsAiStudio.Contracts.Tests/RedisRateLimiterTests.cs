using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core.Errors;
using StackExchange.Redis;

namespace BytemountsAiStudio.Contracts.Tests;

/// Dağıtık hız sınırı (P4-03).
///
/// NEDEN GEREKLİ OLDU: P4-01 ile birden fazla worker gerçekten
/// koşuyor. Süreç içi bir sınır, iki worker'da iki kat istek demek —
/// sağlayıcı bizi keser ve sebep hiçbir logda görünmez, çünkü her
/// worker kendi sayacına göre kurallara uyuyor.
///
/// TESTLER AYRI LIMITER NESNELERİ KULLANIYOR ve bu tesadüf değil: tek
/// nesne üzerinde geçen bir test, süreç içi sayaçla da geçerdi ve
/// dağıtıklığı hiç sınamazdı. İki nesne = iki worker.
public sealed class RedisRateLimiterTests : IAsyncLifetime, IDisposable
{
    private const string Endpoint = "127.0.0.1:6380";

    private ConnectionMultiplexer? _connection;
    private string _key = string.Empty;
    private bool _available;
    private string? _reason;

    public async Task InitializeAsync()
    {
        // Her test kendi sağlayıcı anahtarını kullanıyor: paylaşılan
        // bir anahtar, testlerin birbirinin sayacını tüketmesi demekti.
        _key = "test-" + Guid.NewGuid().ToString("N")[..10];

        try
        {
            _connection = await ConnectionMultiplexer.ConnectAsync(Endpoint);
            _available = _connection.IsConnected;
        }
        catch (RedisConnectionException ex)
        {
            _reason = ex.Message;
        }
    }

    public Task DisposeAsync()
    {
        _connection?.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose() => _connection?.Dispose();

    private void RequireRedis()
        => Assert.True(_available,
            $"Redis erişilemiyor ({_reason}). `docker compose --profile scale up -d redis`");

    private RedisRateLimiter Limiter(int permits, TimeSpan? window = null)
        => new RedisRateLimiter(_connection!)
            .Configure(_key, new RateLimitPolicy(permits, window ?? TimeSpan.FromMinutes(1)));

    /// SINIRA KADAR GEÇİYOR, SONRASI ERTELENİYOR.
    [Fact]
    public async Task SinirAsilinca_Erteleniyor()
    {
        RequireRedis();

        var limiter = Limiter(3);

        for (var i = 0; i < 3; i++)
        {
            Assert.True((await limiter.AcquireAsync(_key, 1, CancellationToken.None)).IsSuccess);
        }

        var blocked = await limiter.AcquireAsync(_key, 1, CancellationToken.None);

        Assert.True(blocked.IsFailure);

        // HATA DEĞİL, ERTELEME (ADR-011): iş kuyruğu bunu görünce işi
        // ileri tarihe atıyor ve deneme sayacı ARTMIYOR. `Transient`
        // olsaydı üç denemede ölü mektup kutusuna düşerdi.
        Assert.Equal(ErrorKind.Resource, blocked.Error.Kind);
        Assert.Equal("ratelimit.exceeded", blocked.Error.Code);
    }

    /// ASIL İDDİA: SINIR İKİ AYRI LIMITER ARASINDA PAYLAŞILIYOR.
    ///
    /// Bu test süreç içi sayaçla DÜŞER. İki worker, iki nesne, tek
    /// sınır — "hesap başına, worker başına değil" (§8.3) tam olarak
    /// bunu söylüyor.
    [Fact]
    public async Task IkiAyriLimiter_AyniSiniriPaylasiyor()
    {
        RequireRedis();

        var birinci = Limiter(4);
        var ikinci = Limiter(4);

        // İkisi dönüşümlü olarak istiyor: toplam 4 izin.
        Assert.True((await birinci.AcquireAsync(_key, 1, CancellationToken.None)).IsSuccess);
        Assert.True((await ikinci.AcquireAsync(_key, 1, CancellationToken.None)).IsSuccess);
        Assert.True((await birinci.AcquireAsync(_key, 1, CancellationToken.None)).IsSuccess);
        Assert.True((await ikinci.AcquireAsync(_key, 1, CancellationToken.None)).IsSuccess);

        // Beşinci istek hangisinden gelirse gelsin reddedilmeli.
        Assert.True((await birinci.AcquireAsync(_key, 1, CancellationToken.None)).IsFailure);
        Assert.True((await ikinci.AcquireAsync(_key, 1, CancellationToken.None)).IsFailure);
    }

    /// EŞZAMANLI İSTEKLERDE DE SINIR TUTUYOR.
    ///
    /// `GET` sonra `SET` yazsaydık iki istek aynı anda okur, ikisi de
    /// "yer var" görür ve ikisi de geçerdi. Lua tek parça çalıştığı
    /// için araya girilemiyor.
    ///
    /// Testin ölçtüğü şey: yirmi eşzamanlı istekten TAM BEŞİ geçmeli
    /// — bir fazlası bile yarışın açık olduğunu söyler.
    [Fact]
    public async Task EszamanliIstekler_SiniriAsmiyor()
    {
        RequireRedis();

        var limiter = Limiter(5);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(_ => limiter.AcquireAsync(_key, 1, CancellationToken.None)));

        Assert.Equal(5, results.Count(r => r.IsSuccess));
    }

    /// PENCERE DOLUNCA SAYAÇ SIFIRLANIYOR.
    ///
    /// Sıfırlanmasaydı sınır bir kez dolduğunda sonsuza kadar kapalı
    /// kalırdı — yani hız sınırı değil, toplam kota olurdu.
    [Fact]
    public async Task PencereBitince_YenidenIzinVeriliyor()
    {
        RequireRedis();

        var limiter = Limiter(1, TimeSpan.FromMilliseconds(300));

        Assert.True((await limiter.AcquireAsync(_key, 1, CancellationToken.None)).IsSuccess);
        Assert.True((await limiter.AcquireAsync(_key, 1, CancellationToken.None)).IsFailure);

        await Task.Delay(400, CancellationToken.None);

        Assert.True((await limiter.AcquireAsync(_key, 1, CancellationToken.None)).IsSuccess);
    }

    /// "NE KADAR SONRA TEKRAR DENE" GERÇEK BİR SÜRE.
    ///
    /// Sabit bir süre uydurmak, pencerenin sonuna kadar beklemek yerine
    /// erken uyanıp tekrar reddedilmek demekti — kuyruk boşuna dönerdi.
    [Fact]
    public async Task RetryAfter_PencerenKalanindanGeliyor()
    {
        RequireRedis();

        var limiter = Limiter(1, TimeSpan.FromSeconds(30));

        await limiter.AcquireAsync(_key, 1, CancellationToken.None);
        var blocked = await limiter.AcquireAsync(_key, 1, CancellationToken.None);

        Assert.True(blocked.IsFailure);
        Assert.NotNull(blocked.Error.RetryAfter);
        Assert.InRange(blocked.Error.RetryAfter!.Value, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30));
    }

    /// YAPILANDIRILMAMIŞ SAĞLAYICI SINIRSIZ.
    ///
    /// Varsayılan olarak kısıtlamak, her yeni sağlayıcının sessizce
    /// yavaşlaması demekti — `TokenBucketRateLimiter` ile aynı karar.
    [Fact]
    public async Task YapilandirilmamisSaglayici_Sinirsiz()
    {
        RequireRedis();

        var limiter = new RedisRateLimiter(_connection!);

        for (var i = 0; i < 50; i++)
        {
            Assert.True((await limiter.AcquireAsync(_key, 1, CancellationToken.None)).IsSuccess);
        }
    }

    /// BİRDEN FAZLA İZİN TEK SEFERDE.
    ///
    /// Bir çağrı birden fazla izin tüketebiliyor (pahalı bir istek
    /// birkaç birim sayılıyor) ve sınır aşılırsa HİÇBİRİ tüketilmemeli
    /// — yarım tüketilen bir istek, sayacı sessizce kaydırırdı.
    [Fact]
    public async Task YetersizIzin_HicTuketilmiyor()
    {
        RequireRedis();

        var limiter = Limiter(5);

        Assert.True((await limiter.AcquireAsync(_key, 3, CancellationToken.None)).IsSuccess);

        // 3 kullanıldı, 2 kaldı: 3 daha istemek reddedilmeli.
        Assert.True((await limiter.AcquireAsync(_key, 3, CancellationToken.None)).IsFailure);

        // Ve reddedilen istek sayacı BOZMAMALI: 2 hâlâ alınabilir.
        Assert.True((await limiter.AcquireAsync(_key, 2, CancellationToken.None)).IsSuccess);
    }
}
