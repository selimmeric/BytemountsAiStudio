using System.Text.Json;
using BytemountsAiStudio.Workflow.Engine;

namespace BytemountsAiStudio.Workflow.Tests;

/// Koşunun hangi hattan çıktığının kayda geçmesi.
///
/// ***BU DOSYANIN VAR OLMA SEBEBİ:*** sahte hat **gerçek bir video
/// dosyası** üretiyor — doğru süre, doğru çözünürlük, doğru altyazı.
/// Çıktı dizinine bakan bir insan ikisini ayırt edemiyor. İşaret
/// olmasaydı "bu video gerçek mi" sorusunun cevabı maliyet
/// defterindeki sağlayıcı anahtarlarına (`fake-llm`) bakmayı
/// gerektirirdi — ve kimse bakmaz.
public sealed class PipelineStampTests
{
    private static string Pipeline(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("pipeline").GetString()!;
    }

    /// ***HAT ADI BAĞLAMA GİRİYOR.***
    [Theory]
    [InlineData(PipelineKind.Open, "acik")]
    [InlineData(PipelineKind.Fake, "sahte")]
    public void HatAdi_BaglamaGiriyor(PipelineKind kind, string expected)
        => Assert.Equal(expected, Pipeline(WorkflowEngine.WithPipeline("{}", kind)));

    /// MEVCUT ALANLAR KORUNUYOR.
    ///
    /// Başlangıç bağlamı konuyu, dili ve deney atamalarını taşıyor;
    /// işaret eklerken onları düşürmek, koşuyu konusuz bırakırdı.
    [Fact]
    public void MevcutAlanlar_Korunuyor()
    {
        var json = WorkflowEngine.WithPipeline(
            """{"konu":{"baslik":"Test"},"dil":"tr-TR"}""", PipelineKind.Open);

        using var document = JsonDocument.Parse(json);

        Assert.Equal("Test", document.RootElement.GetProperty("konu").GetProperty("baslik").GetString());
        Assert.Equal("tr-TR", document.RootElement.GetProperty("dil").GetString());
        Assert.Equal("acik", document.RootElement.GetProperty("pipeline").GetString());
    }

    /// ***DIŞARIDAN GELEN `pipeline` EZİLİYOR.***
    ///
    /// Bu alanın tek sahibi motor. Çağıranın yazdığı bir değer, sahte
    /// bir koşuyu "açık" gösterebilirdi — yani işaretin tek amacını
    /// ortadan kaldırırdı.
    [Fact]
    public void DisaridanGelenIsaret_Eziliyor()
        => Assert.Equal(
            "sahte",
            Pipeline(WorkflowEngine.WithPipeline("""{"pipeline":"acik"}""", PipelineKind.Fake)));

    /// ***BOZUK BAĞLAM KOŞUYU DÜŞÜRMÜYOR.***
    ///
    /// Bağlam dışarıdan geliyor (CLI, API, zamanlayıcı) ve okunamayan
    /// bir bağlam yüzünden koşuyu hiç başlatmamak, işaretin
    /// kendisinden pahalı olurdu. O durumda yalnızca işaret yazılıyor.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ bozuk json")]
    [InlineData("\"sadece metin\"")]
    [InlineData("[1,2,3]")]
    public void BozukBaglam_YalnizcaIsaret(string? raw)
        => Assert.Equal("acik", Pipeline(WorkflowEngine.WithPipeline(raw, PipelineKind.Open)));

    /// İÇ İÇE NESNELER BOZULMADAN GEÇİYOR.
    ///
    /// Bağlam derin: `experiments.thumbnail.name` gibi alanlar node'lar
    /// tarafından okunuyor. Yeniden yazarken düzleşseydi deney
    /// atamaları kaybolurdu.
    [Fact]
    public void IcIceNesneler_Bozulmuyor()
    {
        var json = WorkflowEngine.WithPipeline(
            """{"experiments":{"thumbnail":{"name":"kanal-varsayilani","konum":"alt"}}}""",
            PipelineKind.Open);

        using var document = JsonDocument.Parse(json);

        var thumbnail = document.RootElement.GetProperty("experiments").GetProperty("thumbnail");

        Assert.Equal("kanal-varsayilani", thumbnail.GetProperty("name").GetString());
        Assert.Equal("alt", thumbnail.GetProperty("konum").GetString());
    }
}
