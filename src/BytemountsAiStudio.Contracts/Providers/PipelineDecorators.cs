using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Content;

namespace BytemountsAiStudio.Contracts.Providers;

/// Sağlayıcıları `ProviderPipeline`'a bağlayan sarmalayıcılar (P0-14).
///
/// ***BU DOSYA UZUN SÜRE YOKTU VE YOKLUĞU SESSİZDİ.***
///
/// `ProviderPipeline` ve yedi ara katman yazılmıştı, testliydi ve
/// mimarinin §9.2'si onları "adaptörlerin içinde değil, zincirde"
/// diye anlatıyordu. Zincir yalnızca TESTLERDE kuruluyordu: üretimde
/// hiçbir sağlayıcı çağrısı zincirden geçmiyordu. Sonucu tek tek
/// görünmez, toplamda ağır:
///
///   - `provider_calls` tablosuna TEK SATIR düşmüyordu; maliyet defteri
///     kalıcı olarak boştu, bütçe kapısı her zaman "0,00 harcandı"
///     görüyordu ve günlük/aylık limit hiçbir zaman dolmuyordu.
///   - `config/providers.json`'daki `requests_per_minute` okunuyor ama
///     hiçbir sınırlayıcıya bağlanmıyordu.
///   - Devre kesici hiç devrede değildi: çökmüş bir sağlayıcıya her
///     node tek tek gidip tek tek zaman aşımına uğruyordu.
///   - Acil durdurma (`bmai dur`) sağlayıcı çağrılarını durdurmuyordu.
///
/// SARMALAMA NEDEN ADAPTÖRÜN İÇİNDE DEĞİL: adaptör yazarının yedi
/// kaygıyı doğru sırayla uygulamasını beklemek, er geç birinin
/// unutması demek — ve unutulduğunda ortaya çıkan şey bir hata değil,
/// bir SESSİZLİK.
///
/// SARMALANMAYAN ÇAĞRILAR: `ListVoicesAsync` ve `FindExistingAsync`
/// para harcamıyor ve `ProviderResponse` döndürmüyor. Onları zorla
/// zincire sokmak, ölçüm defterine maliyeti olmayan satırlar yazmak
/// olurdu.
public static class Pipelined
{
    public static ILlmProvider Wrap(this ILlmProvider inner, ProviderPipeline? pipeline)
        => pipeline is null ? inner : new PipelinedLlmProvider(inner, pipeline);

    public static ITtsProvider Wrap(this ITtsProvider inner, ProviderPipeline? pipeline)
        => pipeline is null ? inner : new PipelinedTtsProvider(inner, pipeline);

    public static IImageProvider Wrap(this IImageProvider inner, ProviderPipeline? pipeline)
        => pipeline is null ? inner : new PipelinedImageProvider(inner, pipeline);

    public static IMusicProvider Wrap(this IMusicProvider inner, ProviderPipeline? pipeline)
        => pipeline is null ? inner : new PipelinedMusicProvider(inner, pipeline);

    public static ISearchProvider Wrap(this ISearchProvider inner, ProviderPipeline? pipeline)
        => pipeline is null ? inner : new PipelinedSearchProvider(inner, pipeline);

    public static IVisionProvider Wrap(this IVisionProvider inner, ProviderPipeline? pipeline)
        => pipeline is null ? inner : new PipelinedVisionProvider(inner, pipeline);

    public static IPublisher Wrap(this IPublisher inner, ProviderPipeline? pipeline)
        => pipeline is null ? inner : new PipelinedPublisher(inner, pipeline);

    /// Ortak çağrı kalıbı.
    internal static Task<Result<ProviderResponse<T>>> RunAsync<T>(
        ProviderPipeline pipeline,
        string providerKey,
        string operation,
        ProviderContext context,
        decimal estimatedCost,
        bool cacheable,
        Func<CancellationToken, Task<Result<ProviderResponse<T>>>> execute,
        CancellationToken cancellationToken)
        => pipeline.InvokeAsync(
            new ProviderInvocation<T>
            {
                ProviderKey = providerKey,
                Operation = operation,
                Context = context,
                Execute = execute,
                EstimatedCost = estimatedCost,
                Cacheable = cacheable,
            },
            cancellationToken);

    /// ***TAHMİN SIFIR VE BU BİR EKSİKLİK, GİZLENMİYOR.***
    ///
    /// Bütçe kapısı iki şeye bakıyor: bugüne kadar HARCANAN (deftere
    /// yazılı, gerçek) ve bu çağrının TAHMİNİ maliyeti. İkincisi sıfır
    /// olduğu için kapı "bu çağrı bütçeyi aşacak" diyemiyor; yalnızca
    /// "bütçe ZATEN dolu" diyebiliyor. Yani sınır bir çağrı GECİKMEYLE
    /// uygulanıyor.
    ///
    /// Sıfır seçilmesinin sebebi tahminin YANLIŞ olmasının daha kötü
    /// olması: jeton sayısını karakterden bölerek tahmin etmek Türkçe
    /// metinde sistematik olarak sapıyor ve düşük tahmin, kapıyı
    /// gereğinden geç kapatırken YANLIŞ bir güven veriyor. Gerçek
    /// birimler çağrı dönünce ölçülüp deftere yazılıyor; hattaki her
    /// sağlayıcı şu an zaten ücretsiz olduğu için aradaki fark bugün
    /// sıfır. Ücretli bir sağlayıcı bağlandığında burası gerçek bir
    /// tahminle doldurulmalı ve bu not o zamana kadar duruyor.
}

/// LLM — hattın en pahalı ve en sık çağrılan sağlayıcısı.
internal sealed class PipelinedLlmProvider(ILlmProvider inner, ProviderPipeline pipeline) : ILlmProvider
{
    public string Key => inner.Key;

    public LlmCapabilities Capabilities => inner.Capabilities;

    public Task<Result<ProviderResponse<LlmResponse>>> CompleteAsync(
        LlmRequest request, ProviderContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Pipelined.RunAsync(
            pipeline, inner.Key, "complete", context,
            estimatedCost: 0m, cacheable: true,
            ct => inner.CompleteAsync(request, context, ct), cancellationToken);
    }

    public Task<Result<ProviderResponse<IReadOnlyList<float>>>> EmbedAsync(
        string text, ProviderContext context, CancellationToken cancellationToken)
        => Pipelined.RunAsync(
            pipeline, inner.Key, "embed", context,
            estimatedCost: 0m, cacheable: true,
            ct => inner.EmbedAsync(text, context, ct), cancellationToken);
}

internal sealed class PipelinedTtsProvider(ITtsProvider inner, ProviderPipeline pipeline) : ITtsProvider
{
    public string Key => inner.Key;

    public bool SupportsWordTimings => inner.SupportsWordTimings;

    public Task<Result<ProviderResponse<TtsResponse>>> SynthesizeAsync(
        TtsRequest request, ProviderContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // ***SES ÖNBELLEKLENMİYOR.***
        //
        // `TtsResponse.Audio` ham bayt: üç dakikalık bir ses ~5 MB,
        // JSON'da base64 olarak ~7 MB. Cümle başına bir kayıtla
        // önbellek dakikalar içinde gigabaytlara çıkardı — ve
        // idempotency'nin amacı ikinci kez ÖDEME yapmamak, üretilen
        // medyayı saklamak değil. Medyanın yeri depo (`assets`).
        return Pipelined.RunAsync(
            pipeline, inner.Key, "synthesize", context,
            estimatedCost: 0m, cacheable: false,
            ct => inner.SynthesizeAsync(request, context, ct), cancellationToken);
    }

    /// SES LİSTESİ ZİNCİRDEN GEÇMİYOR: para harcamıyor ve ölçüm
    /// defterine maliyeti olmayan satırlar yazardı.
    public Task<Result<IReadOnlyList<VoiceInfo>>> ListVoicesAsync(
        LanguageTag language, CancellationToken cancellationToken)
        => inner.ListVoicesAsync(language, cancellationToken);
}

internal sealed class PipelinedImageProvider(IImageProvider inner, ProviderPipeline pipeline) : IImageProvider
{
    public string Key => inner.Key;

    public ImageProviderKind Kind => inner.Kind;

    public Task<Result<ProviderResponse<IReadOnlyList<ImageCandidate>>>> FindAsync(
        ImageQuery query, ProviderContext context, CancellationToken cancellationToken)
        => Pipelined.RunAsync(
            pipeline, inner.Key, "find", context,
            estimatedCost: 0m, cacheable: true,
            ct => inner.FindAsync(query, context, ct), cancellationToken);

    public Task<Result<ProviderResponse<GeneratedImage>>> GenerateAsync(
        ImagePrompt prompt, ProviderContext context, CancellationToken cancellationToken)
        // ÜRETİLEN GÖRSEL ÖNBELLEKLENMİYOR: ham bayt taşıyor.
        => Pipelined.RunAsync(
            pipeline, inner.Key, "generate", context,
            estimatedCost: 0m, cacheable: false,
            ct => inner.GenerateAsync(prompt, context, ct), cancellationToken);
}

internal sealed class PipelinedMusicProvider(IMusicProvider inner, ProviderPipeline pipeline) : IMusicProvider
{
    public string Key => inner.Key;

    public Task<Result<ProviderResponse<MusicTrack>>> SelectAsync(
        MusicQuery query, ProviderContext context, CancellationToken cancellationToken)
        => Pipelined.RunAsync(
            pipeline, inner.Key, "select", context,
            estimatedCost: 0m, cacheable: true,
            ct => inner.SelectAsync(query, context, ct), cancellationToken);
}

internal sealed class PipelinedSearchProvider(ISearchProvider inner, ProviderPipeline pipeline) : ISearchProvider
{
    public string Key => inner.Key;

    public Task<Result<ProviderResponse<IReadOnlyList<SearchHit>>>> SearchAsync(
        SearchQuery query, ProviderContext context, CancellationToken cancellationToken)
        => Pipelined.RunAsync(
            pipeline, inner.Key, "search", context,
            estimatedCost: 0m, cacheable: true,
            ct => inner.SearchAsync(query, context, ct), cancellationToken);
}

internal sealed class PipelinedVisionProvider(IVisionProvider inner, ProviderPipeline pipeline) : IVisionProvider
{
    public string Key => inner.Key;

    public Task<Result<ProviderResponse<VisionVerdict>>> JudgeAsync(
        VisionQuery query, ProviderContext context, CancellationToken cancellationToken)
        => Pipelined.RunAsync(
            pipeline, inner.Key, "judge", context,
            estimatedCost: 0m, cacheable: true,
            ct => inner.JudgeAsync(query, context, ct), cancellationToken);
}

/// Yayıncı — zincire EN ÇOK ihtiyaç duyan sağlayıcı.
///
/// Devre kesici burada bir kolaylık değil zorunluluk: YouTube 403
/// dönmeye başladığında (kota, askıya alınmış proje) her yayın
/// node'unun tek tek gidip tek tek düşmesi, on yedi projelik bir
/// havuzda on yedi başarısız yükleme demek.
internal sealed class PipelinedPublisher(IPublisher inner, ProviderPipeline pipeline) : IPublisher
{
    public string Key => inner.Key;

    public string Platform => inner.Platform;

    public PublishCapabilities Capabilities => inner.Capabilities;

    public Task<Result<ProviderResponse<PublishResult>>> PublishAsync(
        PublishRequest request, ProviderContext context, CancellationToken cancellationToken)
        // YAYIN SONUCU ÖNBELLEKLENİYOR ve bu KASITLI: aynı idempotency
        // anahtarıyla ikinci bir yükleme yapılmamalı (§15.2). Zincirin
        // en dış katmanı tam olarak bunu engelliyor.
        => Pipelined.RunAsync(
            pipeline, inner.Key, "publish", context,
            estimatedCost: 0m, cacheable: true,
            ct => inner.PublishAsync(request, context, ct), cancellationToken);

    public Task<Result<PublishResult?>> FindExistingAsync(
        string idempotencyKey, CancellationToken cancellationToken)
        => inner.FindExistingAsync(idempotencyKey, cancellationToken);
}
