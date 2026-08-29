using System.Net;
using System.Text;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Providers.Open;

namespace BytemountsAiStudio.Providers.Open.Tests;

/// Yol PARÇASINA göre eşleşen, sıralı cevap veren sahte sunucu.
///
/// Yoklama (polling) sınamak için şart: aynı adrese arka arkaya
/// gelen isteklere farklı cevap verebilmek gerekiyor. Tek cevaplı bir
/// sahte sunucuyla "hazır olana kadar bekliyor" iddiası
/// sınanamazdı — sonsuz döngü ya da anında başarı olurdu.
internal sealed class SequenceHandler : HttpMessageHandler
{
    private readonly List<(string Fragment, Queue<(HttpStatusCode Status, string Body)> Responses)> _routes = [];

    /// Gelen isteklerin yolları — "bu adım HİÇ çağrılmadı" iddiasının
    /// sınanabilmesi için.
    public List<string> Requested { get; } = [];

    public SequenceHandler Route(string fragment, params string[] bodies)
    {
        var queue = new Queue<(HttpStatusCode, string)>();

        foreach (var body in bodies)
        {
            queue.Enqueue((HttpStatusCode.OK, body));
        }

        _routes.Add((fragment, queue));
        return this;
    }

    public SequenceHandler Route(string fragment, HttpStatusCode status, string body = "{}")
    {
        var queue = new Queue<(HttpStatusCode, string)>();
        queue.Enqueue((status, body));

        _routes.Add((fragment, queue));
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        Requested.Add(url);

        foreach (var (fragment, responses) in _routes)
        {
            if (!url.Contains(fragment, StringComparison.Ordinal))
            {
                continue;
            }

            // SON CEVAP TEKRARLANIYOR: yoklama sınırını sınayan testte
            // kuyruğun tükenmesi, testin sınadığı şeyi değiştirirdi.
            var (status, body) = responses.Count > 1 ? responses.Dequeue() : responses.Peek();

            return Task.FromResult(new HttpResponseMessage(status)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            RequestMessage = request,
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });
    }
}

internal sealed class FixedCredentials(Dictionary<string, string> values) : ICredentialSource
{
    public string? Get(string name) => values.GetValueOrDefault(name);
}

/// TikTok ve Instagram yayıncıları (P6-01, P6-02).
///
/// ANAHTAR OLMADAN SINANIYOR: adres parametrik olduğu için sahte bir
/// sunucuya yöneltiliyor. "Anahtar gelince deneriz" demek, anahtarın
/// geldiği gün yazılmamış kodu keşfetmek olurdu.
public sealed class PublisherTests
{
    private static readonly Uri Fake = new("https://sahte.test/v2/");

    private static PublishRequest Request(
        string videoPath, Visibility visibility = Visibility.Public, Uri? videoUrl = null)
        => new()
        {
            VideoPath = videoPath,
            VideoUrl = videoUrl,
            Visibility = visibility,
            IdempotencyKey = "anahtar-1",
            Metadata = new PublishMetadata
            {
                Title = "Başlık",
                Description = "Açıklama",
                Language = LanguageTag.Create("tr-TR"),
            },
        };

    private static ProviderContext Context()
        => new() { IdempotencyKey = "anahtar-1", CorrelationId = "test" };

    private static string TempVideo()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bmai-{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(path, new byte[1024]);

        return path;
    }

    /* ================= TikTok ================= */

    private static TikTokPublisher TikTok(SequenceHandler handler, TikTokOptions? options = null)
        => new(
            new HttpClient(handler),
            options ?? new TikTokOptions
            {
                BaseAddress = Fake,
                PollInterval = TimeSpan.FromMilliseconds(1),
            },
            new FixedCredentials(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TIKTOK_ACCESS_TOKEN"] = "jeton",
            }));

    private static SequenceHandler TikTokHappyPath(string privacy = "PUBLIC_TO_EVERYONE")
        => new SequenceHandler()
            .Route(
                "creator_info",
                "{\"data\":{\"privacy_level_options\":[\"" + privacy + "\",\"SELF_ONLY\"]}}")
            .Route("video/init", """{"data":{"publish_id":"p-1","upload_url":"https://yukle.test/p-1"}}""")
            .Route("yukle.test", "{}")
            .Route("status/fetch",
                """{"data":{"status":"PROCESSING_UPLOAD"}}""",
                """{"data":{"status":"PUBLISH_COMPLETE","publicaly_available_post_id":["v-42"]}}""");

    /// ÜÇ ADIM DA KOŞUYOR VE SONUÇ ÖLÇÜLEN DEĞERLERDEN GELİYOR.
    [Fact]
    public async Task TikTok_UcAdimVeYayin()
    {
        var handler = TikTokHappyPath();
        var path = TempVideo();

        try
        {
            var result = await TikTok(handler).PublishAsync(
                Request(path), Context(), CancellationToken.None);

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);
            Assert.Equal("v-42", result.Value.Value.ExternalId);
            Assert.Equal(Visibility.Public, result.Value.Value.Visibility);

            // YÜKLEME GERÇEKTEN YAPILDI: `init` alıp yüklemeden
            // "yayınlandı" demek, TikTok'un hiç görmediği bir videoyu
            // yayınlanmış saymak olurdu.
            Assert.Contains(handler.Requested, r => r.Contains("yukle.test", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// ***İZİN VERİLMEYEN GÖRÜNÜRLÜKTE YAYIN HİÇ BAŞLAMIYOR.***
    ///
    /// BU DOSYANIN EN ÖNEMLİ TESTİ. Denetimden geçmemiş uygulamalar
    /// yalnızca `SELF_ONLY` yayınlayabiliyor ve TikTok bu durumda hata
    /// VERMİYOR: videoyu sessizce gizli yayınlıyor. Sistem "herkese
    /// açık yayınlandı" der, video kimseye görünmez ve bu ancak
    /// haftalar sonra "izlenme neden sıfır" diye fark edilir.
    [Fact]
    public async Task TikTok_IzinsizGorunurluk_YuklemeYapilmiyor()
    {
        var handler = TikTokHappyPath(privacy: "SELF_ONLY");
        var path = TempVideo();

        try
        {
            var result = await TikTok(handler).PublishAsync(
                Request(path, Visibility.Public), Context(), CancellationToken.None);

            Assert.True(result.IsFailure);
            Assert.Equal("tiktok.visibility_not_allowed", result.Error.Code);

            // VE VİDEO HİÇ YÜKLENMEDİ: boşuna yüklemek, hem kotayı hem
            // bant genişliğini harcayıp sonucu yine reddetmek olurdu.
            Assert.DoesNotContain(handler.Requested,
                r => r.Contains("yukle.test", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// GÖRÜNÜRLÜK OKUNAMAZSA YAYIN YAPILMIYOR.
    ///
    /// Boş liste "her şey serbest" değil, "cevabı anlamadım" demek —
    /// ve o hâlde yayınlamak görünürlüğü şansa bırakmak olurdu.
    [Fact]
    public async Task TikTok_GorunurlukOkunamadi_Reddediliyor()
    {
        var handler = new SequenceHandler().Route("creator_info", """{"data":{}}""");
        var path = TempVideo();

        try
        {
            var result = await TikTok(handler).PublishAsync(
                Request(path), Context(), CancellationToken.None);

            Assert.True(result.IsFailure);
            Assert.Equal("tiktok.no_privacy_options", result.Error.Code);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// YOKLAMA SINIRI: HATA DEĞİL, ERTELEME.
    ///
    /// Video muhtemelen hâlâ işleniyor. Kalıcı hata saymak, gerçekte
    /// yayınlanmış bir videoyu "düştü" diye işaretleyip yeniden
    /// yükletmek olurdu (ADR-011).
    [Fact]
    public async Task TikTok_YoklamaSiniri_KaynakHatasi()
    {
        var handler = new SequenceHandler()
            .Route("creator_info", """{"data":{"privacy_level_options":["PUBLIC_TO_EVERYONE"]}}""")
            .Route("video/init", """{"data":{"publish_id":"p-1","upload_url":"https://yukle.test/p-1"}}""")
            .Route("yukle.test", "{}")
            .Route("status/fetch", """{"data":{"status":"PROCESSING_UPLOAD"}}""");

        var path = TempVideo();

        try
        {
            var publisher = TikTok(handler, new TikTokOptions
            {
                BaseAddress = Fake,
                PollInterval = TimeSpan.FromMilliseconds(1),
                MaxPollAttempts = 3,
            });

            var result = await publisher.PublishAsync(Request(path), Context(), CancellationToken.None);

            Assert.True(result.IsFailure);
            Assert.Equal("tiktok.still_processing", result.Error.Code);
            Assert.Equal(ErrorKind.Resource, result.Error.Kind);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// PLATFORM REDDETTİYSE KALICI HATA.
    [Fact]
    public async Task TikTok_YayinReddedildi_KaliciHata()
    {
        var handler = new SequenceHandler()
            .Route("creator_info", """{"data":{"privacy_level_options":["PUBLIC_TO_EVERYONE"]}}""")
            .Route("video/init", """{"data":{"publish_id":"p-1","upload_url":"https://yukle.test/p-1"}}""")
            .Route("yukle.test", "{}")
            .Route("status/fetch", """{"data":{"status":"FAILED","fail_reason":"telif"}}""");

        var path = TempVideo();

        try
        {
            var result = await TikTok(handler).PublishAsync(
                Request(path), Context(), CancellationToken.None);

            Assert.True(result.IsFailure);
            Assert.Equal("tiktok.publish_failed", result.Error.Code);
            Assert.Contains("telif", result.Error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// ADRES PARAMETRİK: istekler ayarlanan sunucuya gidiyor.
    ///
    /// Kodda sabit olsaydı bu test yazılamazdı — ve yazılamayan test,
    /// çalışmayan kodun sessiz kalması demek.
    [Fact]
    public async Task TikTok_AdresParametrik()
    {
        var handler = TikTokHappyPath();
        var path = TempVideo();

        try
        {
            await TikTok(handler).PublishAsync(Request(path), Context(), CancellationToken.None);

            Assert.All(
                handler.Requested.Where(r => !r.Contains("yukle.test", StringComparison.Ordinal)),
                r => Assert.StartsWith("https://sahte.test/v2/", r, StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /* ================= Instagram ================= */

    private static InstagramPublisher Instagram(
        SequenceHandler handler, InstagramOptions? options = null)
        => new(
            new HttpClient(handler),
            options ?? new InstagramOptions
            {
                BaseAddress = Fake,
                PollInterval = TimeSpan.FromMilliseconds(1),
            },
            new FixedCredentials(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["INSTAGRAM_ACCESS_TOKEN"] = "jeton",
                ["INSTAGRAM_USER_ID"] = "hesap-1",
            }));

    /// İKİ ADIM DA KOŞUYOR.
    [Fact]
    public async Task Instagram_KonteynerSonraYayin()
    {
        var handler = new SequenceHandler()
            .Route("hesap-1/media_publish", """{"id":"reel-9"}""")
            .Route("hesap-1/media", """{"id":"kon-1"}""")
            .Route("kon-1?fields",
                """{"status_code":"IN_PROGRESS"}""",
                """{"status_code":"FINISHED"}""");

        var result = await Instagram(handler).PublishAsync(
            Request("yok.mp4", videoUrl: new Uri("https://cdn.test/v.mp4")),
            Context(),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);
        Assert.Equal("reel-9", result.Value.Value.ExternalId);
    }

    /// ***VİDEO ADRESİ YOKSA YAYIN HİÇ DENENMİYOR.***
    ///
    /// Instagram dosya kabul etmiyor, ÇEKİYOR. Yerel bir dosya yolu
    /// Meta tarafında "medya indirilemedi" diye anlaşılmaz bir kodla
    /// düşüyor ve sebebini bulmak saatler alıyor.
    [Fact]
    public async Task Instagram_AdresYok_Reddediliyor()
    {
        var handler = new SequenceHandler();

        var result = await Instagram(handler).PublishAsync(
            Request("C:/yerel/video.mp4"), Context(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("instagram.no_public_url", result.Error.Code);
        Assert.Empty(handler.Requested);
    }

    /// GİZLİ REELS DİYE BİR ŞEY YOK.
    ///
    /// "Gizli yayınladık" demek, herkese açık olan bir videoyu gizli
    /// sanmak olurdu — ve bu, gizli kalması gereken bir içerik için
    /// geri alınamaz bir hata.
    [Fact]
    public async Task Instagram_GizliIstendi_Reddediliyor()
    {
        var result = await Instagram(new SequenceHandler()).PublishAsync(
            Request("v.mp4", Visibility.Private, new Uri("https://cdn.test/v.mp4")),
            Context(),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("instagram.public_only", result.Error.Code);
    }

    /// META İŞLEYEMEDİYSE KALICI HATA.
    [Fact]
    public async Task Instagram_KonteynerHatasi_Kalici()
    {
        var handler = new SequenceHandler()
            .Route("hesap-1/media", """{"id":"kon-1"}""")
            .Route("kon-1?fields", """{"status_code":"ERROR","status":"bicim desteklenmiyor"}""");

        var result = await Instagram(handler).PublishAsync(
            Request("v.mp4", videoUrl: new Uri("https://cdn.test/v.mp4")),
            Context(),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("instagram.container_failed", result.Error.Code);
        Assert.Equal(ErrorKind.Permanent, result.Error.Kind);
    }

    /// SÜRESİ DOLAN KONTEYNER GEÇİCİ HATA.
    ///
    /// Yeniden denemek ANLAMLI: yeni bir konteyner açılacak. Kalıcı
    /// saymak, düzelebilir bir durumu ölü mektuba göndermek olurdu.
    [Fact]
    public async Task Instagram_SuresiDolanKonteyner_Gecici()
    {
        var handler = new SequenceHandler()
            .Route("hesap-1/media", """{"id":"kon-1"}""")
            .Route("kon-1?fields", """{"status_code":"EXPIRED"}""");

        var result = await Instagram(handler).PublishAsync(
            Request("v.mp4", videoUrl: new Uri("https://cdn.test/v.mp4")),
            Context(),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Transient, result.Error.Kind);
    }

    /// YOKLAMA SINIRI: ERTELEME.
    [Fact]
    public async Task Instagram_YoklamaSiniri_KaynakHatasi()
    {
        var handler = new SequenceHandler()
            .Route("hesap-1/media", """{"id":"kon-1"}""")
            .Route("kon-1?fields", """{"status_code":"IN_PROGRESS"}""");

        var publisher = Instagram(handler, new InstagramOptions
        {
            BaseAddress = Fake,
            PollInterval = TimeSpan.FromMilliseconds(1),
            MaxPollAttempts = 3,
        });

        var result = await publisher.PublishAsync(
            Request("v.mp4", videoUrl: new Uri("https://cdn.test/v.mp4")),
            Context(),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Resource, result.Error.Kind);
    }

    /// HIZ SINIRI DA ERTELEME.
    [Fact]
    public async Task Instagram_HizSiniri_KaynakHatasi()
    {
        var handler = new SequenceHandler()
            .Route("hesap-1/media", HttpStatusCode.TooManyRequests);

        var result = await Instagram(handler).PublishAsync(
            Request("v.mp4", videoUrl: new Uri("https://cdn.test/v.mp4")),
            Context(),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Resource, result.Error.Kind);
    }

    /* ---- yetenekler ---- */

    /// PLATFORM SINIRLARI BİLDİRİLİYOR.
    ///
    /// Sınırı modele bırakmak, 2.200 karakteri aşan bir altyazının
    /// API tarafında reddedilmesi ve sebebinin görünmemesi demekti.
    [Fact]
    public void Yetenekler_SinirlariBildiriyor()
    {
        var tiktok = new TikTokPublisher(new HttpClient(new SequenceHandler())).Capabilities;
        var instagram = new InstagramPublisher(new HttpClient(new SequenceHandler())).Capabilities;

        Assert.Equal(2_200, tiktok.MaxTitleLength);
        Assert.Equal(2_200, instagram.MaxTitleLength);

        // TikTok zamanlama DESTEKLEMİYOR: desteklediğini söylemek,
        // zamanlanmış bir yayının sessizce hemen çıkması olurdu.
        Assert.False(tiktok.SupportsScheduling);
        Assert.False(instagram.SupportsScheduling);
    }
}
