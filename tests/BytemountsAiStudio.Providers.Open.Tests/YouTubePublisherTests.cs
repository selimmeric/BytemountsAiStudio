using System.Net;
using System.Text;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Providers.Open;

namespace BytemountsAiStudio.Providers.Open.Tests;

/// Sürdürülebilir yükleme protokolünü konuşan sahte sunucu.
///
/// Tek cevaplı bir sahte sunucuyla "kaldığı yerden devam ediyor"
/// iddiası sınanamazdı: protokolün tamamı 308 + `Range` başlığı
/// üzerine kurulu ve o başlık olmadan devam noktası bilinmiyor.
internal sealed class YouTubeServer : HttpMessageHandler
{
    private const string Session = "https://yukle.test/oturum/1";

    /// Sunucunun ALDIĞI bayt — bizim gönderdiğimiz değil.
    public long Received { get; set; }

    /// Kaç bayt kabul edilecek. Kısmi kabul, ağın parçayı yarıda
    /// kesmesini taklit ediyor.
    public long AcceptPerChunk { get; set; } = long.MaxValue;

    public long Total { get; set; }

    /// Oturum tamamlanmış sayılsın mı (çökme kurtarma senaryosu).
    public bool AlreadyComplete { get; set; }

    public HttpStatusCode? SessionStatus { get; set; }

    /// Hata gövdesi: Google kota aşımını 403 + `quotaExceeded` ile
    /// bildiriyor ve sınıflandırma o metne bakıyor.
    public string SessionBody { get; set; } = """{"error":{"errors":[{"reason":"quotaExceeded"}],"message":"quota"}}""";

    public HttpStatusCode? UploadStatus { get; set; }

    public string UploadBody { get; set; } = "{}";

    public List<string> Requested { get; } = [];

    public List<string> Ranges { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        Requested.Add(url);

        if (url.Contains("oauth2", StringComparison.Ordinal) || url.Contains("token", StringComparison.Ordinal))
        {
            return Json(HttpStatusCode.OK, """{"access_token":"taze-jeton","expires_in":3600}""");
        }

        if (request.Method == HttpMethod.Post && url.Contains("uploadType=resumable", StringComparison.Ordinal))
        {
            if (SessionStatus is { } status)
            {
                return Json(status, SessionBody);
            }

            var response = Json(HttpStatusCode.OK, "{}");
            response.Headers.Location = new Uri(Session);

            return response;
        }

        if (request.Method == HttpMethod.Put)
        {
            var range = request.Content?.Headers.TryGetValues("Content-Range", out var values) == true
                ? values.FirstOrDefault()
                : null;

            Ranges.Add(range ?? "-");

            if (UploadStatus is { } upload)
            {
                return Json(upload, UploadBody);
            }

            // Boyut sorgusu: `bytes */toplam`.
            if (range?.Contains("*/", StringComparison.Ordinal) == true)
            {
                return AlreadyComplete
                    ? Json(HttpStatusCode.OK, """{"id":"v-eski","status":{"privacyStatus":"private"}}""")
                    : Resume(Received);
            }

            var body = request.Content is null
                ? []
                : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            Received += Math.Min(body.Length, AcceptPerChunk);

            return Received >= Total
                ? Json(HttpStatusCode.OK, """{"id":"v-99","status":{"privacyStatus":"public"}}""")
                : Resume(Received);
        }

        return Json(HttpStatusCode.NotFound, "{}");
    }

    private static HttpResponseMessage Resume(long received)
    {
        var response = new HttpResponseMessage((HttpStatusCode)308)
        {
            Content = new StringContent(string.Empty),
        };

        response.Headers.TryAddWithoutValidation("Range", $"bytes=0-{received - 1}");

        return response;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

/// YouTube yayıncısı (P1-24, P1-25).
///
/// ANAHTAR OLMADAN SINANIYOR: adres parametrik olduğu için sahte bir
/// sunucuya yöneltiliyor. Protokolün tamamı (oturum açma, parça
/// yükleme, 308 ile devam, çökme kurtarma) gerçek bir hesap olmadan
/// koşuyor.
public sealed class YouTubePublisherTests
{
    private static readonly Uri Fake = new("https://sahte.test/upload/videos");

    private static YouTubeOptions Options(int chunk = 262_144) => new()
    {
        UploadAddress = Fake,
        ApiAddress = new Uri("https://sahte.test/api/"),
        TokenAddress = new Uri("https://sahte.test/oauth2/token"),
        ChunkBytes = chunk,
    };

    private static YouTubePublisher Publisher(
        YouTubeServer server, YouTubeOptions? options = null, bool withAccessToken = true)
        => new(
            new HttpClient(server),
            options ?? Options(),
            new FixedCredentials(withAccessToken
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["YOUTUBE_ACCESS_TOKEN"] = "jeton",
                }
                : new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["YOUTUBE_REFRESH_TOKEN"] = "yenileme",
                    ["YOUTUBE_CLIENT_ID"] = "kimlik",
                    ["YOUTUBE_CLIENT_SECRET"] = "sir",
                }));

    private static (string Path, long Size) Video(int bytes = 600_000)
    {
        var path = Path.Combine(Path.GetTempPath(), $"bmai-yt-{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(path, new byte[bytes]);

        return (path, bytes);
    }

    private static PublishRequest Request(
        string path, Visibility visibility = Visibility.Public, string? resume = null)
        => new()
        {
            VideoPath = path,
            Visibility = visibility,
            IdempotencyKey = "anahtar",
            ResumeToken = resume,
            Metadata = new PublishMetadata
            {
                Title = "Başlık",
                Description = "Açıklama",
                Language = LanguageTag.Create("tr-TR"),
            },
        };

    private static ProviderContext Context()
        => new() { IdempotencyKey = "anahtar", CorrelationId = "test" };

    /* ---- yükleme ---- */

    /// PARÇA PARÇA YÜKLENİYOR VE TAMAMLANIYOR.
    [Fact]
    public async Task ParcaliYukleme_Tamamlaniyor()
    {
        var (path, size) = Video();
        var server = new YouTubeServer { Total = size };

        try
        {
            var result = await Publisher(server).PublishAsync(
                Request(path), Context(), CancellationToken.None);

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);
            Assert.Equal("v-99", result.Value.Value.ExternalId);

            // BİRDEN FAZLA PARÇA: 600 KB, 256 KB'lık parçalarla üçe
            // bölünüyor. Tek istekte gönderilseydi ağdaki bir kesinti
            // bütün bant genişliğini çöpe atardı.
            Assert.True(server.Ranges.Count >= 3, $"Parça sayısı: {server.Ranges.Count}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// PARÇA BOYUTU 256 KiB'İN KATINA HİZALANIYOR.
    ///
    /// Google'ın protokolü bunu şart koşuyor ve katı olmayan bir parça
    /// 400 ile reddediliyor — hata mesajı sebebini söylemeden.
    [Fact]
    public async Task ParcaBoyutu_Hizalaniyor()
    {
        var (path, size) = Video(600_000);
        var server = new YouTubeServer { Total = size };

        try
        {
            await Publisher(server, Options(chunk: 300_000)).PublishAsync(
                Request(path), Context(), CancellationToken.None);

            var first = server.Ranges.First(r => !r.Contains("*/", StringComparison.Ordinal));

            // 300.000 -> 262.144 (bir kat). Aralık `bytes 0-262143/...`
            Assert.StartsWith("bytes 0-262143/", first, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /* ---- çökme kurtarma ---- */

    /// ***TAMAMLANMIŞ OTURUM İKİNCİ KEZ YÜKLENMİYOR.***
    ///
    /// BU DOSYANIN EN ÖNEMLİ TESTİ. Çökme, yükleme ile veritabanı
    /// yazımı arasında olabiliyor: video YouTube'da var ama bizim
    /// kaydımızda yok. Yeniden yüklemek AYNI VİDEOYU İKİ KEZ
    /// yayınlamak ve 1.600 birim kotayı ikinci kez harcamak olurdu
    /// (§2.4/16).
    [Fact]
    public async Task TamamlanmisOturum_YenidenYuklenmiyor()
    {
        var (path, size) = Video();

        var server = new YouTubeServer
        {
            Total = size,
            AlreadyComplete = true,
        };

        try
        {
            var result = await Publisher(server).PublishAsync(
                Request(path, resume: "https://yukle.test/oturum/1"),
                Context(),
                CancellationToken.None);

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);
            Assert.Equal("v-eski", result.Value.Value.ExternalId);

            // VE HİÇ BAYT GÖNDERİLMEDİ: tek istek boyut sorgusuydu.
            Assert.All(server.Ranges, r => Assert.Contains("*/", r, StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// DEVAM NOKTASI SUNUCUDAN OKUNUYOR, BİZDEN DEĞİL.
    ///
    /// Gönderdiğimiz bir parça karşı tarafa hiç ulaşmamış olabilir;
    /// oradan devam etmek dosyada delik bırakır ve YouTube bozuk bir
    /// video kabul eder.
    [Fact]
    public async Task DevamNoktasi_SunucudanOkunuyor()
    {
        var (path, size) = Video();

        var server = new YouTubeServer
        {
            Total = size,

            // Sunucu her parçanın yalnızca yarısını alıyor: bizim
            // "gönderdim" saydığımız bayt ile onun aldığı ayrışıyor.
            AcceptPerChunk = 131_072,
        };

        try
        {
            var result = await Publisher(server).PublishAsync(
                Request(path), Context(), CancellationToken.None);

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);

            // TOPLAM ALINAN, DOSYA BOYUTUNA EŞİT: delik yok.
            Assert.Equal(size, server.Received);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// İLERLEME YOKSA SONSUZ DÖNGÜ DEĞİL, ERTELEME.
    [Fact]
    public async Task IlerlemeYok_KaynakHatasi()
    {
        var (path, size) = Video();

        var server = new YouTubeServer
        {
            Total = size,
            AcceptPerChunk = 0,
        };

        try
        {
            var result = await Publisher(server).PublishAsync(
                Request(path), Context(), CancellationToken.None);

            Assert.True(result.IsFailure);
            Assert.Equal(ErrorKind.Resource, result.Error.Kind);
            Assert.Equal("youtube.no_progress", result.Error.Code);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// DÜŞMÜŞ OTURUM GEÇİCİ HATA.
    ///
    /// Oturum bir hafta ömürlü; yeniden denemek yeni bir oturum açar.
    /// Kalıcı saymak, düzelebilir bir durumu ölü mektuba göndermek
    /// olurdu.
    [Fact]
    public async Task DusmusOturum_GeciciHata()
    {
        var (path, size) = Video();

        var server = new YouTubeServer
        {
            Total = size,
            UploadStatus = HttpStatusCode.NotFound,
        };

        try
        {
            var result = await Publisher(server).PublishAsync(
                Request(path, resume: "https://yukle.test/oturum/1"),
                Context(),
                CancellationToken.None);

            Assert.True(result.IsFailure);
            Assert.Equal(ErrorKind.Transient, result.Error.Kind);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /* ---- kota ---- */

    /// ***KOTA HATASI KAYNAK SINIFI, KALICI DEĞİL.***
    ///
    /// Yarın kota sıfırlanıyor ve iş o zaman koşabilir. Kalıcı saymak,
    /// üretilmiş bir videoyu çöpe atmak olurdu (ADR-011).
    [Fact]
    public async Task KotaDoldu_KaynakHatasi()
    {
        var (path, size) = Video();

        var server = new YouTubeServer
        {
            Total = size,
            SessionStatus = HttpStatusCode.Forbidden,
        };

        try
        {
            var result = await Publisher(server).PublishAsync(
                Request(path), Context(), CancellationToken.None);

            Assert.True(result.IsFailure);
            Assert.Equal(ErrorKind.Resource, result.Error.Kind);
            Assert.Equal("youtube.quota", result.Error.Code);

            // ERTELEME KOTA SIFIRLANMASINA KADAR: rastgele bir bekleme,
            // aynı hatayı saatlerce tekrarlamak olurdu.
            Assert.NotNull(result.Error.RetryAfter);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// YAYIN MALİYETİ KOTA DEFTERİYLE AYNI.
    ///
    /// İki ayrı sayı olsaydı zamanlayıcı yanlış planlar ve gün
    /// ortasında kota biterdi.
    [Fact]
    public void YayinMaliyeti_KotaDefteriyleAyni()
        => Assert.Equal(
            QuotaLedger.UploadCost,
            new YouTubePublisher(new HttpClient(new YouTubeServer())).Capabilities.QuotaCostPerPublish);

    /* ---- kimlik ---- */

    /// ERİŞİM JETONU YOKSA YENİLEME JETONUYLA ALINIYOR.
    ///
    /// Jeton bir saat ömürlü. Gece koşan bir fabrikada "jeton süresi
    /// doldu" hatası, sabaha kadar hiçbir videonun yayınlanmaması
    /// demek.
    [Fact]
    public async Task ErisimJetonuYok_YenilemeKullaniliyor()
    {
        var (path, size) = Video();
        var server = new YouTubeServer { Total = size };

        try
        {
            var result = await Publisher(server, withAccessToken: false).PublishAsync(
                Request(path), Context(), CancellationToken.None);

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);
            Assert.Contains(server.Requested, r => r.Contains("oauth2", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// HİÇ KİMLİK YOKSA KALICI HATA.
    [Fact]
    public async Task KimlikYok_KaliciHata()
    {
        var publisher = new YouTubePublisher(
            new HttpClient(new YouTubeServer()),
            Options(),
            new FixedCredentials(new Dictionary<string, string>(StringComparer.Ordinal)));

        var result = await publisher.TokenAsync(CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("youtube.no_credentials", result.Error.Code);
    }

    /* ---- görünürlük ---- */

    /// GERÇEKLEŞEN GÖRÜNÜRLÜK PLATFORMDAN OKUNUYOR.
    ///
    /// Zamanlanmış bir yükleme gizli başlıyor; "herkese açık
    /// yayınlandı" demek yanlış olurdu.
    [Fact]
    public async Task Gorunurluk_PlatformdanOkunuyor()
    {
        var (path, size) = Video();
        var server = new YouTubeServer { Total = size };

        try
        {
            // Sunucu `public` dönüyor; istek `Unlisted` diyordu.
            var result = await Publisher(server).PublishAsync(
                Request(path, Visibility.Unlisted), Context(), CancellationToken.None);

            Assert.Equal(Visibility.Public, result.Value.Value.Visibility);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// ANAHTAR OTURUM ADRESİ DEĞİLSE ARAMA YAPILMIYOR.
    ///
    /// YouTube'da keyfi bir idempotency anahtarıyla "bunu yükledim mi"
    /// diye sorulacak uç nokta YOK. Uydurma bir arama (`search`) hem
    /// 100 birim kota harcar hem eşleşmeyi şansa bırakırdı.
    [Fact]
    public async Task OturumOlmayanAnahtar_AramaYapilmiyor()
    {
        var server = new YouTubeServer();

        var result = await Publisher(server).FindExistingAsync("duz-anahtar", CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.Empty(server.Requested);
    }
}
