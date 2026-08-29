using System.Text.Json;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Nodes;
using BytemountsAiStudio.Providers.Fake;
using BytemountsAiStudio.Workflow.Engine;

namespace BytemountsAiStudio.Nodes.Tests;

/// Yayın node'u (P1-25, P6-01, P6-02).
///
/// BORU HATTININ UCU BURASIYDI VE YOKTU. `IPublisher` yazılmıştı,
/// adaptörler yazılmıştı, ama hiçbir node onları çağırmıyordu:
/// üretilen video `output/` klasöründe kalıyordu. Bu, "yazıldı ama
/// bağlanmadı" hatasının en pahalı hâliydi — eksik olan şey ürünün
/// kendisi.
public sealed class PublishHandlerTests
{
    private const string FullContext = """
        {
          "topic": { "topic": "Göbeklitepe", "language": "tr-TR" },
          "render": { "output_path": "C:/tmp/video.mp4" },
          "seo": { "title": "Başlık", "description": "Açıklama", "tags": ["tarih", "arkeoloji"] },
          "thumbnail": { "asset": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }
        }
        """;

    private static NodeContext Context(string runContext, string config = "{}", string nodeId = "yayin")
    {
        using var contextDocument = JsonDocument.Parse(runContext);
        using var configDocument = JsonDocument.Parse(config);

        return new NodeContext
        {
            RunId = Guid.CreateVersion7(),
            NodeId = nodeId,
            NodeType = "publish.upload",
            Attempt = 1,
            Config = configDocument.RootElement.Clone(),
            RunContext = contextDocument.RootElement.Clone(),
            IdempotencyKey = "anahtar-1",
            CorrelationId = "test",
        };
    }

    /// Sahte yayıncının platform adı `fake`; ayar açıkça veriliyor
    /// çünkü varsayılan `youtube` ve sessizce başka bir yayıncıya
    /// düşmek, videoyu yanlış yere yollamak olurdu.
    private const string Fake = """{"platform":"fake"}""";

    private static PublishHandler Handler(params IPublisher[] publishers)
        => new(publishers.Length > 0 ? publishers : [new FakePublisher()]);

    /* ---- mutlu yol ---- */

    /// YAYIN SONUCU KAYDA GİRİYOR.
    [Fact]
    public async Task Yayin_SonucKaydaGiriyor()
    {
        var result = await Handler().ExecuteAsync(
            Context(FullContext, Fake), CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);
        Assert.Equal("fake", result.Value.GetProperty("platform").GetString());
        Assert.False(string.IsNullOrWhiteSpace(result.Value.GetProperty("external_id").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(result.Value.GetProperty("url").GetString()));
    }

    /// VARSAYILAN GÖRÜNÜRLÜK GİZLİ.
    ///
    /// Otomatik bir hattın varsayılanı "herkese açık" olsaydı, ilk
    /// yanlış yapılandırma yayına çıkmış bir videoyla sonuçlanır ve
    /// geri alınamazdı.
    [Fact]
    public void VarsayilanGorunurluk_Gizli()
    {
        Assert.Equal(Visibility.Private, PublishHandler.VisibilityOf(null));
        Assert.Equal(Visibility.Private, PublishHandler.VisibilityOf("bilinmeyen"));
        Assert.Equal(Visibility.Public, PublishHandler.VisibilityOf("public"));
    }

    /// AYNI ANAHTARLA İKİNCİ YAYIN AYNI VİDEOYU DÖNDÜRÜYOR.
    ///
    /// Upload başarılı olup veritabanı yazımı çökerse retry ikinci
    /// kopyayı yüklerdi (§2.4/16). Sahte yayıncı bu kuralı hatırlıyor,
    /// yani kural sahte hatta da sınanıyor.
    [Fact]
    public async Task AyniAnahtar_IkinciYayinAyniVideo()
    {
        var publisher = new FakePublisher();
        var handler = Handler(publisher);

        var first = await handler.ExecuteAsync(Context(FullContext, Fake), CancellationToken.None);
        var second = await handler.ExecuteAsync(Context(FullContext, Fake), CancellationToken.None);

        Assert.Equal(
            first.Value.GetProperty("external_id").GetString(),
            second.Value.GetProperty("external_id").GetString());
    }

    /* ---- eksik girdi ---- */

    /// VİDEOSUZ YAYIN YOK.
    [Fact]
    public async Task VideoYok_Reddediliyor()
    {
        var result = await Handler().ExecuteAsync(
            Context("""{"seo":{"title":"x"}}"""), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("publish.no_video", result.Error.Code);
    }

    /// BAŞLIKSIZ YAYIN YOK.
    ///
    /// Dosya adını başlık yapmak, izleyicinin gördüğü ilk şeyin bir
    /// GUID olması demekti.
    [Fact]
    public async Task BaslikYok_Reddediliyor()
    {
        var result = await Handler().ExecuteAsync(
            Context("""{"render":{"output_path":"v.mp4"}}"""), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("publish.no_title", result.Error.Code);
    }

    /* ---- platform ---- */

    /// PLATFORM AYARDAN GELİYOR.
    ///
    /// Aynı graf farklı kanallarda farklı platforma yayınlayabilmeli;
    /// kodda seçilseydi her platform yeni bir node tipi olurdu.
    [Fact]
    public async Task Platform_AyardanGeliyor()
    {
        var result = await Handler(new FakePublisher(), new SecondPlatform()).ExecuteAsync(
            Context(FullContext, """{"platform":"tiktok"}"""),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);
        Assert.Equal("tiktok", result.Value.GetProperty("platform").GetString());
    }

    /// ***TANINMAYAN PLATFORM SESSİZ GEÇİLMİYOR.***
    ///
    /// Sessiz geçmek, hiçbir yere yayınlanmamış bir videoyu
    /// "yayınlandı" diye işaretlemek olurdu — ve bu, ancak kimse
    /// videoyu bulamadığında fark edilirdi.
    [Fact]
    public async Task TaninmayanPlatform_Reddediliyor()
    {
        var result = await Handler().ExecuteAsync(
            Context(FullContext, """{"platform":"vimeo"}"""),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("publish.unknown_platform", result.Error.Code);

        // TANIMLI PLATFORMLAR DA YAZILI: hangisini yazacağını aramak
        // zorunda kalmasın.
        Assert.Contains("fake", result.Error.Message, StringComparison.Ordinal);
    }

    /* ---- hata sınıfı ---- */

    /// ***KAYNAK HATASI OLDUĞU GİBİ GEÇİYOR.***
    ///
    /// Kota hatası ERTELEME (ADR-011): yarın kota sıfırlanıyor ve iş o
    /// zaman koşabilir. Node burada kalıcıya çevirseydi, üretilmiş bir
    /// video çöpe giderdi.
    [Fact]
    public async Task KotaHatasi_KaynakOlarakGeciyor()
    {
        var result = await Handler(new QuotaExhausted()).ExecuteAsync(
            Context(FullContext, """{"platform":"youtube"}"""),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(Core.Errors.ErrorKind.Resource, result.Error.Kind);
    }

    /* ---- kayıt ---- */

    /// NODE TİPİ TANIMLI.
    ///
    /// Kayıtlı olmasaydı yayın içeren bir graf "bilinmeyen node tipi"
    /// diye reddedilirdi — onay kapısıyla aynı ders.
    [Fact]
    public void NodeTipi_Tanimli()
        => Assert.Contains("publish.upload", NodeHandlerRegistration.KnownNodeTypes);

    /// YAYIN KUYRUĞU AYRI.
    ///
    /// Yükleme dakikalarca sürüyor ve ağa çıkıyor; render kuyruğunda
    /// olsaydı bir yükleme, sırada bekleyen bütün render'ları
    /// bloklardı.
    [Fact]
    public void Kuyruk_Upload()
        => Assert.Equal(QueueClass.Upload, Handler().Queue);

    /* ---- yardımcılar ---- */

    private sealed class SecondPlatform : IPublisher
    {
        public string Key => "tiktok";

        public string Platform => "tiktok";

        public PublishCapabilities Capabilities { get; } = new()
        {
            MaxTitleLength = 2_200,
            MaxDescriptionLength = 2_200,
            MaxTagsTotalLength = 2_200,
            MaxDuration = new Core.Time.Ms(600_000),
            SupportsScheduling = false,
            SupportsCustomThumbnail = false,
            QuotaCostPerPublish = 1,
        };

        public Task<Core.Result<ProviderResponse<PublishResult>>> PublishAsync(
            PublishRequest request, ProviderContext context, CancellationToken cancellationToken)
            => Task.FromResult(Core.Result.Success(ProviderResponse<PublishResult>.Free(
                new PublishResult
                {
                    ExternalId = "tt-1",
                    Url = new Uri("https://www.tiktok.com/video/tt-1"),
                    Visibility = Visibility.Public,
                })));

        public Task<Core.Result<PublishResult?>> FindExistingAsync(
            string idempotencyKey, CancellationToken cancellationToken)
            => Task.FromResult(Core.Result.Success<PublishResult?>(null));
    }

    private sealed class QuotaExhausted : IPublisher
    {
        public string Key => "youtube";

        public string Platform => "youtube";

        public PublishCapabilities Capabilities { get; } = new()
        {
            MaxTitleLength = 100,
            MaxDescriptionLength = 5_000,
            MaxTagsTotalLength = 500,
            MaxDuration = new Core.Time.Ms(600_000),
            SupportsScheduling = true,
            SupportsCustomThumbnail = true,
            QuotaCostPerPublish = 1_600,
        };

        public Task<Core.Result<ProviderResponse<PublishResult>>> PublishAsync(
            PublishRequest request, ProviderContext context, CancellationToken cancellationToken)
            => Task.FromResult(Core.Result.Failure<ProviderResponse<PublishResult>>(
                Core.Errors.Error.Resource("youtube.quota", "kota bitti", TimeSpan.FromHours(6))));

        public Task<Core.Result<PublishResult?>> FindExistingAsync(
            string idempotencyKey, CancellationToken cancellationToken)
            => Task.FromResult(Core.Result.Success<PublishResult?>(null));
    }
}
