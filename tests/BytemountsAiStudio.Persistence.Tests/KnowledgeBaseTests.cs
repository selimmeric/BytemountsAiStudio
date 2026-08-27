using System.Text.Json;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Persistence.Entities;
using BytemountsAiStudio.Persistence.Providers;
using BytemountsAiStudio.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Persistence.Tests;

/// Bilgi tabanı testleri (P1-11).
///
/// Kabul kriteri: "bir videonun tüm kaynakları tek sorguyla listeleniyor."
/// Buradaki testler o sorgunun gerçekten çalıştığını ve tekilleştirmenin
/// doğru anahtarla yapıldığını doğruluyor.
[Collection(DatabaseCollection.Name)]
public sealed class KnowledgeBaseTests(DatabaseFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (!fixture.Available)
        {
            return;
        }

        await using var db = fixture.CreateContext();

        // Sıra önemli: iddialar kaynaklara ve run'a bağlı.
        await db.Database.ExecuteSqlRawAsync("DELETE FROM claims");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM sources");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private void RequireDatabase()
        => Assert.True(fixture.Available, $"PostgreSQL erişilemiyor ({fixture.UnavailableReason}).");

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    private static object SourceJson(string url, string hash, string type = "Encyclopedia")
        => new
        {
            url,
            title = "Başlık",
            source_type = type,
            content_hash = hash,
            excerpt = "Kaynak özeti.",
            length = 120,
        };

    private static object ClaimJson(string text, string verdict, string? source, int sentence = 0)
        => new { text, sentence, verdict, source, reason = "gerekçe" };

    /// Test için gerçek bir run gerekiyor: iddialar run'a yabancı
    /// anahtarla bağlı.
    private static async Task<Guid> CreateRunAsync(StudioDbContext db)
    {
        var workflow = new Workflow
        {
            Key = $"kb-test-{Guid.NewGuid():N}",
            Name = "KB testi",
            ContentKind = ContentKind.Short,
            CurrentVersion = 1,
        };

        var version = new WorkflowVersion { Version = 1, GraphJson = "{}" };
        workflow.Versions.Add(version);
        db.Workflows.Add(workflow);

        var run = new Run { WorkflowVersionId = version.Id, State = RunState.Running };
        db.Runs.Add(run);

        await db.SaveChangesAsync(CancellationToken.None);

        return run.Id;
    }

    [Fact]
    public async Task Kaynaklar_Kaydedilir()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();
        var kb = new KnowledgeBase(db);

        var result = await kb.RecordSourcesAsync(
            Json(new { sources = new[] { SourceJson("https://a.test", new string('a', 64)) } }),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value);
        Assert.Equal(1, await db.Sources.CountAsync(CancellationToken.None));
    }

    /// Tekilleştirme İÇERİK ÖZETİYLE, adresle değil: aynı sayfa iki
    /// farklı adresten gelebiliyor (yönlendirme, izleme parametreleri)
    /// ve aynı içeriği iki kez saklamak kaynak sayımını bozardı.
    [Fact]
    public async Task AyniIcerik_FarkliAdres_TekKayit()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();
        var kb = new KnowledgeBase(db);
        var hash = new string('b', 64);

        await kb.RecordSourcesAsync(
            Json(new { sources = new[] { SourceJson("https://a.test/x", hash) } }),
            CancellationToken.None);

        await kb.RecordSourcesAsync(
            Json(new { sources = new[] { SourceJson("https://a.test/x?utm=1", hash) } }),
            CancellationToken.None);

        Assert.Equal(1, await db.Sources.CountAsync(CancellationToken.None));
    }

    /// Özetsiz bir kaynak sessizce atlanmamalı mı? Atlanıyor — ama
    /// bilinçli: özet tekillik anahtarı ve onsuz kayıt anlamsız.
    [Fact]
    public async Task OzetsizKaynak_Atlanir()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var result = await new KnowledgeBase(db).RecordSourcesAsync(
            Json(new { sources = new[] { new { url = "https://a.test", title = "x" } } }),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value);
    }

    [Fact]
    public async Task Iddialar_Kaydedilir()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();
        var kb = new KnowledgeBase(db);
        var runId = await CreateRunAsync(db);

        await kb.RecordSourcesAsync(
            Json(new { sources = new[] { SourceJson("https://a.test", new string('c', 64)) } }),
            CancellationToken.None);

        var added = await kb.RecordClaimsAsync(runId, Json(new
        {
            claims = new[]
            {
                ClaimJson("Birinci iddia.", "supported", "https://a.test"),
                ClaimJson("İkinci iddia.", "unsupported", null, 1),
            },
            same_model = true,
        }), CancellationToken.None);

        Assert.True(added.IsSuccess);
        Assert.Equal(2, added.Value);
    }

    /// İddia kaynağa ADRESE göre bağlanıyor: iki node birbirinin
    /// veritabanı kimliklerini bilmek zorunda kalmasın.
    [Fact]
    public async Task Iddia_KaynagaAdreseGoreBaglanir()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();
        var kb = new KnowledgeBase(db);
        var runId = await CreateRunAsync(db);

        await kb.RecordSourcesAsync(
            Json(new { sources = new[] { SourceJson("https://kaynak.test", new string('d', 64)) } }),
            CancellationToken.None);

        await kb.RecordClaimsAsync(runId, Json(new
        {
            claims = new[] { ClaimJson("İddia.", "supported", "https://kaynak.test") },
        }), CancellationToken.None);

        await using var fresh = fixture.CreateContext();
        var claim = await fresh.Claims.AsNoTracking()
            .SingleAsync(c => c.RunId == runId, CancellationToken.None);

        Assert.NotNull(claim.SourceId);
    }

    /// ASIL KABUL KRİTERİ: bir videonun tüm kaynakları TEK sorguyla.
    [Fact]
    public async Task VideonunKaynaklari_TekSorguyla()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();
        var kb = new KnowledgeBase(db);
        var runId = await CreateRunAsync(db);

        await kb.RecordSourcesAsync(Json(new
        {
            sources = new[]
            {
                SourceJson("https://bir.test", new string('e', 64)),
                SourceJson("https://iki.test", new string('f', 64)),
            },
        }), CancellationToken.None);

        await kb.RecordClaimsAsync(runId, Json(new
        {
            claims = new[]
            {
                ClaimJson("A", "supported", "https://bir.test"),
                ClaimJson("B", "supported", "https://iki.test"),
                // Aynı kaynağa dayanan ikinci iddia; kaynak tekrar
                // sayılmamalı.
                ClaimJson("C", "supported", "https://bir.test"),
            },
        }), CancellationToken.None);

        await using var fresh = fixture.CreateContext();
        var sources = await new KnowledgeBase(fresh).SourcesForRunAsync(runId, CancellationToken.None);

        Assert.Equal(2, sources.Count);
    }

    /// Araştırmada çekilip senaryoda hiç kullanılmayan bir kaynak
    /// listeye girmiyor — "bu video neye dayanıyor" sorusunun cevabı
    /// KULLANILAN kaynaklar.
    [Fact]
    public async Task KullanilmayanKaynak_VideoListesineGirmez()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();
        var kb = new KnowledgeBase(db);
        var runId = await CreateRunAsync(db);

        await kb.RecordSourcesAsync(Json(new
        {
            sources = new[]
            {
                SourceJson("https://kullanilan.test", new string('1', 64)),
                SourceJson("https://kullanilmayan.test", new string('2', 64)),
            },
        }), CancellationToken.None);

        await kb.RecordClaimsAsync(runId, Json(new
        {
            claims = new[] { ClaimJson("A", "supported", "https://kullanilan.test") },
        }), CancellationToken.None);

        await using var fresh = fixture.CreateContext();
        var sources = await new KnowledgeBase(fresh).SourcesForRunAsync(runId, CancellationToken.None);

        Assert.Single(sources);
        Assert.Equal("https://kullanilan.test", sources[0].Url);
    }

    /// Node yeniden koşabiliyor (retry, hedefli düzeltme); iddialar
    /// birikmemeli.
    [Fact]
    public async Task IddiaYenidenYazilinca_Coklamaz()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();
        var kb = new KnowledgeBase(db);
        var runId = await CreateRunAsync(db);

        var payload = Json(new { claims = new[] { ClaimJson("A", "supported", null) } });

        await kb.RecordClaimsAsync(runId, payload, CancellationToken.None);
        await kb.RecordClaimsAsync(runId, payload, CancellationToken.None);

        await using var fresh = fixture.CreateContext();

        Assert.Equal(1, await fresh.Claims.CountAsync(c => c.RunId == runId, CancellationToken.None));
    }

    [Fact]
    public async Task IddiaOzeti_SayilariDondurur()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();
        var kb = new KnowledgeBase(db);
        var runId = await CreateRunAsync(db);

        await kb.RecordClaimsAsync(runId, Json(new
        {
            claims = new[]
            {
                ClaimJson("A", "supported", null),
                ClaimJson("B", "unsupported", null),
                ClaimJson("C", "contradicted", null),
            },
        }), CancellationToken.None);

        await using var fresh = fixture.CreateContext();
        var (total, supported, contradicted) =
            await new KnowledgeBase(fresh).ClaimSummaryAsync(runId, CancellationToken.None);

        Assert.Equal(3, total);
        Assert.Equal(1, supported);
        Assert.Equal(1, contradicted);
    }

    /// Doğrulamanın çıkarımla aynı modelden gelip gelmediği kayda
    /// giriyor: aynıysa sonuç iyimser olma eğiliminde.
    [Fact]
    public async Task AyniModelBayragi_Saklanir()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();
        var kb = new KnowledgeBase(db);
        var runId = await CreateRunAsync(db);

        await kb.RecordClaimsAsync(runId, Json(new
        {
            claims = new[] { ClaimJson("A", "supported", null) },
            same_model = true,
        }), CancellationToken.None);

        await using var fresh = fixture.CreateContext();
        var claim = await fresh.Claims.AsNoTracking()
            .SingleAsync(c => c.RunId == runId, CancellationToken.None);

        Assert.True(claim.SameModel);
    }
}
