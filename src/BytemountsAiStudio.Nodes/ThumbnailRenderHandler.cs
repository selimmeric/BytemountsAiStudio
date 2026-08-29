using System.Text.Json;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Assets;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Core.Learning;
using BytemountsAiStudio.Media.Rendering.Text;
using BytemountsAiStudio.Workflow.Engine;

namespace BytemountsAiStudio.Nodes;

/// Kapak görseli node'u (P1-21'in eksik halkası).
///
/// `ThumbnailRenderer` yazılmış ve testliydi ama HİÇBİR NODE ONU
/// ÇAĞIRMIYORDU. Sonuç mekanik QC'de görüldü: `qc.thumbnail`
/// kontrolü her koşuda "ölçülmedi" diye düşüyordu ve bu BLOKLAYICI bir
/// kontrol — yani hiçbir video otomatik geçemiyordu. Faz 2'nin kabul
/// kriteri ("insan müdahalesi olmadan hazır") tam da burada
/// kırılıyordu.
///
/// SEO'DAN SONRA KOŞUYOR: kapak metni başlıktan geliyor ve başlık
/// SEO node'unun çıktısı. Önce koşsaydı kapakta konu adı yazardı —
/// başlıkla kapak arasındaki tutarsızlık, izleyicinin tıkladığı şeyle
/// gördüğü şeyin farklı olması demek.
public sealed class ThumbnailRenderHandler(
    IStorageProvider storage, IChannelPolicy? channels = null) : INodeHandler
{
    /// Kapak yazı tipi zinciri — kanal ayarı yoksa.
    ///
    /// Timeline'daki varsayılanla AYNI liste olmak zorunda değil ve
    /// aynı: iki farklı varsayılan, ayar yazmayan bir kanalda kapak ile
    /// altyazının farklı yazı tipiyle çıkması demekti.
    public static IReadOnlyList<string> DefaultFonts { get; } =
        ["Inter", "Noto Sans", "Segoe UI", "Arial"];

    public string NodeType => "thumbnail.render";

    /// Ağa çıkmıyor, model çağırmıyor: en hafif sınıf.
    public QueueClass Queue => QueueClass.Search;

    public async Task<Result<JsonElement>> ExecuteAsync(NodeContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // BAŞLIK SEO'DAN, konu adından değil.
        //
        // Konu adına düşmek "kapak üretildi" demenin kolay yolu olurdu
        // ama izleyicinin gördüğü başlıkla kapaktaki metin ayrışırdı.
        var title = NodeJson.Text(context.RunContext, "seo.title");

        if (string.IsNullOrWhiteSpace(title))
        {
            // KALICI: yeniden denemek SEO çıktısı üretmiyor. Sıranın
            // yanlış olduğunu söylemek, sessizce konu adına düşmekten
            // iyi.
            return Error.Permanent("thumbnail.no_title",
                "Başlık yok; kapak `seo.generate` sonrasında koşmalı.");
        }

        var language = LanguageTag.Create(
            NodeJson.Text(context.RunContext, "topic.language") ?? "tr-TR");

        var background = await BackgroundAsync(context.RunContext, cancellationToken).ConfigureAwait(false);

        // KAPAK DENEYE AÇIK (P5-03).
        //
        // Deney yoksa `Default` — bugünkü kapak. Varsayılanı ayrı bir
        // yerde tutmamak önemli: kontrol kolu ile "deney yok" hâli
        // farklı kapaklar üretseydi, deneyin karşılaştırdığı taban
        // kanalın gerçek tabanı olmazdı.
        var style = ThumbnailVariantSettings.Default;
        var experiment = ExperimentContext.ConfigFor(context.RunContext, "thumbnail");

        if (experiment is not null)
        {
            var parsed = ThumbnailVariant.Parse(experiment);

            if (parsed.IsFailure)
            {
                return Result.Failure<JsonElement>(parsed.Error);
            }

            style = parsed.Value;
        }

        // BÜYÜK HARF DİLE DUYARLI: `ToUpperInvariant` "istanbul"u
        // "ISTANBUL" yapıyor, doğrusu "İSTANBUL". Kapak kanalın en çok
        // görülen tek görseli; oradaki noktasız İ, o kanalın Türkçe
        // yazamadığını söylüyor.
        var drawn = ThumbnailVariant.ApplyCase(title, language, style.Uppercase);

        // ***YAZI TİPİ ZİNCİRİ KANALDAN (P3-01).***
        //
        // Burada SABİTTİ ve timeline aynı değeri kanaldan okuyordu:
        // `font_stack: ["Noto Sans Arabic", ...]` yazan bir kanalda
        // altyazılar değişiyor, KAPAK DEĞİŞMİYORDU. Arapça ya da
        // Japonca bir kanalda kapaktaki başlık tofu (kutu) karakterlerle
        // çiziliyordu — ve kapak, kanalın arama sonuçlarında görünen tek
        // görseli.
        var fonts = channels is not null && context.ChannelId is { } thumbnailChannel
            ? (await channels.SettingsAsync(thumbnailChannel, cancellationToken)
                .ConfigureAwait(false))?.FontStack
            : null;

        var renderer = new ThumbnailRenderer(fonts ?? DefaultFonts);

        var rendered = renderer.Render(new ThumbnailRequest
        {
            Title = drawn,
            Language = language,
            BackgroundImage = background,
            TextPosition = style.Position,
            ScrimAlpha = style.ScrimAlpha,
            FontSize = style.FontSize,
        });

        if (rendered.IsFailure)
        {
            return Result.Failure<JsonElement>(rendered.Error);
        }

        using var stream = new MemoryStream(rendered.Value);

        var stored = await storage.PutAsync(
            stream,
            new AssetMetadata
            {
                Kind = AssetKind.Image,
                MimeType = "image/jpeg",
                Width = ThumbnailRenderer.Width,
                Height = ThumbnailRenderer.Height,
                SourceProvider = "thumbnail.render",
            },
            cancellationToken).ConfigureAwait(false);

        if (stored.IsFailure)
        {
            return Result.Failure<JsonElement>(stored.Error);
        }

        return Result.Success(NodeJson.From(new
        {
            asset = stored.Value.Ref.ToString(),
            width = ThumbnailRenderer.Width,
            height = ThumbnailRenderer.Height,
            // BOYUT ÖLÇÜLÜYOR, VARSAYILMIYOR (ADR-006): QC 2 MB
            // sınırına bakıyor ve o sınırı aşan bir kapak platformda
            // reddediliyor. Beklenen boyutu yazmak, gerçekte aşan bir
            // dosyayı geçirmek olurdu.
            size_bytes = rendered.Value.Length,
            // ÇİZİLEN metin yazılıyor, SEO başlığı değil: büyük harf
            // kolunda ikisi farklı ve kapakta ne yazdığını bilmek
            // sonradan tek yol.
            title = drawn,
            has_background = background is not null,
            variant = ExperimentContext.VariantName(context.RunContext, "thumbnail"),
        }));
    }

    /// Kapağın arka planı: ilk sahnenin görseli.
    ///
    /// Bulunamazsa `null` ve düz renk kullanılıyor — kapak yine
    /// üretiliyor. Arka plan yüzünden koşuyu düşürmek, okunabilir bir
    /// kapağı estetik bir tercih için çöpe atmak olurdu.
    private async Task<byte[]?> BackgroundAsync(
        JsonElement runContext, CancellationToken cancellationToken)
    {
        if (!runContext.TryGetProperty("visuals", out var visuals)
            || visuals.ValueKind != JsonValueKind.Object
            || !visuals.TryGetProperty("images", out var images)
            || images.ValueKind != JsonValueKind.Array
            || images.GetArrayLength() == 0)
        {
            return null;
        }

        var first = images[0];

        var reference = first.ValueKind == JsonValueKind.String
            ? first.GetString()
            : first.TryGetProperty("asset", out var asset) ? asset.GetString() : null;

        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var parsed = AssetRef.TryCreate(reference);

        if (parsed.IsFailure)
        {
            return null;
        }

        var opened = await storage.OpenAsync(parsed.Value, cancellationToken).ConfigureAwait(false);

        if (opened.IsFailure)
        {
            return null;
        }

        using var buffer = new MemoryStream();

        await using (var stream = opened.Value)
        {
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        return buffer.ToArray();
    }
}
