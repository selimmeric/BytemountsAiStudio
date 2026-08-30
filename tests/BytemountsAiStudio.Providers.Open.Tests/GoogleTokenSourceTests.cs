using System.Net;
using System.Text;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Providers.Open;

namespace BytemountsAiStudio.Providers.Open.Tests;

/// Google erişim jetonunun yenilenmesi ve önbelleklenmesi (P5-01).
///
/// ***BU DOSYANIN VAR OLMA SEBEBİ:*** yenileme akışı yalnızca
/// `YouTubePublisher` içindeydi ve `YouTubeAnalyticsProvider` **statik
/// bir jeton** okuyordu. Google'ın erişim jetonu bir saat ömürlü: gece
/// koşan bir ölçüm çekimi ilk saatten sonra düşerdi ve öğrenme
/// döngüsünün verisi hiç gelmezdi.
///
/// Yenilemeyi ikinci kez yazmak yerine ortak sınıf çıkarıldı; bu
/// dosya o sınıfın sözleşmesini sabitliyor.
public sealed class GoogleTokenSourceTests
{
    private sealed class TokenServer(
        HttpStatusCode status = HttpStatusCode.OK,
        string body = """{"access_token":"taze-jeton","expires_in":3600}""") : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;

            return Task.FromResult(new HttpResponseMessage(status)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class Keys(Dictionary<string, string> values)
        : Contracts.Providers.ICredentialSource
    {
        public string? Get(string name) => values.GetValueOrDefault(name);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly Uri TokenAddress = new("https://oauth2.test/token");

    /// Yenileme için gereken üçlü.
    private static Keys Refreshable() => new(new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["YOUTUBE_REFRESH_TOKEN"] = "yenileme",
        ["YOUTUBE_CLIENT_ID"] = "istemci",
        ["YOUTUBE_CLIENT_SECRET"] = "sir",
    });

    private static GoogleTokenSource Source(
        TokenServer server, Contracts.Providers.ICredentialSource keys, TimeProvider? time = null)
        => new(new HttpClient(server), TokenAddress, new GoogleTokenVariables(), keys, time);

    /* ---- yenileme ---- */

    /// ***YENİLEME JETONUYLA TAZE JETON ALINIYOR.***
    ///
    /// BU DOSYANIN EN ÖNEMLİ TESTİ. Analitik sağlayıcı bunu hiç
    /// yapmıyordu: statik jeton bir saat sonra ölüyordu.
    [Fact]
    public async Task YenilemeJetonu_TazeJetonVeriyor()
    {
        var server = new TokenServer();

        var token = await Source(server, Refreshable()).GetAsync(null, CancellationToken.None);

        Assert.True(token.IsSuccess, token.IsFailure ? token.Error.Message : null);
        Assert.Equal("taze-jeton", token.Value);
        Assert.Equal(1, server.Calls);
    }

    /// ***DOĞRUDAN VERİLEN JETON UCA HİÇ GİTMİYOR.***
    ///
    /// `YOUTUBE_ACCESS_TOKEN` verilmişse yenilemeye gerek yok — ve
    /// gitmek, elle verilmiş bir jetonu boşuna doğrulamaya çalışmak
    /// olurdu.
    [Fact]
    public async Task DogrudanJeton_UcaGitmiyor()
    {
        var server = new TokenServer();

        var keys = new Keys(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["YOUTUBE_ACCESS_TOKEN"] = "elle-verilen",
        });

        var token = await Source(server, keys).GetAsync(null, CancellationToken.None);

        Assert.Equal("elle-verilen", token.Value);
        Assert.Equal(0, server.Calls);
    }

    /// KİMLİK EKSİKSE KALICI HATA — VE MESAJ NEYİN EKSİK OLDUĞUNU SAYIYOR.
    [Fact]
    public async Task KimlikYok_KaliciHata()
    {
        var token = await Source(
            new TokenServer(),
            new Keys(new Dictionary<string, string>(StringComparer.Ordinal)))
            .GetAsync(null, CancellationToken.None);

        Assert.True(token.IsFailure);
        Assert.Equal("google.no_credentials", token.Error.Code);
        Assert.Equal(ErrorKind.Permanent, token.Error.Kind);
        Assert.Contains("YOUTUBE_REFRESH_TOKEN", token.Error.Message, StringComparison.Ordinal);
    }

    /* ---- önbellek ---- */

    /// ***İKİNCİ ÇAĞRI UCA GİTMİYOR.***
    ///
    /// Yayıncı her çağrıda yeniden yeniliyordu. Elli videoluk bir
    /// günlük ölçüm çekimi elli jeton yenilemesi demekti; Google'ın
    /// jeton ucunun da kendi hız sınırı var ve ona takılmak, asıl
    /// işin kotasıyla hiç ilgisi olmayan bir yerden çökmek olurdu.
    [Fact]
    public async Task IkinciCagri_OnbelleklenIyor()
    {
        var server = new TokenServer();
        var source = Source(server, Refreshable());

        await source.GetAsync(null, CancellationToken.None);
        var second = await source.GetAsync(null, CancellationToken.None);

        Assert.Equal("taze-jeton", second.Value);
        Assert.Equal(1, server.Calls);
    }

    /// ***SÜRE DOLUNCA YENİDEN ALINIYOR.***
    [Fact]
    public async Task SureDolunca_YenidenAliniyor()
    {
        var server = new TokenServer();
        var time = new FixedTime(new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.Zero));
        var source = Source(server, Refreshable(), time);

        await source.GetAsync(null, CancellationToken.None);

        // Bir saatlik jeton + bir dakikalık marj → 59. dakikada
        // sona eriyor.
        //
        // ***SINIR DIŞARIDA:*** tam sona erme anında jeton ARTIK
        // servis edilmiyor (`ExpiresAt > now`). İçeride olsaydı, süresi
        // o an dolan bir jetonla istek atılırdı ve dönen hata
        // ("invalid credentials") sebebini söylemezdi.
        time.Now = time.Now.AddMinutes(58);
        await source.GetAsync(null, CancellationToken.None);
        Assert.Equal(1, server.Calls);

        time.Now = time.Now.AddMinutes(1);
        await source.GetAsync(null, CancellationToken.None);
        Assert.Equal(2, server.Calls);
    }

    /// ***ÖMÜR CEVAPTAN OKUNUYOR, VARSAYILMIYOR.***
    ///
    /// Google süreyi `expires_in` ile söylüyor. "Bir saat" diye
    /// varsaymak, Google süreyi kısalttığında süresi dolmuş bir
    /// jetonu önbellekten servis etmek demekti.
    [Fact]
    public async Task KisaOmur_ErkenYenileniyor()
    {
        var server = new TokenServer(body: """{"access_token":"kisa","expires_in":300}""");
        var time = new FixedTime(new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.Zero));
        var source = Source(server, Refreshable(), time);

        await source.GetAsync(null, CancellationToken.None);

        // Beş dakikalık jeton + bir dakikalık marj → 4. dakikada
        // sona eriyor. Bir saatlik varsayım yapılsaydı bu jeton
        // elli beş dakika boyunca ÖLÜ olduğu hâlde servis edilirdi.
        time.Now = time.Now.AddMinutes(3);
        await source.GetAsync(null, CancellationToken.None);
        Assert.Equal(1, server.Calls);

        time.Now = time.Now.AddMinutes(2);
        await source.GetAsync(null, CancellationToken.None);
        Assert.Equal(2, server.Calls);
    }

    /// ***HESAP BAŞINA AYRI ÖNBELLEK (P4-04).***
    ///
    /// Tek bir gözde saklamak, bir hesabın jetonunu diğerine
    /// göndermek olurdu — yükleme başka projenin kotasından düşerdi
    /// ve defter yanlış projeyi dolu gösterirdi.
    [Fact]
    public async Task HesapBasina_AyriOnbellek()
    {
        var server = new TokenServer();

        var keys = new Keys(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["YOUTUBE_REFRESH_TOKEN"] = "yenileme",
            ["YOUTUBE_CLIENT_ID"] = "istemci",
            ["YOUTUBE_CLIENT_SECRET"] = "sir",
            ["YOUTUBE_REFRESH_TOKEN_PROJE_02"] = "yenileme-2",
            ["YOUTUBE_CLIENT_ID_PROJE_02"] = "istemci-2",
            ["YOUTUBE_CLIENT_SECRET_PROJE_02"] = "sir-2",
        });

        var source = Source(server, keys);

        await source.GetAsync(null, CancellationToken.None);
        await source.GetAsync("proje-02", CancellationToken.None);

        // İKİ AYRI YENİLEME: ikinci hesap birincinin önbelleğini
        // kullanamaz.
        Assert.Equal(2, server.Calls);
    }

    /// ÖNBELLEK ELLE BOŞALTILABİLİYOR.
    ///
    /// Sunucu "bu jeton geçersiz" derse önbellekteki kopyanın da
    /// geçersiz olduğu kesin; tutmak, süre dolana kadar aynı hatayı
    /// tekrarlamak demekti.
    [Fact]
    public async Task Bosaltilinca_YenidenAliniyor()
    {
        var server = new TokenServer();
        var source = Source(server, Refreshable());

        await source.GetAsync(null, CancellationToken.None);
        source.Invalidate();
        await source.GetAsync(null, CancellationToken.None);

        Assert.Equal(2, server.Calls);
    }

    /* ---- hata sınıfları ---- */

    /// ***4xx KALICI: yenileme jetonu iptal edilmiş.***
    ///
    /// Yeniden denemek düzeltmiyor; insanın yeniden yetki vermesi
    /// gerekiyor. Geçici saymak, iptal edilmiş bir kimlikle sonsuza
    /// kadar denemek olurdu.
    [Fact]
    public async Task Reddedilen_KaliciHata()
    {
        var token = await Source(
            new TokenServer(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}"""),
            Refreshable())
            .GetAsync(null, CancellationToken.None);

        Assert.True(token.IsFailure);
        Assert.Equal("google.token_rejected", token.Error.Code);
        Assert.Equal(ErrorKind.Permanent, token.Error.Kind);
    }

    /// 5xx GEÇİCİ: jeton ucu düşmüş, ikinci deneme geçebilir.
    [Fact]
    public async Task SunucuHatasi_GeciciHata()
    {
        var token = await Source(
            new TokenServer(HttpStatusCode.ServiceUnavailable, "bakim"),
            Refreshable())
            .GetAsync(null, CancellationToken.None);

        Assert.True(token.IsFailure);
        Assert.Equal(ErrorKind.Transient, token.Error.Kind);
    }

    /// BAŞARISIZ YENİLEME ÖNBELLEĞE GİRMİYOR.
    [Fact]
    public async Task BasarisizYenileme_Onbelleklenmiyor()
    {
        var server = new TokenServer(HttpStatusCode.ServiceUnavailable, "bakim");
        var source = Source(server, Refreshable());

        await source.GetAsync(null, CancellationToken.None);
        await source.GetAsync(null, CancellationToken.None);

        Assert.Equal(2, server.Calls);
    }
}
