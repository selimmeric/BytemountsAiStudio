using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Persistence;
using BytemountsAiStudio.Persistence.Entities;
using BytemountsAiStudio.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Api.Tests;

/// Gece raporu (P2-13).
///
/// FAZ 2'NİN KABUL KRİTERİ BURADA ÖLÇÜLÜYOR: "bir gecede 3–5 video
/// insan müdahalesi olmadan hazır". İddianın sayıya dönüştüğü yer bu;
/// koşuları tek tek açıp saymak, cevabı her sabah elle üretmekti.
[Collection(DatabaseCollection.Name)]
public sealed class MorningReportTests(DatabaseFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (!fixture.Available)
        {
            return;
        }

        await using var db = fixture.CreateContext();

        await db.Database.ExecuteSqlRawAsync("DELETE FROM approvals");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM provider_calls");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM node_executions");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM run_events");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM jobs");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM runs");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private void RequireDatabase()
        => Assert.True(fixture.Available, $"PostgreSQL erişilemiyor ({fixture.UnavailableReason}).");

    private static async Task<Guid> RunAsync(
        StudioDbContext db, RunState state, int minutesAgo = 60, int retryLoop = 0, string? error = null)
    {
        var workflow = new Persistence.Entities.Workflow
        {
            Key = "rapor-" + Guid.NewGuid().ToString("N")[..8],
            Name = "Rapor testi",
            CurrentVersion = 1,
        };

        var version = new WorkflowVersion { Version = 1, GraphJson = """{"key":"t","name":"t","nodes":[],"edges":[]}""" };
        workflow.Versions.Add(version);
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync(CancellationToken.None);

        var created = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo);

        var run = new Run
        {
            WorkflowVersionId = version.Id,
            State = state,
            CreatedAt = created,
            StartedAt = created,
            FinishedAt = state is RunState.Completed or RunState.Failed
                ? created.AddMinutes(10)
                : null,
            RetryLoop = retryLoop,
            ErrorJson = error,
        };

        db.Runs.Add(run);
        await db.SaveChangesAsync(CancellationToken.None);

        return run.Id;
    }

    private static async Task ScoreAsync(StudioDbContext db, Guid runId, double score, int loop = 0)
    {
        db.NodeExecutions.Add(new NodeExecution
        {
            RunId = runId,
            NodeId = "qc",
            NodeType = "qc.mechanical",
            State = NodeState.Succeeded,
            Loop = loop,
            Attempt = 1,
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            OutputJson = "{\"score\":"
                         + score.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-20),
        });

        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// KABUL KRİTERİ: üç insan müdahalesiz video yeterli.
    [Fact]
    public async Task UcTamamlanmisVideo_KriterSaglandi()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        for (var i = 0; i < 3; i++)
        {
            await RunAsync(db, RunState.Completed);
        }

        var report = await MorningReport.BuildAsync(
            db, MorningReport.DefaultWindow, CancellationToken.None);

        Assert.Equal(3, report.UnattendedVideos);
        Assert.True(report.AcceptanceMet);
    }

    /// ONAY BEKLEYEN VİDEO SAYILMIYOR.
    ///
    /// "5 video üretildi" ile "5 video üretildi ama 4'ü onay bekliyor"
    /// tamamen farklı sonuçlar. İkincisi otonomi değil ve ikisini aynı
    /// sayıya sıkıştırmak, kriterin sağlandığı izlenimi verirdi.
    [Fact]
    public async Task OnayBekleyenler_KriterdenSayilmiyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        await RunAsync(db, RunState.Completed);

        for (var i = 0; i < 4; i++)
        {
            await RunAsync(db, RunState.WaitingApproval);
        }

        var report = await MorningReport.BuildAsync(
            db, MorningReport.DefaultWindow, CancellationToken.None);

        Assert.Equal(5, report.Runs);
        Assert.Equal(1, report.UnattendedVideos);
        Assert.Equal(4, report.WaitingApproval);
        Assert.False(report.AcceptanceMet);
    }

    /// PENCERE ÖNCESİ KOŞULAR GİRMİYOR: "gece ne oldu" sorusunun
    /// cevabına dünkü gündüzü karıştırmak, soruyu bulanıklaştırırdı.
    [Fact]
    public async Task EskiKosular_PencereDisindaKaliyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        await RunAsync(db, RunState.Completed, minutesAgo: 60);
        await RunAsync(db, RunState.Completed, minutesAgo: 60 * 30);

        var report = await MorningReport.BuildAsync(
            db, TimeSpan.FromHours(12), CancellationToken.None);

        Assert.Equal(1, report.Runs);
    }

    /// AÇIK ONAYLAR PENCEREDEN BAĞIMSIZ: dün geceden kalmış bir onay
    /// bugünün penceresine girmiyor ama hâlâ insanın işi. Yalnızca
    /// pencere içindekileri saymak, birikmiş kuyruğu görünmez kılardı.
    [Fact]
    public async Task EskiAcikOnay_YineDeSayiliyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var runId = await RunAsync(db, RunState.WaitingApproval, minutesAgo: 60 * 40);

        db.Approvals.Add(new Approval
        {
            RunId = runId,
            NodeId = "onay",
            Reason = "eski",
            State = ApprovalState.Pending,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-2),
        });

        await db.SaveChangesAsync(CancellationToken.None);

        var report = await MorningReport.BuildAsync(
            db, TimeSpan.FromHours(12), CancellationToken.None);

        Assert.Equal(0, report.Runs);
        Assert.Equal(1, report.PendingApprovals);
    }

    /// MALİYET VE VİDEO BAŞINA MALİYET ÖLÇÜLÜYOR.
    [Fact]
    public async Task Maliyet_VideoBasinaHesaplaniyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        await RunAsync(db, RunState.Completed);
        await RunAsync(db, RunState.Completed);

        for (var i = 0; i < 2; i++)
        {
            db.ProviderCalls.Add(new ProviderCall
            {
                ProviderKey = "test",
                Operation = "test",
                Cost = 0.05m,
                Succeeded = true,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
            });
        }

        await db.SaveChangesAsync(CancellationToken.None);

        var report = await MorningReport.BuildAsync(
            db, MorningReport.DefaultWindow, CancellationToken.None);

        Assert.Equal(0.10m, report.Cost);
        Assert.Equal(0.05m, report.CostPerRun);
    }

    /// HİÇ KOŞU YOKSA SIFIRA BÖLÜNMÜYOR ve ortalamalar `null`.
    ///
    /// Sıfır göstermek "hepsi anında bitti, skor sıfır" gibi okunurdu;
    /// ölçüm yokluğu ile sıfır ölçüm farklı şeyler.
    [Fact]
    public async Task HicKosuYok_OrtalamalarBos()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var report = await MorningReport.BuildAsync(
            db, MorningReport.DefaultWindow, CancellationToken.None);

        Assert.Equal(0, report.Runs);
        Assert.Equal(0m, report.CostPerRun);
        Assert.Null(report.AverageMinutes);
        Assert.Null(report.AverageScore);
        Assert.False(report.AcceptanceMet);
    }

    /// QC SKORLARI `node_executions` çıktısından okunuyor.
    [Fact]
    public async Task QcSkorlari_CiktidanOkunuyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        await ScoreAsync(db, await RunAsync(db, RunState.Completed), 0.8);
        await ScoreAsync(db, await RunAsync(db, RunState.Completed), 0.6);

        var report = await MorningReport.BuildAsync(
            db, MorningReport.DefaultWindow, CancellationToken.None);

        Assert.Equal(2, report.ScoredRuns);
        Assert.Equal(0.7, report.AverageScore);
    }

    /// RUN BAŞINA **SON** SKOR SAYILIYOR, hepsi değil.
    ///
    /// Hedefli retry bir videoyu birden çok tura sokuyor ve her turda
    /// QC yeniden koşuyor. Hepsini ortalamaya katmak, düzelme ÖNCESİ
    /// skorları da gecenin kalitesine yazmak olurdu: retry ne kadar iyi
    /// çalışırsa ortalama o kadar düşerdi — sistemin kendini
    /// düzeltmesi rapora bir kusur gibi yansırdı.
    ///
    /// Bu testi yazarken eşsizlik kısıtı (P2-07) beni yakaladı: aynı
    /// run'a iki QC kaydı yazmak tur numarası olmadan mümkün değil ve
    /// o kısıt, doğru soruyu sordurdu.
    [Fact]
    public async Task RetryTuru_YalnizcaSonSkorSayiliyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var runId = await RunAsync(db, RunState.Completed, retryLoop: 1);

        // İlk tur düştü (0,40), ikinci tur geçti (0,90).
        await ScoreAsync(db, runId, 0.40, loop: 0);
        await ScoreAsync(db, runId, 0.90, loop: 1);

        var report = await MorningReport.BuildAsync(
            db, MorningReport.DefaultWindow, CancellationToken.None);

        // Ortalama 0,65 DEĞİL: teslim edilen video 0,90.
        Assert.Equal(1, report.ScoredRuns);
        Assert.Equal(0.9, report.AverageScore);
        Assert.Equal(1, report.RetryLoops);
    }

    /// DÜŞME SEBEPLERİ GRUPLANIYOR: "3 koşu düştü" tek başına neyi
    /// düzelteceğini söylemiyor.
    [Fact]
    public async Task DusmeSebepleri_KodaGoreGrupleniyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        await RunAsync(db, RunState.Failed, error: """{"Code":"render.ffmpeg_failed","Message":"x"}""");
        await RunAsync(db, RunState.Failed, error: """{"Code":"render.ffmpeg_failed","Message":"y"}""");
        await RunAsync(db, RunState.Failed, error: """{"Code":"tts.no_voice","Message":"z"}""");

        var report = await MorningReport.BuildAsync(
            db, MorningReport.DefaultWindow, CancellationToken.None);

        Assert.Equal(3, report.Failed);
        Assert.Equal(2, report.Failures.Count);

        // En sık düşen sebep ÖNCE: sabah ilk bakılacak satır o.
        Assert.Equal("render.ffmpeg_failed", report.Failures[0].Code);
        Assert.Equal(2, report.Failures[0].Count);
    }

    /// Okunamayan bir çıktı ortalamayı bozmuyor: o kayıt sayılmıyor,
    /// diğerleri sayılmaya devam ediyor.
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("bozuk json", null)]
    [InlineData("""{"score":0.75}""", 0.75)]
    [InlineData("""{"score":"metin"}""", null)]
    [InlineData("{}", null)]
    public void SkorOkuma_Guvenli(string? json, double? expected)
        => Assert.Equal(expected, MorningReport.ScoreOf(json));

    [Theory]
    [InlineData(null, null)]
    [InlineData("bozuk", null)]
    [InlineData("""{"Code":"x.y"}""", "x.y")]
    [InlineData("""{"Message":"kod yok"}""", null)]
    public void HataKoduOkuma_Guvenli(string? json, string? expected)
        => Assert.Equal(expected, MorningReport.ErrorCodeOf(json));
}
