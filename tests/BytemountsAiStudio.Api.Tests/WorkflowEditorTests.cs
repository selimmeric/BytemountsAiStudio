using BytemountsAiStudio.Api;
using BytemountsAiStudio.Persistence;
using BytemountsAiStudio.TestSupport;
using BytemountsAiStudio.Workflow.Engine;
using Microsoft.EntityFrameworkCore;
using WorkflowEntity = BytemountsAiStudio.Persistence.Entities.Workflow;
using WorkflowVersionEntity = BytemountsAiStudio.Persistence.Entities.WorkflowVersion;

namespace BytemountsAiStudio.Api.Tests;

/// İş akışı editörünün sunucu tarafı (P3-05).
///
/// EDİTÖRÜN KENDİ DOĞRULAMA KURALLARI YOK ve testlerin çoğu bunu
/// koruyor: doğrulama motorun kullandığı `WorkflowValidator` ile
/// koşuyor. Kuralları tarayıcıda tekrar yazmak, iki kural setinin
/// zamanla ayrışması demekti — editör "geçerli" der, motor reddeder
/// ve aradaki farkı ancak kaydetmeye çalışan kişi görürdü.
[Collection(DatabaseCollection.Name)]
public sealed class WorkflowEditorTests(DatabaseFixture fixture) : IAsyncLifetime
{
    private static readonly NodeRegistry Registry = new NodeRegistry()
        .Register(new StubHandler("test.a"))
        .Register(new StubHandler("test.b"));

    public async Task InitializeAsync()
    {
        if (!fixture.Available)
        {
            return;
        }

        await using var db = fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM workflow_versions; DELETE FROM workflows");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private void RequireDatabase()
        => Assert.True(fixture.Available, $"PostgreSQL erişilemiyor ({fixture.UnavailableReason}).");

    private static string Graph(params string[] nodeIds)
    {
        var nodes = string.Join(",", nodeIds.Select(id => $$"""{"id":"{{id}}","type":"test.a"}"""));

        return $$"""{"schema_version":1,"key":"k","name":"n","nodes":[{{nodes}}],"edges":[]}""";
    }

    /* ---- doğrulama ---- */

    /// TANINMAYAN TİP REDDEDİLİYOR — kural motordan geliyor.
    [Fact]
    public void BilinmeyenTip_Reddediliyor()
    {
        var result = WorkflowEditor.Validate(
            """{"schema_version":1,"key":"k","name":"n","nodes":[{"id":"a","type":"uydurma"}],"edges":[]}""",
            Registry);

        Assert.False(result.Valid);
        Assert.Contains(result.Issues, i => i.Code == "node.unknown_type");
    }

    /// BOZUK JSON İSTİSNA ATMIYOR, KENDİ KODUYLA BİLDİRİLİYOR.
    ///
    /// Gerçek bir hata: `WorkflowGraph.Parse` bozuk metinde istisna
    /// atıyordu ve doğrulama ucu HTTP cevabına bir yığın izi
    /// düşürüyordu. Ayrı bir kod taşıyor çünkü yapılacak şey de ayrı:
    /// JSON'u düzeltmek, node eklemek değil.
    [Theory]
    [InlineData("bu json degil")]
    [InlineData("{")]
    [InlineData("[]")]
    public void BozukJson_ParseHatasi(string json)
    {
        var result = WorkflowEditor.Validate(json, Registry);

        Assert.False(result.Valid);
        Assert.Contains(result.Issues, i => i.Code == "workflow.parse");
    }

    [Fact]
    public void GecerliGraf_Geciyor()
        => Assert.True(WorkflowEditor.Validate(Graph("a"), Registry).Valid);

    /// PALET KAYITLI TİPLERDEN GELİYOR, elle yazılmış bir listeden
    /// değil: liste olsaydı yeni bir handler eklendiğinde palete
    /// eklemeyi unutmak mümkün olurdu ve o node hiç kullanılamazdı —
    /// ya da daha kötüsü, paletteki bir tip kayıtlı olmaz ve graf
    /// kaydedilemezdi.
    [Fact]
    public void Palet_KayittanGeliyor()
        => Assert.Equal(["test.a", "test.b"], WorkflowEditor.Palette(Registry));

    /* ---- kaydetme ---- */

    /// KAYDETMEK YENİ SÜRÜM ÜRETİYOR, ESKİSİNİ DEĞİŞTİRMİYOR.
    ///
    /// Koşan run'lar başladıkları grafa bağlı (§6.2): yerinde
    /// düzenleme, yarısı eski yarısı yeni kurallarla üretilmiş bir
    /// video demekti.
    [Fact]
    public async Task Kaydetme_YeniSurumUretiyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();
        await SeedAsync(db, "k", currentVersion: 1, versions: [1]);

        var result = await WorkflowEditor.SaveAsync(db, Registry, "k", Graph("a", "b"), CancellationToken.None);

        Assert.True(result.Saved);
        Assert.Equal(2, result.Version);

        // Eski sürüm DURUYOR ve içeriği değişmemiş.
        //
        // İçerik GRAF OLARAK karşılaştırılıyor, metin olarak değil:
        // kolon `jsonb` ve Postgres anahtarları yeniden sıralayıp
        // boşluk ekliyor. Metin karşılaştırması, hiçbir şey
        // değişmemişken kırmızı yanardı.
        var old = await db.WorkflowVersions.AsNoTracking()
            .SingleAsync(v => v.Version == 1, CancellationToken.None);

        var parsed = Workflow.Definition.WorkflowGraph.Parse(old.GraphJson);

        Assert.NotNull(parsed);
        Assert.Equal(["eski"], parsed.Nodes.Select(n => n.Id));
    }

    /// SONRAKİ NUMARA EN YÜKSEK SÜRÜMDEN, `CurrentVersion`'DAN DEĞİL.
    ///
    /// İkisi ayrışabiliyor: bir sürüme geri dönülmüşse
    /// `CurrentVersion` daha küçük olur. Ondan +1 almak, VAR OLAN bir
    /// sürümün numarasını ikinci kez kullanmak — yani bir grafın
    /// üzerine başka bir graf yazmak demekti ve o graf koşan
    /// run'ların bağlı olduğu graf olabilirdi.
    [Fact]
    public async Task GeriDonulmusSurum_UzerineYazmiyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();

        // v1..v5 var ama v3'e geri dönülmüş.
        await SeedAsync(db, "k", currentVersion: 3, versions: [1, 2, 3, 4, 5]);

        var result = await WorkflowEditor.SaveAsync(db, Registry, "k", Graph("a"), CancellationToken.None);

        Assert.True(result.Saved);
        Assert.Equal(6, result.Version);

        // Ve hiçbir sürüm kaybolmadı.
        Assert.Equal(6, await db.WorkflowVersions.CountAsync(CancellationToken.None));
    }

    /// SUNUCU İSTEMCİYE GÜVENMİYOR: doğrulama kaydetmede TEKRAR
    /// koşuyor.
    ///
    /// İstemci zaten doğrulamış olabilir ama tarayıcıyı atlayan
    /// herhangi bir çağrı bozuk graf kaydedebilirdi — ve o graf bir
    /// sonraki run'da patlardı, yani hata üretim zamanına ertelenmiş
    /// olurdu.
    [Fact]
    public async Task BozukGraf_KaydedilmiyorVeSurumArtmiyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();
        await SeedAsync(db, "k", currentVersion: 1, versions: [1]);

        var result = await WorkflowEditor.SaveAsync(
            db, Registry, "k",
            """{"schema_version":1,"key":"k","name":"n","nodes":[{"id":"a","type":"yok"}],"edges":[]}""",
            CancellationToken.None);

        Assert.False(result.Saved);
        Assert.Contains(result.Issues, i => i.Code == "node.unknown_type");

        // Hiçbir sürüm eklenmedi.
        Assert.Equal(1, await db.WorkflowVersions.CountAsync(CancellationToken.None));
    }

    /// OLMAYAN İŞ AKIŞI KENDİ KODUYLA BİLDİRİLİYOR.
    ///
    /// "Geçersiz graf" ile "yanlış adres" farklı şeyler ve çağıranın
    /// grafı mı yoksa adresi mi düzelteceğini bilmesi gerekiyor.
    [Fact]
    public async Task OlmayanIsAkisi_KendiKoduyla()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();

        var result = await WorkflowEditor.SaveAsync(
            db, Registry, "yokboyle", Graph("a"), CancellationToken.None);

        Assert.False(result.Saved);
        Assert.Contains(result.Issues, i => i.Code == "workflow.unknown");
    }

    private static async Task SeedAsync(
        StudioDbContext db, string key, int currentVersion, int[] versions)
    {
        var workflow = new WorkflowEntity { Key = key, Name = key, CurrentVersion = currentVersion };
        db.Workflows.Add(workflow);

        foreach (var version in versions)
        {
            db.WorkflowVersions.Add(new WorkflowVersionEntity
            {
                Workflow = workflow,
                Version = version,
                GraphJson = """{"schema_version":1,"key":"k","name":"n","nodes":[{"id":"eski","type":"test.a"}],"edges":[]}""",
            });
        }

        await db.SaveChangesAsync(CancellationToken.None);
    }

    private sealed class StubHandler(string nodeType) : INodeHandler
    {
        public string NodeType => nodeType;

        public Core.Execution.QueueClass Queue => Core.Execution.QueueClass.Llm;

        public Task<Core.Result<System.Text.Json.JsonElement>> ExecuteAsync(
            NodeContext context, CancellationToken cancellationToken)
            => throw new NotSupportedException("Editör testi node çalıştırmıyor.");
    }
}
