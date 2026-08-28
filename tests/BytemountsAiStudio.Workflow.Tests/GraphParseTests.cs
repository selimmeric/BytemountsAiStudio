using BytemountsAiStudio.Workflow.Definition;

namespace BytemountsAiStudio.Workflow.Tests;

/// `WorkflowGraph.Parse` sözünü tutuyor mu.
///
/// İMZA `WorkflowGraph?` DİYOR, yani "okunamayabilir". Ama
/// `JsonSerializer.Deserialize` yalnızca metin literal `null`
/// olduğunda `null` döndürüyor; BOZUK metinde istisna atıyor.
///
/// Sonuç şuydu: bütün çağıranlar (`WorkflowEngine`,
/// `ApprovalService`, `DeadLetterTriage`, panel sorguları) `is null`
/// diye bakıyordu ve o kontrollerin HİÇBİRİ bozuk bir kayıtta
/// çalışmıyordu. Depoda bozuk tek bir graf satırı, "graf okunamadı"
/// yerine motorda işlenmemiş bir istisna demekti.
///
/// Editörde görüldü: doğrulama ucuna bozuk JSON gönderince HTTP
/// cevabına bir yığın izi düştü.
public sealed class GraphParseTests
{
    [Theory]
    [InlineData("bu json degil")]
    [InlineData("{")]
    [InlineData("[1,2,3")]
    [InlineData("")]
    public void BozukJson_NullDonuyorIstisnaAtmiyor(string json)
        => Assert.Null(WorkflowGraph.Parse(json));

    /// Literal `null` da `null`: eskiden de böyleydi, korunuyor.
    [Fact]
    public void LiteralNull_NullDonuyor()
        => Assert.Null(WorkflowGraph.Parse("null"));

    /// GEÇERLİ GRAF HÂLÂ OKUNUYOR.
    ///
    /// İstisnayı yutmak, geçerli girdiyi de sessizce yutan bir
    /// düzeltmeye dönüşebilirdi — o zaman hiçbir graf çalışmazdı.
    [Fact]
    public void GecerliGraf_Okunuyor()
    {
        var graph = WorkflowGraph.Parse(
            """
            {"schema_version":1,"key":"k","name":"n",
             "nodes":[{"id":"a","type":"test.a"}],"edges":[]}
            """);

        Assert.NotNull(graph);
        Assert.Equal("k", graph.Key);
        Assert.Single(graph.Nodes);
    }

    /// JSON GEÇERLİ AMA GRAF DEĞİLSE de istisna atmıyor.
    ///
    /// Bir dizi ya da sayı, `WorkflowGraph`'a dönüşemiyor; bu da
    /// "okunamadı" demek, çökme değil.
    [Theory]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"metin\"")]
    public void GrafOlmayanGecerliJson_NullDonuyor(string json)
        => Assert.Null(WorkflowGraph.Parse(json));
}
