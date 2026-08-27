using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Providers.Fake;
using BytemountsAiStudio.Providers.Llm;
using BytemountsAiStudio.Providers.Open;
using BytemountsAiStudio.Workflow.Engine;

namespace BytemountsAiStudio.Nodes;

/// Node işleyicilerinin kaydı.
///
/// Kayıt tek yerde, çünkü workflow doğrulaması bilinen tipleri buradan
/// alıyor: kayıtlı olmayan bir node tipine sahip graf hiç kaydedilemiyor
/// (§6.2). İki ayrı yerde kayıt yapılsaydı, biri eksik kalır ve hata ancak
/// run'ın ortasında ortaya çıkardı.
public static class NodeHandlerRegistration
{
    private static readonly string[] TurkishTags = ["sahte", "test"];

    private static readonly string[] EnglishTags = ["fake", "test"];

    /// Faz 0'ın sahte hattı: tüm sağlayıcılar fake, ağa çıkılmıyor.
    public static NodeRegistry BuildFakeRegistry(
        IStorageProvider storage,
        string outputDirectory,
        string ffmpegPath = "ffmpeg",
        string ffprobePath = "ffprobe")
    {
        var llm = new FakeLlmProvider
        {
            // Sahte model senaryoyu istemden turetiyor: handler'in sahte
            // saglayiciyi onceden doldurmasi gerekmiyor.
            ToolResponder = (tool, messages) =>
            {
                var last = messages.Count > 0 ? messages[^1].Content : "konu";
                var turkish = last.Contains("tr-TR", StringComparison.Ordinal);

                return tool.Name switch
                {
                    "emit_script" => System.Text.Json.JsonSerializer.Serialize(new
                    {
                        sentences = ScriptGenerateHandler.BuildSentences(last, turkish ? "tr-TR" : "en-US"),
                    }),

                    // Sahte metadata GERCEKCI uzunlukta: sinirlarin
                    // altinda kalan bir baslik, kirpma yolunu hic
                    // sinamazdi. Kirpmanin kendi testleri ayri
                    // (SeoGenerateHandlerTests); burada amac hattin
                    // ucundan ucuna kosmasi.
                    "emit_metadata" => System.Text.Json.JsonSerializer.Serialize(new
                    {
                        title = turkish ? "Sahte Baslik: Kisa Video" : "Fake Title: Short Video",
                        description = turkish ? "Sahte aciklama." : "Fake description.",
                        tags = turkish ? TurkishTags : EnglishTags,
                    }),

                    _ => null,
                };
            },
        };

        return new NodeRegistry()
            .Register(new TopicSelectHandler())
            .Register(new ResearchHandler())
            .Register(new ScriptGenerateHandler(llm))
            .Register(new TtsSynthesizeHandler(new FakeTtsProvider(), storage, ffprobePath))
            .Register(new VisualResolveHandler(new FakeImageProvider(ImageProviderKind.Generative), storage))
            .Register(new TimelineCompileHandler(storage))
            .Register(new MediaRenderHandler(storage, outputDirectory, ffmpegPath, ffprobePath))
            .Register(new SeoGenerateHandler(llm));
    }

    /// ANAHTARSIZ GERÇEK hat (ADR-015).
    ///
    /// Hiçbiri API anahtarı istemiyor:
    ///   - LLM     : Ollama, yerel, ücretsiz
    ///   - Araştırma: Wikipedia resmî API
    ///   - Görsel  : önce Openverse (CC, gerçek fotoğraf), yoksa Pollinations
    ///   - Ses     : Windows'un yerel konuşma sentezi (tr-TR: Microsoft Tolga)
    ///
    /// Kalite ücretli sağlayıcıların altında — özellikle seste. Ama sistemin
    /// gerçek içerikle uçtan uca çalıştığını kanıtlıyor ve anahtar geldiğinde
    /// değişecek tek şey bu metottaki satırlar olacak.
    public static NodeRegistry BuildOpenRegistry(
        IStorageProvider storage,
        HttpClient http,
        string outputDirectory,
        string ffmpegPath = "ffmpeg",
        string ffprobePath = "ffprobe")
        => new NodeRegistry()
            .Register(new TopicSelectHandler())
            // Wikipedia METIN, Wikidata OLGU veriyor. Ikisi birlikte:
            // metinden cikarilan bir tarih yanlis olabilir, alandan
            // okunan tarihte cikarim adimi hic yok (P1-05).
            .Register(new WikipediaResearchHandler(
                WikipediaProviderAdapter.From(new WikipediaProvider(http)),
                new WikidataProvider(http)))
            // Katmanlı sağlayıcı TEK sağlayıcıyla bile devrede (P1-03):
            // anahtar geldiğinde değişen tek şey bu sözlük olsun, çağıran
            // taraf hiç değişmesin. Strong katmanı tanımlı değil, o yüzden
            // senaryo isteği Cheap'e düşüyor ve bu çıktıya yazılıyor —
            // "senaryo yerel modelle üretildi" bilgisi kayda geçsin.
            .Register(new ScriptGenerateHandler(
                TieredLlmProvider.Single(new OllamaLlmProvider(http))))
            .Register(new TtsSynthesizeHandler(new WindowsSpeechTtsProvider(), storage, ffprobePath))
            // ONCE STOK, BULUNAMAZSA URET (P1-18).
            //
            // Openverse gercek fotograf veriyor; uretilen gorselde eller,
            // yazilar ve mimari detaylar hala guvenilmez ve belgesel
            // anlatida bir hata icerigin tamamini supheli gosteriyor.
            // Ama soyut bir cumlenin stok karsiligi yok - orada
            // Pollinations devreye giriyor.
            .Register(new VisualResolveHandler(
                new StockFirstImageProvider(
                    new OpenverseImageProvider(http),
                    new PollinationsImageProvider(http),
                    StockFirstImageProvider.HttpDownloader(http)),
                storage))
            .Register(new TimelineCompileHandler(storage))
            .Register(new MediaRenderHandler(storage, outputDirectory, ffmpegPath, ffprobePath))
            .Register(new SeoGenerateHandler(TieredLlmProvider.Single(new OllamaLlmProvider(http))));

    /// Yalnızca graf doğrulaması için: hangi node tipleri tanınıyor.
    /// Depolama gerektirmediği için konfigürasyon aşamasında kullanılabilir.
    public static IReadOnlySet<string> KnownNodeTypes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "topic.select",
        "research.deep",
        "script.generate",
        "tts.synthesize",
        "visual.resolve",
        "timeline.compile",
        "media.render",
        "seo.generate",
    };
}
