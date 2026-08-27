using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Time;

namespace BytemountsAiStudio.Contracts.Providers;

/// Bir varlığın kullanım hakkı kaydı.
///
/// §2.3/14: bu bir metadata değil, UYUM KAYDIDIR. Pexels/Pixabay/Unsplash
/// kuralları birbirinden farklı ve zamanla değişiyor; "o gün ne yazıyordu"
/// sorusunun cevabı ancak alındığı anda saklanmışsa vardır.
public sealed record LicenseInfo
{
    public required string Name { get; init; }

    public Uri? Url { get; init; }

    public string? Author { get; init; }

    /// Atıf zorunlu mu. Zorunluysa video açıklamasına eklenmeli.
    public bool RequiresAttribution { get; init; }

    public required DateTimeOffset CapturedAt { get; init; }
}

public enum ImageProviderKind
{
    /// Hazır görsel arar (Pexels, Pixabay, Unsplash).
    Stock = 0,

    /// Prompt'tan görsel üretir (Flux, OpenAI, Stability).
    Generative = 1,
}

public sealed record ImageQuery
{
    public required string Terms { get; init; }

    /// İstenen en–boy. Stok görseller nadiren tam uyar; kadraja uydurma
    /// render tarafının işi (cover/crop/blur arka plan).
    public double? PreferredAspectRatio { get; init; }

    public int MaxResults { get; init; } = 10;

    /// Metin içeren görseller elenir. Çok dilli türevde aynı görsel farklı
    /// dillerde kullanılacağı için üzerindeki yazı sorun olur (§20.7).
    public bool ExcludeTextInImage { get; init; }
}

public sealed record ImageCandidate
{
    public required Uri Url { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public required LicenseInfo License { get; init; }

    public string? Description { get; init; }

    /// Sağlayıcının alaka puanı. Sahne–görsel uygunluğu ayrıca VLM ile
    /// kontrol edilir; bu puana güvenilmez (§2.2/11).
    public double? Relevance { get; init; }
}

public sealed record ImagePrompt
{
    public required string Text { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public string? StyleHint { get; init; }

    /// Tekrarlanabilirlik için. Destekleyen sağlayıcılarda aynı tohum +
    /// aynı prompt aynı görseli verir; render önbelleğini anlamlı kılar.
    public int? Seed { get; init; }
}

public sealed record GeneratedImage
{
    public required ReadOnlyMemory<byte> Data { get; init; }

    public required string MimeType { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    /// Üretilen görselin lisansı da kaydedilir: sağlayıcının kullanım şartları
    /// ticari kullanıma izin veriyor mu sorusu sonradan sorulacak.
    public required LicenseInfo License { get; init; }
}

/// Görsel sağlayıcı.
///
/// Arama ve üretim tek arayüzde ama <see cref="Kind"/> ile ayrılıyor: yönlendirme
/// politikası "önce stok, bulunamazsa üret" sırasını bu bayrağa göre kurar.
/// Desteklenmeyen işlem çağrılırsa hata döner — patlamaz.
public interface IImageProvider : IProvider
{
    ImageProviderKind Kind { get; }

    Task<Result<ProviderResponse<IReadOnlyList<ImageCandidate>>>> FindAsync(
        ImageQuery query,
        ProviderContext context,
        CancellationToken cancellationToken);

    Task<Result<ProviderResponse<GeneratedImage>>> GenerateAsync(
        ImagePrompt prompt,
        ProviderContext context,
        CancellationToken cancellationToken);
}

public sealed record MusicQuery
{
    /// Cinematic / Documentary / Suspense / Emotional / Energetic / Ambient.
    public required string Mood { get; init; }

    public required Ms MinimumDuration { get; init; }
}

public sealed record MusicTrack
{
    public required Uri Url { get; init; }

    public required Ms Duration { get; init; }

    /// §2.3/13: lisans kanıtı olmayan müzik yayına giremez — Content ID
    /// talebi kanalın gelirini götürür. Bu alan bloklayıcı QC kuralına bağlı.
    public required LicenseInfo License { get; init; }

    public string? Title { get; init; }
}

public interface IMusicProvider : IProvider
{
    Task<Result<ProviderResponse<MusicTrack>>> SelectAsync(
        MusicQuery query,
        ProviderContext context,
        CancellationToken cancellationToken);
}
