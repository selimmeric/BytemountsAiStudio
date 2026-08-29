using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Assets;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Persistence.Storage;

/// İçerik-adresli varlık deposu — yerel disk üzerinde.
///
/// İki taraf var: dosya sistemi (baytlar) ve `assets` tablosu (metadata,
/// lisans, kullanım). İkisi de sha256 ile adreslenir.
///
/// Yazma sırası kasıtlı: ÖNCE dosya, SONRA kayıt. Ters olsaydı kayıt var
/// dosya yok durumu oluşurdu ve render "varlık var" deyip patlardı. Bu
/// sırayla en kötü durum yetim dosyadır — zararsız ve temizlenebilir.
///
/// Faz 4'te S3'e geçilecek; `IStorageProvider` arayüzü aynı kalır.
public sealed class FileSystemAssetStore(StudioDbContext db, string rootPath) : IStorageProvider
{
    public string Key => "filesystem";

    public string RootPath => rootPath;

    /// VERİTABANI ERİŞİMİ SIRAYA SOKULUYOR.
    ///
    /// `DbContext` iş parçacığı güvenli DEĞİL ve bu depo tek bir
    /// bağlamı paylaşıyor. Görsel node'u sahneleri PARALEL üretiyor
    /// (ikişer ikişer) ve her biri bittiğinde buraya yazıyor — yani
    /// aynı bağlam üzerinde iki eşzamanlı sorgu.
    ///
    /// Sonuç: "A second operation was started on this context
    /// instance" ve node'un düşmesi. Gerçek bir veritabanıyla ilk
    /// uçtan uca koşuda ortaya çıktı; testler sahte depo kullandığı
    /// için hiç görünmemişti.
    ///
    /// KİLİT YALNIZCA VERİTABANI BÖLÜMÜNDE. Dosyayı diske yazmak ve
    /// hash almak paralel kalıyor — işin pahalı kısmı orası ve onu da
    /// sıraya sokmak, paralel görsel üretiminin bütün kazancını
    /// silerdi.
    private static readonly SemaphoreSlim DatabaseGate = new(1, 1);

    public async Task<Result<StoredAsset>> PutAsync(
        Stream content,
        AssetMetadata metadata,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(metadata);

        // Akışı bir kez okuyup hem hash'liyoruz hem yazıyoruz. İki kez okumak
        // için akışın seekable olması gerekirdi; sağlayıcıların döndürdüğü
        // ağ akışları seekable değil.
        var temporaryPath = Path.Combine(rootPath, "_tmp", Guid.CreateVersion7().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(Path.GetDirectoryName(temporaryPath)!);

        string sha;
        long bytes;

        try
        {
            await using (var file = File.Create(temporaryPath))
            using (var hasher = SHA256.Create())
            await using (var hashing = new CryptoStream(file, hasher, CryptoStreamMode.Write))
            {
                await content.CopyToAsync(hashing, cancellationToken).ConfigureAwait(false);
                await hashing.FlushFinalBlockAsync(cancellationToken).ConfigureAwait(false);
                sha = Convert.ToHexStringLower(hasher.Hash!);
            }

            bytes = new FileInfo(temporaryPath).Length;
        }
        catch (IOException ex)
        {
            SafeDelete(temporaryPath);
            return Error.Transient("storage.write_failed", ex.Message);
        }

        var assetRef = AssetRef.Create(sha);
        var relativePath = assetRef.RelativePath(ExtensionFor(metadata.MimeType));
        var finalPath = Path.Combine(rootPath, relativePath);

        Asset? existing;

        await DatabaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            existing = await db.Assets
                .FirstOrDefaultAsync(a => a.Sha256 == sha, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            DatabaseGate.Release();
        }

        if (existing is not null && File.Exists(finalPath))
        {
            SafeDelete(temporaryPath);
            return new StoredAsset
            {
                Ref = assetRef,
                Bytes = existing.Bytes,
                Metadata = metadata,
                AlreadyExisted = true,
            };
        }

        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

        try
        {
            // overwrite: true — aynı içerik zaten aynı bayt dizisi olduğundan
            // üzerine yazmak veri kaybı değil, yalnızca gereksiz iş.
            File.Move(temporaryPath, finalPath, overwrite: true);
        }
        catch (IOException ex)
        {
            SafeDelete(temporaryPath);
            return Error.Transient("storage.move_failed", ex.Message);
        }

        if (existing is null)
        {
            await DatabaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                // İÇERİK-ADRESLİ DEPODA YARIŞ NORMAL: iki sahne aynı
                // görseli üretebiliyor (aynı sha) ve ikisi de "yok"
                // görüp eklemeye kalkabiliyor. Kilit içinde bir kez
                // daha bakmak, birincil anahtar ihlalini önlüyor.
                if (await db.Assets.AnyAsync(a => a.Sha256 == sha, cancellationToken).ConfigureAwait(false))
                {
                    return new StoredAsset
                    {
                        Ref = assetRef,
                        Bytes = bytes,
                        Metadata = metadata,
                        AlreadyExisted = true,
                    };
                }

                db.Assets.Add(new Asset
                {
                Sha256 = sha,
                Kind = metadata.Kind.ToString(),
                MimeType = metadata.MimeType,
                Bytes = bytes,
                Width = metadata.Width,
                Height = metadata.Height,
                DurationMs = metadata.Duration?.Value,
                StoragePath = relativePath,
                SourceProvider = metadata.SourceProvider,
                SourceUrl = metadata.SourceUrl?.ToString(),
                    LicenseJson = metadata.License is null ? null : JsonSerializer.Serialize(metadata.License),
                });

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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
            AlreadyExisted = false,
        };
    }

    public async Task<Result<Stream>> OpenAsync(AssetRef assetRef, CancellationToken cancellationToken)
    {
        var path = await GetLocalPathAsync(assetRef, cancellationToken).ConfigureAwait(false);

        return path.IsFailure
            ? Result.Failure<Stream>(path.Error)
            : Result.Success<Stream>(File.OpenRead(path.Value));
    }

    public async Task<Result<string>> GetLocalPathAsync(AssetRef assetRef, CancellationToken cancellationToken)
    {
        var asset = await db.Assets
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Sha256 == assetRef.Sha256, cancellationToken)
            .ConfigureAwait(false);

        if (asset is null)
        {
            return Error.Permanent("storage.not_found", $"Varlık kaydı yok: {assetRef}");
        }

        var path = Path.Combine(rootPath, asset.StoragePath);

        // Kayıt var ama dosya yok: bu bir tutarsızlık, sessizce geçilmemeli.
        // Genellikle diskin elle temizlenmesinden ya da farklı bir kök
        // yolundan gelir; ikisi de teşhis edilmesi gereken durumlar.
        return File.Exists(path)
            ? Result.Success(path)
            : Error.Permanent("storage.file_missing", $"Kayıt var ama dosya yok: {path}");
    }

    public async Task<Result<bool>> ExistsAsync(AssetRef assetRef, CancellationToken cancellationToken)
    {
        var exists = await db.Assets
            .AsNoTracking()
            .AnyAsync(a => a.Sha256 == assetRef.Sha256, cancellationToken)
            .ConfigureAwait(false);

        return Result.Success(exists);
    }

    /// Varlığı diskten siler (P4-02).
    ///
    /// ***KAYIT SİLİNMİYOR, YALNIZCA DOSYA.*** Satırın sahibi
    /// süpürücü (`RetentionSweeper`) ve sırası kasıtlı: önce dosya,
    /// sonra satır. Ters sırada, dosya silme düşerse satır gitmiş
    /// olurdu ve dosya sonsuza kadar sahipsiz kalırdı — hiçbir kayıt
    /// onu göstermediği için bir daha bulunamazdı.
    ///
    /// OLMAYAN DOSYA HATA DEĞİL: yarım kalmış bir silmenin ikinci
    /// turu "zaten yok" görüp ilerlemeli.
    public async Task<Result> DeleteAsync(AssetRef assetRef, CancellationToken cancellationToken)
    {
        var storagePath = await db.Assets
            .AsNoTracking()
            .Where(a => a.Sha256 == assetRef.Sha256)
            .Select(a => a.StoragePath)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (storagePath is null)
        {
            return Result.Success();
        }

        try
        {
            SafeDelete(Path.Combine(rootPath, storagePath));
            return Result.Success();
        }
        catch (IOException ex)
        {
            // GEÇİCİ: dosya kilitli olabilir (render hâlâ okuyor).
            // Bir sonraki turda yeniden denenmeli.
            return Error.Transient("storage.delete_failed", ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Error.Permanent("storage.delete_denied", ex.Message);
        }
    }

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
            // Geçici dosya silinemezse işlem başarısız sayılmamalı.
        }
    }

    internal static string ExtensionFor(string mimeType) => mimeType switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "audio/wav" => ".wav",
        "audio/mpeg" => ".mp3",
        "video/mp4" => ".mp4",
        "font/ttf" => ".ttf",
        "application/x-subrip" => ".srt",
        _ => ".bin",
    };
}
