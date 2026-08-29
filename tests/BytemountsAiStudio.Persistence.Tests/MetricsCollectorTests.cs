using System.Text.Json;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Persistence.Entities;
using BytemountsAiStudio.Persistence.Providers;
using BytemountsAiStudio.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Persistence.Tests;

/// Yayın sonrası ölçüm toplama (P5-01).
///
/// ÖĞRENME DÖNGÜSÜNÜN VERİ KAYNAĞI. P5-02'den P5-07'ye kadar yazılan
/// her şey bu tablodan besleniyor ve şimdiye kadar tablo yalnızca elle
/// dolduruluyordu.
[Collection(DatabaseCollection.Name)]
public sealed class MetricsCollectorTests(DatabaseFixture fixture) : IAsyncLifetime
{
    /// Sabit cevap veren ölçüm kaynağı.
    ///
    /// Gerçek bir API olmadan toplama mantığı sınanabilsin: toplayıcı
    /// somut sağlayıcıya değil arayüze bağlı.
    private sealed class StubSource(DailyMetric? metric, int settlingDays = 2) : IDailyMetricsSource
    {
        public string Platform => "youtube";

        public int Calls { get; private set; }

        public bool IsSettled(DateOnly metricDate, DateOnly today)
            => today.DayNumber - metricDate.DayNumber >= settlingDays;

        public Task<Result<DailyMetric?>> DailyAsync(
            string externalId, DateOnly date, CancellationToken cancellationToken)
        {
            Calls++;

            return Task.FromResult(Result.Success(metric));
        }
    }

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
            "DELETE FROM publication_metrics; DELETE FROM node_executions; "
            + "DELETE FROM runs; DELETE FROM workflow_versions; DELETE FROM workflows");
    }

    private void RequireDatabase()
        => Assert.True(fixture.Available, $"PostgreSQL erişilemiyor ({fixture.UnavailableReason}).");

    /// Sabit "bugün": ölçümün oturup oturmadığı karara girdiği için
    /// gerçek saate bağlı bir test, gece yarısı farklı sonuç verirdi.
    private static readonly DateTimeOffset Today = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static MetricsCollector Collector(StudioDbContext db, IDailyMetricsSource source)
        => new(db, source, new FakeTime(Today));

    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /* ---- toplama ---- */

    /// ÖLÇÜM YAZILIYOR VE DAKİKA SANİYEYE ÇEVRİLİYOR.
    ///
    /// Analytics dakika veriyor, tablo saniye tutuyor. Birim
    /// karışması, tutma oranını ALTMIŞ KAT yanlış gösterirdi.
    [Fact]
    public async Task Olcum_YaziliyorVeCevriliyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();

        // 20 Ağustos'ta yayınlandı; 7. gün 27 Ağustos, bugün 1 Eylül —
        // oturmuş.
        var runId = await PublishedRunAsync(db, new DateTimeOffset(2026, 8, 20, 9, 0, 0, TimeSpan.Zero));

        var source = new StubSource(new DailyMetric(new DateOnly(2026, 8, 27), 1_200, 340, 25, 4, 7));
        var summary = await Collector(db, source).CollectAsync(CancellationToken.None);

        Assert.True(summary.IsSuccess);
        Assert.Equal(1, summary.Value.Collected);

        var metric = await db.PublicationMetrics.AsNoTracking()
            .FirstAsync(m => m.RunId == runId, CancellationToken.None);

        Assert.Equal(7, metric.DayOffset);
        Assert.Equal(1_200, metric.Views);
        Assert.Equal(340 * 60, metric.WatchSeconds);
    }

    /// ***OTURMAMIŞ GÜN ÇEKİLMİYOR.***
    ///
    /// BU DOSYANIN EN ÖNEMLİ TESTİ. YouTube'un raporları iki güne
    /// kadar geriden geliyor: yedinci günün sayılarını yedinci gün
    /// çekmek, tamamlanmamış bir sayıyı tam sanmak demek. Sayı makul
    /// görünüyor, kimse şüphelenmiyor ve deney o eksik sayıyla karar
    /// veriyor.
    [Fact]
    public async Task OturmamisGun_Cekilmiyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();

        // 27 Ağustos'ta yayınlandı; 7. gün 3 Eylül — bugün 1 Eylül,
        // henüz gelmedi.
        await PublishedRunAsync(db, new DateTimeOffset(2026, 8, 27, 9, 0, 0, TimeSpan.Zero));

        var source = new StubSource(new DailyMetric(new DateOnly(2026, 9, 3), 999, 999, 0, 0, 0));
        var summary = await Collector(db, source).CollectAsync(CancellationToken.None);

        Assert.Equal(1, summary.Value.NotSettled);
        Assert.Equal(0, summary.Value.Collected);

        // VE API'YE HİÇ GİDİLMEDİ: boşuna kota harcamak, oturmamış bir
        // günü sormanın ikinci bedeli olurdu.
        Assert.Equal(0, source.Calls);
    }

    /// ***VERİ YOKSA SIFIR YAZILMIYOR.***
    ///
    /// "O gün hiç izlenme yok" ile "o günün verisi gelmedi" farklı iki
    /// şey. Sıfır yazmak, gelmemiş bir günü ölçülmüş saymak ve bütün
    /// ortalamaları aşağı çekmek olurdu — deney de o sıfırla karar
    /// verirdi.
    [Fact]
    public async Task VeriYok_SifirYazilmiyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();
        await PublishedRunAsync(db, new DateTimeOffset(2026, 8, 20, 9, 0, 0, TimeSpan.Zero));

        var summary = await Collector(db, new StubSource(null)).CollectAsync(CancellationToken.None);

        Assert.Equal(1, summary.Value.NoData);
        Assert.Empty(await db.PublicationMetrics.ToListAsync(CancellationToken.None));
    }

    /// AYNI GÜN İKİNCİ KEZ ÇEKİLMİYOR.
    ///
    /// Veritabanı kısıtı zaten engelliyor ama sorgulamadan yazmak, her
    /// turda bir hata üretip logu doldururdu.
    [Fact]
    public async Task AyniGun_IkinciKezCekilmiyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();
        var runId = await PublishedRunAsync(db, new DateTimeOffset(2026, 8, 20, 9, 0, 0, TimeSpan.Zero));

        db.PublicationMetrics.Add(new PublicationMetric
        {
            RunId = runId, DayOffset = 7, Views = 500, WatchSeconds = 1_000,
        });

        await db.SaveChangesAsync(CancellationToken.None);

        var source = new StubSource(new DailyMetric(new DateOnly(2026, 8, 27), 9_999, 9_999, 0, 0, 0));
        var summary = await Collector(db, source).CollectAsync(CancellationToken.None);

        Assert.Equal(1, summary.Value.AlreadyHave);
        Assert.Equal(0, source.Calls);

        // ESKİ DEĞER KORUNUYOR: ikinci çekim üzerine yazsaydı, sabit
        // bir yaştan okuma iddiası (7. gün) bozulurdu.
        var metric = await db.PublicationMetrics.AsNoTracking()
            .FirstAsync(m => m.RunId == runId, CancellationToken.None);

        Assert.Equal(500, metric.Views);
    }

    /* ---- kaynak seçimi ---- */

    /// YOUTUBE OLMAYAN YAYIN ATLANIYOR.
    ///
    /// Analytics yalnızca YouTube'u biliyor; TikTok yayınını buraya
    /// karıştırmak, hiç çekilmemiş bir platformu "veri yok" diye
    /// raporlamak olurdu.
    [Fact]
    public void BaskaPlatform_Atlaniyor()
    {
        Assert.Null(MetricsCollector.Parse("""{"platform":"tiktok","external_id":"t-1"}"""));
        Assert.NotNull(MetricsCollector.Parse("""{"platform":"youtube","external_id":"v-1"}"""));
    }

    /// KİMLİKSİZ ÇIKTI ATLANIYOR.
    [Fact]
    public void KimliksizCikti_Atlaniyor()
    {
        Assert.Null(MetricsCollector.Parse("""{"platform":"youtube"}"""));
        Assert.Null(MetricsCollector.Parse("bozuk"));
    }

    /// ÖLÇÜM GÜNÜ DENEY DEĞERLENDİRMESİYLE AYNI.
    ///
    /// İki ayrı sayı olsaydı, çekilen gün ile okunan gün ayrışır ve
    /// deney "veri yok" derken tabloda veri dururdu.
    [Fact]
    public void OlcumGunu_DeneyleAyni()
        => Assert.Equal(ExperimentService.MetricDay, MetricsCollector.MetricDay);

    /* ---- yardımcılar ---- */

    private static async Task<Guid> PublishedRunAsync(StudioDbContext db, DateTimeOffset publishedAt)
    {
        var workflow = new Persistence.Entities.Workflow
        {
            Key = "olcum-" + Guid.NewGuid().ToString("N")[..8],
            Name = "ölçüm",
            CurrentVersion = 1,
        };

        var version = new WorkflowVersion
        {
            Workflow = workflow,
            Version = 1,
            GraphJson = """{"schema_version":1,"key":"k","name":"n","nodes":[],"edges":[]}""",
        };

        var run = new Run { WorkflowVersion = version, State = RunState.Completed };

        db.Workflows.Add(workflow);
        db.WorkflowVersions.Add(version);
        db.Runs.Add(run);
        await db.SaveChangesAsync(CancellationToken.None);

        db.NodeExecutions.Add(new NodeExecution
        {
            RunId = run.Id,
            NodeId = "yayin",
            NodeType = "publish.upload",
            Attempt = 1,
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            State = NodeState.Succeeded,
            OutputJson = JsonSerializer.Serialize(new
            {
                platform = "youtube",
                external_id = "v-" + Guid.NewGuid().ToString("N")[..6],
            }),
            FinishedAt = publishedAt,
        });

        await db.SaveChangesAsync(CancellationToken.None);

        return run.Id;
    }
}
