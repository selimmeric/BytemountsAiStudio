using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Providers.Llm;
using BytemountsAiStudio.Providers.Open;

namespace BytemountsAiStudio.Nodes;

/// Katalogdaki TARİFİ nesneye çeviren yer.
///
/// ***BU SINIFIN ADI `ProviderCatalog`'UN KENDİ YORUMUNDA GEÇİYORDU VE
/// SINIF HİÇ YAZILMAMIŞTI:*** *"Katalog sağlayıcıyı ÜRETMİYOR, yalnızca
/// TARİF EDİYOR. Nesne üretimi `ProviderFactory`'de kalıyor."* Öyle bir
/// dosya yoktu.
///
/// Sonucu ADR-015'in merkezî iddiasının **yanlış** olmasıydı:
///
/// > *"Anahtar geldiğinde yapılacak tek şey `enabled` alanını açıp
/// > yönlendirme listesinin başına almak."*
///
/// Gerçekte `routing` bloğu hiçbir kurulum kodundan okunmuyordu —
/// yalnızca `bmai providers` ekranı onu **gösteriyordu**. Sağlayıcılar
/// `NodeHandlerRegistration` içinde elle, sabit bir sırayla
/// kuruluyordu. Yani anahtar geldiğinde yapılması gereken şey bir
/// yapılandırma değişikliği değil, bir **kod** değişikliğiydi.
///
/// Bunun ölçülebilir bedeli: **beş adaptör hiçbir yerden
/// kurulmuyordu.** `SearxngSearchProvider`, `DuckDuckGoProvider`,
/// `GeminiLlmProvider`, `ElevenLabsTtsProvider`, `PexelsImageProvider`
/// — hepsi yazılmış, testlenmiş ve erişilemezdi. Anahtar gelse bile
/// kullanılamazlardı.
///
/// ***ANAHTARI OLMAYAN SAĞLAYICI SESSİZCE ATLANIYOR ve bu doğru:***
/// bugünkü normal durum bu. Ama katalogda **adı geçmeyen** bir anahtar
/// atlanırken UYARILIYOR — `routing` içindeki bir yazım hatası, o
/// sağlayıcının sessizce kaybolması demekti.
///
/// ***`IsUsableNow` SÜZGECİ, KATALOG DOĞRULAMASI VARKEN BİLE
/// GEREKLİ.*** `ProviderCatalog.Load` zaten yönlendirmenin kapalı ya da
/// anahtarsız bir sağlayıcıya işaret etmesini reddediyor — bu
/// çalışırken öğrenildi, testler önce geçersiz kataloglar kuruyordu.
/// Yine de süzgeç duruyor, iki sebeple:
///
///   - Doğrulama YÜKLEME anında koşuyor; anahtar sonradan
///     kaybolabiliyor (ortam değişkeni silinmiş bir kap, dönen bir
///     jeton). Süzgeç ÇALIŞMA anını görüyor.
///   - Yönlendirmesi olmayan roller (`music`) rol yedeğinden geliyor
///     ve o yol doğrulamadan hiç geçmiyor.
public sealed class ProviderFactory(
    HttpClient http,
    ProviderCatalog catalog,
    ICredentialSource? credentials = null,
    Action<string>? onWarning = null)
{
    /// Rol adları — `config/providers.json` ile aynı olmak zorunda.
    public const string LlmRole = "llm";

    public const string SearchRole = "search";

    public const string StockImageRole = "image.stock";

    public const string GenerativeImageRole = "image.generative";

    public const string TtsRole = "tts";

    public const string MusicRole = "music";

    public const string PublishRole = "publish";

    /// Bir roldeki KULLANILABİLİR sağlayıcılar, yönlendirme sırasında.
    ///
    /// ***YÖNLENDİRME YOKSA ROLE DÜŞÜLÜYOR.*** `routing` bloğunda
    /// karşılığı olmayan bir rol (bugün `music` böyle) aksi hâlde boş
    /// dönerdi ve o rolün bütün sağlayıcıları sessizce kaybolurdu —
    /// düzeltmeye çalıştığımız hatanın aynısı.
    public IReadOnlyList<ProviderDescriptor> Describe(string role)
    {
        var routed = catalog.For(role);

        var candidates = routed.Count > 0
            ? routed
            : catalog.Providers.Where(p => p.Role == role && p.Enabled).ToList();

        return [.. candidates.Where(p => p.IsUsableNow)];
    }

    /// LLM zinciri. Katmanlı sağlayıcı bunu sırayla deniyor.
    public IReadOnlyList<ILlmProvider> Llm()
        => Build<ILlmProvider>(LlmRole, key => key switch
        {
            "ollama" or "ollama-remote"
                => new OllamaLlmProvider(http, OllamaOptions.FromEnvironment()),

            "pollinations-text"
                => new OpenAiCompatibleLlmProvider(
                    http, OpenAiCompatibleOptions.Pollinations(), credentials),

            "openai"
                => new OpenAiCompatibleLlmProvider(
                    http, OpenAiCompatibleOptions.OpenAi(), credentials),

            "openrouter"
                => new OpenAiCompatibleLlmProvider(
                    http, OpenAiCompatibleOptions.OpenRouter(), credentials),

            "gemini" => new GeminiLlmProvider(http, credentials: credentials),

            _ => null,
        });

    /// Arama sağlayıcıları.
    ///
    /// Wikipedia AYRICA sayfa çekiyor (`IWebFetchProvider`) ve o taraf
    /// bu listeye girmiyor: burada yalnızca arama var.
    public IReadOnlyList<ISearchProvider> Search()
        => Build<ISearchProvider>(SearchRole, key => key switch
        {
            "wikipedia" => new WikipediaProvider(http),
            "wikidata" => new WikidataProvider(http),
            "searxng" => new SearxngSearchProvider(http),
            "duckduckgo-ia" => new DuckDuckGoProvider(http),
            _ => null,
        });

    public IReadOnlyList<IImageProvider> StockImages()
        => Build<IImageProvider>(StockImageRole, key => key switch
        {
            "openverse" => new OpenverseImageProvider(http),
            "pexels" => new PexelsImageProvider(http, credentials),
            _ => null,
        });

    public IReadOnlyList<IImageProvider> GenerativeImages()
        => Build<IImageProvider>(GenerativeImageRole, key => key switch
        {
            "pollinations" => new PollinationsImageProvider(http),
            _ => null,
        });

    /// Konuşma sentezi zinciri.
    ///
    /// SIRA KATALOGDAN: `windows-speech` yalnızca Windows'ta ses
    /// veriyor ve Linux kabında Kaynak hatası dönüp sıradakine
    /// (Piper) geçiyor. Sırayı koda gömmek, kabın farklı bir sıra
    /// istemesi hâlinde kod değiştirmek demekti.
    public IReadOnlyList<ITtsProvider> Tts()
        => Build<ITtsProvider>(TtsRole, key => key switch
        {
            "windows-speech" => new WindowsSpeechTtsProvider(),
            "piper" => new SidecarTtsProvider(http, ToolsSidecarOptions.FromEnvironment()),
            "elevenlabs" => new ElevenLabsTtsProvider(http, credentials: credentials),
            _ => null,
        });

    public IReadOnlyList<IMusicProvider> Music()
        => Build<IMusicProvider>(MusicRole, key => key switch
        {
            "openverse-audio" => new OpenverseMusicProvider(http),
            _ => null,
        });

    /// Yayıncılar.
    ///
    /// ***SAHTE YAYINCI BURADA YOK ve olmamalı:*** katalog gerçek
    /// servisleri tarif ediyor. Sahte yayıncıyı kayıt tarafı ekliyor,
    /// çünkü o bir SERVİS değil bir TEST ARACI.
    public IReadOnlyList<IPublisher> Publishers()
        => Build<IPublisher>(PublishRole, key => key switch
        {
            "youtube" => new YouTubePublisher(http, credentials: credentials),
            "tiktok" => new TikTokPublisher(http, credentials: credentials),
            "instagram" => new InstagramPublisher(http, credentials: credentials),
            _ => null,
        });

    private List<T> Build<T>(string role, Func<string, T?> create)
        where T : class
    {
        var built = new List<T>();

        foreach (var descriptor in Describe(role))
        {
            var provider = create(descriptor.Key);

            if (provider is null)
            {
                // ***KATALOGDA VAR, KODDA KARŞILIĞI YOK.***
                //
                // Sessiz geçmek, `routing` içindeki bir yazım hatasının
                // (`"pollinatons"`) o sağlayıcıyı kaybettirmesi ve
                // kimsenin fark etmemesi demekti. Katalog veri olduğu
                // için derleyici de yakalayamıyor.
                onWarning?.Invoke(
                    $"'{descriptor.Key}' katalogda '{role}' rolünde tanımlı ama kodda karşılığı yok; atlanıyor.");

                continue;
            }

            built.Add(provider);
        }

        return built;
    }
}
