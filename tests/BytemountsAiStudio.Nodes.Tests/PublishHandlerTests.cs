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
        => Handler(new UnlimitedQuotaPool(), publishers);

    private static PublishHandler Handler(IQuotaPool quota, params IPublisher[] publishers)
        => new(publishers.Length > 0 ? publishers : [new FakePublisher()], quota);

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

    /* ---- kota havuzu (P4-04) ---- */

    /// ***KOTA YÜKLEMEDEN ÖNCE REZERVE EDİLİYOR.***
    ///
    /// Kotayı yüklemeye başladıktan sonra öğrenmek, dakikalarca bant
    /// genişliği harcayıp sonunda reddedilmek demek.
    [Fact]
    public async Task Kota_YuklemedenOnceRezerve()
    {
        var quota = new RecordingQuota();

        var result = await Handler(quota, new FakePublisher()).ExecuteAsync(
            Context(FullContext, Fake), CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);
        Assert.Equal(1, quota.Calls);

        // KAPAK VARSA MALİYETE GİRİYOR: kapak ayrı bir çağrı ve ayrı
        // 50 birim. Hep kapaksız varsaymak, kotayı eksik saymak olurdu.
        Assert.Equal(QuotaLedger.UploadCost + QuotaLedger.ThumbnailCost, quota.LastCost);
    }

    /// SEÇİLEN HESAP KAYDA GİRİYOR.
    ///
    /// Bir hesap kapanırsa "hangi videolar oradan gitti" sorusu
    /// cevaplanabilmeli. Havuzda on yedi proje varken bunu sonradan
    /// bulmanın yolu yok.
    [Fact]
    public async Task SecilenHesap_KaydaGiriyor()
    {
        var result = await Handler(new RecordingQuota(), new FakePublisher()).ExecuteAsync(
            Context(FullContext, Fake), CancellationToken.None);

        Assert.Equal("proje-01", result.Value.GetProperty("quota_account").GetString());
    }

    /// ***HAVUZ TÜKENDİĞİNDE ERTELEME, BAŞARISIZLIK DEĞİL.***
    ///
    /// Yarın kota sıfırlanıyor ve iş o zaman koşabilir; kalıcı saymak
    /// üretilmiş bir videoyu çöpe atmak olurdu (ADR-011).
    [Fact]
    public async Task HavuzTukendi_KaynakHatasi()
    {
        var result = await Handler(new ExhaustedQuota(), new FakePublisher()).ExecuteAsync(
            Context(FullContext, Fake), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(Core.Errors.ErrorKind.Resource, result.Error.Kind);
        Assert.Equal("publish.quota_exhausted", result.Error.Code);
    }

    /// ***HESAP YOKLUĞU KALICI HATA — KOTA BİTMESİ DEĞİL.***
    ///
    /// Beklemek hesabı var etmiyor. Erteleme saysaydık, hiç
    /// yapılandırılmamış bir sistem her gün sessizce bekler ve hiç
    /// uyarmazdı.
    [Fact]
    public async Task HesapYok_KaliciHata()
    {
        var result = await Handler(new NoAccountQuota(), new FakePublisher()).ExecuteAsync(
            Context(FullContext, Fake), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(Core.Errors.ErrorKind.Permanent, result.Error.Kind);
        Assert.Equal("publish.no_quota_account", result.Error.Code);
    }

    /// KOTA REDDEDİLDİYSE YÜKLEME HİÇ DENENMİYOR.
    [Fact]
    public async Task KotaReddedildi_YuklemeYok()
    {
        var publisher = new CountingPublisher();

        await Handler(new ExhaustedQuota(), publisher).ExecuteAsync(
            Context(FullContext, """{"platform":"sayan"}"""), CancellationToken.None);

        Assert.Equal(0, publisher.Calls);
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

    private sealed class RecordingQuota : IQuotaPool
    {
        public int Calls { get; private set; }

        public int LastCost { get; private set; }

        public Task<Core.Result<PoolDecision>> ReserveAsync(
            string providerKey, Guid? channelId, int cost, CancellationToken cancellationToken)
        {
            Calls++;
            LastCost = cost;

            return Task.FromResult(Core.Result.Success(new PoolDecision(
                PoolOutcome.Selected, "proje-01", cost, 8_400, 50_000, "seçildi")));
        }
    }

    private sealed class ExhaustedQuota : IQuotaPool
    {
        public Task<Core.Result<PoolDecision>> ReserveAsync(
            string providerKey, Guid? channelId, int cost, CancellationToken cancellationToken)
            => Task.FromResult(Core.Result.Success(new PoolDecision(
                PoolOutcome.Exhausted, null, cost, 0, 900, "hepsi dolu")));
    }

    private sealed class NoAccountQuota : IQuotaPool
    {
        public Task<Core.Result<PoolDecision>> ReserveAsync(
            string providerKey, Guid? channelId, int cost, CancellationToken cancellationToken)
            => Task.FromResult(Core.Result.Success(new PoolDecision(
                PoolOutcome.NoAccounts, null, cost, 0, 0, "hesap yok")));
    }

    private sealed class CountingPublisher : IPublisher
    {
        public int Calls { get; private set; }

        public string Key => "sayan";

        public string Platform => "sayan";

        public PublishCapabilities Capabilities { get; } = new()
        {
            MaxTitleLength = 100,
            MaxDescriptionLength = 5_000,
            MaxTagsTotalLength = 500,
            MaxDuration = new Core.Time.Ms(600_000),
            SupportsScheduling = false,
            SupportsCustomThumbnail = false,
            QuotaCostPerPublish = 1_600,
        };

        public Task<Core.Result<ProviderResponse<PublishResult>>> PublishAsync(
            PublishRequest request, ProviderContext context, CancellationToken cancellationToken)
        {
            Calls++;

            return Task.FromResult(Core.Result.Success(ProviderResponse<PublishResult>.Free(
                new PublishResult
                {
                    ExternalId = "s-1",
                    Url = new Uri("https://ornek.test/s-1"),
                    Visibility = Visibility.Private,
                })));
        }

        public Task<Core.Result<PublishResult?>> FindExistingAsync(
            string idempotencyKey, CancellationToken cancellationToken)
            => Task.FromResult(Core.Result.Success<PublishResult?>(null));
    }

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
