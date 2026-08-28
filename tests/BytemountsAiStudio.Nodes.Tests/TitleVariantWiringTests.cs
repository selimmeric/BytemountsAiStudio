using System.Text.Json;
using BytemountsAiStudio.Contracts.Prompts;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Learning;
using BytemountsAiStudio.Workflow.Engine;

namespace BytemountsAiStudio.Nodes.Tests;

/// Başlık deneyinin İSTEME KADAR gittiği (P5-03).
///
/// Deney atanmış olması yetmiyor: stil isteme girmezse iki kol aynı
/// başlığı üretir. Bu testler ayarı okumakla yetinmiyor, MODELE GİDEN
/// METNİ okuyor — arada kalan her adım (bağlam köprüsü, şablon
/// değişkeni, istem sürümü) gerçekten çalışmak zorunda.
public sealed class TitleVariantWiringTests
{
    /// Modele giden isteği kaydeden sahte sağlayıcı.
    private sealed class RecordingLlm : ILlmProvider
    {
        public string Key => "kayit";

        public LlmRequest? Last { get; private set; }

        public LlmCapabilities Capabilities => new()
        {
            SupportsToolUse = true,
            SupportsVision = false,
            ContextWindowTokens = 8_000,
            SupportsEmbeddings = false,
        };

        public Task<Result<ProviderResponse<LlmResponse>>> CompleteAsync(
            LlmRequest request, ProviderContext context, CancellationToken cancellationToken)
        {
            Last = request;

            return Task.FromResult(Result.Success(ProviderResponse<LlmResponse>.Free(
                new LlmResponse
                {
                    ModelId = "kayit",
                    ToolArguments =
                        """{"title":"Göbeklitepe neden önemli?","description":"Kısa anlatı.","tags":["tarih"]}""",
                })));
        }

        public Task<Result<ProviderResponse<IReadOnlyList<float>>>> EmbedAsync(
            string text, ProviderContext context, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    /// Çağrılırsa testi düşüren sağlayıcı.
    private sealed class ForbiddenLlm : ILlmProvider
    {
        public string Key => "yasak";

        public LlmCapabilities Capabilities => new()
        {
            SupportsToolUse = true,
            SupportsVision = false,
            ContextWindowTokens = 8_000,
            SupportsEmbeddings = false,
        };

        public Task<Result<ProviderResponse<LlmResponse>>> CompleteAsync(
            LlmRequest request, ProviderContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Bozuk deneyde model çağrılmamalıydı.");

        public Task<Result<ProviderResponse<IReadOnlyList<float>>>> EmbedAsync(
            string text, ProviderContext context, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private static NodeContext Context(string? titleConfig)
    {
        // Ham dize yerine birleştirme: `$$""" ... {{deger}}}}` biçimi
        // kapanış süslü ayraçlarında belirsiz oluyor ve derleyici
        // haklı olarak reddediyor.
        var experiments = titleConfig is null
            ? string.Empty
            : ",\"experiments\":{\"title\":{\"experiment\":\"e\",\"variant\":\"v\","
                + "\"name\":\"kol-b\",\"config\":" + titleConfig + "}}";

        var json = """
            {
              "topic": { "topic": "Göbeklitepe", "language": "tr-TR" },
              "script": { "sentences": ["Göbeklitepe dünyanın bilinen en eski tapınağıdır."] }
            """
            + experiments + Environment.NewLine + "}";

        using var document = JsonDocument.Parse(json);

        return new NodeContext
        {
            RunId = Guid.CreateVersion7(),
            NodeId = "seo",
            NodeType = "seo.generate",
            Attempt = 1,
            Config = JsonDocument.Parse("{}").RootElement.Clone(),
            RunContext = document.RootElement.Clone(),
            IdempotencyKey = "test",
            CorrelationId = "test",
        };
    }

    private static string PromptText(RecordingLlm llm)
    {
        Assert.NotNull(llm.Last);

        return string.Join('\n', llm.Last!.Messages.Select(m => m.Content));
    }

    /// DENEY YOKSA BUGÜNKÜ DAVRANIŞ: düz stil.
    [Fact]
    public async Task DeneyYok_DuzStil()
    {
        var llm = new RecordingLlm();

        var result = await new SeoGenerateHandler(llm)
            .ExecuteAsync(Context(null), CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);
        Assert.Contains("Başlık stili: duz", PromptText(llm), StringComparison.Ordinal);
        Assert.Equal("duz", result.Value.GetProperty("title_style").GetString());
    }

    /// ATANAN KOL MODELE GİDEN METNE GİRİYOR.
    ///
    /// Zincirin tamamı sınanıyor: run bağlamı → varyant ayarı → şablon
    /// değişkeni → modele giden mesaj. Aradaki herhangi bir halka
    /// kopsa deney iki kolda da aynı istemi kullanırdı.
    [Fact]
    public async Task SoruKolu_IstemeGiriyor()
    {
        var llm = new RecordingLlm();

        var result = await new SeoGenerateHandler(llm)
            .ExecuteAsync(Context("""{"stil":"soru"}"""), CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);
        Assert.Contains("Başlık stili: soru", PromptText(llm), StringComparison.Ordinal);

        // KOL ÇIKTIYA DA YAZILIYOR: hangi başlığın hangi stille
        // üretildiği, atama tablosuna bakmadan görülüyor.
        Assert.Equal("soru", result.Value.GetProperty("title_style").GetString());
    }

    /// İKİ KOL GERÇEKTEN FARKLI İSTEM ÜRETİYOR.
    [Fact]
    public async Task IkiKol_FarkliIstem()
    {
        var first = new RecordingLlm();
        var second = new RecordingLlm();

        await new SeoGenerateHandler(first).ExecuteAsync(
            Context("""{"stil":"duz"}"""), CancellationToken.None);

        await new SeoGenerateHandler(second).ExecuteAsync(
            Context("""{"stil":"sayi"}"""), CancellationToken.None);

        Assert.NotEqual(PromptText(first), PromptText(second));
    }

    /// BOZUK DENEY MODELE HİÇ GİTMİYOR.
    ///
    /// Doğrulama model çağrısından ÖNCE: bozuk bir deney yüzünden
    /// token yakmak, hatayı pahalı hâle getirirdi.
    [Fact]
    public async Task BozukKol_ModelCagrilmiyor()
    {
        var result = await new SeoGenerateHandler(new ForbiddenLlm())
            .ExecuteAsync(Context("""{"stil":"bagir"}"""), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("variant.unknown_value", result.Error.Code);
    }

    /// YER TUTUCUSUZ İSTEM SÜRÜMÜYLE DENEY KOŞMUYOR.
    ///
    /// ASIL SESSİZ HATA BU. Şablon, kendisinde olmayan yer tutuculara
    /// verilen değerleri yutuyor: `{{baslik_stili}}` içermeyen bir
    /// istem sürümüyle koşan deney iki kolda da AYNI istemi kullanır,
    /// haftalarca veri toplar ve "fark yok" der.
    [Fact]
    public async Task YerTutucusuzIstemSurumu_Reddediliyor()
    {
        var directory = Path.Combine(Path.GetTempPath(), "istem-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(directory, "seo.generate"));

        await File.WriteAllTextAsync(
            Path.Combine(directory, "seo.generate", "v1.md"),
            """
            ---
            key: seo.generate
            version: 1
            description: yer tutucusuz eski surum
            ---

            # system
            Sen kısa video metadatası yazıyorsun.

            # user
            {{language}} dilinde {{topic}} için başlık üret.

            {{script}}
            """,
            CancellationToken.None);

        try
        {
            var registry = PromptRegistry.Load(directory);
            Assert.True(registry.IsSuccess, registry.IsFailure ? registry.Error.Message : string.Empty);

            var result = await new SeoGenerateHandler(new ForbiddenLlm(), registry.Value)
                .ExecuteAsync(Context("""{"stil":"soru"}"""), CancellationToken.None);

            Assert.True(result.IsFailure);
            Assert.Equal("variant.placeholder_missing", result.Error.Code);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
