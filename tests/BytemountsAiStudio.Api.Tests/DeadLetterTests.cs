using BytemountsAiStudio.Api;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Persistence.Entities;
using BytemountsAiStudio.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Api.Tests;

/// Ölü mektup kuyruğu sorgusunun testleri (P1-29).
[Collection(DatabaseCollection.Name)]
public sealed class DeadLetterTests(DatabaseFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (!fixture.Available)
        {
            return;
        }

        await using var db = fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM jobs");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private void RequireDatabase()
        => Assert.True(fixture.Available, $"PostgreSQL erişilemiyor ({fixture.UnavailableReason}).");

    /// YALNIZCA düşen işler. Bekleyen ve tamamlanan işleri de
    /// göstermek, "kalıcı olarak ne düştü" sorusunu binlerce normal
    /// işin arasına gömerdi.
    [Fact]
    public async Task YalnizcaDusenIsler_Listeleniyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        db.Jobs.AddRange(
            new Job { Queue = QueueClass.Render, State = JobState.DeadLettered, LastError = "ffmpeg düştü" },
            new Job { Queue = QueueClass.Llm, State = JobState.Pending },
            new Job { Queue = QueueClass.Tts, State = JobState.Succeeded });

        await db.SaveChangesAsync(CancellationToken.None);

        var dead = await RunQueries.DeadLettersAsync(db, 50, CancellationToken.None);

        var single = Assert.Single(dead);
        Assert.Equal("Render", single.Queue);
        Assert.Equal("ffmpeg düştü", single.LastError);
    }

    /// EN YENİ ÖNCE: DLQ'ya bakmanın sebebi neredeyse her zaman "az
    /// önce ne düştü". Onay kuyruğunun tersi.
    [Fact]
    public async Task EnYeniOnce()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var older = new Job
        {
            Queue = QueueClass.Llm, State = JobState.DeadLettered, LastError = "eski",
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-2),
        };

        var newer = new Job
        {
            Queue = QueueClass.Llm, State = JobState.DeadLettered, LastError = "yeni",
            CreatedAt = DateTimeOffset.UtcNow,
        };

        db.Jobs.AddRange(older, newer);
        await db.SaveChangesAsync(CancellationToken.None);

        var dead = await RunQueries.DeadLettersAsync(db, 50, CancellationToken.None);

        Assert.Equal("yeni", dead[0].LastError);
        Assert.Equal("eski", dead[1].LastError);
    }

    [Fact]
    public async Task HicDusenYoksa_BosListe()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        Assert.Empty(await RunQueries.DeadLettersAsync(db, 50, CancellationToken.None));
    }
}
