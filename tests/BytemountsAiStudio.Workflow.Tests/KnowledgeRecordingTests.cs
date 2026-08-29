using System.Text.Json;
using BytemountsAiStudio.Persistence;
using BytemountsAiStudio.Persistence.Entities;
using BytemountsAiStudio.Queue;
using BytemountsAiStudio.TestSupport;
using BytemountsAiStudio.Workflow.Definition;
using BytemountsAiStudio.Workflow.Engine;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Workflow.Tests;

/// Kalıcı bilgi tabanına yazımın sınanması (P1-06).
///
/// ***BU DOSYANIN VAR OLMA SEBEBİ:*** `KnowledgeBase` yazılmış,
/// testlenmiş ve **hiçbir yerden çağrılmıyordu**. Araştırma node'ları
/// ve iddia denetimi kaynakları ve iddiaları yalnızca run bağlamı
/// JSON'una yazıyordu; `sources` ve `claims` tablolarına tek satır
/// girmiyordu. Koşular arası kaynak yeniden kullanımı, telif/atıf
/// denetimi ve "bu iddia hangi kaynaktan geldi" sorgusu **boş tablo**
/// üzerinden koşuyor ve sessizce sıfır sonuç dönüyordu.
///
/// Kaydın kendi testleri yeşildi — `KnowledgeBase`'i doğrudan
/// çağırıyorlardı. Buradakiler motorun onu ÇAĞIRDIĞINI sınıyor.
[Collection(DatabaseCollection.Name)]
public sealed class KnowledgeRecordingTests(DatabaseFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => CleanAsync();

    public Task DisposeAsync() => CleanAsync();

    private async Task CleanAsync()
    {
        if (!fixture.Available)
        {
            return;
        }

        await using var db = fixture.CreateContext();

        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM claims; DELETE FROM sources; DELETE FROM node_executions; "
            + "DELETE FROM jobs; DELETE FROM runs; DELETE FROM workflow_versions; "
            + "DELETE FROM workflows WHERE key = 'bilgi-testi'");
    }

    private void RequireDatabase()
        => Assert.True(fixture.Available, $"PostgreSQL erişilemiyor ({fixture.UnavailableReason}).");

    /// Araştırma çıktısı üreten tek node'luk bir graf koşturur.
    private sealed class SourceHandler(string outputJson) : INodeHandler
    {
        public string NodeType => "research.deep";

        public Core.Execution.QueueClass Queue => Core.Execution.QueueClass.Search;

        public Task<Core.Result<JsonElement>> ExecuteAsync(
            NodeContext context, CancellationToken cancellationToken)
            => Task.FromResult(Core.Result.Success(
                JsonDocument.Parse(outputJson).RootElement.Clone()));
    }

    private static async Task<Guid> RunOnceAsync(StudioDbContext db, string outputJson)
    {
        var workflow = new Persistence.Entities.Workflow
        {
            Key = "bilgi-testi",
            Name = "Bilgi testi",
        };

        // ***GRAF NESNEDEN ÜRETİLİYOR, ELLE YAZILMIYOR.***
        //
        // İlk yazımda JSON elle yazılmıştı ve CI'da "Workflow grafı
        // okunamadı" diye düştü: şemanın istediği alanlar (`key`,
        // `name`) eksikti. Yerelde veritabanı olmadığı için
        // görülmemişti. Nesneden üretmek, şema değiştiğinde testin de
        // değişmesini derleyicinin garanti etmesi demek.
        var graph = new WorkflowGraph
        {
            Key = "bilgi-testi",
            Name = "Bilgi testi",
            Nodes =
            [
                new()
                {
                    Id = "research.deep",
                    Type = "research.deep",
                    Config = JsonDocument.Parse("{}").RootElement.Clone(),
                },
            ],
            Edges = [],
        };

        var version = new Persistence.Entities.WorkflowVersion
        {
            Workflow = workflow,
            Version = 1,
            GraphJson = graph.ToJson(),
        };

        db.Workflows.Add(workflow);
        db.WorkflowVersions.Add(version);
        await db.SaveChangesAsync();

        var registry = new NodeRegistry().Register(new SourceHandler(outputJson));
        var engine = new WorkflowEngine(db, new JobQueue(db), registry);

        var run = await engine.StartRunAsync(version.Id, null, null, CancellationToken.None);

        Assert.True(run.IsSuccess, run.IsFailure ? run.Error.Message : string.Empty);

        var executed = await engine.ExecuteNextAsync(
            "test-worker", Core.Execution.QueueClass.Search, CancellationToken.None);

        Assert.True(executed.IsSuccess, executed.IsFailure ? executed.Error.Message : string.Empty);

        return run.Value;
    }

    /* ---- kaynaklar ---- */

    /// ***ARAŞTIRMA ÇIKTISI `sources` TABLOSUNA DÜŞÜYOR.***
    ///
    /// BU DOSYANIN EN ÖNEMLİ TESTİ. Düşmeseydi tablo kalıcı olarak boş
    /// kalır ve koşular arası kaynak yeniden kullanımı hiç çalışmazdı.
    [Fact]
    public async Task ArastirmaCiktisi_KaynakYaziyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();

        await RunOnceAsync(db, """
            {"sources":[
              {"url":"https://tr.wikipedia.org/wiki/Test","title":"Test",
               "content_hash":"aaaa","fetched_at":"2026-08-29T00:00:00Z"}
            ]}
            """);

        await using var check = fixture.CreateContext();

        Assert.Equal(1, await check.Sources.AsNoTracking().CountAsync());
    }

    /// ***KAYNAKSIZ ÇIKTI HİÇBİR ŞEY YAZMIYOR.***
    ///
    /// Seçim node TİPİNE göre değil ÇIKTININ ŞEKLİNE göre: tipe
    /// baksaydı liste güncellenmeyi beklerdi ve yeni bir araştırma
    /// node'u eklendiğinde kimse eklemeyi hatırlamazdı.
    [Fact]
    public async Task KaynaksizCikti_YazmaYok()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();

        await RunOnceAsync(db, """{"senaryo":"bir metin"}""");

        await using var check = fixture.CreateContext();

        Assert.Equal(0, await check.Sources.AsNoTracking().CountAsync());
    }

    /* ---- iddialar ---- */

    /// İDDİA ÇIKTISI `claims` TABLOSUNA DÜŞÜYOR.
    [Fact]
    public async Task IddiaCiktisi_ClaimsYaziyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();

        await RunOnceAsync(db, """
            {"sources":[
              {"url":"https://tr.wikipedia.org/wiki/Test","title":"Test",
               "content_hash":"bbbb","fetched_at":"2026-08-29T00:00:00Z"}
            ],
             "claims":[
              {"text":"Bir iddia.","verdict":"supported","reason":"Kaynak destekliyor.",
               "source":"https://tr.wikipedia.org/wiki/Test"}
            ]}
            """);

        await using var check = fixture.CreateContext();

        Assert.Equal(1, await check.Claims.AsNoTracking().CountAsync());

        // ***KAYNAK EŞLEŞMESİ KURULMUŞ OLMALI.***
        //
        // Kaynaklar iddialardan ÖNCE yazılıyor: ters sırada eşleştirme
        // boş kalırdı ve "bu iddia hangi kaynaktan geldi" sorusu
        // cevapsız olurdu.
        //
        // ALAN ADI `source`, `source_url` DEĞİL — ilk yazımda yanlıştı
        // ve test CI'da bu satırda düştü. `ClaimCheckHandler`'ın
        // ürettiği ada uymak zorunlu: test kendi uydurduğu bir şemayı
        // sınasaydı, gerçek çıktının eşleşip eşleşmediğini hiç
        // söylemezdi.
        var claim = await check.Claims.AsNoTracking().FirstAsync();

        Assert.NotNull(claim.SourceId);
    }

    /* ---- dayanıklılık ---- */

    /// ***BOZUK KAYNAK KAYDI KOŞUYU DÜŞÜRMÜYOR.***
    ///
    /// Bilgi tabanı bir tanı ve yeniden kullanım aracı; yazılamaması
    /// videoyu geçersiz kılmıyor. Koşuyu düşürseydi, tamamlanmış bir
    /// videoyu bir yan kaydın başarısızlığı yüzünden çöp ederdik.
    [Fact]
    public async Task BozukKaynak_KosuyuDusurmuyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();

        var runId = await RunOnceAsync(db, """{"sources":[{"eksik":"alanlar"}]}""");

        await using var check = fixture.CreateContext();

        var execution = await check.NodeExecutions.AsNoTracking()
            .FirstAsync(e => e.RunId == runId);

        Assert.Equal(Core.Execution.NodeState.Succeeded, execution.State);
    }
}
