using System.Net;
using System.Text;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Providers.Llm;

namespace BytemountsAiStudio.Providers.Llm.Tests;

/// Sahte HTTP islemci: gercek Ollama olmadan protokol davranisi sinanabilsin.
internal sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
{
    public int Calls { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
    }
}

/// Protokol testleri: gercek Ollama gerektirmiyor.
///
/// Asil deger hata siniflandirmasinda: 5xx GECICI (yeniden denemek ise
/// yarayabilir), 4xx KALICI (ayni istek yine reddedilir). Bunu yanlis
/// yapmak ya bosuna para harcatir ya da gecici bir kesintide run'i oldururdu.
public sealed class OllamaProtocolTests
{
    private static OllamaLlmProvider Provider(HttpStatusCode status, string body)
        => new(new HttpClient(new StubHandler(status, body)));

    private static LlmRequest Request(ToolSchema? tool = null) => new()
    {
        Tier = ModelTier.Cheap,
        Messages = [new(ChatRole.User, "merhaba")],
        ForcedTool = tool,
    };

    [Fact]
    public async Task BasariliCevap_MetinVeTokenSayisiDoner()
    {
        var provider = Provider(HttpStatusCode.OK,
            """{"message":{"role":"assistant","content":"selam"},"prompt_eval_count":12,"eval_count":5}""");

        var result = await provider.CompleteAsync(
            Request(), ProviderContext.ForTest(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("selam", result.Value.Value.Text);
        Assert.Equal(12, result.Value.Usage.InputTokens);
        Assert.Equal(5, result.Value.Usage.OutputTokens);
    }

    [Fact]
    public async Task ZorunluArac_CevabiToolArgumentsaKoyar()
    {
        // Sema zorlandiginda cevap serbest metin degil, dogrulanacak JSON.
        var provider = Provider(HttpStatusCode.OK,
            """{"message":{"role":"assistant","content":"{\"sentences\":[\"a\"]}"}}""");

        var tool = new ToolSchema("emit", "test",
            """{"type":"object","properties":{"sentences":{"type":"array"}}}""");

        var result = await provider.CompleteAsync(
            Request(tool), ProviderContext.ForTest(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.Value.Text);
        Assert.Contains("sentences", result.Value.Value.ToolArguments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SunucuHatasi_GECICIsayilir()
    {
        var provider = Provider(HttpStatusCode.InternalServerError, """{"error":"overloaded"}""");

        var result = await provider.CompleteAsync(
            Request(), ProviderContext.ForTest(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Transient, result.Error.Kind);
    }

    [Fact]
    public async Task IstemciHatasi_KALICIsayilir()
    {
        // Ayni istek yine reddedilir; yeniden denemek yalnizca zaman kaybi.
        var provider = Provider(HttpStatusCode.BadRequest, """{"error":"model not found"}""");

        var result = await provider.CompleteAsync(
            Request(), ProviderContext.ForTest(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Permanent, result.Error.Kind);
    }

    [Fact]
    public async Task KesilmisCevap_Isaretlenir()
    {
        // Sessizce kisalmis bir senaryoyu "basarili" saymak, yarim videoyu
        // yayina gonderirdi.
        var provider = Provider(HttpStatusCode.OK,
            """{"message":{"role":"assistant","content":"yarim"},"done_reason":"length"}""");

        var result = await provider.CompleteAsync(
            Request(), ProviderContext.ForTest(), CancellationToken.None);

        Assert.True(result.Value.Value.Truncated);
    }

    [Fact]
    public async Task BosCevap_GeciciHataDoner()
    {
        var provider = Provider(HttpStatusCode.OK, """{"done_reason":"stop"}""");

        var result = await provider.CompleteAsync(
            Request(), ProviderContext.ForTest(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Transient, result.Error.Kind);
    }
}
