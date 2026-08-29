using System.Text.Json;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Assets;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Workflow.Engine;

namespace BytemountsAiStudio.Nodes;

/// Yayın node'u (P1-25, P6-01, P6-02).
///
/// BORU HATTININ UCU BURASIYDI VE YOKTU. `IPublisher` yazılmıştı,
/// YouTube/TikTok/Instagram adaptörleri yazılmıştı, ama hiçbir node
/// onları çağırmıyordu: üretilen video `output/` klasöründe kalıyordu.
/// Bu, bu depoda defalarca ödenen "yazıldı ama bağlanmadı" hatasının
/// en pahalı hâliydi — çünkü eksik olan şey ÜRÜNÜN KENDİSİ.
///
/// PLATFORM AYARDAN GELİYOR, KODDA SEÇİLMİYOR: aynı graf farklı
/// kanallarda farklı platforma yayınlayabilmeli. Tanınmayan bir
/// platform SESSİZ GEÇİLMİYOR — sessiz geçmek, hiçbir yere
/// yayınlanmamış bir videoyu "yayınlandı" diye işaretlemek olurdu.
public sealed class PublishHandler(IReadOnlyList<IPublisher> publishers, IQuotaPool quota) : INodeHandler
{
    public string NodeType => "publish.upload";

    /// Ağa çıkıyor ve uzun sürüyor: kendi kuyruk sınıfı.
    public QueueClass Queue => QueueClass.Upload;

    public async Task<Result<JsonElement>> ExecuteAsync(NodeContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var videoPath = NodeJson.Text(context.RunContext, "render.output_path");

        if (string.IsNullOrWhiteSpace(videoPath))
        {
            return Error.Permanent("publish.no_video",
                "Yayınlanacak video yok; `media.render` sonrasında koşmalı.");
        }

        var title = NodeJson.Text(context.RunContext, "seo.title");

        if (string.IsNullOrWhiteSpace(title))
        {
            // BAŞLIKSIZ YAYIN YOK. Dosya adını başlık yapmak, izleyicinin
            // gördüğü ilk şeyin bir GUID olması demekti.
            return Error.Permanent("publish.no_title",
                "Başlık yok; `seo.generate` sonrasında koşmalı.");
        }

        // PLATFORM GİRDİDEN SONRA ÇÖZÜLÜYOR.
        //
        // Videosu olmayan bir koşuda "tanınmayan platform" demek,
        // yanlış soruna işaret etmek olurdu: eksik olan şey platform
        // değil, videonun kendisi.
        var platform = NodeJson.Text(context.Config, "platform")
            ?? NodeJson.Text(context.RunContext, "channel.platform")
            ?? "youtube";

        var publisher = publishers.FirstOrDefault(
            p => string.Equals(p.Platform, platform, StringComparison.OrdinalIgnoreCase));

        if (publisher is null)
        {
            return Error.Permanent("publish.unknown_platform",
                $"'{platform}' için yayıncı yok. Tanımlılar: "
                + string.Join(", ", publishers.Select(p => p.Platform)));
        }

        var request = new PublishRequest
        {
            VideoPath = videoPath,
            VideoUrl = Url(context.RunContext),
            Thumbnail = Thumbnail(context.RunContext),
            Visibility = VisibilityOf(NodeJson.Text(context.Config, "visibility")),
            Metadata = new PublishMetadata
            {
                Title = title,
                Description = NodeJson.Text(context.RunContext, "seo.description") ?? string.Empty,
                Tags = Tags(context.RunContext),
                Language = LanguageTag.Create(
                    NodeJson.Text(context.RunContext, "topic.language") ?? "tr-TR"),
            },

            // İDEMPOTENCY ANAHTARI RUN'DAN: aynı run ikinci kez
            // yayınlamaya kalkarsa (hedefli yeniden koşma, çökme
            // kurtarma) aynı anahtar geliyor.
            IdempotencyKey = context.IdempotencyKey,

            // SÜRDÜRME JETONU BAĞLAMDAN: yarım kalmış bir yükleme
            // baştan başlamıyor. Olmadan, 60 MB'lık bir video her
            // denemede sıfırdan gidiyordu.
            ResumeToken = NodeJson.Text(context.RunContext, $"{context.NodeId}.resume_token"),
        };

        // ---- KOTA REZERVASYONU, YÜKLEMEDEN ÖNCE (P4-04) ----
        //
        // YouTube günlük 10.000 birim veriyor ve bir yükleme 1.600 —
        // proje başına günde ALTI video. Kotayı yüklemeye BAŞLADIKTAN
        // sonra öğrenmek, dakikalarca bant genişliği harcayıp sonunda
        // reddedilmek demek.
        //
        // REZERVASYON HARCAMADAN ÖNCE: aynı anda başlayan iki yükleme
        // aksi hâlde ikisi de "yer var" görürdü.
        var cost = QuotaLedger.CostOf(
            withThumbnail: request.Thumbnail is not null,
            withPlaylist: false);

        var reservation = await quota.ReserveAsync(
            publisher.Key, ChannelOf(context.RunContext), cost, cancellationToken).ConfigureAwait(false);

        if (reservation.IsFailure)
        {
            return Result.Failure<JsonElement>(reservation.Error);
        }

        if (!reservation.Value.Granted)
        {
            // HAVUZ TÜKENDİ: KAYNAK hatası, başarısızlık değil
            // (ADR-011). Yarın kota sıfırlanıyor ve iş o zaman
            // koşabilir; kalıcı saymak üretilmiş bir videoyu çöpe
            // atmak olurdu.
            //
            // HESAP YOKLUĞU AYRI: beklemek onu var etmiyor, bu bir
            // yapılandırma hatası ve KALICI.
            return reservation.Value.Outcome == PoolOutcome.NoAccounts
                ? Error.Permanent("publish.no_quota_account", reservation.Value.Reason)
                : Error.Resource("publish.quota_exhausted", reservation.Value.Reason,
                    QuotaLedger.NextReset(DateTimeOffset.UtcNow) - DateTimeOffset.UtcNow);
        }

        var result = await publisher.PublishAsync(
            request,
            new ProviderContext
            {
                IdempotencyKey = context.IdempotencyKey,
                CorrelationId = context.CorrelationId,
            },
            cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            // HATA SINIFI OLDUĞU GİBİ GEÇİYOR (ADR-011): kota hatası
            // KAYNAK sınıfı ve iş erteleniyor, düşmüyor. Burada kalıcıya
            // çevirmek, üretilmiş bir videoyu çöpe atmak olurdu.
            return Result.Failure<JsonElement>(result.Error);
        }

        var published = result.Value.Value;

        return Result.Success(NodeJson.From(new
        {
            platform = publisher.Platform,
            external_id = published.ExternalId,
            url = published.Url.ToString(),

            // GERÇEKLEŞEN GÖRÜNÜRLÜK YAZILIYOR, İSTENEN DEĞİL:
            // zamanlanmış bir yükleme gizli başlıyor ve kayıt bunu
            // söylemezse "yayında" sanılırdı.
            visibility = published.Visibility.ToString(),
            scheduled_for = published.ScheduledFor,
            quota_spent = published.QuotaSpent,

            // HANGİ HESAPTAN YÜKLENDİĞİ KAYDA GİRİYOR.
            //
            // Bir hesap kapanırsa ya da askıya alınırsa, "hangi
            // videolar oradan gitti" sorusu cevaplanabilmeli. Havuzda
            // on yedi proje varken bunu sonradan bulmanın yolu yok.
            quota_account = reservation.Value.Account,
            quota_reserved = reservation.Value.Cost,
            quota_remaining = reservation.Value.RemainingAfter,
            resume_token = published.ResumeToken,
        }));
    }

    /// Koşunun kanalı — kota kapsamı için.
    private static Guid? ChannelOf(JsonElement runContext)
        => Guid.TryParse(NodeJson.Text(runContext, "channel.id"), out var id) ? id : null;

    /// Kapak varlığı.
    private static AssetRef? Thumbnail(JsonElement runContext)
    {
        var reference = NodeJson.Text(runContext, "thumbnail.asset");

        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var parsed = AssetRef.TryCreate(reference);

        return parsed.IsSuccess ? parsed.Value : null;
    }

    /// Videonun dışarıdan erişilebilir adresi (Instagram için şart).
    private static Uri? Url(JsonElement runContext)
    {
        var url = NodeJson.Text(runContext, "render.public_url");

        return Uri.TryCreate(url, UriKind.Absolute, out var address) ? address : null;
    }

    private static IReadOnlyList<string> Tags(JsonElement runContext)
    {
        if (runContext.ValueKind != JsonValueKind.Object
            || !runContext.TryGetProperty("seo", out var seo)
            || seo.ValueKind != JsonValueKind.Object
            || !seo.TryGetProperty("tags", out var tags)
            || tags.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. tags.EnumerateArray().Select(t => t.GetString() ?? string.Empty)
            .Where(t => t.Length > 0)];
    }

    /// İstenen görünürlük.
    ///
    /// VARSAYILAN GİZLİ. Otomatik bir hattın varsayılanı "herkese açık"
    /// olsaydı, ilk yanlış yapılandırma yayına çıkmış bir videoyla
    /// sonuçlanırdı ve geri alınamazdı.
    internal static Visibility VisibilityOf(string? configured) => configured?.ToLowerInvariant() switch
    {
        "public" => Visibility.Public,
        "unlisted" => Visibility.Unlisted,
        _ => Visibility.Private,
    };
}
