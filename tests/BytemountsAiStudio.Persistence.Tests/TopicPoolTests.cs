using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Persistence.Entities;
using BytemountsAiStudio.Persistence.Providers;
using BytemountsAiStudio.TestSupport;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace BytemountsAiStudio.Persistence.Tests;

/// Konu havuzu ve pgvector tekillik testleri (P1-08, ADR-003, §20.5).
///
/// Kabul kriteri: "'En Tehlikeli 10 Yer' ile 'En Tehlikeli 10 Bölge'
/// aynı sayılıyor; TR/EN çifti sayılmıyor." İlki embedding benzerliğiyle,
/// ikincisi kapsam kuralıyla sağlanıyor.
[Collection(DatabaseCollection.Name)]
public sealed class TopicPoolTests(DatabaseFixture fixture) : IAsyncLifetime
{
    private const int Dimensions = 768;

    public async Task InitializeAsync()
    {
        if (!fixture.Available)
        {
            return;
        }

        await using var db = fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM topics");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private void RequireDatabase()
        => Assert.True(fixture.Available, $"PostgreSQL erişilemiyor ({fixture.UnavailableReason}).");

    /// Yön verilen bir birim vektör.
    ///
    /// `spread` küçüldükçe vektörler birbirine yaklaşıyor; böylece
    /// "çok benzer" ve "az benzer" durumları kontrollü üretilebiliyor.
    private static float[] Vector(double angle, double spread = 1.0)
    {
        var values = new float[Dimensions];

        values[0] = (float)Math.Cos(angle * spread);
        values[1] = (float)Math.Sin(angle * spread);

        for (var i = 2; i < Dimensions; i++)
        {
            values[i] = 0f;
        }

        return values;
    }

    private static TopicScore Good() => new()
    {
        Demand = 85,
        Fit = 85,
        Sourceability = 85,
        Visualizability = 85,
        Freshness = 85,
        Risk = 0,
    };

    private static async Task PublishAsync(
        StudioDbContext db, string title, string language, float[] embedding, Guid? channelId = null)
    {
        db.Topics.Add(new Topic
        {
            ChannelId = channelId,
            Title = title,
            Language = language,
            State = TopicState.Published,
            OverallScore = 80,
            Embedding = new Vector(embedding.AsMemory()),
        });

        await db.SaveChangesAsync(CancellationToken.None);
    }

    // ---- Tekillik ----

    /// ASIL KABUL KRİTERİ: anlamca aynı iki başlık tekrar sayılıyor.
    /// Dizge karşılaştırması bunu yakalayamazdı.
    [Fact]
    public async Task CokBenzerKonu_Reddedilir()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();
        var pool = new TopicPool(db);

        await PublishAsync(db, "Dünyanın En Tehlikeli 10 Yeri", "tr-TR", Vector(0.0));

        var decision = await pool.AdmitAsync(
            null, "tr-TR", "En Tehlikeli 10 Bölge", Good(), Vector(0.001), CancellationToken.None);

        Assert.True(decision.IsSuccess);
        Assert.Equal(TopicDecision.Reject, decision.Value);
    }

    [Fact]
    public async Task FarkliKonu_KabulEdilir()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();
        var pool = new TopicPool(db);

        await PublishAsync(db, "Dünyanın En Tehlikeli 10 Yeri", "tr-TR", Vector(0.0));

        var decision = await pool.AdmitAsync(
            null, "tr-TR", "Göbeklitepe", Good(), Vector(Math.PI / 2), CancellationToken.None);

        Assert.Equal(TopicDecision.Accept, decision.Value);
    }

    /// §20.5: TR kanalında yayınlanan bir konu, EN kanalında tekrar
    /// DEĞİL — farklı izleyici. Kapsamı daraltmak çok dilli üretimi
    /// ilk günden imkânsız kılardı.
    [Fact]
    public async Task AyniKonuFarkliDil_TekrarSayilmaz()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();
        var pool = new TopicPool(db);

        await PublishAsync(db, "Dünyanın En Tehlikeli 10 Yeri", "tr-TR", Vector(0.0));

        var decision = await pool.AdmitAsync(
            null, "en-US", "10 Most Dangerous Places", Good(), Vector(0.0), CancellationToken.None);

        Assert.Equal(TopicDecision.Accept, decision.Value);
    }

    /// Aynı dilde ama BAŞKA kanalda yayınlanmış bir konu da tekrar
    /// sayılmıyor: kapsam kanal + dil.
    [Fact]
    public async Task AyniKonuFarkliKanal_TekrarSayilmaz()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var channel = new Channel { Name = $"kanal-{Guid.NewGuid():N}", Language = "tr-TR" };
        db.Channels.Add(channel);
        await db.SaveChangesAsync(CancellationToken.None);

        await PublishAsync(db, "Aynı Konu", "tr-TR", Vector(0.0), channel.Id);

        var decision = await new TopicPool(db).AdmitAsync(
            null, "tr-TR", "Aynı Konu", Good(), Vector(0.0), CancellationToken.None);

        Assert.Equal(TopicDecision.Accept, decision.Value);
    }

    /// Yalnızca YAYINLANMIŞ konular tekrar engeli: reddedilmiş bir konu
    /// zaten yayınlanmadı, engel olmamalı.
    [Fact]
    public async Task ReddedilmisKonu_TekrarEngeliDegil()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        db.Topics.Add(new Topic
        {
            Title = "Reddedilmiş",
            Language = "tr-TR",
            State = TopicState.Rejected,
            Embedding = new Vector(Vector(0.0).AsMemory()),
        });

        await db.SaveChangesAsync(CancellationToken.None);

        var decision = await new TopicPool(db).AdmitAsync(
            null, "tr-TR", "Benzer Konu", Good(), Vector(0.0), CancellationToken.None);

        Assert.Equal(TopicDecision.Accept, decision.Value);
    }

    /// Gömme yoksa tekillik kontrolü YAPILAMAZ. Boş liste dönmek
    /// "benzer yok" demek olurdu ve bu yanlış bir güvence.
    [Fact]
    public async Task GommeYok_AcikHataVerir()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var result = await new TopicPool(db).SimilarPublishedAsync(
            null, "tr-TR", [], CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("topic.no_embedding", result.Error.Code);
    }

    // ---- Karar ve kayıt ----

    [Fact]
    public async Task KabulEdilenKonu_KuyrugaGirer()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        await new TopicPool(db).AdmitAsync(
            null, "tr-TR", "İyi Konu", Good(), Vector(0.0), CancellationToken.None);

        await using var fresh = fixture.CreateContext();
        var topic = await fresh.Topics.AsNoTracking()
            .SingleAsync(t => t.Title == "İyi Konu", CancellationToken.None);

        Assert.Equal(TopicState.Queued, topic.State);
    }

    [Fact]
    public async Task DusukSkorluKonu_Reddedilir()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var weak = new TopicScore
        {
            Demand = 10, Fit = 10, Sourceability = 10,
            Visualizability = 10, Freshness = 10, Risk = 0,
        };

        var decision = await new TopicPool(db).AdmitAsync(
            null, "tr-TR", "Zayıf Konu", weak, null, CancellationToken.None);

        Assert.Equal(TopicDecision.Reject, decision.Value);
    }

    /// "Skor düşük" yetmez: risk vetosu mu, tekrar mı, yoksa gerçekten
    /// düşük skor mu? Üçü farklı düzeltme gerektiriyor.
    [Fact]
    public async Task RedGerekcesi_HangiKuralinDevreyeGirdiginiSoyler()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var risky = Good() with { Risk = 90 };

        await new TopicPool(db).AdmitAsync(
            null, "tr-TR", "Riskli Konu", risky, null, CancellationToken.None);

        await using var fresh = fixture.CreateContext();
        var topic = await fresh.Topics.AsNoTracking()
            .SingleAsync(t => t.Title == "Riskli Konu", CancellationToken.None);

        Assert.Contains("Risk skoru", topic.RejectedReason!, StringComparison.Ordinal);
    }

    /// Bir konu tekrar diye reddedildiğinde "neye benzedi" sorusu
    /// sorulacak; benzerlik skorla birlikte saklanıyor.
    [Fact]
    public async Task Benzerlik_SkorlaBirlikteSaklanir()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();
        var pool = new TopicPool(db);

        await PublishAsync(db, "Önceki", "tr-TR", Vector(0.0));

        await pool.AdmitAsync(null, "tr-TR", "Tekrar", Good(), Vector(0.001), CancellationToken.None);

        await using var fresh = fixture.CreateContext();
        var topic = await fresh.Topics.AsNoTracking()
            .SingleAsync(t => t.Title == "Tekrar", CancellationToken.None);

        Assert.Contains("similarity", topic.ScoresJson, StringComparison.Ordinal);
    }

    // ---- Kuyruktan alma ----

    [Fact]
    public async Task SiradakiKonu_EnYuksekSkorlu()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();
        var pool = new TopicPool(db);

        await pool.AdmitAsync(null, "tr-TR", "Orta", Good() with { Demand = 70 }, null, CancellationToken.None);
        await pool.AdmitAsync(null, "tr-TR", "Yüksek", Good() with { Demand = 100 }, null, CancellationToken.None);

        await using var fresh = fixture.CreateContext();
        var taken = await new TopicPool(fresh).TakeNextAsync(null, "tr-TR", CancellationToken.None);

        Assert.True(taken.IsSuccess, taken.IsFailure ? taken.Error.Message : string.Empty);
        Assert.Equal("Yüksek", taken.Value.Title);
        Assert.Equal(TopicState.InProgress, taken.Value.State);
    }

    /// Konu YOKLUĞU bir hata değil, bir DURUM: havuz boşsa üretim
    /// beklemeli, düşmemeli (ADR-011).
    [Fact]
    public async Task HavuzBos_KaynakHatasiDoner()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var taken = await new TopicPool(db).TakeNextAsync(null, "tr-TR", CancellationToken.None);

        Assert.True(taken.IsFailure);
        Assert.Equal(Core.Errors.ErrorKind.Resource, taken.Error.Kind);
        Assert.NotNull(taken.Error.RetryAfter);
    }

    /// Alınan konu bir daha alınmamalı: iki worker aynı videoyu iki kez
    /// üretmesin.
    [Fact]
    public async Task AlinanKonu_BirDahaAlinmaz()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();
        var pool = new TopicPool(db);

        await pool.AdmitAsync(null, "tr-TR", "Tek Konu", Good(), null, CancellationToken.None);

        await using var first = fixture.CreateContext();
        var taken = await new TopicPool(first).TakeNextAsync(null, "tr-TR", CancellationToken.None);

        await using var second = fixture.CreateContext();
        var again = await new TopicPool(second).TakeNextAsync(null, "tr-TR", CancellationToken.None);

        Assert.True(taken.IsSuccess);
        Assert.True(again.IsFailure);
    }

    [Fact]
    public async Task KuyruktakiSayi_Sayilir()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();
        var pool = new TopicPool(db);

        await pool.AdmitAsync(null, "tr-TR", "Bir", Good(), null, CancellationToken.None);
        await pool.AdmitAsync(null, "tr-TR", "İki", Good(), null, CancellationToken.None);
        await pool.AdmitAsync(null, "en-US", "Three", Good(), null, CancellationToken.None);

        await using var fresh = fixture.CreateContext();

        Assert.Equal(2, await new TopicPool(fresh).QueuedCountAsync(null, "tr-TR", CancellationToken.None));
        Assert.Equal(1, await new TopicPool(fresh).QueuedCountAsync(null, "en-US", CancellationToken.None));
    }
}
