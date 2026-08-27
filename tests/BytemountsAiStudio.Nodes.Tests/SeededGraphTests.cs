using System.Text.Json;
using BytemountsAiStudio.Nodes;
using BytemountsAiStudio.Persistence;

namespace BytemountsAiStudio.Nodes.Tests;

/// Tohumlanan grafın node kayıtlarıyla tutarlılığı (§6.2).
///
/// Veritabanı GEREKTİRMİYOR: graf bir sabit, kayıt bir küme. İkisinin
/// ayrışması run'ı çalışma ortasında düşürürdü ve bunu yakalamak için
/// Postgres ayağa kaldırmak gereksiz — ayrıca veritabanı gerektiren bir
/// test, veritabanı olmayan bir makinede hiç koşmuyor demek.
public sealed class SeededGraphTests
{
    private static JsonDocument Graph() => JsonDocument.Parse(DatabaseSeeder.FakeGraphJson);

    private static List<(string Id, string Type)> Nodes()
    {
        using var document = Graph();

        return [.. document.RootElement.GetProperty("nodes")
            .EnumerateArray()
            .Select(n => (n.GetProperty("id").GetString()!, n.GetProperty("type").GetString()!))];
    }

    private static List<(string From, string To)> Edges()
    {
        using var document = Graph();

        return [.. document.RootElement.GetProperty("edges")
            .EnumerateArray()
            .Select(e => (e.GetProperty("from").GetString()!, e.GetProperty("to").GetString()!))];
    }

    /// Kayıtlı olmayan bir node tipi grafı hiç kaydettirmemeli; kaçarsa
    /// hata run'ın ortasında ortaya çıkar.
    [Fact]
    public void GraftakiTumNodeTipleri_Kayitli()
    {
        var unknown = Nodes()
            .Select(n => n.Type)
            .Where(t => !NodeHandlerRegistration.KnownNodeTypes.Contains(t))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(unknown.Count == 0, $"Kayitli olmayan node tipi: {string.Join(", ", unknown)}");
    }

    [Fact]
    public void NodeKimlikleri_Tekil()
    {
        var nodes = Nodes();

        Assert.Equal(nodes.Count, nodes.Select(n => n.Id).Distinct(StringComparer.Ordinal).Count());
    }

    /// Her kenar var olan node'lara işaret etmeli. Yazım hatası içeren
    /// bir kenar sessizce hiç izlenmezdi.
    [Fact]
    public void Kenarlar_VarOlanNodelariGosterir()
    {
        var ids = Nodes().Select(n => n.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var (from, to) in Edges())
        {
            Assert.Contains(from, ids, StringComparer.Ordinal);
            Assert.Contains(to, ids, StringComparer.Ordinal);
        }
    }

    /// Kök hariç her node'a bir kenar gelmeli. Gelmiyorsa o node hiç
    /// koşmaz ve run "başarılı" biter — sessiz eksik çıktı.
    [Fact]
    public void KokDisindakiHerNode_Ulasilabilir()
    {
        var reachable = Edges().Select(e => e.To).ToHashSet(StringComparer.Ordinal);
        var roots = Nodes().Select(n => n.Id).Where(id => !reachable.Contains(id)).ToList();

        Assert.Single(roots);
    }

    [Fact]
    public void SeoNodeu_GraftaVar()
    {
        Assert.Contains(Nodes(), n => n.Type == "seo.generate");
    }

    /// Metadata SENARYODAN türüyor; senaryo node'undan sonra gelmeli.
    [Fact]
    public void SeoNodeu_SenaryodanSonra()
    {
        var edges = Edges();
        var order = new List<string>();
        var reachable = edges.Select(e => e.To).ToHashSet(StringComparer.Ordinal);
        var current = Nodes().Select(n => n.Id).First(id => !reachable.Contains(id));

        while (current is not null)
        {
            order.Add(current);
            current = edges.FirstOrDefault(e => e.From == current).To;
        }

        Assert.True(
            order.IndexOf("seo") > order.IndexOf("script"),
            $"seo node'u senaryodan once geliyor: {string.Join(" -> ", order)}");
    }
}
