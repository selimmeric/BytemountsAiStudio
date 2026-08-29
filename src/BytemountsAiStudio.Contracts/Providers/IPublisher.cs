using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Assets;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Time;

namespace BytemountsAiStudio.Contracts.Providers;

public enum Visibility
{
    Private = 0,
    Unlisted = 1,
    Public = 2,
}

/// Platformun sert sınırları.
///
/// Bu değerleri modele bıraktığımızda 100 karakteri aşan başlık üretiliyor ve
/// yükleme API tarafında reddediliyor; kullanıcı nedenini göremiyor. Sınır
/// burada bildirilir, kırpma kod tarafında yapılır (§7.2).
public sealed record PublishCapabilities
{
    public required int MaxTitleLength { get; init; }

    public required int MaxDescriptionLength { get; init; }

    public required int MaxTagsTotalLength { get; init; }

    public required Ms MaxDuration { get; init; }

    public required bool SupportsScheduling { get; init; }

    public required bool SupportsCustomThumbnail { get; init; }

    /// Tek yüklemenin tükettiği kota birimi. §15.1: YouTube'da 1.600 birim,
    /// günlük 10.000 birimlik havuzdan. Zamanlayıcı bu sayıya göre planlar —
    /// kota bu sistemde bütçe kadar birinci sınıf bir kaynak (ADR-011).
    public required int QuotaCostPerPublish { get; init; }
}

public sealed record PublishMetadata
{
    public required string Title { get; init; }

    public required string Description { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public required LanguageTag Language { get; init; }

    public string? CategoryId { get; init; }

    public string? PlaylistId { get; init; }
}

public sealed record PublishRequest
{
    public required string VideoPath { get; init; }

    /// Videonun DIŞARIDAN ERİŞİLEBİLİR adresi.
    ///
    /// Bazı platformlar dosya kabul etmiyor, ÇEKİYOR: Instagram Graph
    /// API'ye `video_url` veriliyor ve Meta o adresi kendi
    /// sunucularından indiriyor. Yerel bir dosya yolu orada işe
    /// yaramıyor ve hata Meta tarafında, anlaşılmaz bir kodla
    /// dönüyor — bu yüzden alan burada ve eksikliği yayından ÖNCE
    /// yakalanıyor.
    public Uri? VideoUrl { get; init; }

    public required PublishMetadata Metadata { get; init; }

    public AssetRef? Thumbnail { get; init; }

    public Visibility Visibility { get; init; } = Visibility.Private;

    /// Doluysa video gizli yüklenip bu anda yayına alınır.
    /// §15.3: kota gündüz harcanır, yayın istenen saatte olur — kota ile
    /// yayın temposunu birbirinden ayıran pratik çözüm bu.
    public DateTimeOffset? PublishAt { get; init; }

    /// Aynı anahtarla ikinci yükleme yapılmaz. §2.4/16: upload başarılı olup
    /// DB yazımı çökerse retry ikinci kopyayı yüklerdi.
    public required string IdempotencyKey { get; init; }

    /// Yarım kalmış yüklemeyi sürdürmek için saklanan oturum durumu.
    public string? ResumeToken { get; init; }
}

public sealed record PublishResult
{
    public required string ExternalId { get; init; }

    public required Uri Url { get; init; }

    public required Visibility Visibility { get; init; }

    public DateTimeOffset? ScheduledFor { get; init; }

    /// Yükleme yarıda kaldıysa doldurulur; iş yeniden denendiğinde
    /// baştan değil kaldığı yerden devam eder.
    public string? ResumeToken { get; init; }

    /// Bu yüklemenin gerçekte harcadığı kota. Rezervasyon ile gerçekleşenin
    /// karşılaştırılması, kota muhasebesinin kaymasını yakalar.
    public int QuotaSpent { get; init; }
}

/// Bir yayın platformu.
///
/// §34: YouTube tek hedef değil. TikTok, Instagram ve X aynı arayüzün arkasına
/// girecek — her birinin sınırları <see cref="Capabilities"/> ile bildirilir,
/// dolayısıyla platform eklemek boru hattını değiştirmez.
public interface IPublisher : IProvider
{
    /// "youtube", "tiktok", "instagram".
    string Platform { get; }

    PublishCapabilities Capabilities { get; }

    Task<Result<ProviderResponse<PublishResult>>> PublishAsync(
        PublishRequest request,
        ProviderContext context,
        CancellationToken cancellationToken);

    /// Çökme kurtarma: `Uploading` durumunda kalmış bir kaydın gerçekten
    /// tamamlanıp tamamlanmadığını platforma sorar. Çift yükleme sorununun
    /// tek doğru çözümü bu (§15.2).
    Task<Result<PublishResult?>> FindExistingAsync(
        string idempotencyKey,
        CancellationToken cancellationToken);
}

public sealed record MetricSnapshot
{
    public required DateOnly Date { get; init; }

    public required long Views { get; init; }

    public long Impressions { get; init; }

    public double? ClickThroughRate { get; init; }

    public double? AverageViewDurationSeconds { get; init; }

    public long Likes { get; init; }

    public long Comments { get; init; }

    public long SubscribersGained { get; init; }
}

/// Yayın sonrası ölçüm.
///
/// §2.5/27: veri 24–72 saat gecikmeli gelir ve tek başına neden–sonuç
/// kurmaya yetmez. Öğrenme döngüsü bu veriyi deney çerçevesiyle birlikte
/// kullanır; ham metriğe bakıp strateji değiştirmek batıl inanç üretir.
public interface IAnalyticsProvider : IProvider
{
    Task<Result<ProviderResponse<IReadOnlyList<MetricSnapshot>>>> FetchAsync(
        string externalId,
        DateOnly from,
        DateOnly to,
        ProviderContext context,
        CancellationToken cancellationToken);
}
