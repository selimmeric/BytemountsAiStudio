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

    /// Ayar belgesindeki en-boy oranından tuval seçer (P3-03).
    ///
    /// TANINMAYAN DEĞER DİKEY'E DÜŞÜYOR ve bunu bilerek yapıyoruz:
    /// bu sistem ağırlıklı olarak Shorts üretiyor, yanlış yazılmış bir
    /// ayarın bedeli yanlış oran olmalı — hiç video olmaması değil.
    /// Çağıran, tanınıp tanınmadığını `TryParseAspect` ile öğrenip
    /// kayda geçirebiliyor.
    public static Canvas ForAspect(string? aspect)
        => TryParseAspect(aspect) ?? Shorts1080;

    /// Tanınan oranlar. `null` = tanınmadı.
    ///
    /// AYRI BİR METOT çünkü "tanınmadı" bilgisi kaybolmamalı: sessizce
    /// dikeye düşen bir yatay video, ancak render bittikten sonra
    /// fark edilirdi — ve o noktada on beş dakikalık bir render
    /// harcanmış olurdu.
    public static Canvas? TryParseAspect(string? aspect)
        => aspect?.Trim() switch
        {
            "9:16" or "dikey" or "portrait" or "shorts" => Shorts1080,
            "16:9" or "yatay" or "landscape" or "video" => Landscape1080,
            _ => null,
        };

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{Width}x{Height}@{Fps}");
}
