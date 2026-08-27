using System.Net;
using System.Text;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Providers.Open;

namespace BytemountsAiStudio.Providers.Open.Tests;

/// Yola göre cevap veren sahte HTTP işleyici.
///
/// robots.txt ve sayfa aynı istemciden geliyor; ikisini ayrı ayrı
/// ayarlayabilmek gerekiyor.
internal sealed class RouteHandler : HttpMessageHandler
{
    private readonly Dictionary<string, (HttpStatusCode Status, string Body, string Mime)> _routes = new(StringComparer.Ordinal);

    public List<string> Requested { get; } = [];

    public RouteHandler Route(string path, string body,
        HttpStatusCode status = HttpStatusCode.OK, string mime = "text/html")
    {
        _routes[path] = (status, body, mime);
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.PathAndQuery;
        Requested.Add(path);

        if (!_routes.TryGetValue(path, out var route))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                RequestMessage = request,
                Content = new StringContent(string.Empty),
            });
        }

        return Task.FromResult(new HttpResponseMessage(route.Status)
        {
            RequestMessage = request,
            Content = new StringContent(route.Body, Encoding.UTF8, route.Mime),
        });
    }
}

/// WebFetch testleri (P1-06).
///
/// Ağa çıkmıyor. Sınanan şey dört kapı: şema, alan adı, robots.txt ve
/// boyut. Bunların hepsi sağlayıcının İÇİNDE, çünkü çağırana bırakılan
/// bir kural er geç bir yerde atlanır.
public sealed class WebFetchProviderTests
{
    private const string Page = """
        <html><head><title>Test Sayfasi</title></head>
        <body><article><p>
        Göbeklitepe, Şanlıurfa yakınlarında yer alan ve dünyanın bilinen en eski
        tapınak yapısı olarak kabul edilen arkeolojik alandır. Yaklaşık on bir bin
        yıl önce inşa edildiği düşünülmektedir ve bulunuşu tarih öncesi toplumlara
        dair yerleşik kabulleri kökten değiştirmiştir.
        </p></article></body></html>
        """;

    private static ProviderContext Context() => ProviderContext.ForTest("fetch");

    private static WebFetchProvider Provider(RouteHandler handler, Action<WebFetchProvider>? _ = null)
        => new(new HttpClient(handler));

    [Fact]
    public async Task IzinliSayfa_Cekilir()
    {
        var handler = new RouteHandler()
            .Route("/robots.txt", "User-agent: *\nDisallow: /gizli/")
            .Route("/makale", Page);

        var result = await Provider(handler).FetchAsync(
            new Uri("https://ornek.com/makale"), Context(), CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);
        Assert.Equal("Test Sayfasi", result.Value.Value.Title);
        Assert.Contains("Göbeklitepe", result.Value.Value.MainText, StringComparison.Ordinal);
        Assert.False(result.Value.Value.IsPaywalled);
        Assert.Equal(64, result.Value.Value.ContentHash.Length);
    }

    /// robots.txt yasağı KALICI hata: yeniden denemek dosyayı
    /// değiştirmez, yalnızca bir yasağı ikinci kez ihlal eder.
    [Fact]
    public async Task RobotsYasagi_KaliciHataVerirVeSayfayaHicGidilmez()
    {
        var handler = new RouteHandler()
            .Route("/robots.txt", "User-agent: *\nDisallow: /gizli/")
            .Route("/gizli/sayfa", Page);

        var result = await Provider(handler).FetchAsync(
            new Uri("https://ornek.com/gizli/sayfa"), Context(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("fetch.robots_disallow", result.Error.Code);
        Assert.Equal(ErrorKind.Permanent, result.Error.Kind);

        // Asıl kanıt: sayfaya HİÇ istek gitmemiş.
        Assert.DoesNotContain("/gizli/sayfa", handler.Requested, StringComparer.Ordinal);
    }

    /// robots.txt yoksa kısıt yok (RFC 9309).
    [Fact]
    public async Task RobotsYok_CekimeIzinVerilir()
    {
        var handler = new RouteHandler().Route("/makale", Page);

        var result = await Provider(handler).FetchAsync(
            new Uri("https://ornek.com/makale"), Context(), CancellationToken.None);

        Assert.True(result.IsSuccess);
    }

    /// 5xx'te ÇEKMİYORUZ. "Okuyamadım, o hâlde serbesttir" demek tam
    /// ters yönde bir hata olurdu — hem de sunucu zorlanırken.
    [Fact]
    public async Task RobotsSunucuHatasi_CekimErtelenir()
    {
        var handler = new RouteHandler()
            .Route("/robots.txt", "hata", HttpStatusCode.InternalServerError)
            .Route("/makale", Page);

        var result = await Provider(handler).FetchAsync(
            new Uri("https://ornek.com/makale"), Context(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("robots.unavailable", result.Error.Code);
        Assert.Equal(ErrorKind.Transient, result.Error.Kind);
        Assert.DoesNotContain("/makale", handler.Requested, StringComparer.Ordinal);
    }

    /// Her sayfa için robots.txt çekmek, istek sayısını ikiye katlardı.
    [Fact]
    public async Task Robots_OnbellegeAlinir()
    {
        var handler = new RouteHandler()
            .Route("/robots.txt", "User-agent: *\nDisallow:")
            .Route("/bir", Page)
            .Route("/iki", Page);

        var provider = Provider(handler);

        await provider.FetchAsync(new Uri("https://ornek.com/bir"), Context(), CancellationToken.None);
        await provider.FetchAsync(new Uri("https://ornek.com/iki"), Context(), CancellationToken.None);

        Assert.Single(handler.Requested, p => p == "/robots.txt");
    }

    [Fact]
    public async Task EngelliAlanAdi_HicDenenmez()
    {
        var handler = new RouteHandler();

        var result = await Provider(handler).FetchAsync(
            new Uri("https://www.instagram.com/p/abc"), Context(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("fetch.blocked_host", result.Error.Code);
        Assert.Empty(handler.Requested);
    }

    [Fact]
    public async Task IzinliListeDoluysa_DisaridakiReddedilir()
    {
        var handler = new RouteHandler().Route("/robots.txt", string.Empty).Route("/x", Page);

        var provider = new WebFetchProvider(new HttpClient(handler))
        {
            AllowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "vikipedi.org" },
        };

        var disallowed = await provider.FetchAsync(
            new Uri("https://ornek.com/x"), Context(), CancellationToken.None);

        Assert.True(disallowed.IsFailure);
        Assert.Equal("fetch.not_allowed", disallowed.Error.Code);
    }

    /// Alt alan adları da sayılıyor: `facebook.com` engelliyse
    /// `m.facebook.com` de engelli.
    [Fact]
    public async Task AltAlanAdi_EngelliSayilir()
    {
        var result = await Provider(new RouteHandler()).FetchAsync(
            new Uri("https://m.facebook.com/sayfa"), Context(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("fetch.blocked_host", result.Error.Code);
    }

    [Theory]
    [InlineData("ftp://ornek.com/dosya")]
    [InlineData("file:///C:/gizli.txt")]
    public async Task HttpDisiSema_Reddedilir(string url)
    {
        var result = await Provider(new RouteHandler()).FetchAsync(
            new Uri(url), Context(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("fetch.scheme", result.Error.Code);
    }

    /// Content-Length'e güvenilmiyor ama söylediğinde de dinleniyor:
    /// gereksiz yere indirmenin anlamı yok.
    [Fact]
    public async Task BoyutSiniri_AkisSirasindaUygulanir()
    {
        var big = "<html><body><p>" + new string('a', 200_000) + "</p></body></html>";

        var handler = new RouteHandler()
            .Route("/robots.txt", string.Empty)
            .Route("/buyuk", big);

        var provider = new WebFetchProvider(new HttpClient(handler)) { MaxBytes = 10_000 };

        var result = await provider.FetchAsync(
            new Uri("https://ornek.com/buyuk"), Context(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("fetch.too_large", result.Error.Code);
    }

    [Fact]
    public async Task MetinOlmayanIcerik_Reddedilir()
    {
        var handler = new RouteHandler()
            .Route("/robots.txt", string.Empty)
            .Route("/resim.png", "binary", mime: "image/png");

        var result = await Provider(handler).FetchAsync(
            new Uri("https://ornek.com/resim.png"), Context(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("fetch.not_text", result.Error.Code);
    }

    /// JS ile yüklenen sayfa: mesaj sidecar'a yönlendiriyor, çünkü
    /// bu yoldan alınamayacağı belli.
    [Fact]
    public async Task CokAzMetin_SidecarGerektiginiSoyler()
    {
        var handler = new RouteHandler()
            .Route("/robots.txt", string.Empty)
            .Route("/spa", "<html><body><div id=\"root\"></div></body></html>");

        var result = await Provider(handler).FetchAsync(
            new Uri("https://ornek.com/spa"), Context(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("fetch.too_little_text", result.Error.Code);
        Assert.Contains("sidecar", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SunucuHatasi_GeciciSayilir()
    {
        var handler = new RouteHandler()
            .Route("/robots.txt", string.Empty)
            .Route("/x", "bozuk", HttpStatusCode.BadGateway);

        var result = await Provider(handler).FetchAsync(
            new Uri("https://ornek.com/x"), Context(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Transient, result.Error.Kind);
    }

    [Fact]
    public async Task DortYuzDort_KaliciSayilir()
    {
        var handler = new RouteHandler().Route("/robots.txt", string.Empty);

        var result = await Provider(handler).FetchAsync(
            new Uri("https://ornek.com/yok"), Context(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Permanent, result.Error.Kind);
    }

    /// 429 KAYNAK hatası: hemen yeniden denemek aynı cevabı alır.
    [Fact]
    public async Task IstekSiniri_KaynakHatasiVerir()
    {
        var handler = new RouteHandler()
            .Route("/robots.txt", string.Empty)
            .Route("/x", "dur", (HttpStatusCode)429);

        var result = await Provider(handler).FetchAsync(
            new Uri("https://ornek.com/x"), Context(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Resource, result.Error.Kind);
        Assert.NotNull(result.Error.RetryAfter);
    }

    /// Aynı metin aynı özeti vermeli: "bu kaynak değişmiş mi" sorusu
    /// buna dayanıyor.
    [Fact]
    public async Task AyniIcerik_AyniOzet()
    {
        var handler = new RouteHandler().Route("/robots.txt", string.Empty).Route("/x", Page);
        var provider = Provider(handler);

        var first = await provider.FetchAsync(new Uri("https://ornek.com/x"), Context(), CancellationToken.None);
        var second = await provider.FetchAsync(new Uri("https://ornek.com/x"), Context(), CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(first.Value.Value.ContentHash, second.Value.Value.ContentHash);
    }
}
