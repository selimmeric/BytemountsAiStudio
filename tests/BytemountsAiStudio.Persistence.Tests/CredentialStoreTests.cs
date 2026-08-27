using BytemountsAiStudio.Core.Observability;
using BytemountsAiStudio.Persistence.Entities;
using BytemountsAiStudio.Persistence.Providers;
using BytemountsAiStudio.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Persistence.Tests;

/// Kimlik deposunun testleri (P1-01).
///
/// Burada asıl doğrulanan şey "kaydettiğimi geri okuyabiliyor muyum" değil —
/// o zaten kolay. Asıl mesele gizli değerin veritabanına DÜZ girmemesi ve
/// çözüm sırasının (kanal → genel → ortam) tam olarak bu sırada işlemesi.
[Collection(DatabaseCollection.Name)]
public sealed class CredentialStoreTests(DatabaseFixture fixture) : IAsyncLifetime
{
    private const string Secret = "sk-test-1234567890-GIZLI-DEGER";

    /// Her test kendi anahtar halkasını kullanıyor: makinedeki gerçek
    /// halkaya dokunmuyoruz ve testler birbirinin anahtarını okuyamıyor.
    private readonly DirectoryInfo _keyRing = new(Path.Combine(
        Path.GetTempPath(), $"bmai-keyring-{Guid.NewGuid():N}"));

    public async Task InitializeAsync()
    {
        SecretRedactor.Clear();

        if (!fixture.Available)
        {
            return;
        }

        await using var db = fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM credentials");
    }

    public Task DisposeAsync()
    {
        SecretRedactor.Clear();

        try
        {
            if (_keyRing.Exists)
            {
                _keyRing.Delete(recursive: true);
            }
        }
        catch (IOException)
        {
            // Geçici dizin silinemezse test sonucunu etkilemez.
        }

        return Task.CompletedTask;
    }

    private void RequireDatabase()
        => Assert.True(fixture.Available, $"PostgreSQL erişilemiyor ({fixture.UnavailableReason}).");

    private CredentialStore Create(StudioDbContext db, Func<string, string>? envName = null)
        => new(db, KeyRing.Create(_keyRing))
        {
            // Testler gerçek ortam değişkenlerine dokunmuyor: her test kendi
            // adını üretiyor, böylece makinede tanımlı bir OPENAI_API_KEY
            // testin sonucunu değiştiremiyor.
            EnvironmentVariableName = envName ?? (key => $"BMAI_TEST_YOK_{key.ToUpperInvariant()}"),
        };

    [Fact]
    public async Task KaydedilenAnahtar_GeriOkunur()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();
        var store = Create(db);

        Assert.True((await store.SetAsync("openai", null, Secret, CancellationToken.None)).IsSuccess);

        var read = await store.GetAsync("openai", null, CancellationToken.None);

        Assert.True(read.IsSuccess);
        Assert.Equal(Secret, read.Value);
    }

    /// P1-01'in asıl şartı: yedeği alan kişi anahtarları göremesin.
    [Fact]
    public async Task VeritabanindaDuzMetinYok()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        await Create(db).SetAsync("openai", null, Secret, CancellationToken.None);

        await using var fresh = fixture.CreateContext();
        var row = await fresh.Credentials.AsNoTracking()
            .SingleAsync(CancellationToken.None);

        Assert.DoesNotContain(Secret, row.CipherText, StringComparison.Ordinal);

        // Maskeli hâl saklanıyor ama gizli değeri ele vermiyor.
        Assert.Equal("***EGER", row.Masked);
    }

    [Fact]
    public async Task KanalaOzelKayit_GenelKaydiEzer()
    {
        // Sıra bilinçli (bkz. ICredentialStore): en dar kapsam kazanır,
        // yoksa bir kanalın kendi hesabı olamazdı.
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var channel = new Channel { Name = $"kanal-{Guid.NewGuid():N}", Language = "tr-TR" };
        db.Channels.Add(channel);
        await db.SaveChangesAsync(CancellationToken.None);

        var store = Create(db);
        await store.SetAsync("openai", null, "GENEL-ANAHTAR-123456", CancellationToken.None);
        await store.SetAsync("openai", channel.Id, "KANAL-ANAHTARI-123456", CancellationToken.None);

        var scoped = await store.GetAsync("openai", channel.Id, CancellationToken.None);
        var global = await store.GetAsync("openai", null, CancellationToken.None);

        Assert.Equal("KANAL-ANAHTARI-123456", scoped.Value);
        Assert.Equal("GENEL-ANAHTAR-123456", global.Value);
    }

    [Fact]
    public async Task KanalaOzelKayitYoksa_GenelKayitKullanilir()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var channel = new Channel { Name = $"kanal-{Guid.NewGuid():N}", Language = "tr-TR" };
        db.Channels.Add(channel);
        await db.SaveChangesAsync(CancellationToken.None);

        var store = Create(db);
        await store.SetAsync("openai", null, Secret, CancellationToken.None);

        var read = await store.GetAsync("openai", channel.Id, CancellationToken.None);

        Assert.Equal(Secret, read.Value);
    }

    [Fact]
    public async Task DepodaYoksa_OrtamDegiskenineDusulur()
    {
        RequireDatabase();
        var name = $"BMAI_TEST_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(name, Secret);

        try
        {
            await using var db = fixture.CreateContext();
            var store = Create(db, _ => name);

            var read = await store.GetAsync("openai", null, CancellationToken.None);

            Assert.True(read.IsSuccess);
            Assert.Equal(Secret, read.Value);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public async Task HicbirYerdeYoksa_KaliciHataVeNeYapilacagi()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var read = await Create(db).GetAsync("openai", null, CancellationToken.None);

        Assert.True(read.IsFailure);
        Assert.Equal("credential.missing", read.Error.Code);

        // Bu hatayı gören kişi genellikle anahtarı koymayı unutan kişi;
        // mesaj ne yapılacağını söylemeli.
        Assert.Contains("BMAI_TEST_YOK_OPENAI", read.Error.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AyniAnahtarIkinciKez_GuncellerCoklamaz()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();
        var store = Create(db);

        await store.SetAsync("openai", null, "ILK-ANAHTAR-1234567", CancellationToken.None);
        await store.SetAsync("openai", null, "IKINCI-ANAHTAR-1234", CancellationToken.None);

        await using var fresh = fixture.CreateContext();

        Assert.Equal(1, await fresh.Credentials.CountAsync(CancellationToken.None));
        Assert.Equal("IKINCI-ANAHTAR-1234",
            (await store.GetAsync("openai", null, CancellationToken.None)).Value);
    }

    /// Listeleme yolunda gizli değer HİÇ bulunmamalı — bu yol arayüze ve
    /// loglara çıkıyor.
    [Fact]
    public async Task Listeleme_GizliDegerDondurmez()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();
        var store = Create(db);

        await store.SetAsync("openai", null, Secret, CancellationToken.None);

        var list = await store.ListAsync(null, CancellationToken.None);
        var serialized = System.Text.Json.JsonSerializer.Serialize(list);

        Assert.Single(list);
        Assert.DoesNotContain(Secret, serialized, StringComparison.Ordinal);
        Assert.Equal("***EGER", list[0].Masked);
    }

    /// Anahtar okunduğu anda süzgece giriyor; sonrasında bir istisna
    /// mesajında geçse bile maskeleniyor.
    [Fact]
    public async Task OkunanAnahtar_LoglardanSuzulur()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();
        var store = Create(db);

        await store.SetAsync("openai", null, Secret, CancellationToken.None);
        await store.GetAsync("openai", null, CancellationToken.None);

        var line = SecretRedactor.Redact($"HTTP 401: Authorization: Bearer {Secret} reddedildi");

        Assert.DoesNotContain(Secret, line, StringComparison.Ordinal);
        Assert.Contains("***", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Silme_KaydiKaldirir()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();
        var store = Create(db);

        await store.SetAsync("openai", null, Secret, CancellationToken.None);

        Assert.True((await store.DeleteAsync("openai", null, CancellationToken.None)).IsSuccess);
        Assert.True((await store.GetAsync("openai", null, CancellationToken.None)).IsFailure);
        Assert.True((await store.DeleteAsync("openai", null, CancellationToken.None)).IsFailure);
    }

    [Fact]
    public async Task KullanimZamani_Isaretlenir()
    {
        // "Bu anahtar hiç devreye girdi mi" sorusunun cevabı.
        RequireDatabase();
        await using var db = fixture.CreateContext();
        var store = Create(db);

        await store.SetAsync("openai", null, Secret, CancellationToken.None);

        await using (var before = fixture.CreateContext())
        {
            Assert.Null((await before.Credentials.AsNoTracking()
                .SingleAsync(CancellationToken.None)).LastUsedAt);
        }

        await store.GetAsync("openai", null, CancellationToken.None);

        await using var after = fixture.CreateContext();

        Assert.NotNull((await after.Credentials.AsNoTracking()
            .SingleAsync(CancellationToken.None)).LastUsedAt);
    }
}
