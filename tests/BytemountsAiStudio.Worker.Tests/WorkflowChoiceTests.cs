using BytemountsAiStudio.Persistence;
using Microsoft.EntityFrameworkCore;
using WorkflowEntity = BytemountsAiStudio.Persistence.Entities.Workflow;
using WorkflowVersionEntity = BytemountsAiStudio.Persistence.Entities.WorkflowVersion;
using ChannelEntity = BytemountsAiStudio.Persistence.Entities.Channel;
using BytemountsAiStudio.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace BytemountsAiStudio.Worker.Tests;

/// Kanalın iş akışını kim seçiyor (P3-10).
///
/// FAZ 3'ÜN İDDİASI "iki kanal farklı iş akışlarıyla üretir" ve bu
/// iddianın kanala mı yoksa operatöre mi ait olduğu tek bir şeye
/// bakıyor: seçim AYARDAN mı geliyor, her çağrıda elle mi veriliyor.
///
/// Testler gerçek veritabanına karşı koşuyor çünkü sınanan şeyin
/// yarısı sorgunun kendisi — sıralama, filtre, sürüm eşleşmesi.
[Collection(DatabaseCollection.Name)]
public sealed class WorkflowChoiceTests(DatabaseFixture fixture) : IAsyncLifetime
{
    /// Testler koleksiyon fixture'ını paylaşıyor, yani aynı
    /// veritabanına yazıyorlar. Temizlik olmadan bir testin
    /// tanımladığı iş akışı sonrakinin "seçenek birden fazla"
    /// hatası oluyordu — sınanan kuralın kendisi testleri bozuyordu.
    public async Task InitializeAsync()
    {
        if (!fixture.Available)
        {
            return;
        }

        await using var db = fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM workflow_versions; DELETE FROM workflows; DELETE FROM channels");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private void RequireDatabase()
        => Assert.True(fixture.Available, $"PostgreSQL erişilemiyor ({fixture.UnavailableReason}).");

    private const string Graph = """{"nodes":[{"id":"a","type":"noop"}],"edges":[]}""";

    /// KANAL KENDİ İŞ AKIŞINI SEÇİYOR.
    ///
    /// İki kanal, aynı veritabanı, iki farklı graf — ve hiçbir yerde
    /// komut satırı yok. Faz 3'ün kabul kriteri tam olarak bu.
    [Fact]
    public async Task IkiKanal_AyarlarindanFarkliIsAkisiSeciyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();

        await SeedWorkflowAsync(db, "kisa");
        await SeedWorkflowAsync(db, "uzun");

        var shortChannel = Channel("Dikey", """{"workflow_key":"kisa"}""");
        var longChannel = Channel("Yatay", """{"workflow_key":"uzun"}""");

        db.Channels.AddRange(shortChannel, longChannel);
        await db.SaveChangesAsync(CancellationToken.None);

        var first = await ChooseAsync(db, shortChannel);
        var second = await ChooseAsync(db, longChannel);

        Assert.Equal("kisa", first.Key);
        Assert.Equal("uzun", second.Key);
        Assert.NotNull(first.VersionId);
        Assert.NotNull(second.VersionId);

        // Ve gerçekten FARKLI graflar: aynı sürüme işaret etselerdi
        // "iki farklı iş akışı" iddiası boş olurdu.
        Assert.NotEqual(first.VersionId, second.VersionId);
    }

    /// ANAHTAR YOKSA VE SEÇENEK BİRDEN FAZLAYSA TAHMİN EDİLMİYOR.
    ///
    /// GERÇEK HATA BURADAYDI: eski sorgu `ChannelId` üzerinden
    /// sıralayıp ilk satırı alıyordu. İki iş akışının ikisi de genel
    /// olduğu için sıralama onları AYIRMIYORDU ve "ilk" tamamen
    /// Postgres'in döndürme sırasıydı.
    ///
    /// Bugün `shorts-fake` geliyordu; tablo yeniden yazıldığında
    /// `video-uzun` gelebilirdi ve her dikey kanal sessizce dokuz
    /// dakikalık yatay video üretmeye başlardı. Hiçbir kayıt bunu
    /// söylemezdi: run başarıyla biterdi, video da "üretilmiş"
    /// olurdu.
    [Fact]
    public async Task AnahtarYokSecenekCok_TahminEtmiyorSebepSoyluyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();

        await SeedWorkflowAsync(db, "kisa");
        await SeedWorkflowAsync(db, "uzun");

        var channel = Channel("Anahtarsiz", "{}");
        db.Channels.Add(channel);
        await db.SaveChangesAsync(CancellationToken.None);

        var choice = await ChooseAsync(db, channel);

        Assert.Null(choice.VersionId);
        Assert.NotNull(choice.Problem);

        // Sebep hangi seçenekler olduğunu da söylüyor: "seçemedim"
        // tek başına ayarlara ne yazılacağını söylemiyor.
        Assert.Contains("kisa", choice.Problem, StringComparison.Ordinal);
        Assert.Contains("uzun", choice.Problem, StringComparison.Ordinal);
        Assert.Contains("workflow_key", choice.Problem, StringComparison.Ordinal);
    }

    /// Tek seçenek varsa belirsizlik de yok: seçiliyor.
    [Fact]
    public async Task AnahtarYokTekSecenek_Seciliyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();

        await SeedWorkflowAsync(db, "tek");

        var channel = Channel("Tek", "{}");
        db.Channels.Add(channel);
        await db.SaveChangesAsync(CancellationToken.None);

        var choice = await ChooseAsync(db, channel);

        Assert.Equal("tek", choice.Key);
        Assert.NotNull(choice.VersionId);
        Assert.Null(choice.Problem);
    }

    /// OLMAYAN ANAHTAR SESSİZ DÜŞMÜYOR, ADIYLA BİLDİRİLİYOR.
    ///
    /// Yazım hatası olan bir `workflow_key` en olası yapılandırma
    /// hatası ve "iş akışı bulunamadı" onu aramaya yardım etmiyor.
    [Fact]
    public async Task OlmayanAnahtar_AdiylaBildiriliyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();

        await SeedWorkflowAsync(db, "kisa");

        var channel = Channel("Yanlis", """{"workflow_key":"kisaa"}""");
        db.Channels.Add(channel);
        await db.SaveChangesAsync(CancellationToken.None);

        var choice = await ChooseAsync(db, channel);

        Assert.Null(choice.VersionId);
        Assert.Contains("kisaa", choice.Problem!, StringComparison.Ordinal);
    }

    /// BAŞKA KANALIN ÖZEL İŞ AKIŞI KULLANILAMIYOR.
    ///
    /// Kanal A kendisi için bir graf tanımladıysa, kanal B onu adıyla
    /// isteyerek kullanamamalı — yoksa "kanala özel" ifadesi bir
    /// sahiplik değil yalnızca bir etiket olurdu.
    [Fact]
    public async Task BaskaKanalinOzelIsAkisi_Kullanilamiyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();

        var owner = Channel("Sahibi", "{}");
        var other = Channel("Digeri", """{"workflow_key":"ozel"}""");

        db.Channels.AddRange(owner, other);
        await db.SaveChangesAsync(CancellationToken.None);

        await SeedWorkflowAsync(db, "ozel", owner.Id);

        // Sahibi kullanabiliyor.
        Assert.NotNull((await ChooseAsync(db, owner)).VersionId);

        // Diğeri adıyla istese de kullanamıyor.
        var choice = await ChooseAsync(db, other);

        Assert.Null(choice.VersionId);
        Assert.Contains("ozel", choice.Problem!, StringComparison.Ordinal);
    }

    /// ANAHTAR GENEL OLARAK TEKİL — şemanın kendisi söylüyor.
    ///
    /// Bu test bir davranış değil bir VARSAYIM pinliyor: seçim kodu
    /// "aynı anahtarla iki kayıt" durumunu ele almıyor, çünkü o durum
    /// veritabanına giremiyor. Kısıt kalkarsa bu test düşer ve seçim
    /// kodunun yeniden düşünülmesi gerektiğini söyler.
    ///
    /// Eski kod tam da o imkânsız durum için bir öncelik kuralı
    /// taşıyordu ve yorumu onu gerçekmiş gibi anlatıyordu.
    [Fact]
    public async Task AyniAnahtarliIkinciIsAkisi_VeritabaniReddediyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();

        var channel = Channel("Sahibi", "{}");
        db.Channels.Add(channel);
        await db.SaveChangesAsync(CancellationToken.None);

        await SeedWorkflowAsync(db, "ortak");

        await Assert.ThrowsAsync<DbUpdateException>(
            () => SeedWorkflowAsync(db, "ortak", channel.Id));
    }

    /// SÜRÜMÜ EKSİK İŞ AKIŞI "BULUNAMADI" DEĞİL, "TUTARSIZ".
    ///
    /// İkisi farklı yerlere baktırıyor: biri ayara, diğeri veriye.
    [Fact]
    public async Task GuncelSurumYok_KayitTutarsizDiyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();

        db.Workflows.Add(new WorkflowEntity
        {
            Key = "yarim",
            Name = "yarim",
            // Sürüm 3 deniyor ama ortada hiç sürüm kaydı yok.
            CurrentVersion = 3,
        });

        var channel = Channel("Yarim", """{"workflow_key":"yarim"}""");
        db.Channels.Add(channel);
        await db.SaveChangesAsync(CancellationToken.None);

        var choice = await ChooseAsync(db, channel);

        Assert.Null(choice.VersionId);
        Assert.Contains("v3", choice.Problem!, StringComparison.Ordinal);
    }

    /* ---- yardımcılar ---- */

    private static ChannelEntity Channel(string name, string settings)
        => new() { Name = name, Language = "tr-TR", SettingsJson = settings };

    private static async Task<Guid> SeedWorkflowAsync(
        StudioDbContext db, string key, Guid? channelId = null)
    {
        var workflow = new WorkflowEntity
        {
            Key = key,
            Name = key,
            ChannelId = channelId,
            CurrentVersion = 1,
        };

        var version = new WorkflowVersionEntity
        {
            Workflow = workflow,
            Version = 1,
            GraphJson = Graph,
        };

        db.Workflows.Add(workflow);
        db.WorkflowVersions.Add(version);
        await db.SaveChangesAsync(CancellationToken.None);

        return version.Id;
    }

    /// Servis sağlayıcısı yalnızca `DbContext` taşıyor: sınanan karar
    /// veritabanına bakıyor, başka hiçbir şeye değil.
    private static Task<OrchestratorService.WorkflowChoice> ChooseAsync(
        StudioDbContext db, ChannelEntity channel)
    {
        var services = new ServiceCollection();
        services.AddSingleton(db);

        return OrchestratorService.ResolveWorkflowAsync(
            services.BuildServiceProvider(), channel, CancellationToken.None);
    }
}
