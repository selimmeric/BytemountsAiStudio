using System.Globalization;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Core.Time;
using BytemountsAiStudio.Providers.Fake.Media;

namespace BytemountsAiStudio.Providers.Fake;

/// Deterministik sahte görsel sağlayıcı. Hem stok arama hem üretim modunda
/// çalışabilir; <see cref="Kind"/> kurucudan verilir.
///
/// Üretilen PNG gerçek ve geçerlidir — FFmpeg okuyabilir. Rengi prompt'un
/// hash'inden gelir, yani her sahne farklı renkte çıkar ve sahte videoda
/// sahne geçişleri gözle doğrulanabilir.
public sealed class FakeImageProvider(ImageProviderKind kind = ImageProviderKind.Stock) : IImageProvider
{
    public string Key => kind == ImageProviderKind.Stock ? "fake-stock" : "fake-imagegen";

    public ImageProviderKind Kind => kind;

    public int GenerateCount => _generateCount;

    private int _generateCount;

    public Task<Result<ProviderResponse<IReadOnlyList<ImageCandidate>>>> FindAsync(
        ImageQuery query,
        ProviderContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        if (kind != ImageProviderKind.Stock)
        {
            return Task.FromResult(Result.Failure<ProviderResponse<IReadOnlyList<ImageCandidate>>>(
                Error.Permanent("fake.image.not_stock", "Bu sağlayıcı arama yapmaz, üretir.")));
        }

        var candidates = new List<ImageCandidate>();

        for (var i = 0; i < Math.Min(query.MaxResults, 5); i++)
        {
            var hash = Determinism.Hash(query.Terms, Determinism.Format($"{i}"));
            var slug = Determinism.Token(hash, 9);

            candidates.Add(new ImageCandidate
            {
                Url = new Uri(Determinism.Format($"https://fake-stock.invalid/{slug}.png")),
                Width = 1920,
                Height = 1080,
                Description = Determinism.Format($"'{query.Terms}' için sahte stok görsel {i + 1}"),
                Relevance = 1.0 - (i * 0.1),
                License = new LicenseInfo
                {
                    Name = "Fake Stock License",
                    Url = new Uri("https://fake-stock.invalid/license"),
                    Author = Determinism.Format($"sahte-yazar-{Determinism.Range(hash, 1, 50)}"),
                    RequiresAttribution = false,
                    CapturedAt = Determinism.Epoch,
                },
            });
        }

        return Task.FromResult(Result.Success(
            new ProviderResponse<IReadOnlyList<ImageCandidate>>(candidates, UsageUnits.OfRequests())));
    }

    public Task<Result<ProviderResponse<GeneratedImage>>> GenerateAsync(
        ImagePrompt prompt,
        ProviderContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _generateCount);

        var hash = Determinism.Hash(prompt.Text, prompt.StyleHint, prompt.Seed?.ToString(CultureInfo.InvariantCulture));
        var (r, g, b) = Determinism.Color(hash);
        var png = PngWriter.SolidColor(prompt.Width, prompt.Height, r, g, b);

        var image = new GeneratedImage
        {
            Data = png,
            MimeType = "image/png",
            Width = prompt.Width,
            Height = prompt.Height,
            License = new LicenseInfo
            {
                Name = "Fake Generated",
                RequiresAttribution = false,
                CapturedAt = Determinism.Epoch,
            },
        };

        return Task.FromResult(Result.Success(
            new ProviderResponse<GeneratedImage>(image, new UsageUnits { Images = 1 })));
    }
}

/// Deterministik sahte müzik seçici.
///
/// Lisans bilgisi her zaman dolu döner: lisanssız müziğin yayına girememesi
/// bloklayıcı bir QC kuralı, sahte sağlayıcı o kuralı yanlışlıkla geçemesin.
public sealed class FakeMusicProvider : IMusicProvider
{
    public string Key => "fake-music";

    public Task<Result<ProviderResponse<MusicTrack>>> SelectAsync(
        MusicQuery query,
        ProviderContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var hash = Determinism.Hash(query.Mood, Determinism.Format($"{query.MinimumDuration.Value}"));
        var slug = Determinism.Token(hash, 8);

        var track = new MusicTrack
        {
            Url = new Uri(Determinism.Format($"https://fake-music.invalid/{slug}.wav")),
            // Seçilen parça istenen süreden kısa olmamalı; kısa olsaydı
            // döngüleme davranışı sahtede hiç sınanmazdı.
            Duration = new Ms(query.MinimumDuration.Value + 30_000),
            Title = Determinism.Format($"Sahte {query.Mood} parça ({slug})"),
            License = new LicenseInfo
            {
                Name = "Fake Music License",
                Author = "sahte-besteci",
                RequiresAttribution = true,
                CapturedAt = Determinism.Epoch,
            },
        };

        return Task.FromResult(Result.Success(
            new ProviderResponse<MusicTrack>(track, UsageUnits.OfRequests())));
    }
}
