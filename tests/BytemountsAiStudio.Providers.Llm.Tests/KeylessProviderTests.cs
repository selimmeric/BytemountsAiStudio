using System.Net;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core.Errors;

namespace BytemountsAiStudio.Providers.Llm.Tests;

/// Anahtarsız bulut sağlayıcısı ve görme modeli (ADR-015, P2-06).
///
/// ***BU DOSYANIN VAR OLMA SEBEBİ İKİ AYRI BOŞLUK:***
///
/// 1. Katalogda `pollinations-text` satırı VARDI ve karşılığında hiçbir
///    kod yoktu. Anahtarsız hattın tek LLM'i Ollama'ydı, yani **yerel
///    bir GPU** — GPU'su olmayan bir makinede anahtarsız hat senaryo
///    üretemiyordu. "Anahtarsız çalışır" iddiası pratikte "yerel modeli
///    olan makinede çalışır" demekti.
///
/// 2. Semantik QC'nin görme modeli yoktu ve kontroller "ölçülemedi"
///    diye düşüyordu — doğru ama pahalı: hiçbir video otomatik
///    geçemiyordu, yani otonomi bu kontrolde duruyordu.
///
/// AĞA ÇIKILMIYOR: gönderilen isteğin ŞEKLİ ve cevabın ayrıştırılması
/// sınanıyor.
public sealed class KeylessProviderTests
{
    /// Bir piksellik PNG — MIME tespitinin sınanabilmesi için gerçek
    /// bir imza taşıyor.
    private static readonly byte[] Png =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01];

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46];

    /* ---- anahtarsız metin ---- */

    /// ***ANAHTAR YOKKEN İSTEK YİNE GİDİYOR.***
    ///
    /// BU DOSYANIN EN ÖNEMLİ TESTİ. Anahtar zorunlu tutulsaydı
    /// Pollinations hiç çalışmaz ve anahtarsız hat yine GPU'ya bağlı
    /// kalırdı.
    [Fact]
    public async Task AnahtarYok_IstekGidiyor()
    {
        var handler = new CaptureHandler(HttpStatusCode.OK,
            """{"model":"openai","choices":[{"finish_reason":"stop","message":{"content":"merhaba"}}]}""");

        using var http = new HttpClient(handler);

        var provider = new OpenAiCompatibleLlmProvider(
            http, OpenAiCompatibleOptions.Pollinations(), new StubCredentials(null));

        var result = await provider.CompleteAsync(
            new LlmRequest { Messages = [new(ChatRole.User, "selam")], Tier = ModelTier.Cheap },
            ProviderContext.ForTest(),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);

        // ***BOŞ "Bearer " BAŞLIĞI GÖNDERİLMİYOR.***
        //
        // Bazı sunucular boş bir yetkilendirme başlığını 401 ile
        // reddediyor: "anahtar yok" ile "anahtar boş" farklı şeyler.
        Assert.Null(handler.LastHeaders?.Authorization);
    }

    /// ANAHTAR VARSA GÖNDERİLİYOR.
    ///
    /// "Anahtarsız çalışır" ile "anahtar kullanılmaz" farklı şeyler:
    /// jeton verildiğinde daha yüksek hız sınırı tanınıyor.
    [Fact]
    public async Task AnahtarVarsa_Gonderiliyor()
    {
        var handler = new CaptureHandler(HttpStatusCode.OK,
            """{"model":"openai","choices":[{"finish_reason":"stop","message":{"content":"x"}}]}""");

        using var http = new HttpClient(handler);

        var provider = new OpenAiCompatibleLlmProvider(
            http, OpenAiCompatibleOptions.Pollinations(), new StubCredentials("jeton-123"));

        await provider.CompleteAsync(
            new LlmRequest { Messages = [new(ChatRole.User, "selam")], Tier = ModelTier.Cheap },
            ProviderContext.ForTest(),
            CancellationToken.None);

        Assert.Equal("Bearer jeton-123", handler.LastHeaders?.Authorization);
    }

    /// ***ANAHTAR ZORUNLU OLAN SAĞLAYICI HÂLÂ ANAHTAR İSTİYOR.***
    ///
    /// Anahtarı isteğe bağlı yapan değişiklik, ücretli sağlayıcıları da
    /// anahtarsız hâle getirseydi 401 döngüsünde kota harcanırdı.
    [Fact]
    public async Task UcretliSaglayici_AnahtarIstiyor()
    {
        using var http = new HttpClient(new CaptureHandler(HttpStatusCode.OK, "{}"));

        var provider = new OpenAiCompatibleLlmProvider(
            http, OpenAiCompatibleOptions.OpenAi(), new StubCredentials(null));

        var result = await provider.CompleteAsync(
            new LlmRequest { Messages = [new(ChatRole.User, "x")], Tier = ModelTier.Cheap },
            ProviderContext.ForTest(),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("openai.no_key", result.Error.Code);
        Assert.Equal(ErrorKind.Permanent, result.Error.Kind);
    }

    /// GÖMME SUNMUYOR VE BUNU AÇIKÇA SÖYLÜYOR.
    [Fact]
    public async Task Pollinations_GommeSunmuyor()
    {
        using var http = new HttpClient(new CaptureHandler(HttpStatusCode.OK, "{}"));

        var result = await new OpenAiCompatibleLlmProvider(http, OpenAiCompatibleOptions.Pollinations())
            .EmbedAsync("metin", ProviderContext.ForTest(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("pollinations-text.no_embeddings", result.Error.Code);
    }

    /* ---- görme ---- */

    private static OpenAiCompatibleVisionProvider Vision(CaptureHandler handler, HttpClient http)
        => new(http, OpenAiCompatibleOptions.Pollinations());

    /// ***GÖRSEL `data:` URI OLARAK GİDİYOR.***
    ///
    /// Karelerin bir kısmı üretilmiş görseller ve hiçbir yerde
    /// barındırılmıyor; bir URL vermek, önce yüklemek demekti.
    [Fact]
    public async Task Gorsel_DataUriOlarakGidiyor()
    {
        var handler = new CaptureHandler(HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"{\"relevance\":0.8,\"reason\":\"uygun\"}"}}]}""");

        using var http = new HttpClient(handler);

        var result = await Vision(handler, http).JudgeAsync(
            new VisionQuery { Image = Png, Sentence = "Bir kedi görüyoruz." },
            ProviderContext.ForTest(),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);
        Assert.NotNull(handler.LastBody);
        Assert.Contains("data:image/png;base64,", handler.LastBody, StringComparison.Ordinal);

        // ***CÜMLE DE İSTEME GİRİYOR:*** model neyi değerlendireceğini
        // bilmeden bir skor üretemez.
        //
        // GÖVDE AYRIŞTIRILARAK BAKILIYOR, düz metinde aranmıyor:
        // `JsonSerializer` ASCII dışı karakterleri kaçış dizisine
        // çeviriyor: gövdede "görüyoruz" değil "görüyoruz"
        // yazıyor ve düz arama, cümle gerçekten gönderilmiş olsa bile
        // bulamıyordu. İlk yazımda test tam da bu yüzden düştü —
        // istek doğruydu, arama yanlıştı.
        using var body = System.Text.Json.JsonDocument.Parse(handler.LastBody!);

        var prompt = body.RootElement
            .GetProperty("messages")[0]
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString();

        Assert.NotNull(prompt);
        Assert.Contains("Bir kedi görüyoruz.", prompt, StringComparison.Ordinal);
    }

    /// MIME TÜRÜ İÇERİKTEN, UZANTIDAN DEĞİL.
    ///
    /// `VisionQuery` yalnızca bayt taşıyor ve yanlış bir MIME, modelin
    /// görseli hiç açamaması demek.
    [Fact]
    public async Task MimeTuru_IcerikMirasi()
    {
        var handler = new CaptureHandler(HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"{\"relevance\":0.5}"}}]}""");

        using var http = new HttpClient(handler);

        await Vision(handler, http).JudgeAsync(
            new VisionQuery { Image = Jpeg, Sentence = "cümle" },
            ProviderContext.ForTest(),
            CancellationToken.None);

        Assert.Contains("data:image/jpeg;base64,", handler.LastBody!, StringComparison.Ordinal);
    }

    /// ***JSON METNİN İÇİNE GÖMÜLMÜŞ OLSA DA AYRIŞTIRILIYOR.***
    ///
    /// Ücretsiz uçlar biçim talimatını sık sık kısmen uyguluyor
    /// (```json blokları, "İşte değerlendirme:" önsözleri). Her
    /// seferinde geçici hata dönmek, her karenin iki kez ölçülmesi
    /// demekti.
    [Fact]
    public async Task GomuluJson_Ayristiriliyor()
    {
        var handler = new CaptureHandler(HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"İşte değerlendirme:\n```json\n{\"relevance\": 0.9, \"reason\": \"tam uygun\", \"description\": \"bir kedi\"}\n```"}}]}""");

        using var http = new HttpClient(handler);

        var result = await Vision(handler, http).JudgeAsync(
            new VisionQuery { Image = Png, Sentence = "cümle" },
            ProviderContext.ForTest(),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);
        Assert.Equal(0.9, result.Value.Value.Relevance);
        Assert.Equal("tam uygun", result.Value.Value.Reason);
        Assert.Equal("bir kedi", result.Value.Value.Description);
    }

    /// ***0–100 ÖLÇEĞİ 0–1'E ÇEKİLİYOR.***
    ///
    /// Model bazen yüzde veriyor. 85'i "çok alakalı" saymak yerine
    /// 1,0'a çekmek, eşiğin anlamını korumanın tek yolu: ölçek
    /// karışıklığı sessizce geçseydi her kare "alakalı" çıkardı.
    [Fact]
    public async Task YuzdeOlcegi_BireCekiliyor()
    {
        var handler = new CaptureHandler(HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"{\"relevance\": 85}"}}]}""");

        using var http = new HttpClient(handler);

        var result = await Vision(handler, http).JudgeAsync(
            new VisionQuery { Image = Png, Sentence = "cümle" },
            ProviderContext.ForTest(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(0.85, result.Value.Value.Relevance);
    }

    /// JSON GELMEZSE GEÇİCİ HATA — İKİNCİ DENEME GENELLİKLE GEÇERLİ.
    [Fact]
    public async Task JsonYok_GeciciHata()
    {
        var handler = new CaptureHandler(HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"Bu görsel uygun görünüyor."}}]}""");

        using var http = new HttpClient(handler);

        var result = await Vision(handler, http).JudgeAsync(
            new VisionQuery { Image = Png, Sentence = "cümle" },
            ProviderContext.ForTest(),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Transient, result.Error.Kind);
    }

    /// ***HIZ SINIRI KAYNAK HATASI, BAŞARISIZLIK DEĞİL.***
    ///
    /// Ücretsiz uçta sınıra takılmak normal: iş düşmemeli, ertelenmeli.
    [Fact]
    public async Task HizSiniri_KaynakHatasi()
    {
        var handler = new CaptureHandler(HttpStatusCode.TooManyRequests, "rate limited");

        using var http = new HttpClient(handler);

        var result = await Vision(handler, http).JudgeAsync(
            new VisionQuery { Image = Png, Sentence = "cümle" },
            ProviderContext.ForTest(),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Resource, result.Error.Kind);
    }

    /// GÖRME MODELİ TANIMSIZSA KALICI HATA.
    ///
    /// Yeniden denemek bu sağlayıcıya görme yeteneği kazandırmıyor.
    [Fact]
    public async Task GormeModeliYok_KaliciHata()
    {
        using var http = new HttpClient(new CaptureHandler(HttpStatusCode.OK, "{}"));

        var result = await new OpenAiCompatibleVisionProvider(
            http, OpenAiCompatibleOptions.OpenRouter()).JudgeAsync(
            new VisionQuery { Image = Png, Sentence = "cümle" },
            ProviderContext.ForTest(),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("openrouter.no_vision", result.Error.Code);
        Assert.Equal(ErrorKind.Permanent, result.Error.Kind);
    }

    /// ÖLÇÜM BİRİMİ YAZILIYOR: bir görsel, bir istek.
    ///
    /// Yazılmasaydı görme çağrıları maliyet defterinde sıfır maliyetli
    /// görünürdü ve "semantik QC ne kadar tutuyor" sorusu cevapsız
    /// kalırdı.
    [Fact]
    public async Task OlcumBirimi_Yaziliyor()
    {
        var handler = new CaptureHandler(HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"{\"relevance\":0.5}"}}]}""");

        using var http = new HttpClient(handler);

        var result = await Vision(handler, http).JudgeAsync(
            new VisionQuery { Image = Png, Sentence = "cümle" },
            ProviderContext.ForTest(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value.Usage.Images);
        Assert.Equal(1, result.Value.Usage.Requests);
    }
}
