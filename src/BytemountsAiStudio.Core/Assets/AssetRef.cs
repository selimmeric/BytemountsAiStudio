using System.Globalization;

namespace BytemountsAiStudio.Core.Assets;

public enum AssetKind
{
    Image = 0,
    Video = 1,
    Audio = 2,
    Music = 3,
    Font = 4,
    Subtitle = 5,
    Output = 6,
}

/// Bir varliga icerik-adresli referans: sha256.
///
/// §10.1: ayni gorsel 40 videoda kullanilsa tek satir ve tek dosya. Adresin
/// icerikten turemesi tekillestirmeyi, render onbellegini ve "bu gorseli nerede
/// kullandik" sorgusunu bedavaya getiriyor.
///
/// ADR-007: timeline icindeki her varlik bu referansla cozumlenmis olmali -
/// render sirasinda indirilecek bir URL kalmaz.
public readonly record struct AssetRef
{
    private const int Sha256HexLength = 64;

    private AssetRef(string sha256) => Sha256 = sha256;

    public string Sha256 { get; }

    public static Result<AssetRef> TryCreate(string? sha256)
    {
        if (string.IsNullOrWhiteSpace(sha256))
        {
            return Errors.Error.Permanent("asset.ref.empty", "Varlik referansi bos olamaz.");
        }

        var value = sha256.Trim();

        // "sha256:" oneki timeline JSON'inda okunabilirlik icin kullaniliyor.
        const string prefix = "sha256:";
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = value[prefix.Length..];
        }

        if (value.Length != Sha256HexLength)
        {
            return Errors.Error.Permanent(
                "asset.ref.length",
                $"sha256 {Sha256HexLength} karakter olmali, {value.Length} geldi.");
        }

        foreach (var c in value)
        {
            var isHex = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!isHex)
            {
                return Errors.Error.Permanent(
                    "asset.ref.format", $"sha256 onaltilik olmali, gecersiz karakter: '{c}'.");
            }
        }

        return new AssetRef(value.ToLowerInvariant());
    }

    public static AssetRef Create(string sha256)
    {
        var result = TryCreate(sha256);
        return result.IsSuccess
            ? result.Value
            : throw new ArgumentException(result.Error.Message, nameof(sha256));
    }

    /// Diskte sharded yerlesim: ab/cd/abcd... - tek dizinde yuz binlerce dosya
    /// dosya sistemini yavaslatir.
    public string RelativePath(string extension)
        => string.Create(CultureInfo.InvariantCulture,
            $"{Sha256[..2]}/{Sha256[2..4]}/{Sha256}{extension}");

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"sha256:{Sha256}");
}
