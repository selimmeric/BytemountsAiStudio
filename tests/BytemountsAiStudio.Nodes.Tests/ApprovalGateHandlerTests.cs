using System.Text.Json;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Nodes;
using BytemountsAiStudio.Workflow.Engine;

namespace BytemountsAiStudio.Nodes.Tests;

/// Onay kapısı node'unun testleri (P1-27).
///
/// Veritabanı yok, ağ yok: sınanan şey, kapının çıktısının motorla
/// olan SÖZLEŞMESİ. Motor node tipine değil bu çıktıya bakıyor, çünkü
/// aynı node bir koşuda insana sorup diğerinde sormuyor.
public sealed class ApprovalGateHandlerTests
{
    private static NodeContext Context(object config, object? runContext = null) => new()
    {
        RunId = Guid.CreateVersion7(),
        NodeId = "onay",
        NodeType = "human.approval",
        Attempt = 1,
        Config = JsonSerializer.SerializeToElement(config),
        RunContext = JsonSerializer.SerializeToElement(runContext ?? new { }),
        IdempotencyKey = "onay-test",
        CorrelationId = "onay-test",
    };

    private static async Task<JsonElement> RunAsync(object config, object? runContext = null)
    {
        var result = await new ApprovalGateHandler().ExecuteAsync(Context(config, runContext), CancellationToken.None);

        Assert.True(result.IsSuccess);

        return result.Value;
    }

    [Fact]
    public async Task OtonomKanal_ParkEtmez()
    {
        var output = await RunAsync(new { mode = "auto" });

        Assert.False(output.GetProperty("awaiting_approval").GetBoolean());
        Assert.False(ApprovalGate.Awaits(output));
    }

    [Fact]
    public async Task OnayKipi_ParkEder()
    {
        var output = await RunAsync(new { mode = "approval" });

        Assert.True(ApprovalGate.Awaits(output));
    }

    /// Kip belirtilmemişse ONAY varsayılıyor: yapılandırma eksikliği
    /// yüzünden bir kanalın sessizce otonom hâle gelmesi, tersinden
    /// çok daha pahalı.
    [Fact]
    public async Task KipYoksa_OnayVarsayilir()
    {
        Assert.True(ApprovalGate.Awaits(await RunAsync(new { })));
    }

    [Fact]
    public async Task SecmeliKip_YuksekSkoruGecirir()
    {
        var output = await RunAsync(
            new { mode = "selective", min_score = 0.7 },
            new { qc = new { score = 0.9 } });

        Assert.False(ApprovalGate.Awaits(output));
    }

    [Fact]
    public async Task SecmeliKip_DusukSkoruSorar()
    {
        var output = await RunAsync(
            new { mode = "selective", min_score = 0.7 },
            new { qc = new { score = 0.4 } });

        Assert.True(ApprovalGate.Awaits(output));
    }

    /// Skor OKUNAMAZSA insana soruluyor. "Ölçülmedi" ile "iyi" aynı
    /// şey değil.
    [Fact]
    public async Task SecmeliKip_SkorYoksaSorar()
    {
        var output = await RunAsync(new { mode = "selective", min_score = 0.7 }, new { qc = new { } });

        Assert.True(ApprovalGate.Awaits(output));
    }

    /// Skor birden fazla yerde olabiliyor: QC node'unun adı grafa göre
    /// değişiyor ve alan adı da `score` ya da `total_score` olabiliyor.
    [Theory]
    [InlineData("qc", "score")]
    [InlineData("quality", "score")]
    [InlineData("qc", "total_score")]
    public void Skor_FarkliYerlerdenOkunur(string node, string field)
    {
        var context = JsonSerializer.SerializeToElement(
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [node] = new Dictionary<string, object>(StringComparer.Ordinal) { [field] = 0.63 },
            });

        Assert.Equal(0.63, ApprovalGateHandler.ScoreFrom(context));
    }

    [Fact]
    public void SkorYoksa_NullDoner()
    {
        Assert.Null(ApprovalGateHandler.ScoreFrom(JsonSerializer.SerializeToElement(new { })));
        Assert.Null(ApprovalGateHandler.ScoreFrom(
            JsonSerializer.SerializeToElement(new { qc = new { score = "metin" } })));
    }

    /// Gerekçe çıktıda: panelde bakan kişinin göreceği ilk şey.
    [Fact]
    public async Task Gerekce_CiktidaYaziliyor()
    {
        var output = await RunAsync(
            new { mode = "selective", min_score = 0.7 },
            new { qc = new { score = 0.4 } });

        Assert.False(string.IsNullOrWhiteSpace(output.GetProperty("reason").GetString()));
        // Skor da yazılıyor: eşiği tartışan biri, o koşudaki gerçek
        // skoru aramak zorunda kalmasın.
        Assert.Equal(0.4, output.GetProperty("score").GetDouble());
    }

    /// Motor node TİPİNE bakmıyor: `awaiting_approval` alanı yoksa ya
    /// da false ise park edilmiyor.
    [Theory]
    [InlineData("""{"awaiting_approval":false}""")]
    [InlineData("""{"baska":"alan"}""")]
    [InlineData("""{"awaiting_approval":"true"}""")]
    [InlineData("""[]""")]
    public void ParkSozlesmesi_YalnizcaGercekTrue(string json)
    {
        using var document = JsonDocument.Parse(json);

        Assert.False(ApprovalGate.Awaits(document.RootElement));
    }
}
