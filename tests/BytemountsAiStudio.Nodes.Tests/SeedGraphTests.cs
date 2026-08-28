using BytemountsAiStudio.Nodes;
using BytemountsAiStudio.Persistence;
using BytemountsAiStudio.Quality;
using BytemountsAiStudio.Workflow.Definition;

namespace BytemountsAiStudio.Nodes.Tests;

/// Depodaki GERÇEK tohum grafını sınar.
///
/// Doğrulama testi bunu iddia ediyordu ama grafın kendisini değil,
/// test dosyasına elle yazılmış üç node'luk bir kopyasını
/// doğruluyordu: gerçek graf bozulsa test yine geçerdi. Şimdi
/// `DatabaseSeeder.FakeGraphJson` sabitinin kendisi okunuyor —
/// zaten `public` olmasının sebebi buydu.
public sealed class SeedGraphTests
{
    private static WorkflowGraph Seed()
    {
        var graph = WorkflowGraph.Parse(DatabaseSeeder.FakeGraphJson);

        Assert.NotNull(graph);

        return graph;
    }

    [Fact]
    public void TohumGrafi_DogrulamadanGeciyor()
        => Assert.Empty(WorkflowValidator.Validate(Seed(), NodeHandlerRegistration.KnownNodeTypes));

    /// KALİTE DÖNGÜSÜ GRAFTA OLMALI.
    ///
    /// QC ve onay kapısı yazılmış, kayıtlı ve testliydi — ama hiçbir
    /// grafta yoktu. Yani gerçek bir koşuda QC hiç çalışmıyor, skor
    /// hiç üretilmiyor, seçici onay hiç devreye girmiyor ve hedefli
    /// retry hiç tetiklenmiyordu. Faz 2'nin tamamı yazılıp
    /// erişilemez durumdaydı.
    [Fact]
    public void TohumGrafi_KaliteDongusunuIceriyor()
    {
        var types = Seed().Nodes.Select(n => n.Type).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("qc.mechanical", types);
        Assert.Contains("human.approval", types);
    }

    /// ONAY EN SONDA: kendisinden sonra hiçbir node yoksa "onaylandı"
    /// kararının ardından koşacak bir şey de yok demektir. Onayı
    /// ortada bırakmak, insanı henüz üretilmemiş bir videoya
    /// baktırmak olurdu.
    [Fact]
    public void OnayKapisi_RenderVeQcSonrasinda()
    {
        var graph = Seed();
        var approval = graph.Nodes.Single(n => n.Type == "human.approval");
        var qc = graph.Nodes.Single(n => n.Type == "qc.mechanical");

        Assert.Contains(qc.Id, graph.Predecessors(approval.Id));
        Assert.DoesNotContain(graph.Edges, e => e.From == approval.Id);
    }

    /// RETRY HEDEFLERİ GRAFA DENK GELİYOR.
    ///
    /// Planlayıcı boru hattı aşamalarını adlandırıyor
    /// (`media.render`), graf node'lara kendi kimliklerini veriyor
    /// (`render`). Bu test bu iki dünyanın buluştuğunu doğruluyor:
    /// buluşmadığında hedefli retry hiçbir node bulamıyor ve QC'nin
    /// düşürdüğü video düzeltilmeden kalıyor.
    [Theory]
    [InlineData(RetryTarget.Script)]
    [InlineData(RetryTarget.Visuals)]
    [InlineData(RetryTarget.Timeline)]
    [InlineData(RetryTarget.Render)]
    [InlineData(RetryTarget.Metadata)]
    public void HerRetryHedefi_TohumGrafindaKarsiligiVar(RetryTarget target)
    {
        var resolved = Seed().ResolveTargets(RetryPlanner.NodesFrom(target));

        Assert.NotEmpty(resolved);
    }

    /// Hedefli retry'ın ANLAMI: önceki aşamalar korunuyor.
    [Fact]
    public void RenderHedefi_SenaryoyuKapsamiyor()
    {
        var resolved = Seed().ResolveTargets(RetryPlanner.NodesFrom(RetryTarget.Render));

        Assert.Contains("render", resolved);
        Assert.DoesNotContain("script", resolved);
        Assert.DoesNotContain("research", resolved);
    }
}
