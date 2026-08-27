using System.Globalization;

namespace BytemountsAiStudio.Core.Content;

/// Video tuvali: cozunurluk ve kare hizi.
///
/// H.264 kodlayicilar tek sayili boyutlarda calismaz (yuv420p kroma alt
/// ornekleme 2'ye bolunebilir boyut ister). Bunu render sirasinda kesfetmek
/// yerine tuval olusurken engelliyoruz.
public readonly record struct Canvas
{
    public Canvas(int width, int height, int fps)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentException($"Tuval boyutu pozitif olmali: {width}x{height}");
        }

        if (width % 2 != 0 || height % 2 != 0)
        {
            throw new ArgumentException(
                $"Tuval boyutu cift olmali (yuv420p gereksinimi): {width}x{height}");
        }

        if (fps is < 1 or > 120)
        {
            throw new ArgumentOutOfRangeException(nameof(fps), fps, "Kare hizi 1-120 araliginda olmali.");
        }

        Width = width;
        Height = height;
        Fps = fps;
    }

    public int Width { get; }

    public int Height { get; }

    public int Fps { get; }

    public double AspectRatio => (double)Width / Height;

    public bool IsPortrait => Height > Width;

    public static Canvas Shorts1080 => new(1080, 1920, 30);

    public static Canvas Landscape1080 => new(1920, 1080, 30);

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{Width}x{Height}@{Fps}");
}
