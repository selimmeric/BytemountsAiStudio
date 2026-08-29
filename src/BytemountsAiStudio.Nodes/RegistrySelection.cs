using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Workflow.Engine;

namespace BytemountsAiStudio.Nodes;

/// Hangi hattın koşacağının KARAR VERİLDİĞİ TEK YER.
///
/// ***BU SINIF, BULUNAN EN AĞIR TUTARSIZLIĞI KAPATIYOR.***
///
/// Üç host üç ayrı karar veriyordu ve hiçbiri bunu söylemiyordu:
///
///   - `Api`      → `BuildOpenRegistry`  (gerçek hat)
///   - `Cli`      → `--acik` bayrağıyla seçilebilir, varsayılan sahte
///   - `Worker`   → `BuildFakeRegistry`, **SABİT, seçenek yok**
///
/// Worker kuyruğu boşaltan taraf: zamanlayıcı (`OrchestratorService`)
/// koşu başlatıyor, işler kuyruğa giriyor ve `QueueWorker` onları
/// çalıştırıyor. Yani **otonom fabrika, tasarlandığı gibi koştuğunda
/// baştan sona SAHTE video üretiyordu.** Gerçek içerik yalnızca elle
/// `bmai run --acik` çağırarak üretilebiliyordu — ki o da fabrikanın
/// var oluş sebebinin tersi.
///
/// Daha kötüsü, `docker-compose.uygulama.yml` bunun tam tersini
/// söylüyordu: *"zamanlayıcı varsayılan kapalı, çünkü bu döngü gerçek
/// para harcayabiliyor"*. Harcayamazdı: o Worker'da para harcayan tek
/// bir sağlayıcı yoktu. Yorum ile kod ayrışmıştı ve ayrışma yorumun
/// lehineydi — yani okuyan yanlış şeye inanıyordu.
///
/// ***VARSAYILAN GERÇEK HAT ve bu bilinçli bir tercih.***
///
/// Sahte varsayılan "güvenli" görünüyor ve aslında en tehlikelisi:
/// sahte hat gerçek bir video dosyası üretiyor — doğru süre, doğru
/// çözünürlük, doğru altyazı. Çıktı dizinine bakan bir insan ikisini
/// ayırt edemiyor. Sessizce sahte üretmek, gürültülü şekilde
/// başarısız olmaktan kötü.
///
/// Gerçek hattın anahtar gerektirmemesi bu tercihi mümkün kılıyor
/// (ADR-015): Wikipedia, Openverse, Pollinations görseli ve yerel
/// konuşma sentezi hiçbir anahtar istemiyor. Zamanlayıcı da zaten
/// varsayılan kapalı, yani kimse istemeden hiçbir şey koşmuyor.
///
/// SEÇİM HER ZAMAN LOGLANIYOR: hangi hattın açık olduğu, açılışta bir
/// satır olarak yazılıyor ve her koşunun bağlamına giriyor.
public static class RegistrySelection
{
    /// `acik` / `open` → gerçek hat. `sahte` / `fake` → sahte hat.
    public const string Variable = "BMAI_PIPELINE";

    /// Ortamdan okunan hat.
    ///
    /// TANINMAYAN DEĞER SESSİZCE VARSAYILANA DÜŞMÜYOR: `warning`
    /// döndürülüyor ve host onu logluyor. `BMAI_PIPELINE=achik` yazan
    /// biri, neden sahte video aldığını asla anlayamazdı.
    public static (PipelineKind Kind, string? Warning) FromEnvironment(
        Func<string, string?>? read = null)
    {
        var raw = (read ?? Environment.GetEnvironmentVariable)(Variable);

        if (string.IsNullOrWhiteSpace(raw))
        {
            return (PipelineKind.Open, null);
        }

        // KÜÇÜK HARFE ÇEVİRİ `InvariantCulture` İLE: Türkçe kültürde
        // "ACIK".ToLower() → "acık" değil ama "I" → "ı" oluyor ve
        // "ACIK" hiçbir zaman "acik"e eşleşmezdi. Bu depoda birkaç kez
        // ödenmiş bir hata.
        return raw.Trim().ToLowerInvariant() switch
        {
            "acik" or "açık" or "open" or "gercek" or "gerçek" => (PipelineKind.Open, null),
            "sahte" or "fake" or "test" => (PipelineKind.Fake, null),
            _ => (PipelineKind.Open,
                $"`{Variable}` tanınmadı: '{raw}'. Geçerli değerler: acik | sahte. "
                + "GERÇEK hat kullanılıyor."),
        };
    }

    /// Kaydı kurar.
    ///
    /// `override` AÇIK BİR SEÇİMİ ORTAMIN ÖNÜNE GEÇİRİYOR: CLI'nin
    /// `--acik` bayrağı böyle çalışıyor. Sıra tersine olsaydı,
    /// makinedeki bir ortam değişkeni kullanıcının o çağrıda yazdığı
    /// bayrağı sessizce yok sayardı (`MediaTools` ile aynı gerekçe).
    public static NodeRegistry Build(
        IStorageProvider storage,
        HttpClient http,
        string outputDirectory,
        ITopicUniqueness uniqueness,
        IChannelPolicy channels,
        ProviderPipeline? pipeline,
        IQuotaPool quota,
        PipelineKind? kindOverride = null,
        Action<string>? onWarning = null,
        string? ffmpegPath = null,
        string? ffprobePath = null)
    {
        PipelineKind kind;

        if (kindOverride is { } forced)
        {
            kind = forced;
        }
        else
        {
            var (fromEnvironment, warning) = FromEnvironment();
            kind = fromEnvironment;

            if (warning is not null)
            {
                onWarning?.Invoke(warning);
            }
        }

        // ***HANGİ HAT AÇIK, HER ZAMAN SÖYLENİYOR.***
        //
        // Sessiz kalsaydı sahte hatta koşan bir kurulum aylarca fark
        // edilmezdi — çıktılar geçerli görünüyor.
        onWarning?.Invoke(kind == PipelineKind.Open
            ? "Hat: GERÇEK (anahtarsız sağlayıcılar). Değiştirmek için BMAI_PIPELINE=sahte."
            : "Hat: SAHTE — üretilen videolar gerçek içerik DEĞİL. "
              + "Gerçek hat için BMAI_PIPELINE=acik.");

        return kind == PipelineKind.Open
            ? NodeHandlerRegistration.BuildOpenRegistry(
                storage, http, outputDirectory, uniqueness, channels, pipeline, quota,
                ffmpegPath, ffprobePath)
            : NodeHandlerRegistration.BuildFakeRegistry(
                storage, outputDirectory, uniqueness, channels, pipeline,
                ffmpegPath, ffprobePath);
    }
}
