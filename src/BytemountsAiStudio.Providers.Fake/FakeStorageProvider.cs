using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Core.Assets;

namespace BytemountsAiStudio.Providers.Fake;

/// İçerik-adresli sahte varlık deposu. Geçici bir dizine yazar.
///
/// Neden bellek içi değil: render'ın <see cref="GetLocalPathAsync"/> ile gerçek
/// bir dosya yolu alması gerekiyor — FFmpeg bellekteki diziyi okuyamaz. Sahte
/// depo bunu sağlamazsa Faz 0'ın uçtan uca hedefi sahte sağlayıcılarla
/// sınanamaz, ki bütün amaç oydu.
///
/// Gerçek CAS uygulaması P0-04'te gelecek; bu, aynı sözleşmenin test ölçeğinde
/// karşılığı.
public sealed class FakeStorageProvider : IStorageProvider, IDisposable
{
    private readonly ConcurrentDictionary<string, StoredAsset> _index = new(StringComparer.Ordinal);
    private readonly string _root;
    private bool _disposed;

    public FakeStorageProvider(string? root = null)
    {
        _root = root ?? Path.Combine(
            Path.GetTempPath(),
            "bmai-fake-store",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

        Directory.CreateDirectory(_root);
    }

    public string Key => "fake-storage";

    public string RootPath => _root;

    /// Depodaki benzersiz içerik sayısı. Tekilleştirmenin gerçekten çalıştığını
    /// doğrulamak için gerekli.
    public int Count => _index.Count;

    public async Task<Result<StoredAsset>> PutAsync(
        Stream content,
        AssetMetadata metadata,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(metadata);

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        var bytes = buffer.ToArray();

        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var assetRef = AssetRef.Create(sha);

        if (_index.TryGetValue(sha, out var existing))
        {
            // Aynı içerik ikinci kez geldi: yeni dosya yazılmaz, var olan döner.
            return Result.Success(existing with { AlreadyExisted = true });
        }

        var path = Path.Combine(_root, assetRef.RelativePath(ExtensionFor(metadata.MimeType)));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);

        var stored = new StoredAsset
        {
            Ref = assetRef,
            Bytes = bytes.LongLength,
            Metadata = metadata,
            AlreadyExisted = false,
        };

        _index[sha] = stored;
        return Result.Success(stored);
    }

    public Task<Result<Stream>> OpenAsync(AssetRef assetRef, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var pathResult = ResolvePath(assetRef);
        if (pathResult.IsFailure)
        {
            return Task.FromResult(Result.Failure<Stream>(pathResult.Error));
        }

        Stream stream = File.OpenRead(pathResult.Value);
        return Task.FromResult(Result.Success(stream));
    }

    public Task<Result<string>> GetLocalPathAsync(AssetRef assetRef, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ResolvePath(assetRef));
    }

    public Task<Result<bool>> ExistsAsync(AssetRef assetRef, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Result.Success(_index.ContainsKey(assetRef.Sha256)));
    }

    /// Sahte depo da silmeyi GERÇEKTEN yapıyor (P4-02).
    ///
    /// "Sildim" deyip tutmasaydı, saklama süpürücüsünün testleri
    /// silinen varlığın gerçekten gittiğini hiç sınayamazdı — ve
    /// süpürücü sahte hatta yeşil, gerçek hatta bozuk olurdu.
    public Task<Result> DeleteAsync(AssetRef assetRef, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // OLMAYAN VARLIK HATA DEĞİL: yarım kalmış bir silmenin ikinci
        // turu ilerlemeli.
        if (_index.TryRemove(assetRef.Sha256, out var stored))
        {
            try
            {
                File.Delete(Path.Combine(
                    _root, assetRef.RelativePath(ExtensionFor(stored.Metadata.MimeType))));
            }
            catch (IOException)
            {
                // Sahte depo geçici dizinde: dosya kalıntısı testi
                // etkilemiyor, dizin sonunda tümden siliniyor.
            }
        }

        return Task.FromResult(Result.Success());
    }

    private Result<string> ResolvePath(AssetRef assetRef)
    {
        if (!_index.TryGetValue(assetRef.Sha256, out var stored))
        {
            return Error.Permanent(
                "storage.not_found", $"Varlık depoda yok: {assetRef}");
        }

        return Path.Combine(_root, assetRef.RelativePath(ExtensionFor(stored.Metadata.MimeType)));
    }

    private static string ExtensionFor(string mimeType) => mimeType switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "audio/wav" => ".wav",
        "audio/mpeg" => ".mp3",
        "video/mp4" => ".mp4",
        _ => ".bin",
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Geçici dizin temizlenemediyse test başarısız sayılmamalı;
            // işletim sistemi er geç toplar.
        }
    }
}
