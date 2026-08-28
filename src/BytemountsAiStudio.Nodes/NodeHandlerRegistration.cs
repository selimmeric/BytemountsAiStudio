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
        string ffprobePath = "ffprobe",
        ITopicUniqueness? uniqueness = null)
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

                    // Sahte cikarim: TEK iddia, ilk cumleden. Sahte
                    // dogrulama: DESTEKLENIYOR. Ikisi de sabit cunku
                    // buradaki amac hattin ucundan ucuna kosmasi;
                    // ayristirma ve karar mantiginin kendi testleri
                    // ayri (ClaimCheckHandlerTests).
                    "emit_claims" => System.Text.Json.JsonSerializer.Serialize(new
                    {
                        claims = new[]
                        {
                            new { text = turkish ? "Sahte bir olgu iddiasi." : "A fake factual claim.", sentence_index = 0 },
                        },
                    }),

                    "emit_verdict" => System.Text.Json.JsonSerializer.Serialize(new
                    {
                        verdict = "supported",
                        reason = turkish ? "Sahte kaynak destekliyor." : "Fake source supports it.",
                    }),

                    _ => null,
                };
            },
        };

        return new NodeRegistry()
            .Register(new TopicSelectHandler(uniqueness))
            .Register(new ResearchHandler())
            .Register(new ScriptGenerateHandler(llm))
            .Register(new TtsSynthesizeHandler(new FakeTtsProvider(), storage, ffprobePath))
            .Register(new VisualResolveHandler(new FakeImageProvider(ImageProviderKind.Generative), storage))
            // MUZIK TIMELINE'DAN ONCE: derleme adimi bagladan muzigi
            // okuyor ve o sirada indirilmis olmasi gerekiyor.
            // Sonrasinda kosulsaydi her videonun ilk turu muziksiz
            // cikardi ve bunu kimse fark etmezdi - muziksiz video da
            // gecerli goründuğu icin.
            .Register(new MusicSelectHandler(
                new FakeMusicProvider(), storage, FakeMusicDownloader()))
            .Register(new TimelineCompileHandler(storage))
            .Register(new MediaRenderHandler(storage, outputDirectory, ffmpegPath, ffprobePath))
            // Cikarim ve dogrulama AYNI sahte modelden; gercek hatta
            // da su an oyle. Ayrimin gerekcesi ClaimCheckHandler'da.
            .Register(new ClaimCheckHandler(llm))
            .Register(new SeoGenerateHandler(llm))
            // KAPAK SEO'DAN SONRA: kapak metni başlıktan geliyor.
            // Önce koşsaydı kapakta konu adı yazardı ve izleyicinin
            // tıkladığı başlıkla gördüğü kapak ayrışırdı.
            //
            // Bu node uzun süre YOKTU: `ThumbnailRenderer` yazılmış ve
            // testliydi ama kimse çağırmıyordu. Sonuç `qc.thumbnail`
            // kontrolünün her koşuda "ölçülmedi" diye düşmesiydi — ve o
            // BLOKLAYICI bir kontrol, yani hiçbir video otomatik
            // geçemiyordu.
            .Register(new ThumbnailRenderHandler(storage))
            .Register(new QualityCheckHandler(storage))
            // SEMANTİK QC GÖRME MODELİ OLMADAN KAYITLI.
            //
            // Model yokken kontroller "ölçülemedi" diye DÜŞÜYOR ve
            // video insana gidiyor — sessizce geçmiyor. Kaydetmemek,
            // semantik kontrolün hiç var olmadığı bir hat demekti ve
            // model geldiğinde de kimse eklemeyi hatırlamazdı.
            .Register(new SemanticQualityHandler(storage))
            .Register(new ApprovalGateHandler());
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
        string ffprobePath = "ffprobe",
        ITopicUniqueness? uniqueness = null)
    {
        // Yerel LLM TEK YERDE kuruluyor.
        //
        // Dort ayri yerde `new OllamaLlmProvider(http)` yazmak, ortam
        // degiskeni degistiginde birinin unutulmasi demekti - ve o biri
        // sessizce localhost'a baglanmaya devam ederdi.
        var llm = TieredLlmProvider.Single(
            new OllamaLlmProvider(http, OllamaOptions.FromEnvironment()));

        // Araclar yan-servisi (P1-04). Kapali olabilir ve bu NORMAL:
        // ilk cagri Kaynak hatasi donuyor, TTS isleyicisi karakter
        // bazli dagitima dusuyor ve kalan cumleler icin bir daha
        // denemiyor. Yan-servis acikken ayni hat kelime zamanlarini
        // sesten OLCUYOR (P1-15).
        var sidecar = new ToolsSidecar(http, ToolsSidecarOptions.FromEnvironment());

        return new NodeRegistry()
            .Register(new TopicSelectHandler(uniqueness))
            // Wikipedia METIN, Wikidata OLGU veriyor. Ikisi birlikte:
            // metinden cikarilan bir tarih yanlis olabilir, alandan
            // okunan tarihte cikarim adimi hic yok (P1-05).
            // PLANLI arastirma (P1-09). Onceki hal sabitti: "konuyu
            // Wikipedia'da ara, ilk uc sonucu cek" - bir konu icin
            // calisiyor, digeri icin hic sonuc vermiyordu ve neden
            // vermedigini soyleyecek bir sey yoktu.
            .Register(new ResearchAgentHandler(
                llm,
                WikipediaProviderAdapter.From(new WikipediaProvider(http)),
                new WikidataProvider(http)))
            // Katmanlı sağlayıcı TEK sağlayıcıyla bile devrede (P1-03):
            // anahtar geldiğinde değişen tek şey bu sözlük olsun, çağıran
            // taraf hiç değişmesin. Strong katmanı tanımlı değil, o yüzden
            // senaryo isteği Cheap'e düşüyor ve bu çıktıya yazılıyor —
            // "senaryo yerel modelle üretildi" bilgisi kayda geçsin.
            .Register(new ScriptGenerateHandler(llm))
            // ONCE WINDOWS, OLMAZSA PIPER (P1-26).
            //
            // Windows'un yerel sesi bedava, hizli ve bu makinede Turkce
            // icin kurulu - ama YALNIZCA kurulu dil paketleri icin ses
            // veriyor. Ikinci dilde Kaynak hatasi donuyor ve sira
            // Piper'a geciyor; Piper cevrimdisi ve istenen dili
            // konusuyor.
            .Register(new TtsSynthesizeHandler(
                new FallbackTtsProvider([
                    new WindowsSpeechTtsProvider(),
                    new SidecarTtsProvider(http, ToolsSidecarOptions.FromEnvironment()),
                ]),
                storage,
                ffprobePath,
                sidecar))
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
            // MUZIK TIMELINE'DAN ONCE: derleme adimi baglamdan muzigi
            // okuyor ve o sirada indirilmis olmasi gerekiyor.
            // Sonrasinda kosulsaydi HER videonun ilk turu muziksiz
            // cikardi ve bunu kimse fark etmezdi - muziksiz video da
            // gecerli gorundugu icin.
            .Register(new MusicSelectHandler(
                new OpenverseMusicProvider(http), storage, MusicSelectHandler.HttpDownloader(http)))
            .Register(new TimelineCompileHandler(storage))
            .Register(new MediaRenderHandler(storage, outputDirectory, ffmpegPath, ffprobePath))
            .Register(new ClaimCheckHandler(llm))
            .Register(new SeoGenerateHandler(llm))
            // KAPAK SEO'DAN SONRA: kapak metni başlıktan geliyor.
            // Önce koşsaydı kapakta konu adı yazardı ve izleyicinin
            // tıkladığı başlıkla gördüğü kapak ayrışırdı.
            //
            // Bu node uzun süre YOKTU: `ThumbnailRenderer` yazılmış ve
            // testliydi ama kimse çağırmıyordu. Sonuç `qc.thumbnail`
            // kontrolünün her koşuda "ölçülmedi" diye düşmesiydi — ve o
            // BLOKLAYICI bir kontrol, yani hiçbir video otomatik
            // geçemiyordu.
            .Register(new ThumbnailRenderHandler(storage))
            // QC HER İKİ hatta da: skoru üreten tek yer burası ve
            // olmadan onay kapısı hep "skor yok" görüyor, yani
            // seçici onay hiç devreye giremiyor (P2-08).
            .Register(new QualityCheckHandler(storage))
            // SEMANTİK QC GÖRME MODELİ OLMADAN KAYITLI.
            //
            // Model yokken kontroller "ölçülemedi" diye DÜŞÜYOR ve
            // video insana gidiyor — sessizce geçmiyor. Kaydetmemek,
            // semantik kontrolün hiç var olmadığı bir hat demekti ve
            // model geldiğinde de kimse eklemeyi hatırlamazdı.
            .Register(new SemanticQualityHandler(storage))
            // Onay kapısı HER İKİ hatta da kayıtlı: sahte hatta
            // kayıtlı olmasaydı onay içeren bir graf sahte koşuda
            // "bilinmeyen node tipi" diye reddedilirdi.
            .Register(new ApprovalGateHandler());
    }

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
        "thumbnail.render",
        "claim.check",
        "music.select",
        "qc.mechanical",
        "qc.semantic",
        "human.approval",
    };

    /// Sahte hattın müzik indiricisi.
    ///
    /// Gerçek bir WAV üretiyor, boş bayt dizisi değil: sahte parça
    /// indirilemezse müzik yolu (timeline'a bağlanma, ducking, render)
    /// sahte hatta HİÇ koşmaz ve Faz 0 kabulü müziksiz geçerdi.
    private static Func<Uri, CancellationToken, Task<Core.Result<DownloadedAudio>>> FakeMusicDownloader()
        => (_, _) => Task.FromResult(Core.Result.Success(
            new DownloadedAudio(
                Providers.Fake.Media.WavWriter.Silence(new Core.Time.Ms(90_000)), "audio/wav")));
}
