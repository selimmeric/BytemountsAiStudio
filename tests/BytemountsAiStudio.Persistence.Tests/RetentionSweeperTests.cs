using BytemountsAiStudio.Core.Assets;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Persistence.Entities;
using BytemountsAiStudio.Persistence.Storage;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Persistence.Tests;

/// Saklama süpürücüsü (P4-02).
///
/// ***BU DOSYANIN VAR OLMA SEBEBİ:*** `RetentionPolicy` yazılmış,
/// testlenmiş ve **hiçbir yerden çağrılmıyordu**. Dosyanın kendi
/// yorumunun tarif ettiği sorun aynen duruyordu: hiçbir ara varlık
/// silinmiyor, depo sınırsız büyüyor ve maliyet üretimle değil
/// **geçmişle** orantılı hâle geliyordu.
///
/// Kuralın kendi testleri yeşildi — kararı doğrudan çağırıyorlardı.
/// Buradakiler kararın gerçek satırlara UYGULANDIĞINI sınıyor.
[Collection(DatabaseCollection.Name)]
public sealed class RetentionSweeperTests(DatabaseFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
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
            "DELETE FROM assets; DELETE FROM node_executions; DELETE FROM runs");
    }

    private void RequireDatabase()
        => Assert.True(fixture.Available, $"PostgreSQL erişilemiyor ({fixture.UnavailableReason}).");

    /// Silinenleri sayan depo.
    ///
    /// Sahte depo yerine YEREL bir çift: `Persistence.Tests` sahte
    /// sağlayıcı projesine bağlı değil ve bu testler için bağlanması
    /// da gerekmiyor. Ayrıca silme HATASI yolu ancak elle
    /// başarısızlaştırılabilen bir çiftle sınanabiliyor.
    private sealed class DeletingStore : IStorageProvider
    {
        public string Key => "test-depo";

        public List<string> Deleted { get; } = [];

        public bool Fail { get; set; }

        public Task<Result> DeleteAsync(AssetRef assetRef, CancellationToken cancellationToken)
        {
            if (Fail)
            {
                return Task.FromResult(
                    Result.Failure(Core.Errors.Error.Transient("depo.dustu", "silinemedi")));
            }

            Deleted.Add(assetRef.Sha256);
            return Task.FromResult(Result.Success());
        }

        public Task<Result<StoredAsset>> PutAsync(
            Stream content, AssetMetadata metadata, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<Result<Stream>> OpenAsync(AssetRef assetRef, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<Result<string>> GetLocalPathAsync(
            AssetRef assetRef, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<Result<bool>> ExistsAsync(AssetRef assetRef, CancellationToken cancellationToken)
            => Task.FromResult(Result.Success(!Deleted.Contains(assetRef.Sha256)));
    }

    /// Deterministik sha256 — testler arasında çakışmasın diye
    /// tohumdan üretiliyor.
    private static string Hash(char seed) => new(seed, 64);

    private static Asset Row(char seed, AssetKind kind, DateTimeOffset createdAt, string? license = null)
        => new()
        {
            Sha256 = Hash(seed),
            Kind = kind.ToString(),
            MimeType = "image/png",
            StoragePath = $"as/{seed}.png",
            Bytes = 1024,
            LicenseJson = license,
            CreatedAt = createdAt,
        };

    private static RetentionSweeper Sweeper(StudioDbContext db, DeletingStore storage)
        => new(db, storage, new FixedTime(Now));

    /* ---- silme ---- */

    /// ***OTUZ GÜNDEN ESKİ ARA ÜRÜN SİLİNİYOR.***
    ///
    /// BU DOSYANIN EN ÖNEMLİ TESTİ. Süpürücü olmadan bu satır sonsuza
    /// kadar duruyordu.
    [Fact]
    public async Task EskiAraUrun_Siliniyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();
        var storage = new DeletingStore();

        db.Assets.Add(Row('a', AssetKind.Image, Now.AddDays(-40)));
        await db.SaveChangesAsync();

        var result = await Sweeper(db, storage).SweepAsync(CancellationToken.None);

        Assert.Equal(1, result.Deleted);
        Assert.Equal(0, result.Failed);
        Assert.Empty(await db.Assets.AsNoTracking().ToListAsync());
    }

    /// SINIR DIŞARIDA: tam otuz günlük varlık "otuz günden eski" DEĞİL.
    ///
    /// Kuralın adı ile davranışı ayrışırsa, silme kararında en kötü
    /// kural türü ortaya çıkar.
    [Fact]
    public async Task TamOtuzGun_Silinmiyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();
        var storage = new DeletingStore();

        db.Assets.Add(Row('b', AssetKind.Image, Now.AddDays(-30)));
        await db.SaveChangesAsync();

        var result = await Sweeper(db, storage).SweepAsync(CancellationToken.None);

        Assert.Equal(0, result.Deleted);
        Assert.Single(await db.Assets.AsNoTracking().ToListAsync());
    }

    /// ***LİSANS KAYDI OLAN VARLIK HİÇ SİLİNMİYOR.***
    ///
    /// Lisans kaydı hangi dosyaya ait olduğunu söylüyor; dosya gidince
    /// kayıt bir şeyi ispatlamıyor ve uyum kaydı, kanıtı olmayan bir
    /// beyana dönüşürdü (§2.3/14).
    [Fact]
    public async Task LisansliVarlik_Silinmiyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();
        var storage = new DeletingStore();

        db.Assets.Add(Row('c', AssetKind.Image, Now.AddDays(-100), """{"name":"CC BY 4.0"}"""));
        await db.SaveChangesAsync();

        var result = await Sweeper(db, storage).SweepAsync(CancellationToken.None);

        Assert.Equal(0, result.Deleted);
        Assert.Single(await db.Assets.AsNoTracking().ToListAsync());
    }

    /// ***YAYINLANMIŞ KOŞUNUN VARLIKLARI SİLİNMİYOR.***
    ///
    /// Platformdaki kopya bizim değil: kaldırılabiliyor, yeniden
    /// kodlanıyor ve indirilemiyor. Yeniden yüklemek gerektiğinde
    /// elimizde kalan tek şey bu varlıklar.
    [Fact]
    public async Task YayinlanmisKosununVarliklari_Silinmiyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();
        var storage = new DeletingStore();

        var run = new Run
        {
            WorkflowVersionId = Guid.CreateVersion7(),
            State = RunState.Completed,
            ContextJson = "{}",
        };

        db.Runs.Add(run);

        db.NodeExecutions.Add(new NodeExecution
        {
            RunId = run.Id,
            NodeId = "publish.upload",
            NodeType = "publish.upload",
            Attempt = 1,
            State = NodeState.Succeeded,
            IdempotencyKey = Guid.CreateVersion7().ToString("N"),
            OutputJson = $$"""{"url":"https://ornek/1","asset":"sha256:{{new string('d', 64)}}"}""",
        });

        db.Assets.Add(Row('d', AssetKind.Image, Now.AddDays(-100)));
        await db.SaveChangesAsync();

        var result = await Sweeper(db, storage).SweepAsync(CancellationToken.None);

        Assert.Equal(0, result.Deleted);
        Assert.Single(await db.Assets.AsNoTracking().ToListAsync());
    }

    /* ---- kuru koşu ---- */

    /// KURU KOŞU SAYIYOR AMA SİLMİYOR.
    [Fact]
    public async Task KuruKosu_Silmiyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();
        var storage = new DeletingStore();

        db.Assets.Add(Row('e', AssetKind.Image, Now.AddDays(-40)));
        await db.SaveChangesAsync();

        var result = await Sweeper(db, storage).SweepAsync(CancellationToken.None, dryRun: true);

        Assert.Equal(1, result.Deleted);
        Assert.Equal(1024, result.BytesFreed);
        Assert.Single(await db.Assets.AsNoTracking().ToListAsync());
    }

    /* ---- parti sınırı ---- */

    /// ***TEK TURDA PARTİ SINIRI KADAR SİLİNİYOR.***
    ///
    /// Kural aylardır uygulanmadıysa ilk koşu on binlerce varlık
    /// bulabilir; hepsini tek turda silmek saatlerce süren ve yarıda
    /// kesilebilen bir işlem demekti.
    [Fact]
    public async Task PartiSiniri_Uygulaniyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();
        var storage = new DeletingStore();

        foreach (var seed in "0123456789")
        {
            db.Assets.Add(Row(seed, AssetKind.Image, Now.AddDays(-40)));
        }

        await db.SaveChangesAsync();

        var result = await Sweeper(db, storage).SweepAsync(CancellationToken.None, batchSize: 3);

        Assert.Equal(3, result.Deleted);
        Assert.Equal(7, await db.Assets.AsNoTracking().CountAsync());
    }

    /* ---- silme hatası ---- */

    /// ***DEPODAN SİLİNEMEDİYSE SATIR DA SİLİNMİYOR.***
    ///
    /// Sırası kasıtlı: satır önce silinseydi ve depo düşseydi, dosya
    /// sonsuza kadar sahipsiz kalırdı — hiçbir kayıt onu göstermediği
    /// için bir daha da bulunamazdı. Ters sırada en kötü ihtimal, bir
    /// sonraki turda yeniden denenmesi.
    [Fact]
    public async Task DepoDustu_SatirDuruyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();
        var storage = new DeletingStore { Fail = true };

        db.Assets.Add(Row('f', AssetKind.Image, Now.AddDays(-40)));
        await db.SaveChangesAsync();

        var result = await Sweeper(db, storage).SweepAsync(CancellationToken.None);

        Assert.Equal(0, result.Deleted);
        Assert.Equal(1, result.Failed);
        Assert.Single(await db.Assets.AsNoTracking().ToListAsync());
    }

    /* ---- hash tarama ---- */

    /// ***REFERANS TARAMASI ALAN ADIYLA DEĞİL, METİNLE.***
    ///
    /// Node çıktılarında varlık referansı onlarca farklı alan adı
    /// altında duruyor. Bir alan adı listesi tutmak, yeni bir node
    /// eklendiğinde onun varlıklarının sessizce silinebilir sayılması
    /// demekti.
    [Fact]
    public void ReferansTaramasi_IcIceAlanlariBuluyor()
    {
        var full = new string('a', 64);

        var json = $$"""
            {"timeline_asset":"sha256:{{full}}",
             "images":[{"asset":"sha256:{{new string('b', 64)}}"}],
             "kisa":"sha256:abc"}
            """;

        var hashes = RetentionSweeper.AssetHashes(json).ToList();

        Assert.Equal(2, hashes.Count);
        Assert.Contains(full, hashes, StringComparer.Ordinal);

        // KISA EŞLEŞME SAYILMIYOR: 64 haneden kısa bir dizge varlık
        // referansı değil ve onu saymak, alakasız bir varlığı
        // "yayınlanmış" sayarak sonsuza kadar saklamak olurdu.
        Assert.DoesNotContain("abc", hashes, StringComparer.Ordinal);
    }
}
