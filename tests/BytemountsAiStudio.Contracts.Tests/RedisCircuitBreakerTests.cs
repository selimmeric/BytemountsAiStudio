using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core.Errors;
using StackExchange.Redis;

namespace BytemountsAiStudio.Contracts.Tests;

/// Dağıtık devre kesici (P4-03).
///
/// NEDEN PAYLAŞILMASI GEREKİYOR: sağlayıcı ölmüşse bunu ÖĞRENEN
/// worker bir tanedir, ama BİLMESİ gereken hepsidir. Süreç içi devre
/// kesiciyle her worker aynı dersi ayrı ayrı alıyor — beş worker,
/// eşikten beş kat fazla başarısız istek ve beş kat gecikme.
public sealed class RedisCircuitBreakerTests : IAsyncLifetime, IDisposable
{
    private const string Endpoint = "127.0.0.1:6380";

    private ConnectionMultiplexer? _connection;
    private string _key = string.Empty;
    private bool _available;
    private string? _reason;

    public async Task InitializeAsync()
    {
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

    private RedisCircuitBreaker Breaker(int threshold = 3, TimeSpan? openFor = null)
        => new(_connection!, threshold, openFor ?? TimeSpan.FromMinutes(5));

    /// EŞİĞE KADAR DEVRE KAPALI.
    ///
    /// Tek bir hata devreyi açsaydı, geçici bir ağ hatası bütün
    /// sağlayıcıyı beş dakika kapatırdı.
    [Fact]
    public async Task EsigeKadar_DevreKapali()
    {
        RequireRedis();

        var breaker = Breaker(threshold: 3);

        await breaker.RecordFailureAsync(_key, CancellationToken.None);
        await breaker.RecordFailureAsync(_key, CancellationToken.None);

        Assert.True((await breaker.CheckAsync(_key, CancellationToken.None)).IsSuccess);
    }

    /// EŞİK AŞILINCA DEVRE AÇILIYOR VE SEBEP ERTELEME.
    [Fact]
    public async Task EsikAsilinca_DevreAciliyor()
    {
        RequireRedis();

        var breaker = Breaker(threshold: 3);

        for (var i = 0; i < 3; i++)
        {
            await breaker.RecordFailureAsync(_key, CancellationToken.None);
        }

        var check = await breaker.CheckAsync(_key, CancellationToken.None);

        Assert.True(check.IsFailure);
        Assert.Equal("circuit.open", check.Error.Code);

        // HATA DEĞİL, ERTELEME (ADR-011): işler kuyrukta bekliyor,
        // run'lar düşmüyor ve sağlayıcı toparlandığında kaldığı
        // yerden devam ediyor.
        Assert.Equal(ErrorKind.Resource, check.Error.Kind);
        Assert.NotNull(check.Error.RetryAfter);
    }

    /// ASIL İDDİA: DEVRE İKİ AYRI NESNE ARASINDA PAYLAŞILIYOR.
    ///
    /// Bu test süreç içi devre kesiciyle DÜŞER. Bir worker öğreniyor,
    /// diğeri biliyor — bütün mesele bu.
    [Fact]
    public async Task BirWorkerOgreniyor_DigeriBiliyor()
    {
        RequireRedis();

        var ogrenen = Breaker(threshold: 3);
        var digeri = Breaker(threshold: 3);

        // Hataların hepsini BİR worker alıyor.
        for (var i = 0; i < 3; i++)
        {
            await ogrenen.RecordFailureAsync(_key, CancellationToken.None);
        }

        // Diğeri hiç hata almamış ama devrenin açık olduğunu biliyor.
        Assert.True((await digeri.CheckAsync(_key, CancellationToken.None)).IsFailure);
    }

    /// HATALAR DA PAYLAŞILIYOR.
    ///
    /// Üç worker birer hata alıyor ve eşik üç: devre açılmalı. Süreç
    /// içi sayaçla üçü de "bir hata aldım" der ve devre hiç açılmazdı
    /// — yani ölü bir sağlayıcıya istek atmaya devam edilirdi.
    [Fact]
    public async Task AyriWorkerlarinHatalari_Toplaniyor()
    {
        RequireRedis();

        foreach (var _ in Enumerable.Range(0, 3))
        {
            await Breaker(threshold: 3).RecordFailureAsync(_key, CancellationToken.None);
        }

        Assert.True((await Breaker(threshold: 3).CheckAsync(_key, CancellationToken.None)).IsFailure);
    }

    /// BAŞARI DEVREYİ KAPATIYOR VE SAYACI SIFIRLIYOR.
    ///
    /// Yalnızca sayacı silmek, açık bir devreyi açık bırakırdı ve
    /// başarılı bir istek hiçbir şeyi değiştirmezdi.
    [Fact]
    public async Task Basari_DevreyiKapatiyor()
    {
        RequireRedis();

        var breaker = Breaker(threshold: 2);

        await breaker.RecordFailureAsync(_key, CancellationToken.None);
        await breaker.RecordFailureAsync(_key, CancellationToken.None);

        Assert.True((await breaker.CheckAsync(_key, CancellationToken.None)).IsFailure);

        await breaker.RecordSuccessAsync(_key, CancellationToken.None);

        Assert.True((await breaker.CheckAsync(_key, CancellationToken.None)).IsSuccess);
    }

    /// SÜRE DOLUNCA TEK DENEMEYE İZİN VERİLİYOR.
    ///
    /// Sürenin dolması yarı açık durumun kendisi: bir sonraki istek
    /// geçiyor. Ayrı bir "yarı açık" bayrağı tutmak, üç durumu iki
    /// anahtarla senkron tutmak demekti.
    [Fact]
    public async Task SureDolunca_YenidenDenenebiliyor()
    {
        RequireRedis();

        var breaker = Breaker(threshold: 1, openFor: TimeSpan.FromMilliseconds(300));

        await breaker.RecordFailureAsync(_key, CancellationToken.None);

        Assert.True((await breaker.CheckAsync(_key, CancellationToken.None)).IsFailure);

        await Task.Delay(400, CancellationToken.None);

        Assert.True((await breaker.CheckAsync(_key, CancellationToken.None)).IsSuccess);
    }

    /// AÇIK DEVRE ÜST ÜSTE HATAYLA UZAMIYOR.
    ///
    /// Her hata açılma zamanını yenileseydi, sürekli istek alan bir
    /// sağlayıcının devresi hiç kapanmaz ve yarı açık deneme anına
    /// ulaşılamazdı. Redis `SET ... NX` bunu engelliyor.
    [Fact]
    public async Task AcikDevre_HatalarlaUzamiyor()
    {
        RequireRedis();

        var breaker = Breaker(threshold: 1, openFor: TimeSpan.FromMilliseconds(500));

        await breaker.RecordFailureAsync(_key, CancellationToken.None);

        var ilkKontrol = await breaker.CheckAsync(_key, CancellationToken.None);
        Assert.True(ilkKontrol.IsFailure);
        var ilk = ilkKontrol.Error.RetryAfter!.Value;

        await Task.Delay(200, CancellationToken.None);
        await breaker.RecordFailureAsync(_key, CancellationToken.None);

        var sonraKontrol = await breaker.CheckAsync(_key, CancellationToken.None);
        Assert.True(sonraKontrol.IsFailure);
        var sonra = sonraKontrol.Error.RetryAfter!.Value;

        Assert.True(sonra < ilk, $"Devre uzamış: {ilk} → {sonra}");
    }

    /// HİÇ HATA ALMAMIŞ SAĞLAYICI AÇIK DEĞİL.
    [Fact]
    public async Task BilinmeyenSaglayici_DevreKapali()
    {
        RequireRedis();

        Assert.True((await Breaker().CheckAsync(_key, CancellationToken.None)).IsSuccess);
    }
}
