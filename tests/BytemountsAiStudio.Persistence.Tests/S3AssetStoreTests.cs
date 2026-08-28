using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core.Assets;
using BytemountsAiStudio.Persistence.Storage;
using BytemountsAiStudio.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Persistence.Tests;

/// S3 uyumlu nesne deposu (P4-02).
///
/// GERÇEK MINIO'YA KARŞI KOŞUYOR, sahte bir S3 istemcisine karşı
/// değil. Sahte istemci imzaları doğrular ama asıl sorular onda hiç
/// sorulmaz: nesne anahtarı gerçekten kabul ediliyor mu, olmayan bir
/// nesne 404 mü dönüyor, yükleme imzası geçiyor mu. Bu depoda bellek
/// içi sağlayıcıyla geçip gerçek sistemde düşen testlerin bedeli
/// birden fazla kez ödendi.
///
/// MinIO yoksa testler atlanmıyor, AÇIKÇA düşüyor — "S3 desteği var"
/// iddiası sınanmadan yeşil görünmemeli.
[Collection(DatabaseCollection.Name)]
public sealed class S3AssetStoreTests(DatabaseFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Endpoint = "http://127.0.0.1:9000";
    private const string AccessKey = "bmai";
    private const string SecretKey = "bmai_dev_secret";

    private AmazonS3Client? _client;
    private string _bucket = string.Empty;
    private string _cache = string.Empty;
    private bool _available;
    private string? _reason;

    public async Task InitializeAsync()
    {
        _bucket = "bmai-test-" + Guid.NewGuid().ToString("N")[..12];
        _cache = Path.Combine(Path.GetTempPath(), "bmai-s3-" + Guid.NewGuid().ToString("N")[..8]);

        _client = new AmazonS3Client(AccessKey, SecretKey, new AmazonS3Config
        {
            ServiceURL = Endpoint,

            // MINIO SANAL HOST ADRESLERİNİ KULLANMIYOR: varsayılan
            // `bucket.host` biçimi yerel bir kapta çözümlenmiyor ve
            // istekler bağlantı hatasıyla düşerdi.
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",

            // Uretimdeki istemciyle AYNI ayar (`StorageSelection`):
            // SDK v4'un varsayilan saglama toplami, S3 uyumlu
            // depolarin bir kisminda istekleri reddettiriyor.
            // Testin uretimden farkli kurulmasi, tam da bu depoda
            // tekrar eden hata sinifi.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        });

        try
        {
            await _client.ListBucketsAsync(CancellationToken.None);
            _available = true;
        }
        catch (AmazonS3Exception ex)
        {
            _reason = ex.Message;
        }
        catch (HttpRequestException ex)
        {
            _reason = ex.Message;
        }
    }

    public async Task DisposeAsync()
    {
        if (_available && _client is not null)
        {
            try
            {
                var objects = await _client.ListObjectsV2Async(
                    new ListObjectsV2Request { BucketName = _bucket }, CancellationToken.None);

                foreach (var entry in objects.S3Objects ?? [])
                {
                    await _client.DeleteObjectAsync(_bucket, entry.Key, CancellationToken.None);
                }

                await _client.DeleteBucketAsync(_bucket, CancellationToken.None);
            }
            catch (AmazonS3Exception)
            {
                // Temizlik başarısızlığı testi kırmızıya çevirmemeli.
            }
        }

        if (Directory.Exists(_cache))
        {
            Directory.Delete(_cache, recursive: true);
        }
    }

    private void RequireMinio()
    {
        Assert.True(fixture.Available, $"PostgreSQL erişilemiyor ({fixture.UnavailableReason}).");
        Assert.True(_available,
            $"MinIO erişilemiyor ({_reason}). `docker compose --profile storage up -d minio`");
    }

    private async Task<S3AssetStore> StoreAsync(StudioDbContext db)
    {
        var store = new S3AssetStore(_client!, db, _bucket, _cache);
        var ensured = await store.EnsureBucketAsync(CancellationToken.None);

        Assert.True(ensured.IsSuccess, ensured.IsFailure ? ensured.Error.Message : string.Empty);

        return store;
    }

    private static AssetMetadata Metadata(string mime = "image/png")
        => new() { Kind = AssetKind.Image, MimeType = mime };

    private static MemoryStream Content(string text) => new(Encoding.UTF8.GetBytes(text));

    public void Dispose() => _client?.Dispose();

    /// YAZ, SONRA OKU — deponun en temel sözü.
    [Fact]
    public async Task Yazilan_GeriOkunuyor()
    {
        RequireMinio();

        await using var db = fixture.CreateContext();
        var store = await StoreAsync(db);

        var stored = await store.PutAsync(Content("merhaba dunya"), Metadata(), CancellationToken.None);

        Assert.True(stored.IsSuccess, stored.IsFailure ? stored.Error.Message : string.Empty);

        await using var stream = (await store.OpenAsync(stored.Value.Ref, CancellationToken.None)).Value;
        using var reader = new StreamReader(stream);

        Assert.Equal("merhaba dunya", await reader.ReadToEndAsync(CancellationToken.None));
    }

    /// AYNI İÇERİK AYNI ADRES: depo içerik-adresli.
    ///
    /// İki sahne aynı görseli üretebiliyor; ikinci yazım yeni bir
    /// nesne oluşturmamalı, yoksa depo aynı baytları defalarca
    /// saklardı.
    [Fact]
    public async Task AyniIcerik_AyniAdresVeTekKayit()
    {
        RequireMinio();

        await using var db = fixture.CreateContext();
        var store = await StoreAsync(db);

        var first = await store.PutAsync(Content("tekrar eden icerik"), Metadata(), CancellationToken.None);
        var second = await store.PutAsync(Content("tekrar eden icerik"), Metadata(), CancellationToken.None);

        Assert.Equal(first.Value.Ref, second.Value.Ref);
        Assert.False(first.Value.AlreadyExisted);
        Assert.True(second.Value.AlreadyExisted);

        Assert.Equal(1, await db.Assets.CountAsync(
            a => a.Sha256 == first.Value.Ref.Sha256, CancellationToken.None));
    }

    /// YEREL YOL İNDİRİYOR — ADR-007'nin somutlaştığı yer.
    ///
    /// Render ağa çıkmıyor: uzak depodaki varlık render BAŞLAMADAN
    /// ÖNCE indiriliyor. Önbellek silinip tekrar istendiğinde dosya
    /// yeniden inmeli, yoksa "indirme" yalnızca yazma anındaki
    /// kopyayı bulmuş olurdu ve gerçek indirme hiç sınanmazdı.
    [Fact]
    public async Task YerelYol_OnbellekBossaIndiriyor()
    {
        RequireMinio();

        await using var db = fixture.CreateContext();
        var store = await StoreAsync(db);

        var stored = await store.PutAsync(Content("indirilecek icerik"), Metadata(), CancellationToken.None);

        // Yazma anında bırakılan önbellek kopyasını siliyoruz: bundan
        // sonrası gerçek indirme.
        var first = await store.GetLocalPathAsync(stored.Value.Ref, CancellationToken.None);
        File.Delete(first.Value);

        var second = await store.GetLocalPathAsync(stored.Value.Ref, CancellationToken.None);

        Assert.True(second.IsSuccess, second.IsFailure ? second.Error.Message : string.Empty);
        Assert.True(File.Exists(second.Value));
        Assert.Equal("indirilecek icerik", await File.ReadAllTextAsync(second.Value, CancellationToken.None));
    }

    /// OLMAYAN VARLIK KALICI HATA, GEÇİCİ DEĞİL.
    ///
    /// Ayrım kuyruğun davranışını değiştiriyor (ADR-011): geçici
    /// sayılsaydı sistem olmayan bir nesneyi üç kez ister, üç kez
    /// düşer ve sebep ölü mektup kutusunda genel bir "indirme
    /// başarısız" olurdu.
    [Fact]
    public async Task OlmayanVarlik_KaliciHata()
    {
        RequireMinio();

        await using var db = fixture.CreateContext();
        var store = await StoreAsync(db);

        var missing = AssetRef.Create(new string('a', 64));
        var result = await store.GetLocalPathAsync(missing, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("storage.not_found", result.Error.Code);
        Assert.Equal(Core.Errors.ErrorKind.Permanent, result.Error.Kind);
    }

    /// `ExistsAsync` NESNEYE BAKIYOR, KAYDA DEĞİL.
    ///
    /// İkisi ayrı sistemde ve ayrışabiliyorlar: retention kuralı
    /// nesneyi silmiş ama kayıt durabiliyor. Kayda bakmak, render
    /// sırasında bulunamayan bir varlığı "var" göstermekti.
    [Fact]
    public async Task Varlik_NesneyeBakiyor()
    {
        RequireMinio();

        await using var db = fixture.CreateContext();
        var store = await StoreAsync(db);

        var stored = await store.PutAsync(Content("silinecek"), Metadata(), CancellationToken.None);

        Assert.True((await store.ExistsAsync(stored.Value.Ref, CancellationToken.None)).Value);

        // Nesneyi arkadan siliyoruz; kayıt yerinde kalıyor.
        await _client!.DeleteObjectAsync(
            _bucket, S3AssetStore.KeyFor(stored.Value.Ref, "image/png"), CancellationToken.None);

        Assert.False((await store.ExistsAsync(stored.Value.Ref, CancellationToken.None)).Value);

        // Kayıt hâlâ duruyor: testin ölçtüğü fark tam olarak bu.
        Assert.True(await db.Assets.AnyAsync(
            a => a.Sha256 == stored.Value.Ref.Sha256, CancellationToken.None));
    }

    /// NESNE ANAHTARI DOSYA SİSTEMİ DÜZENİYLE AYNI.
    ///
    /// Tek bir önek altında milyonlarca nesne, konsolda listelenemez
    /// hâle geliyor.
    [Fact]
    public void NesneAnahtari_IkiliOnekTasiyor()
    {
        var assetRef = AssetRef.Create("abcdef" + new string('0', 58));
        var key = S3AssetStore.KeyFor(assetRef, "image/png");

        Assert.StartsWith("ab/cd/", key, StringComparison.Ordinal);
        Assert.EndsWith(".png", key, StringComparison.Ordinal);

        // TERS BÖLÜ YOK: Windows'ta üretilen yol S3 anahtarı olarak
        // kullanılsaydı `ab\cd\...` diye bir nesne oluşurdu ve Linux
        // worker onu hiç bulamazdı.
        Assert.DoesNotContain('\\', key);
    }
}
