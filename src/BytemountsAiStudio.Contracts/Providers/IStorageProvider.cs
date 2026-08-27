using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Assets;
using BytemountsAiStudio.Core.Time;

namespace BytemountsAiStudio.Contracts.Providers;

public sealed record AssetMetadata
{
    public required AssetKind Kind { get; init; }

    public required string MimeType { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    public Ms? Duration { get; init; }

    public string? SourceProvider { get; init; }

    public Uri? SourceUrl { get; init; }

    public LicenseInfo? License { get; init; }
}

public sealed record StoredAsset
{
    public required AssetRef Ref { get; init; }

    public required long Bytes { get; init; }

    public required AssetMetadata Metadata { get; init; }

    /// Bu içerik depoda zaten varsa true — yeni kopya yazılmadı.
    public required bool AlreadyExisted { get; init; }
}

/// İçerik-adresli varlık deposu.
///
/// §10.1: adres içerikten türer (sha256). Aynı görsel kırk videoda kullanılsa
/// tek dosya. Depolama tasarrufu yan fayda; asıl kazanç render önbelleğinin ve
/// "bu görseli nerede kullandık" sorgusunun bedavaya gelmesi.
///
/// Faz 0'da yerel disk, Faz 4'te S3 uyumlu depo — arayüz değişmez.
public interface IStorageProvider : IProvider
{
    /// Akışı depoya yazar. sha256 yazarken hesaplanır; çağıran bilmek zorunda değil.
    Task<Result<StoredAsset>> PutAsync(
        Stream content,
        AssetMetadata metadata,
        CancellationToken cancellationToken);

    Task<Result<Stream>> OpenAsync(AssetRef assetRef, CancellationToken cancellationToken);

    /// Render worker'ının ihtiyacı olan tek şey: yerel dosya yolu.
    ///
    /// ADR-007: render ağa çıkmaz. Uzak depoda tutulan bir varlık, render
    /// başlamadan ÖNCE indirilip yerelde hazır edilir — render sırasında değil.
    Task<Result<string>> GetLocalPathAsync(AssetRef assetRef, CancellationToken cancellationToken);

    Task<Result<bool>> ExistsAsync(AssetRef assetRef, CancellationToken cancellationToken);
}
