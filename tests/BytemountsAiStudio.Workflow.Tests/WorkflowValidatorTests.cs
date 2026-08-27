using System.Text.Json;
using BytemountsAiStudio.Workflow.Definition;

namespace BytemountsAiStudio.Workflow.Tests;

public sealed class WorkflowValidatorTests
{
    private static readonly HashSet<string> KnownTypes = new(StringComparer.Ordinal)
    {
        "topic.select", "script.generate", "media.render", "quality.check",
    };

    private static WorkflowGraph Graph(
        IReadOnlyList<WorkflowNode> nodes, IReadOnlyList<WorkflowEdge> edges)
        => new() { Key = "test", Name = "Test", Nodes = nodes, Edges = edges };

    private static WorkflowNode Node(string id, string type = "script.generate")
        => new() { Id = id, Type = type, Config = JsonDocument.Parse("{}").RootElement };

    private static string[] Codes(WorkflowGraph graph)
        => WorkflowValidator.Validate(graph, KnownTypes).Select(i => i.Code).ToArray();

    [Fact]
    public void GecerliGraf_SorunUretmez()
    {
        var graph = Graph(
            [Node("topic", "topic.select"), Node("script"), Node("render", "media.render")],
            [new() { From = "topic", To = "script" }, new() { From = "script", To = "render" }]);

        Assert.Empty(WorkflowValidator.Validate(graph, KnownTypes));
    }

    [Fact]
    public void BilinmeyenNodeTipi_Yakalanir()
    {
        // Run'ın ortasında "bu node nasıl çalıştırılır" sorusuyla karşılaşmak
        // yerine kayıt anında yakalıyoruz.
        var graph = Graph([Node("x", "hicboyle.birsey.yok")], []);

        Assert.Contains("node.unknown_type", Codes(graph));
    }

    [Fact]
    public void TekrarlananNodeKimligi_Yakalanir()
    {
        var graph = Graph([Node("a"), Node("a")], []);

        Assert.Contains("node.duplicate_id", Codes(graph));
    }

    [Fact]
    public void OlmayanNodeaGidenKenar_Yakalanir()
    {
        var graph = Graph([Node("a")], [new() { From = "a", To = "yok" }]);

        Assert.Contains("edge.unknown_target", Codes(graph));
    }

    [Fact]
    public void ErisilemeyenNode_Yakalanir()
    {
        // Sessizce hiç çalışmaz; neredeyse her zaman bir kenar hatasıdır
        // ve gözle fark edilmesi zordur.
        var graph = Graph(
            [Node("a"), Node("b"), Node("yetim")],
            [new() { From = "a", To = "b" }, new() { From = "yetim", To = "yetim" }]);

        Assert.Contains("node.unreachable", Codes(graph));
    }

    [Fact]
    public void KendineBaglananNode_Yakalanir()
    {
        var graph = Graph([Node("a")], [new() { From = "a", To = "a" }]);

        Assert.Contains("edge.self_loop", Codes(graph));
    }

    [Fact]
    public void GirisNodeuOlmayanGraf_Yakalanir()
    {
        var graph = Graph(
            [Node("a"), Node("b")],
            [new() { From = "a", To = "b" }, new() { From = "b", To = "a" }]);

        Assert.Contains("workflow.no_entry", Codes(graph));
    }

    [Fact]
    public void GecerliDongu_KabulEdilir()
    {
        // QC → render geri dönüşü meşru bir desen; yasak olan sınırsız olması.
        var graph = Graph(
            [Node("render", "media.render"), Node("qc", "quality.check")],
            [
                new() { From = "render", To = "qc" },
                new() { From = "qc", To = "render", When = "qc.passed == false", MaxLoops = 2 },
            ]);

        var codes = Codes(graph);

        Assert.DoesNotContain("edge.loop_too_large", codes);
    }

    [Fact]
    public void CokBuyukDonguSiniri_Yakalanir()
    {
        // Otonom sistemde kontrolsüz döngü = kontrolsüz maliyet.
        var graph = Graph(
            [Node("render", "media.render"), Node("qc", "quality.check")],
            [
                new() { From = "render", To = "qc" },
                new() { From = "qc", To = "render", MaxLoops = 500 },
            ]);

        Assert.Contains("edge.loop_too_large", Codes(graph));
    }

    [Fact]
    public void BozukKosulIfadesi_Yakalanir()
    {
        var graph = Graph(
            [Node("a"), Node("b")],
            [new() { From = "a", To = "b", When = "qc.passed &&" }]);

        Assert.Contains("edge.bad_expression", Codes(graph));
    }

    [Fact]
    public void KodIceriliKosul_Yakalanir()
    {
        var graph = Graph(
            [Node("a"), Node("b")],
            [new() { From = "a", To = "b", When = "System.Environment.Exit(0)" }]);

        Assert.Contains("edge.bad_expression", Codes(graph));
    }

    [Fact]
    public void SeedGrafi_Gecerlidir()
    {
        // Depodaki gerçek `shorts-fake` grafı doğrulamadan geçmeli; geçmezse
        // Faz 0'ın iskeleti kendi kuralına uymuyor demektir.
        var json = """
            {
              "schema_version": 1,
              "key": "shorts-fake",
              "name": "Sahte Shorts",
              "content_kind": "Short",
              "nodes": [
                { "id": "topic",  "type": "topic.select",    "config": {} },
                { "id": "script", "type": "script.generate", "config": {} },
                { "id": "render", "type": "media.render",    "config": {} }
              ],
              "edges": [
                { "from": "topic",  "to": "script" },
                { "from": "script", "to": "render" }
              ]
            }
            """;

        var graph = WorkflowGraph.Parse(json);

        Assert.NotNull(graph);
        Assert.Empty(WorkflowValidator.Validate(graph, KnownTypes));
        Assert.Equal("topic", graph.EntryNodes().Single().Id);
    }

    [Fact]
    public void JsonGidipGelme_KorunmusOlur()
    {
        var graph = Graph(
            [Node("a"), Node("b")],
            [new() { From = "a", To = "b", When = "qc.passed", MaxLoops = 3 }]);

        var round = WorkflowGraph.Parse(graph.ToJson());

        Assert.NotNull(round);
        Assert.Equal(graph.Nodes.Count, round.Nodes.Count);
        Assert.Equal("qc.passed", round.Edges[0].When);
        Assert.Equal(3, round.Edges[0].MaxLoops);
    }
}
