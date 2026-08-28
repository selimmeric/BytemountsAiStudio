using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Assets;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Persistence.Storage;

/// İçerik-adresli varlık deposu — S3 uyumlu nesne deposu üzerinde
/// (P4-02).
///
/// `FileSystemAssetStore` ile AYNI SÖZLEŞME: iki taraf var, nesne
/// deposu (baytlar) ve `assets` tablosu (metadata, lisans). İkisi de
/// sha256 ile adreslenir ve yazma sırası aynı — ÖNCE nesne, SONRA
/// kayıt. Ters olsaydı "kayıt var, nesne yok" durumu oluşur ve render
/// "varlık var" deyip patlardı. Bu sırayla en kötü durum yetim
/// nesnedir: zararsız ve temizlenebilir.
///
/// MINIO, R2 VE S3 AYNI KOD: üçü de aynı API'yi konuşuyor. Geliştirme
/// MinIO'ya karşı yapılıyor — ücretsiz, yerel ve her testte para
/// harcamıyor.
public sealed class S3AssetStore : IStorageProvider
{
    /// Uzaktan indirilen varlıkların yerel kopyası.
    ///
    /// İÇERİK-ADRESLİ OLDUĞU İÇİN ÖNBELLEK GEÇERSİZLEŞMİYOR: sha256
    /// içeriğin kendisi, yani aynı ad her zaman aynı baytlar. "Bu
    /// kopya bayat mı" sorusu hiç sorulmuyor.
    private readonly string _cacheRoot;

    private readonly IAmazonS3 _client;
    private readonly StudioDbContext _db;
    private readonly string _bucket;

    /// Veritabanı erişimi sıraya sokuluyor — `FileSystemAssetStore`
    /// ile aynı sebep: `DbContext` iş parçacığı güvenli değil ve
    /// görsel node'u sahneleri paralel üretip her biri bittiğinde
    /// buraya yazıyor.
    ///
    /// Kilit YALNIZCA veritabanı bölümünde: yükleme ve hash paralel
    /// kalıyor, işin pahalı kısmı orası.
    private static readonly SemaphoreSlim DatabaseGate = new(1, 1);

    public S3AssetStore(IAmazonS3 client, StudioDbContext db, string bucket, string cacheRoot)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);

        _client = client;
        _db = db;
        _bucket = bucket;
        _cacheRoot = cacheRoot;
    }

    public string Key => "s3";

    public string Bucket => _bucket;

    /// Nesne anahtarı — dosya sistemi düzeniyle AYNI.
    ///
    /// `ab/cd/abcd...` biçimi S3'te de değerli: tek bir önek altında
    /// milyonlarca nesne, konsolda listelenemez hâle geliyor ve bazı
    /// uygulamalarda bölümleme (partition) sıcak noktası yaratıyor.
    public static string KeyFor(AssetRef assetRef, string? mimeType = null)
        => assetRef.RelativePath(ExtensionFor(mimeType)).Replace('\\', '/');

    public async Task<Result<StoredAsset>> PutAsync(
        Stream content, AssetMetadata metadata, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(metadata);

        // AKIŞ ÖNCE DİSKE, SONRA S3'E.
        //
        // S3'e yüklerken içerik uzunluğu gerekiyor ve sağlayıcıların
        // döndürdüğü ağ akışları ne seekable ne de uzunluğu bilinir.
        // Belleğe almak da seçenek değildi: bir video segmenti
        // yüzlerce megabayt olabiliyor ve paralel sahnelerde bu
        // çarpılıyor.
        //
        // Geçici dosya AYNI ZAMANDA önbellek kopyası oluyor — yüklenen
        // bir varlığı hemen ardından indirmek anlamsız.
        var temporary = Path.Combine(_cacheRoot, "_tmp",
            Guid.CreateVersion7().ToString("N", CultureInfo.InvariantCulture));

        Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);

        string sha;
        long bytes;

        try
        {
            await using (var file = File.Create(temporary))
            using (var hasher = SHA256.Create())
            await using (var hashing = new CryptoStream(file, hasher, CryptoStreamMode.Write))
            {
                await content.CopyToAsync(hashing, cancellationToken).ConfigureAwait(false);
                await hashing.FlushFinalBlockAsync(cancellationToken).ConfigureAwait(false);
                sha = Convert.ToHexStringLower(hasher.Hash!);
            }

            bytes = new FileInfo(temporary).Length;
        }
        catch (IOException ex)
        {
            SafeDelete(temporary);
            return Error.Transient("storage.write_failed", ex.Message);
        }

        var assetRef = AssetRef.Create(sha);
        var objectKey = KeyFor(assetRef, metadata.MimeType);

        Asset? existing;

        await DatabaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            existing = await _db.Assets
                .FirstOrDefaultAsync(a => a.Sha256 == sha, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            DatabaseGate.Release();
        }

        // KAYIT VARSA BİLE NESNE VARLIĞI SORULUYOR.
        //
        // İkisi ayrı sistemde ve ayrışabiliyorlar: retention kuralı
        // nesneyi silmiş ama kayıt durabiliyor. "Kayıt var" deyip
        // geçmek, render sırasında bulunamayan bir varlık demekti —
        // yani hatayı en pahalı ana ertelemek.
        if (existing is not null && await ObjectExistsAsync(objectKey, cancellationToken).ConfigureAwait(false))
        {
            MoveToCache(temporary, assetRef, metadata.MimeType);

            return new StoredAsset
            {
                Ref = assetRef,
                Bytes = existing.Bytes,
                Metadata = metadata,
                AlreadyExisted = true,
            };
        }

        try
        {
            await using var upload = File.OpenRead(temporary);

            await _client.PutObjectAsync(
                new PutObjectRequest
                {
                    BucketName = _bucket,
                    Key = objectKey,
                    InputStream = upload,
                    ContentType = metadata.MimeType,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex)
        {
            SafeDelete(temporary);

            // AĞ HATASI GEÇİCİ, YETKİ HATASI KALICI (ADR-011).
            //
            // İkisini aynı saymak, yanlış kimlik bilgisiyle
            // yapılandırılmış bir sistemin aynı işi üç kez deneyip üç
            // kez düşmesi ve sebebin ölü mektup kutusunda
            // "storage.upload_failed" olarak görünmesi demekti.
            return IsPermanent(ex)
                ? Error.Permanent("storage.upload_denied", ex.Message)
                : Error.Transient("storage.upload_failed", ex.Message);
        }
        catch (IOException ex)
        {
            SafeDelete(temporary);
            return Error.Transient("storage.upload_failed", ex.Message);
        }

        MoveToCache(temporary, assetRef, metadata.MimeType);

        if (existing is null)
        {
            await DatabaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                // İÇERİK-ADRESLİ DEPODA YARIŞ NORMAL: iki sahne aynı
                // görseli üretebiliyor (aynı sha) ve ikisi de "yok"
                // görüp eklemeye kalkabiliyor.
                if (!await _db.Assets.AnyAsync(a => a.Sha256 == sha, cancellationToken).ConfigureAwait(false))
                {
                    _db.Assets.Add(new Asset
                    {
                        Sha256 = sha,
                        Kind = metadata.Kind.ToString(),
                        MimeType = metadata.MimeType,
                        Bytes = bytes,
                        Width = metadata.Width,
                        Height = metadata.Height,
                        DurationMs = metadata.Duration?.Value,

                        // `StoragePath` NESNE ANAHTARINI TUTUYOR.
                        //
                        // Dosya sistemi deposunda göreli dosya yolu,
                        // burada kova içindeki anahtar — ikisi de
                        // "baytlar nerede" sorusunun cevabı ve aynı
                        // biçimde yazılıyor. Boş bırakmak, depo
                        // değiştiğinde varlığın nereden geldiğini
                        // kaybetmekti.
                        StoragePath = objectKey,
                        SourceProvider = metadata.SourceProvider,
                        SourceUrl = metadata.SourceUrl?.ToString(),
                        LicenseJson = metadata.License is null
                            ? null
                            : System.Text.Json.JsonSerializer.Serialize(metadata.License),
                    });

                    await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                DatabaseGate.Release();
            }
        }

        return new StoredAsset
        {
            Ref = assetRef,
            Bytes = bytes,
            Metadata = metadata,
            AlreadyExisted = existing is not null,
        };
    }

    public async Task<Result<Stream>> OpenAsync(AssetRef assetRef, CancellationToken cancellationToken)
    {
        var path = await GetLocalPathAsync(assetRef, cancellationToken).ConfigureAwait(false);

        return path.IsFailure
            ? Result.Failure<Stream>(path.Error)
            : Result.Success<Stream>(File.OpenRead(path.Value));
    }

    /// Varlığı YERELE indirir ve yolunu döndürür.
    ///
    /// ADR-007'NİN SOMUTLAŞTIĞI YER: render ağa çıkmaz. Uzak depodaki
    /// bir varlık render BAŞLAMADAN ÖNCE indiriliyor, render
    /// sırasında değil.
    ///
    /// Neden bu kadar önemli: ffmpeg bir HTTP adresini de açabilirdi
    /// ve çalışıyor gibi görünürdü. Ama o zaman on dakikalık bir
    /// render'ın ortasında kopan bir bağlantı yarım bir video
    /// bırakırdı ve o video QC'den geçebilirdi — süre doğru,
    /// çözünürlük doğru, son sahne eksik. İndirme ayrı bir adım
    /// olduğunda, başarısızlık ucuz ve görünür oluyor.
    public async Task<Result<string>> GetLocalPathAsync(
        AssetRef assetRef, CancellationToken cancellationToken)
    {
        var mimeType = await MimeTypeAsync(assetRef, cancellationToken).ConfigureAwait(false);
        var cached = CachePath(assetRef, mimeType);

        // ÖNBELLEK GEÇERSİZLEŞMİYOR: sha256 içeriğin kendisi, aynı ad
        // her zaman aynı baytlar demek.
        if (File.Exists(cached))
        {
            return cached;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(cached)!);

        var partial = cached + ".indiriliyor";

        try
        {
            using var response = await _client.GetObjectAsync(
                new GetObjectRequest { BucketName = _bucket, Key = KeyFor(assetRef, mimeType) },
                cancellationToken).ConfigureAwait(false);

            // ÖNCE GEÇİCİ AD, SONRA TAŞIMA. Yarım inen bir dosyayı
            // nihai adına yazmak, sonraki çağrının onu "önbellekte
            // var" sayması demekti — ve bozuk bir görsel, render'da
            // teşhisi çok zor bir hata.
            await using (var file = File.Create(partial))
            {
                await response.ResponseStream.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }

            File.Move(partial, cached, overwrite: true);

            return cached;
        }
        catch (AmazonS3Exception ex)
        {
            SafeDelete(partial);

            return ex.StatusCode == HttpStatusCode.NotFound
                // NESNE YOK: kalıcı. Aynı isteği tekrarlamak aynı
                // cevabı üretiyor ve varlık kendiliğinden geri
                // gelmiyor.
                ? Error.Permanent("storage.not_found", $"Varlık nesne deposunda yok: {assetRef}")
                : IsPermanent(ex)
                    ? Error.Permanent("storage.download_denied", ex.Message)
                    : Error.Transient("storage.download_failed", ex.Message);
        }
        catch (IOException ex)
        {
            SafeDelete(partial);
            return Error.Transient("storage.download_failed", ex.Message);
        }
    }

    public async Task<Result<bool>> ExistsAsync(AssetRef assetRef, CancellationToken cancellationToken)
    {
        var mimeType = await MimeTypeAsync(assetRef, cancellationToken).ConfigureAwait(false);

        return await ObjectExistsAsync(KeyFor(assetRef, mimeType), cancellationToken).ConfigureAwait(false);
    }

    /// Kovanın var olduğundan emin olur.
    ///
    /// Uygulama açılışında çağrılıyor: kova yoksa ilk varlık yazımı
    /// düşerdi ve sebep "NoSuchBucket" olurdu — teşhisi kolay ama
    /// önlenebilir bir başlangıç hatası.
    public async Task<Result> EnsureBucketAsync(CancellationToken cancellationToken)
    {
        try
        {
            var buckets = await _client.ListBucketsAsync(cancellationToken).ConfigureAwait(false);

            if (buckets.Buckets?.Any(b => string.Equals(b.BucketName, _bucket, StringComparison.Ordinal)) == true)
            {
                return Result.Success();
            }

            await _client.PutBucketAsync(new PutBucketRequest { BucketName = _bucket }, cancellationToken)
                .ConfigureAwait(false);

            return Result.Success();
        }
        catch (AmazonS3Exception ex)
        {
            return IsPermanent(ex)
                ? Error.Permanent("storage.bucket_denied", ex.Message)
                : Error.Transient("storage.bucket_failed", ex.Message);
        }
    }

    private async Task<bool> ObjectExistsAsync(string objectKey, CancellationToken cancellationToken)
    {
        try
        {
            await _client.GetObjectMetadataAsync(_bucket, objectKey, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    /// Uzantı için MIME türü `assets` tablosundan geliyor.
    ///
    /// Nesne anahtarı uzantı taşıyor ve uzantı MIME'dan türüyor; kayıt
    /// yoksa uzantısız anahtar deneniyor. Bu, kaydı olmayan bir
    /// varlığın da bulunabilmesini sağlıyor.
    private async Task<string?> MimeTypeAsync(AssetRef assetRef, CancellationToken cancellationToken)
    {
        await DatabaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await _db.Assets.AsNoTracking()
                .Where(a => a.Sha256 == assetRef.Sha256)
                .Select(a => a.MimeType)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            DatabaseGate.Release();
        }
    }

    private string CachePath(AssetRef assetRef, string? mimeType)
        => Path.Combine(_cacheRoot, assetRef.RelativePath(ExtensionFor(mimeType)));

    private void MoveToCache(string temporary, AssetRef assetRef, string? mimeType)
    {
        var cached = CachePath(assetRef, mimeType);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
            File.Move(temporary, cached, overwrite: true);
        }
        catch (IOException)
        {
            // ÖNBELLEĞE ALAMAMAK HATA DEĞİL: varlık nesne deposunda
            // duruyor ve gerektiğinde indirilebiliyor. Yüklemeyi
            // başarılı saymamak, sırf disk dolu diye üretimi
            // durdurmak olurdu.
            SafeDelete(temporary);
        }
    }

    /// Yetki ve yapılandırma hataları KALICI, gerisi geçici.
    ///
    /// Yanlış anahtarla yapılandırılmış bir sistem aynı işi üç kez
    /// deneyip üç kez düşerdi ve sebep ölü mektup kutusunda genel bir
    /// "yükleme başarısız" olarak görünürdü.
    private static bool IsPermanent(AmazonS3Exception ex)
        // OLMAYAN KOVA DA KALICI ve bu gerçek bir koşuda görüldü:
        // kova yokken her yükleme `storage.upload_failed` veriyordu,
        // kuyruk bunu geçici sayıp aynı işi üç kez deniyor ve üç kez
        // düşüyordu. Bir yapılandırma hatası için üç deneme, hem
        // zaman hem de yanlış bir teşhis — kova kendiliğinden geri
        // gelmiyor.
        => ex.ErrorCode is "NoSuchBucket"
            || ex.StatusCode is HttpStatusCode.Forbidden
                or HttpStatusCode.Unauthorized
                or HttpStatusCode.BadRequest;

    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Silinemeyen geçici dosya üretimi durdurmamalı.
        }
    }

    private static string ExtensionFor(string? mimeType) => mimeType switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "audio/wav" or "audio/x-wav" => ".wav",
        "audio/mpeg" => ".mp3",
        "video/mp4" => ".mp4",
        "application/json" => ".json",
        "text/plain" => ".txt",
        _ => ".bin",
    };
}
