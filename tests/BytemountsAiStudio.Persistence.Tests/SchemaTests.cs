using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Persistence;
using BytemountsAiStudio.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using BytemountsAiStudio.TestSupport;

namespace BytemountsAiStudio.Persistence.Tests;

/// Şemanın gerçek PostgreSQL üzerindeki davranışı.
///
/// Ham sorgularda kolonlar `Value` olarak adlandırılıyor: EF'in skaler
/// `SqlQuery&lt;T&gt;` sürümü sonucu bu adla sarmalıyor.
[Collection(DatabaseCollection.Name)]
public sealed class SchemaTests(DatabaseFixture fixture)
{
    /// Veritabanı yoksa test ATLANMAZ, düşer.
    ///
    /// Sessizce geçen bir test, geçmediği hâlde geçmiş görünür ve en çok
    /// güvendiğiniz anda yanıltır. PostgreSQL bu projenin altyapısı;
    /// yokluğu bir mazeret değil, düzeltilecek bir durum.
    private void RequireDatabase()
        => Assert.True(fixture.Available,
            $"PostgreSQL erişilemiyor ({fixture.UnavailableReason}). `docker compose up -d` çalıştırın.");

    [Fact]
    public async Task Migration_TumTablolariOlusturur()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var tables = await db.Database
            .SqlQuery<string>($@"select table_name as ""Value"" from information_schema.tables where table_schema = 'public'")
            .ToListAsync(CancellationToken.None);

        string[] expected =
        [
            "channels", "topics", "workflows", "workflow_versions", "runs",
            "node_executions", "run_events", "jobs", "assets", "provider_calls",
        ];

        var missing = expected.Where(t => !tables.Contains(t, StringComparer.Ordinal)).ToList();
        Assert.True(missing.Count == 0, $"Eksik tablo: {string.Join(", ", missing)}");
    }

    [Fact]
    public async Task Seed_IdempotenttirIkinciCagridaEklemeYapmaz()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        await DatabaseSeeder.SeedAsync(db, CancellationToken.None);
        var second = await DatabaseSeeder.SeedAsync(db, CancellationToken.None);

        Assert.Equal(0, second);
        Assert.Equal(2, await db.Channels.CountAsync(CancellationToken.None));
        Assert.True(await db.Workflows.AnyAsync(
            w => w.Key == DatabaseSeeder.FakeWorkflowKey, CancellationToken.None));
    }

    [Fact]
    public async Task Enumlar_MetinOlarakSaklanir()
    {
        // Sayı olsaydı enum sırasını değiştiren bir refactor veritabanındaki
        // anlamı sessizce kaydırırdı — en sinsi hata türlerinden biri.
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var job = new Job { Queue = QueueClass.Render, State = JobState.Pending };
        db.Jobs.Add(job);
        await db.SaveChangesAsync(CancellationToken.None);

        var stored = await db.Database
            .SqlQuery<string>($@"select queue as ""Value"" from jobs where id = {job.Id}")
            .SingleAsync(CancellationToken.None);

        Assert.Equal("Render", stored);
    }

    [Fact]
    public async Task Embedding_YazilipOkunabilirVeBenzerlikHesaplanir()
    {
        // ADR-003'ün çalıştığının kanıtı: vektör gidip geliyor ve mesafe
        // veritabanı tarafında hesaplanıyor.
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var a = new float[768];
        var b = new float[768];
        a[0] = 1f;
        b[1] = 1f;

        var topicA = new Topic { Title = "Vektör A", Language = "tr-TR", Embedding = new Vector(a) };
        var topicB = new Topic { Title = "Vektör B", Language = "tr-TR", Embedding = new Vector(b) };
        db.Topics.AddRange(topicA, topicB);
        await db.SaveChangesAsync(CancellationToken.None);

        var distance = await db.Database
            .SqlQuery<double>($@"select (t1.embedding <=> t2.embedding)::double precision as ""Value"" from topics t1, topics t2 where t1.id = {topicA.Id} and t2.id = {topicB.Id}")
            .SingleAsync(CancellationToken.None);

        // Dik vektörlerin kosinüs mesafesi 1.0
        Assert.Equal(1.0, distance, 3);
    }

    [Fact]
    public async Task NodeExecution_AyniDenemeIkiKezYazilamaz()
    {
        // Çift tetiklemeyi uygulama katmanında değil veritabanında durduruyoruz;
        // uygulama katmanındaki kontrol yarış koşulunda kaçırırdı.
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var run = await CreateRunAsync(db);

        db.NodeExecutions.Add(new NodeExecution
        {
            RunId = run.Id, NodeId = "script", NodeType = "script.generate",
            Attempt = 1, IdempotencyKey = "k1",
        });
        await db.SaveChangesAsync(CancellationToken.None);

        db.NodeExecutions.Add(new NodeExecution
        {
            RunId = run.Id, NodeId = "script", NodeType = "script.generate",
            Attempt = 1, IdempotencyKey = "k2",
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task JsonbKolonlari_GercektenJsonb()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var columns = await db.Database
            .SqlQuery<string>($@"select table_name || '.' || column_name as ""Value"" from information_schema.columns where table_schema = 'public' and udt_name = 'jsonb'")
            .ToListAsync(CancellationToken.None);

        Assert.Contains("runs.context_json", columns, StringComparer.Ordinal);
        Assert.Contains("jobs.payload_json", columns, StringComparer.Ordinal);
        Assert.Contains("topics.scores_json", columns, StringComparer.Ordinal);
    }

    [Fact]
    public async Task KismiIndeksler_Olusturulmus()
    {
        // Kuyruk tablosu milyonlarca bitmiş iş biriktirecek; sıcak indeksin
        // yalnızca bekleyenleri tutması performansın tamamı.
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var indexes = await db.Database
            .SqlQuery<string>($@"select indexname as ""Value"" from pg_indexes where schemaname = 'public' and indexdef like '%WHERE%'")
            .ToListAsync(CancellationToken.None);

        Assert.Contains("ix_jobs_queue_run_after_priority", indexes, StringComparer.Ordinal);
        Assert.Contains("ix_jobs_lease_expires_at", indexes, StringComparer.Ordinal);
        Assert.Contains("ix_topics_state_overall_score", indexes, StringComparer.Ordinal);
    }

    private static async Task<Run> CreateRunAsync(StudioDbContext db)
    {
        var workflow = new Workflow
        {
            Key = "t-" + Guid.NewGuid().ToString("N")[..8],
            Name = "test",
            CurrentVersion = 1,
        };
        var version = new WorkflowVersion { Version = 1, GraphJson = "{}" };
        workflow.Versions.Add(version);
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync(CancellationToken.None);

        var run = new Run { WorkflowVersionId = version.Id };
        db.Runs.Add(run);
        await db.SaveChangesAsync(CancellationToken.None);
        return run;
    }
}
