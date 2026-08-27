using System.Net;
using System.Text;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Providers.Llm;

namespace BytemountsAiStudio.Providers.Llm.Tests;

/// İsteği yakalayıp sabit bir cevap dönen sahte HTTP işleyici.
internal sealed class CaptureHandler(HttpStatusCode status, string body, string mime = "application/json")
    : HttpMessageHandler
{
    public string? LastBody { get; private set; }

    public Uri? LastUrl { get; private set; }

    public HttpRequestHeaders? LastHeaders { get; private set; }

    public sealed record HttpRequestHeaders(string? Authorization, string? GoogleKey, string? Referer);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastUrl = request.RequestUri;

        LastBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);

        LastHeaders = new HttpRequestHeaders(
            request.Headers.Authorization?.ToString(),
            request.Headers.TryGetValues("x-goog-api-key", out var google) ? google.FirstOrDefault() : null,
            request.Headers.TryGetValues("HTTP-Referer", out var referer) ? referer.FirstOrDefault() : null);

        return new HttpResponseMessage(status)
        {
            RequestMessage = request,
            Content = new StringContent(body, Encoding.UTF8, mime),
        };
    }
}

/// Anahtarı test için veren kaynak. Süreç ortamına DOKUNULMUYOR:
/// `Environment.SetEnvironmentVariable` çağırmak, aynı süreçte koşan
/// komşu testleri kırmanın sessiz bir yolu — bu depoda iki kez yaşandı.
internal sealed class StubCredentials(string? value) : ICredentialSource
{
    public string? Get(string name) => value;
}

/// Bulut LLM adaptörlerinin testleri (P1-02b).
///
/// Ağa ÇIKILMIYOR ve anahtar GEREKMİYOR. Sınanan şey iki taraf:
/// gönderilen isteğin şekli (zorlanmış araç gerçekten zorlanıyor mu)
/// ve HTTP durumunun hata sınıfına çevrilmesi — kuyruğun kararını o
/// belirliyor (ADR-011).
public sealed class OpenAiCompatibleProviderTests
{
    private const string TooledReply = """
        {"model":"gpt-4o","choices":[{"finish_reason":"tool_calls","message":{
          "tool_calls":[{"function":{"name":"emit_script","arguments":"{\"sentences\":[\"bir\"]}"}}]}}],
         "usage":{"prompt_tokens":120,"completion_tokens":45}}
        """;

    private static OpenAiCompatibleLlmProvider Provider(
        CaptureHandler handler, string? key = "sk-test", OpenAiCompatibleOptions? options = null)
        => new(new HttpClient(handler), options ?? OpenAiCompatibleOptions.OpenAi(), new StubCredentials(key));

    private static LlmRequest Request(ToolSchema? tool = null) => new()
    {
        Tier = ModelTier.Strong,
        Temperature = 0.2,
        Messages = [new(ChatRole.System, "sistem"), new(ChatRole.User, "kullanıcı")],
        ForcedTool = tool,
    };

    private static readonly ToolSchema Schema = new(
        "emit_script", "Senaryo", """{"type":"object","properties":{"sentences":{"type":"array"}}}""");

    [Fact]
    public async Task AracCagrisi_Ayristiriliyor()
    {
        var handler = new CaptureHandler(HttpStatusCode.OK, TooledReply);

        var result = await Provider(handler).CompleteAsync(
            Request(Schema), ProviderContext.ForTest(), CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);
        Assert.Contains("sentences", result.Value.Value.ToolArguments!, StringComparison.Ordinal);
        Assert.Null(result.Value.Value.Text);
        Assert.Equal(120, result.Value.Usage.InputTokens);
    }

    /// Model SEÇENEK BIRAKILMIYOR. Serbest bırakılsaydı cevap bazen
    /// metin bazen araç çağrısı gelir ve çağıran taraf ikisini de
    /// ayrıştırmak zorunda kalırdı (§7.2).
    [Fact]
    public async Task ZorlanmisArac_ToolChoiceGonderiyor()
    {
        var handler = new CaptureHandler(HttpStatusCode.OK, TooledReply);

        await Provider(handler).CompleteAsync(Request(Schema), ProviderContext.ForTest(), CancellationToken.None);

        Assert.Contains("\"tool_choice\"", handler.LastBody!, StringComparison.Ordinal);
        Assert.Contains("emit_script", handler.LastBody!, StringComparison.Ordinal);
    }

    /// Araç zorunlu tutulmuşken METİN dönmesi başarı DEĞİL: çağıran
    /// tarafta null bir şemaya ve orada anlaşılmaz bir hataya
    /// dönüşürdü. Geçici, çünkü aynı istek ikinci denemede araç
    /// çağırabiliyor.
    [Fact]
    public async Task AracZorunluAmaMetinDondu_GeciciHata()
    {
        var handler = new CaptureHandler(HttpStatusCode.OK,
            """{"model":"gpt-4o","choices":[{"finish_reason":"stop","message":{"content":"merhaba"}}]}""");

        var result = await Provider(handler).CompleteAsync(
            Request(Schema), ProviderContext.ForTest(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Transient, result.Error.Kind);
        Assert.Contains("no_tool_call", result.Error.Code, StringComparison.Ordinal);
    }

    /// Sessizce kısalmış bir senaryoyu "başarılı" saymamak için.
    [Fact]
    public async Task KesilmisCikti_Isaretleniyor()
    {
        var handler = new CaptureHandler(HttpStatusCode.OK,
            """{"model":"gpt-4o","choices":[{"finish_reason":"length","message":{"content":"yarim"}}]}""");

        var result = await Provider(handler).CompleteAsync(
            Request(), ProviderContext.ForTest(), CancellationToken.None);

        Assert.True(result.Value.Value.Truncated);
    }

    [Fact]
    public async Task AnahtarYoksa_AcikHata()
    {
        var handler = new CaptureHandler(HttpStatusCode.OK, TooledReply);

        var result = await Provider(handler, key: null).CompleteAsync(
            Request(), ProviderContext.ForTest(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Contains("no_key", result.Error.Code, StringComparison.Ordinal);
        Assert.Contains("OPENAI_API_KEY", result.Error.Message, StringComparison.Ordinal);
    }

    /// 402 KAYNAK, kalıcı değil: bakiye bitti demek, ERTELEME demek.
    /// Kalıcı sayılsaydı ödeme yapıldıktan sonra bile çalışmayacak bir
    /// işe dönüşürdü.
    [Fact]
    public async Task BakiyeBitti_KaynakHatasi()
    {
        var handler = new CaptureHandler(HttpStatusCode.PaymentRequired,
            """{"error":{"message":"insufficient credits"}}""");

        var result = await Provider(handler).CompleteAsync(
            Request(), ProviderContext.ForTest(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Resource, result.Error.Kind);
        Assert.Contains("insufficient credits", result.Error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, ErrorKind.Transient)]
    [InlineData(HttpStatusCode.InternalServerError, ErrorKind.Transient)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ErrorKind.Transient)]
    [InlineData(HttpStatusCode.Unauthorized, ErrorKind.Permanent)]
    [InlineData(HttpStatusCode.Forbidden, ErrorKind.Permanent)]
    [InlineData(HttpStatusCode.BadRequest, ErrorKind.Permanent)]
    public async Task HttpDurumu_HataSinifinaCevriliyor(HttpStatusCode status, ErrorKind expected)
    {
        var handler = new CaptureHandler(status, """{"error":{"message":"hata"}}""");

        var result = await Provider(handler).CompleteAsync(
            Request(), ProviderContext.ForTest(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(expected, result.Error.Kind);
    }

    /// Gömme 768 boyut İSTİYOR: şema öyle (ADR-003). Varsayılan 1536
    /// gönderilseydi vektör kolona hiç yazılamaz ve hata veritabanı
    /// katmanında, sebebi görünmeden çıkardı.
    [Fact]
    public async Task Gomme_768BoyutIstiyor()
    {
        var handler = new CaptureHandler(HttpStatusCode.OK,
            """{"data":[{"embedding":[0.1,0.2]}],"usage":{"prompt_tokens":5,"completion_tokens":0}}""");

        await Provider(handler).EmbedAsync("metin", ProviderContext.ForTest(), CancellationToken.None);

        Assert.Contains("\"dimensions\":768", handler.LastBody!.Replace(" ", ""), StringComparison.Ordinal);
    }

    /// OpenRouter embedding SUNMUYOR. Boş bir model adıyla göndermek
    /// anlaşılmaz bir 404 üretirdi; KALICI hata açıkça söylüyor.
    [Fact]
    public async Task GommeSunmayanSaglayici_KaliciHata()
    {
        var handler = new CaptureHandler(HttpStatusCode.OK, "{}");

        var result = await Provider(handler, options: OpenAiCompatibleOptions.OpenRouter())
            .EmbedAsync("metin", ProviderContext.ForTest(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Permanent, result.Error.Kind);
        Assert.Contains("no_embeddings", result.Error.Code, StringComparison.Ordinal);
    }

    /// OpenRouter kendini tanıtan başlıkları istiyor.
    [Fact]
    public async Task OpenRouter_TanitimBasliklariniGonderiyor()
    {
        var handler = new CaptureHandler(HttpStatusCode.OK, TooledReply);

        await Provider(handler, options: OpenAiCompatibleOptions.OpenRouter())
            .CompleteAsync(Request(), ProviderContext.ForTest(), CancellationToken.None);

        Assert.NotNull(handler.LastHeaders!.Referer);
        Assert.StartsWith("Bearer", handler.LastHeaders.Authorization!, StringComparison.Ordinal);
    }

    /// Tanımsız bir katman ALT katmana düşüyor: katman eksikliği
    /// yüzünden çağrının tamamen düşmesi saçma olurdu (P1-03 ile aynı
    /// mantık).
    [Fact]
    public void TanimsizKatman_AltaDusuyor()
    {
        var options = OpenAiCompatibleOptions.OpenAi() with
        {
            Models = new Dictionary<ModelTier, string> { [ModelTier.Cheap] = "yalnizca-ucuz" },
        };

        var provider = new OpenAiCompatibleLlmProvider(
            new HttpClient(new CaptureHandler(HttpStatusCode.OK, "{}")), options, new StubCredentials("k"));

        Assert.Equal("yalnizca-ucuz", provider.ModelFor(ModelTier.Strong));
    }
}

/// Gemini adaptörünün testleri (P1-02b).
public sealed class GeminiProviderTests
{
    private const string TooledReply = """
        {"candidates":[{"finish_reason":"STOP","content":{"parts":[
          {"function_call":{"name":"emit_script","args":{"sentences":["bir"]}}}]}}],
         "usage_metadata":{"prompt_token_count":80,"candidates_token_count":30}}
        """;

    private static GeminiLlmProvider Provider(CaptureHandler handler, string? key = "AIza-test")
        => new(new HttpClient(handler), new GeminiOptions(), new StubCredentials(key));

    private static LlmRequest Request(ToolSchema? tool = null) => new()
    {
        Tier = ModelTier.Strong,
        Temperature = 0.2,
        Messages = [new(ChatRole.System, "sistem istemi"), new(ChatRole.User, "kullanıcı")],
        ForcedTool = tool,
    };

    private static readonly ToolSchema Schema = new(
        "emit_script", "Senaryo", """{"type":"object","properties":{"sentences":{"type":"array"}}}""");

    [Fact]
    public async Task AracCagrisi_Ayristiriliyor()
    {
        var handler = new CaptureHandler(HttpStatusCode.OK, TooledReply);

        var result = await Provider(handler).CompleteAsync(
            Request(Schema), ProviderContext.ForTest(), CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);
        Assert.Contains("sentences", result.Value.Value.ToolArguments!, StringComparison.Ordinal);
        Assert.Equal(80, result.Value.Usage.InputTokens);
    }

    /// SİSTEM İSTEMİ AYRI ALANDA. `contents` içine konulsaydı kullanıcı
    /// mesajı gibi işlenir ve modelin ona uyma zorunluluğu zayıflardı.
    [Fact]
    public async Task SistemIstemi_AyriAlanaGidiyor()
    {
        var handler = new CaptureHandler(HttpStatusCode.OK, TooledReply);

        await Provider(handler).CompleteAsync(Request(), ProviderContext.ForTest(), CancellationToken.None);

        var body = handler.LastBody!;

        Assert.Contains("system_instruction", body, StringComparison.Ordinal);

        // Sistem metni `contents` dizisine GİRMEMELİ.
        var contents = body[body.IndexOf("\"contents\"", StringComparison.Ordinal)..];
        var systemIndex = contents.IndexOf("sistem istemi", StringComparison.Ordinal);
        var instructionIndex = contents.IndexOf("system_instruction", StringComparison.Ordinal);

        Assert.True(systemIndex < 0 || systemIndex > instructionIndex);
    }

    [Fact]
    public async Task ZorlanmisArac_ANYModuGonderiyor()
    {
        var handler = new CaptureHandler(HttpStatusCode.OK, TooledReply);

        await Provider(handler).CompleteAsync(Request(Schema), ProviderContext.ForTest(), CancellationToken.None);

        Assert.Contains("\"mode\":\"ANY\"", handler.LastBody!.Replace(" ", ""), StringComparison.Ordinal);
        Assert.Contains("allowed_function_names", handler.LastBody!, StringComparison.Ordinal);
    }

    /// Anahtar sorgu dizisinde DEĞİL başlıkta: sorgu dizisi sunucu
    /// erişim kayıtlarına ve vekil loglarına düz metin yazılıyor.
    [Fact]
    public async Task Anahtar_Baslikta_SorguDizisindeDegil()
    {
        var handler = new CaptureHandler(HttpStatusCode.OK, TooledReply);

        await Provider(handler).CompleteAsync(Request(), ProviderContext.ForTest(), CancellationToken.None);

        Assert.Equal("AIza-test", handler.LastHeaders!.GoogleKey);
        Assert.DoesNotContain("AIza-test", handler.LastUrl!.ToString(), StringComparison.Ordinal);
    }

    /// 429 KAYNAK, geçici değil: Gemini'nin ücretsiz katmanında bu
    /// genelde GÜNLÜK kota ve dakikalar içinde yeniden denemek yalnızca
    /// kotayı tüketmeye devam ediyor.
    [Fact]
    public async Task Kota_KaynakHatasi()
    {
        var handler = new CaptureHandler(HttpStatusCode.TooManyRequests,
            """{"error":{"message":"quota exceeded"}}""");

        var result = await Provider(handler).CompleteAsync(
            Request(), ProviderContext.ForTest(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Resource, result.Error.Kind);
    }

    [Fact]
    public async Task Gomme_768BoyutIstiyor()
    {
        var handler = new CaptureHandler(HttpStatusCode.OK, """{"embedding":{"values":[0.1,0.2]}}""");

        var result = await Provider(handler).EmbedAsync("metin", ProviderContext.ForTest(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Contains("\"output_dimensionality\":768", handler.LastBody!.Replace(" ", ""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnahtarYoksa_AcikHata()
    {
        var handler = new CaptureHandler(HttpStatusCode.OK, TooledReply);

        var result = await Provider(handler, key: null).CompleteAsync(
            Request(), ProviderContext.ForTest(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Contains("GEMINI_API_KEY", result.Error.Message, StringComparison.Ordinal);
    }
}
