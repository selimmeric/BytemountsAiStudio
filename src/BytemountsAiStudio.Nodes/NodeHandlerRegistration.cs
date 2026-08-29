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
    ///
    /// `uniqueness` VE `channels` ZORUNLU, isteğe bağlı değil.
    ///
    /// İkisi de bir süre `= null` varsayılanlıydı ve sonucu şuydu:
    /// CLI ikisini de veriyordu, API ve Worker HİÇBİRİNİ vermiyordu.
    /// Derleyici bir şey söylemedi, çünkü unutmak geçerli bir çağrıydı.
    ///
    /// Verilmediğinde olan şey sessizdi ama küçük değildi:
    ///   - tekillik ÖLÇÜLMÜYOR, QC "ölçülmedi" deyip her videoyu
    ///     insana gönderiyor — yani otonomi bitiyor
    ///   - kanal kimliği OKUNMUYOR: ses, yazı tipi, en-boy oranı ve
    ///     onay modu varsayılana düşüyor. Üç ayrı kanal, tek tip
    ///     video üretiyor ve "çoklu kanal" iddiası boşalıyor
    ///
    /// Zorunlu olduklarında derleyici bütün çağrı yerlerini
    /// sayıyor. Bir çağıranın gerçekten ihtiyacı yoksa bunu AÇIKÇA
    /// yazması gerekiyor; unutmak artık mümkün değil.
    /// ***`pipeline` ZORUNLU VE NULL OLABİLİR — ikisi birden.***
    ///
    /// Opsiyonel olsaydı bu dosyanın en pahalı dersi bir kez daha
    /// ödenirdi: `uniqueness` ve `channels` bir süre `= null`
    /// varsayılanlıydı ve API ile Worker ikisini de vermiyordu.
    /// Derleyici bir şey söylemedi çünkü unutmak geçerli bir çağrıydı.
    /// Zorunlu olduğunda derleyici bütün çağrı yerlerini sayıyor;
    /// zinciri istemeyen taraf `null` yazmak ZORUNDA ve bu bir karar
    /// olarak görünüyor.
    ///
    /// `null` geçmek gerçek bir seçenek: zincir maliyet defterine
    /// bağlı, defter veritabanına. Veritabanı olmayan bir testte
    /// zincirsiz kurmak doğru davranış.
    public static NodeRegistry BuildFakeRegistry(
        IStorageProvider storage,
        string outputDirectory,
        ITopicUniqueness uniqueness,
        IChannelPolicy channels,
        ProviderPipeline? pipeline,
        string? ffmpegPath = null,
        string? ffprobePath = null)
    {
        // FFMPEG YOLU ORTAMDAN DA GELEBILIYOR (`BMAI_FFMPEG`).
        //
        // Once yalnizca parametreydi ve hicbir host onu VERMIYORDU:
        // hepsi `PATH`'teki "ffmpeg"e dusuyordu. Windows'ta ffmpeg
        // `PATH`'te degilse render her kosuda dusuyor ve tek cozum
        // MAKINENIN `PATH`'ini degistirmek oluyordu; ayni makinede iki
        // farkli ffmpeg surumu kullanmak imkansizdi.
        ffmpegPath = Media.Rendering.MediaTools.Ffmpeg(ffmpegPath);
        ffprobePath = Media.Rendering.MediaTools.Ffprobe(ffprobePath);

        var fakeLlm = new FakeLlmProvider
        {
            // SAHTE MAKALE, GEÇERLİ BİÇİMDE (P6-04). Denetim başlık,
            // uzunluk ve atıf istiyor; bunları sağlamayan bir sahte
            // çıktı, blog hattını sahte koşuda hiç sınayamaz hâle
            // getirirdi.
            TextResponder = request =>
            {
                var text = string.Concat(request.Messages.Select(m => m.Content));

                return text.Contains("MAKALE SENARYO DEĞİL", StringComparison.Ordinal)
                    ? FakeArticle(text)
                    : null;
            },

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

                    // UZUN VIDEO: bolum plani ve bolum senaryosu (P3-02).
                    //
                    // Sahte hat bunlari da kosmali, yoksa uzun video
                    // grafi ilk node'da duser ve yapinin dogru olup
                    // olmadigini ancak gercek bir modelle ogrenirdik.
                    //
                    // BES BOLUM: on dakikalik hedefte planlayicinin
                    // sinirlarina uyan bir sayi. Uc bolum de gecerdi
                    // ama bes, kirpma ve dagitim yollarini da
                    // kosturuyor.
                    "emit_chapters" => System.Text.Json.JsonSerializer.Serialize(new
                    {
                        chapters = Enumerable.Range(1, 5).Select(i => new
                        {
                            title = turkish ? $"Sahte bolum {i}" : $"Fake chapter {i}",
                            question = turkish
                                ? $"{i}. bolum hangi soruyu cevapliyor"
                                : $"What does chapter {i} answer",
                        }),
                    }),

                    // Bolum senaryosu: istemde gecen cumle sayisini
                    // TAM olarak uretmiyor cunku gercek model de
                    // uretmiyor - yaklasik veriyor ve hat buna
                    // dayanikli olmali.
                    "emit_chapter_script" => System.Text.Json.JsonSerializer.Serialize(new
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

        // ZINCIR SAHTE HATTA DA TAKILI.
        //
        // Sahte saglayicilar para harcamiyor ve tam da bu yuzden
        // gerekli: olcumun, butce kapisinin ve devre kesicinin
        // calistigi tek yerde -- yani ucret olmadan -- sinanabilmesi
        // lazim. Yalnizca gercek hatta takili olsaydi, zincirin
        // dogru kurulup kurulmadigi ancak para harcayarak
        // ogrenilirdi.
        var llm = fakeLlm.Wrap(pipeline);

        return new NodeRegistry { Kind = PipelineKind.Fake }
            .Register(new TopicSelectHandler(uniqueness))
            .Register(new ResearchHandler())
            .Register(new ScriptGenerateHandler(llm))
            // Makale node'u HER İKİ hatta da kayıtlı: sahte hatta
            // kayıtlı olmasaydı blog grafı sahte koşuda "bilinmeyen
            // node tipi" diye reddedilirdi (onay kapısıyla aynı ders).
            .Register(new ArticleGenerateHandler(llm))
            // UZUN VIDEO: bolum plani + bolum bolum senaryo (P3-02).
            //
            // Ayni kayitta duruyorlar ama ayni GRAFTA degil: kisa
            // video grafi `script.generate`, uzun video grafi
            // `chapter.plan` + `script.long` kullaniyor. Kayit ortak,
            // secim graftan.
            .Register(new ChapterPlanHandler(llm))
            .Register(new LongScriptHandler(llm))
            .Register(new TtsSynthesizeHandler(
                new FakeTtsProvider().Wrap(pipeline), storage, ffprobePath, channels: channels))
            .Register(new VisualResolveHandler(
                new FakeImageProvider(ImageProviderKind.Generative).Wrap(pipeline), storage))
            // MUZIK TIMELINE'DAN ONCE: derleme adimi bagladan muzigi
            // okuyor ve o sirada indirilmis olmasi gerekiyor.
            // Sonrasinda kosulsaydi her videonun ilk turu muziksiz
            // cikardi ve bunu kimse fark etmezdi - muziksiz video da
            // gecerli goründuğu icin.
            .Register(new MusicSelectHandler(
                new FakeMusicProvider().Wrap(pipeline), storage, FakeMusicDownloader()))
            .Register(new TimelineCompileHandler(storage, channels))
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
            .Register(new ThumbnailRenderHandler(storage, channels))
            .Register(new QualityCheckHandler(storage))
            // SEMANTİK QC GÖRME MODELİ OLMADAN KAYITLI.
            //
            // Model yokken kontroller "ölçülemedi" diye DÜŞÜYOR ve
            // video insana gidiyor — sessizce geçmiyor. Kaydetmemek,
            // semantik kontrolün hiç var olmadığı bir hat demekti ve
            // model geldiğinde de kimse eklemeyi hatırlamazdı.
            .Register(new SemanticQualityHandler(storage))
            .Register(new ApprovalGateHandler(channels))

            // YAYIN NODE'U: boru hattının ucu. Sahte yayıncı gerçek bir
            // kimlik üretiyor ve idempotency'yi hatırlıyor, yani "aynı
            // videoyu iki kez yayınlama" kuralı sahte hatta da
            // sınanabiliyor.
            .Register(new PublishHandler(
                [new Providers.Fake.FakePublisher().Wrap(pipeline)],
                new Providers.Fake.UnlimitedQuotaPool()));
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
        ITopicUniqueness uniqueness,
        IChannelPolicy channels,
        ProviderPipeline? pipeline,
        IQuotaPool quota,
        string? ffmpegPath = null,
        string? ffprobePath = null,
        ProviderCatalog? catalog = null,
        ICredentialSource? credentials = null,
        Action<string>? onWarning = null)
    {
        ArgumentNullException.ThrowIfNull(quota);

        // ***SAGLAYICI SIRASI ARTIK KATALOGDAN (ADR-015).***
        //
        // `routing` blogu hicbir kurulum kodundan OKUNMUYORDU --
        // yalnizca `bmai providers` ekrani onu gosteriyordu.
        // Saglayicilar burada elle, sabit bir sirayla kuruluyordu ve
        // ADR-015'in "anahtar geldiginde yapilacak tek sey `enabled`
        // alanini acip yonlendirme listesinin basina almak" iddiasi
        // YANLISTI: gereken sey bir kod degisikligiydi.
        //
        // Olculebilir bedeli: BES ADAPTOR hicbir yerden kurulmuyordu
        // (Searxng, DuckDuckGo, Gemini, ElevenLabs, Pexels) -- hepsi
        // yazilmis, testlenmis ve erisilemezdi.
        //
        // KATALOG YOKSA ESKI SABIT ZINCIRLER: dosyanin bulunamamasi
        // uretimi durdurmak icin sebep degil. Ama sessiz de degil.
        var factory = catalog is null
            ? null
            : new ProviderFactory(http, catalog, credentials, onWarning);

        // FFMPEG YOLU ORTAMDAN DA GELEBILIYOR (`BMAI_FFMPEG`).
        //
        // Once yalnizca parametreydi ve hicbir host onu VERMIYORDU:
        // hepsi `PATH`'teki "ffmpeg"e dusuyordu. Windows'ta ffmpeg
        // `PATH`'te degilse render her kosuda dusuyor ve tek cozum
        // MAKINENIN `PATH`'ini degistirmek oluyordu; ayni makinede iki
        // farkli ffmpeg surumu kullanmak imkansizdi.
        ffmpegPath = Media.Rendering.MediaTools.Ffmpeg(ffmpegPath);
        ffprobePath = Media.Rendering.MediaTools.Ffprobe(ffprobePath);

        // Yerel LLM TEK YERDE kuruluyor.
        //
        // Dort ayri yerde `new OllamaLlmProvider(http)` yazmak, ortam
        // degiskeni degistiginde birinin unutulmasi demekti - ve o biri
        // sessizce localhost'a baglanmaya devam ederdi.
        //
        // ZINCIR KATMANLI SAGLAYICININ DISINDA, ICINDE DEGIL: icine
        // koymak, yedege dusen her cagriyi ikinci kez olcmek ve ayni
        // isi iki kez saymak olurdu. Disarida oldugunda "bir senaryo
        // uretildi" tek satir, hangi saglayicinin urettigi ise o
        // satirin icinde.
        // ***ANAHTARSIZ HATTIN LLM'İ ARTIK GPU İSTEMİYOR.***
        //
        // Önceden tek seçenek Ollama'ydı, yani YEREL BİR GPU. GPU'su
        // olmayan (ya da GPU'sunu kullanamayan) bir makinede anahtarsız
        // hat senaryo üretemiyordu: "anahtarsız çalışır" iddiası
        // pratikte "yerel modeli olan makinede çalışır" demekti.
        // Katalogda `pollinations-text` satırı VARDI ve karşılığında
        // hiçbir kod yoktu.
        //
        // SIRA ÖNEMLİ: önce Pollinations (anahtarsız, bulut, GPU
        // istemiyor), düşerse Ollama (çevrimdışı, yerel). Tersi
        // olsaydı Ollama'sı olmayan her makinede her senaryo çağrısı
        // önce bağlantı hatası verip sonra buluta düşerdi — çalışırdı
        // ama her çağrıda bir zaman aşımı ödeyerek.
        //
        // Katman başına iki sağlayıcı: yedeğe düşüş `ProviderRouter`
        // içinde ve hangi sağlayıcının ürettiği çıktıya yazılıyor.
        // ZINCIR KATALOGDAN; katalog yoksa ya da bos donduyse eski
        // sabit siradan. Bos donmesi bir YAPILANDIRMA hatasi ve
        // sessiz gecilmiyor.
        var llmChain = Fallback(factory?.Llm(), () => Chain(http), "llm", onWarning);

        var llm = new TieredLlmProvider(
            new Dictionary<ModelTier, IReadOnlyList<ILlmProvider>>
            {
                // UC KATMAN AYNI ZINCIRI PAYLASIYOR: katalog katman
                // ayrimi tasimiyor (model adlari ortam degiskeninden
                // geliyor). Ayri listeler tutmak, katalogda olmayan
                // bir ayrimi varmis gibi gostermekti.
                [ModelTier.Cheap] = llmChain,
                [ModelTier.Standard] = llmChain,
                [ModelTier.Strong] = llmChain,
            }).Wrap(pipeline);

        // Araclar yan-servisi (P1-04). Kapali olabilir ve bu NORMAL:
        // ilk cagri Kaynak hatasi donuyor, TTS isleyicisi karakter
        // bazli dagitima dusuyor ve kalan cumleler icin bir daha
        // denemiyor. Yan-servis acikken ayni hat kelime zamanlarini
        // sesten OLCUYOR (P1-15).
        var sidecar = new ToolsSidecar(http, ToolsSidecarOptions.FromEnvironment());

        // ARAMA ZINCIRDEN GECIYOR, SAYFA CEKME GECMIYOR.
        //
        // Adaptor iki arayuzu tek nesnede tasiyor ve zincir yalnizca
        // `ISearchProvider` tarafini sarabiliyor: `IWebFetchProvider`
        // `ProviderResponse` dondurmuyor, yani olculecek bir birimi de
        // yok. Wikipedia'nin saniyede on istek siniri boylece aramada
        // uygulaniyor, sayfa cekmede uygulanmiyor -- eksik, ve gizli
        // degil.
        var wikipediaProvider = new WikipediaProvider(http);

        var wikipedia = new WikipediaProviderAdapter(
            wikipediaProvider.Wrap(pipeline), wikipediaProvider);

        return new NodeRegistry { Kind = PipelineKind.Open }
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
                wikipedia,
                new WikidataProvider(http)))
            // Katmanlı sağlayıcı TEK sağlayıcıyla bile devrede (P1-03):
            // anahtar geldiğinde değişen tek şey bu sözlük olsun, çağıran
            // taraf hiç değişmesin. Strong katmanı tanımlı değil, o yüzden
            // senaryo isteği Cheap'e düşüyor ve bu çıktıya yazılıyor —
            // "senaryo yerel modelle üretildi" bilgisi kayda geçsin.
            .Register(new ScriptGenerateHandler(llm))
            // Makale node'u HER İKİ hatta da kayıtlı: sahte hatta
            // kayıtlı olmasaydı blog grafı sahte koşuda "bilinmeyen
            // node tipi" diye reddedilirdi (onay kapısıyla aynı ders).
            .Register(new ArticleGenerateHandler(llm))
            // UZUN VIDEO: bolum plani + bolum bolum senaryo (P3-02).
            //
            // Ayni kayitta duruyorlar ama ayni GRAFTA degil: kisa
            // video grafi `script.generate`, uzun video grafi
            // `chapter.plan` + `script.long` kullaniyor. Kayit ortak,
            // secim graftan.
            .Register(new ChapterPlanHandler(llm))
            .Register(new LongScriptHandler(llm))
            // ONCE WINDOWS, OLMAZSA PIPER (P1-26).
            //
            // Windows'un yerel sesi bedava, hizli ve bu makinede Turkce
            // icin kurulu - ama YALNIZCA kurulu dil paketleri icin ses
            // veriyor. Ikinci dilde Kaynak hatasi donuyor ve sira
            // Piper'a geciyor; Piper cevrimdisi ve istenen dili
            // konusuyor.
            // TTS ZINCIRI KATALOGDAN: sira `routing.tts` icinde.
            // Windows'un yerel sesi yalnizca Windows'ta ses veriyor ve
            // Linux kabinda Kaynak hatasi donup siradakine (Piper)
            // geciyor. Sirayi koda gommek, kabin farkli bir sira
            // istemesi halinde KOD degistirmek demekti.
            .Register(new TtsSynthesizeHandler(
                new FallbackTtsProvider(Fallback(
                    factory?.Tts(),
                    () =>
                    [
                        new WindowsSpeechTtsProvider(),
                        new SidecarTtsProvider(http, ToolsSidecarOptions.FromEnvironment()),
                    ],
                    "tts", onWarning)).Wrap(pipeline),
                storage,
                ffprobePath,
                sidecar,
                channels))
            // ONCE STOK, BULUNAMAZSA URET (P1-18).
            //
            // Openverse gercek fotograf veriyor; uretilen gorselde eller,
            // yazilar ve mimari detaylar hala guvenilmez ve belgesel
            // anlatida bir hata icerigin tamamini supheli gosteriyor.
            // Ama soyut bir cumlenin stok karsiligi yok - orada
            // Pollinations devreye giriyor.
            .Register(new VisualResolveHandler(
                // STOK VE URETICI GORSEL DE KATALOGDAN. Pexels
                // adaptoru yazilmis ve HICBIR YERDEN kurulmuyordu:
                // anahtar gelse bile kullanilamazdi.
                new StockFirstImageProvider(
                    Fallback(factory?.StockImages(),
                        () => [new OpenverseImageProvider(http)], "image.stock", onWarning)[0],
                    Fallback(factory?.GenerativeImages(),
                        () => [new PollinationsImageProvider(http)], "image.generative", onWarning)[0],
                    StockFirstImageProvider.HttpDownloader(http)).Wrap(pipeline),
                storage))
            // MUZIK TIMELINE'DAN ONCE: derleme adimi baglamdan muzigi
            // okuyor ve o sirada indirilmis olmasi gerekiyor.
            // Sonrasinda kosulsaydi HER videonun ilk turu muziksiz
            // cikardi ve bunu kimse fark etmezdi - muziksiz video da
            // gecerli gorundugu icin.
            .Register(new MusicSelectHandler(
                Fallback(factory?.Music(), () => [new OpenverseMusicProvider(http)],
                    "music", onWarning)[0].Wrap(pipeline),
                storage,
                MusicSelectHandler.HttpDownloader(http)))
            .Register(new TimelineCompileHandler(storage, channels))
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
            .Register(new ThumbnailRenderHandler(storage, channels))
            // QC HER İKİ hatta da: skoru üreten tek yer burası ve
            // olmadan onay kapısı hep "skor yok" görüyor, yani
            // seçici onay hiç devreye giremiyor (P2-08).
            .Register(new QualityCheckHandler(storage))
            // ***SEMANTİK QC ARTIK GERÇEK BİR GÖRME MODELİYLE (P2-06).***
            //
            // Uzun süre model YOKTU ve kontroller "ölçülemedi" diye
            // düşüyordu — doğru davranış ama pahalı: hiçbir video
            // otomatik geçemiyordu, yani otonomi bu kontrolde
            // duruyordu. Sebep de yazılıydı: 6 GB'lık bir görme
            // modelini karta yüklemek bu makinede mümkün değil.
            //
            // Anahtarsız bir görme modeli bunu GPU'suz çözüyor. Model
            // erişilemezse davranış DEĞİŞMİYOR: kontrol yine
            // "ölçülemedi" diye düşüyor ve video insana gidiyor —
            // sessizce geçmiyor.
            .Register(new SemanticQualityHandler(
                storage,
                new OpenAiCompatibleVisionProvider(
                    http, OpenAiCompatibleOptions.Pollinations()).Wrap(pipeline),
                llm))
            // Onay kapısı HER İKİ hatta da kayıtlı: sahte hatta
            // kayıtlı olmasaydı onay içeren bir graf sahte koşuda
            // "bilinmeyen node tipi" diye reddedilirdi.
            .Register(new ApprovalGateHandler(channels))

            // YAYIN NODE'U: boru hattının ucu. Sahte yayıncı gerçek bir
            // kimlik üretiyor ve idempotency'yi hatırlıyor, yani "aynı
            // videoyu iki kez yayınlama" kuralı sahte hatta da
            // sınanabiliyor.
            // ***GERÇEK YAYINCILAR BURADA (P1-24/25, P6-01/02).***
            //
            // YouTube, TikTok ve Instagram adaptörleri yazılmış,
            // testlenmiş ve HİÇBİR YERDE KURULMUYORDU: gerçek hat da
            // sahte yayıncıyla yayınlıyordu, yani boru hattının UCU
            // hiçbir platforma bağlı değildi.
            //
            // SAHTE YAYINCI DA LİSTEDE ve bu kasıtlı: seçim graftaki
            // `platform` alanından yapılıyor, dolayısıyla `"fake"`
            // yazan bir graf anahtarsız makinede uçtan uca koşabiliyor.
            // Sessiz bir yedek değil, AÇIK bir seçim.
            //
            // ANAHTAR YOKSA YAYIN KALICI HATAYLA DÜŞÜYOR ("kimlik
            // eksik") ve bu doğru: yayınlanmamış bir videoyu
            // "yayınlandı" saymaktansa açık bir hata vermek gerekiyor.
            .Register(new PublishHandler(
                [
                    // GERCEK YAYINCILAR KATALOGDAN. Katalogda hepsi
                    // `enabled: false` (anahtar yok) ve o yuzden bugun
                    // liste bos donuyor -- sabit varsayilan devrede.
                    // Anahtar geldiginde degisecek tek sey katalog
                    // satiri olacak, bu dosya degil.
                    .. Fallback(
                        factory?.Publishers(),
                        () =>
                        [
                            new YouTubePublisher(http, credentials: credentials),
                            new TikTokPublisher(http, credentials: credentials),
                            new InstagramPublisher(http, credentials: credentials),
                        ],
                        "publish", onWarning).Select(p => p.Wrap(pipeline)),

                    // SAHTE YAYINCI KATALOGDAN GELMIYOR ve gelmemeli:
                    // katalog gercek servisleri tarif ediyor, bu bir
                    // TEST ARACI. Secim graftaki `platform` alanindan.
                    new Providers.Fake.FakePublisher().Wrap(pipeline),
                ],
                quota));
    }

    /// Anahtarsız LLM zinciri: önce bulut, sonra yerel.
    ///
    /// TEK YERDE çünkü üç katman aynı sırayı kullanmak zorunda. Ayrı
    /// ayrı yazılsaydı biri güncellenip diğeri unutulur ve `Strong`
    /// katmanı sessizce başka bir sağlayıcıya giderdi — çıktıda
    /// görünürdü ama kimse bakmazdı.
    private static IReadOnlyList<ILlmProvider> Chain(HttpClient http)
        => [
            new OpenAiCompatibleLlmProvider(http, OpenAiCompatibleOptions.Pollinations()),
            new OllamaLlmProvider(http, OllamaOptions.FromEnvironment()),
        ];

    /// Katalogdan gelen liste bossa sabit varsayilana dusuyor.
    ///
    /// ***BOS LISTE SESSIZ GECILMIYOR.*** Katalogda o rolun butun
    /// saglayicilari kapaliysa ya da hepsi anahtar bekliyorsa, hattin
    /// o adimi hic calisamaz. Uyari olmadan, "neden senaryo
    /// uretilmiyor" sorusunun cevabi hicbir yerde olmazdi.
    private static IReadOnlyList<T> Fallback<T>(
        IReadOnlyList<T>? fromCatalog, Func<IReadOnlyList<T>> builtIn,
        string role, Action<string>? onWarning)
    {
        if (fromCatalog is { Count: > 0 })
        {
            return fromCatalog;
        }

        if (fromCatalog is not null)
        {
            onWarning?.Invoke(
                $"Katalogda '{role}' rolu icin kullanilabilir saglayici yok; "
                + "kodun sabit varsayilani kullaniliyor.");
        }

        return builtIn();
    }

    /// Yalnızca graf doğrulaması için: hangi node tipleri tanınıyor.
    /// Depolama gerektirmediği için konfigürasyon aşamasında kullanılabilir.
    public static IReadOnlySet<string> KnownNodeTypes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "topic.select",
        "research.deep",
        "script.generate",
        "article.generate",
        "chapter.plan",
        "script.long",
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
        "publish.upload",
    };

    /// Sahte ama BİÇİMİ GEÇERLİ makale (P6-04).
    ///
    /// İki başlık, iki yüzden fazla kelime ve `[1]` atfı: denetimin
    /// istediği her şey. Denetimi atlatan bir sahte çıktı, denetimin
    /// çalışıp çalışmadığını da gizlerdi.
    private static string FakeArticle(string prompt)
    {
        var turkish = prompt.Contains("tr-TR", StringComparison.Ordinal);
        var builder = new System.Text.StringBuilder();

        builder.AppendLine(turkish ? "# Sahte makale" : "# Fake article").AppendLine();
        builder.AppendLine(turkish
            ? "Bu metin sahte hat tarafından üretildi ve yalnızca boru hattını sınıyor [1]."
            : "This text was produced by the fake pipeline and only exercises the pipeline [1].");

        for (var section = 1; section <= 2; section++)
        {
            builder.AppendLine();
            builder.Append("## ").AppendLine(turkish ? $"Bolum {section}" : $"Section {section}");
            builder.AppendLine();

            for (var paragraph = 0; paragraph < 6; paragraph++)
            {
                builder.Append(turkish
                    ? "Kaynaklara gore bu bolumde anlatilan sey dogrulanabilir bir olgudur"
                    : "According to the sources this section states a verifiable fact");

                builder.Append(" [1]. ");
                builder.AppendLine(turkish
                    ? "Ayrintilar arastirma adiminda toplanan belgelerden geliyor ve metin bunlarin disina cikmiyor."
                    : "Details come from documents gathered in the research step and the text stays within them.");
            }
        }

        return builder.ToString();
    }

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
