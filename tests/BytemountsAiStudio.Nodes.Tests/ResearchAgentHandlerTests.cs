using System.Text.Json;
using BytemountsAiStudio.Nodes;

namespace BytemountsAiStudio.Nodes.Tests;

/// Araştırma planı ayrıştırmasının testleri (P1-09).
///
/// Model çağrılmıyor. Asıl sınanan şey, plan gelmediğinde ya da bozuk
/// geldiğinde ne olduğu: plan bir İYİLEŞTİRME, önkoşul değil.
public sealed class ResearchAgentHandlerTests
{
    private static string Payload(params object[] queries)
        => JsonSerializer.Serialize(new { queries });

    [Fact]
    public void Plan_Ayristirilir()
    {
        var payload = Payload(
            new { text = "Göbeklitepe arkeoloji", language = "tr-TR", intent = "genel" },
            new { text = "Gobekli Tepe excavation", language = "en-US", intent = "kazı" });

        var plan = ResearchAgentHandler.ParsePlan(payload, "Göbeklitepe", "tr-TR");

        Assert.NotNull(plan);
        Assert.Equal(2, plan.Queries.Count);
        Assert.Equal("Göbeklitepe arkeoloji", plan.Queries[0].Text);
        Assert.Equal("kazı", plan.Queries[1].Intent);
    }

    /// SORGU DİLİ içerik dilinden farklı olabilir ve olmalıdır (§20.1):
    /// Türkçe içeriğin çoğu konuda İngilizce kaynağı daha zengin.
    [Fact]
    public void SorguDili_IcerikDilindenFarkliOlabilir()
    {
        var payload = Payload(new { text = "Gobekli Tepe", language = "en-US" });

        var plan = ResearchAgentHandler.ParsePlan(payload, "Göbeklitepe", "tr-TR");

        Assert.Equal("en-US", plan!.Queries[0].Language);
    }

    /// Dil belirtilmemişse İÇERİK dili varsayılıyor. Boş bırakmak,
    /// sağlayıcının varsayılanına düşmek demekti ve o varsayılan
    /// İngilizce.
    [Fact]
    public void DilBelirtilmemis_IcerikDiliVarsayilir()
    {
        var payload = JsonSerializer.Serialize(new { queries = new[] { new { text = "sorgu" } } });

        var plan = ResearchAgentHandler.ParsePlan(payload, "konu", "tr-TR");

        Assert.Equal("tr-TR", plan!.Queries[0].Language);
    }

    [Fact]
    public void BosMetinliSorgu_Atlanir()
    {
        var payload = Payload(
            new { text = "geçerli", language = "tr-TR" },
            new { text = "   ", language = "tr-TR" });

        Assert.Single(ResearchAgentHandler.ParsePlan(payload, "konu", "tr-TR")!.Queries);
    }

    /// Plan gelmezse ya da bozuksa null dönüyor; çağıran taraf konuyu
    /// tek sorgu olarak kullanıyor. Düşürmek yanlış olurdu: eski
    /// davranış zaten buydu ve işe yarıyordu.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ bozuk json")]
    [InlineData("""{"baska":[]}""")]
    [InlineData("""{"queries":[]}""")]
    public void BozukPlan_NullDoner(string? payload)
    {
        Assert.Null(ResearchAgentHandler.ParsePlan(payload, "konu", "tr-TR"));
    }

    [Fact]
    public void HicGecerliSorguYok_NullDoner()
    {
        var payload = Payload(new { text = "  ", language = "tr-TR" });

        Assert.Null(ResearchAgentHandler.ParsePlan(payload, "konu", "tr-TR"));
    }

    [Fact]
    public void Sorgular_Kirpilir()
    {
        var payload = Payload(new { text = "  boşluklu sorgu  ", language = "tr-TR" });

        Assert.Equal("boşluklu sorgu", ResearchAgentHandler.ParsePlan(payload, "konu", "tr-TR")!.Queries[0].Text);
    }
}
