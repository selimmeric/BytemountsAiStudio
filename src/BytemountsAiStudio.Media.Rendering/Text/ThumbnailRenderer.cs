using System.Globalization;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Errors;
using SkiaSharp;

namespace BytemountsAiStudio.Media.Rendering.Text;

/// Kapak görseli üretimi (P1-23).
///
/// Kapak, izlenme oranını en çok belirleyen tek görsel. Bu yüzden
/// üretimi rastgele bir kareye bırakılmıyor: sahne görseli ARKA PLAN,
/// üstüne okunaklı bir başlık geliyor.
///
/// Boyut 1280×720 (16:9) — kısa video dikey olsa bile kapak yatay,
/// çünkü platform onu arama sonuçlarında ve önerilerde yatay
/// gösteriyor. Dikey bir kapak yüklemek, platformun onu kendi
/// kırpması demek ve kırptığı yer genellikle metnin ortası oluyor.
///
/// Metin dile DUYARLI: font zinciri Türkçe ve İngilizce'yi kapsayan
/// ilk yüzü seçiyor, satır kırma kelime sınırında yapılıyor. Karakter
/// sınırında kırmak Türkçe'de kelimeleri ortadan bölerdi.
public sealed class ThumbnailRenderer(IReadOnlyList<string> fontStack)
{
    /// YouTube'un beklediği ölçü. Daha küçüğü platform tarafından
    /// büyütülüyor ve bulanıklaşıyor.
    public const int Width = 1280;

    public const int Height = 720;

    /// Kapak dosyası için üst sınır (2 MB). Aşan dosya reddediliyor.
    public const long MaxBytes = 2 * 1024 * 1024;

    /// Metnin kaplayabileceği en fazla genişlik oranı.
    ///
    /// Kenarlarda boşluk bırakmak şart: platform kapağın köşelerine
    /// süre rozeti ve ilerleme çubuğu koyuyor, oraya denk gelen metin
    /// okunmuyor.
    private const float SafeWidthRatio = 0.86f;

    private const int MaxLines = 3;

    private readonly Lazy<SKFontManager> _fonts = new(SKFontManager.CreateDefault);

    public Result<byte[]> Render(ThumbnailRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return Error.Permanent("thumbnail.no_title", "Kapak icin baslik gerekli.");
        }

        using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
        var canvas = surface.Canvas;

        DrawBackground(canvas, request);
        DrawScrim(canvas, request);

        var drawn = DrawTitle(canvas, request);

        if (drawn.IsFailure)
        {
            return Result.Failure<byte[]>(drawn.Error);
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, request.JpegQuality);

        var bytes = data.ToArray();

        // Boyut sınırı burada uygulanıyor, çağırana bırakılmıyor:
        // 2 MB'ı aşan kapak yükleme sırasında reddediliyor ve o noktada
        // videonun kalan her adımı zaten yapılmış oluyor.
        if (bytes.Length > MaxBytes)
        {
            return Error.Permanent(
                "thumbnail.too_large",
                FormattableString.Invariant($"Kapak {bytes.Length / 1024} KB, sinir {MaxBytes / 1024} KB."));
        }

        return Result.Success(bytes);
    }

    /// Arka plan: sahne görseli varsa o, yoksa düz renk.
    ///
    /// Görsel KAPLAYARAK yerleştiriliyor (cover), sığdırılarak değil:
    /// sığdırmak kenarlarda boş bant bırakır ve kapak amatör görünür.
    private static void DrawBackground(SKCanvas canvas, ThumbnailRequest request)
    {
        canvas.Clear(Parse(request.BackgroundColor));

        if (request.BackgroundImage is not { Length: > 0 } bytes)
        {
            return;
        }

        // `SKBitmap.Decode` bozuk baytlarda null DONMUYOR, ISTISNA
        // ATIYOR. Null kontrolu olu koddu ve bozuk bir arka plan gorseli
        // kapak node'unu cokertirdi. Testle yakalandi.
        SKBitmap? bitmap;

        try
        {
            bitmap = SKBitmap.Decode(bytes);
        }
        catch (ArgumentException)
        {
            bitmap = null;
        }

        if (bitmap is null)
        {
            // Bozuk görsel kapağı düşürmüyor; düz renk zaten geçerli
            // bir kapak. Görsel yüzünden metni de kaybetmek yanlış olurdu.
            return;
        }

        using var _ = bitmap;

        var scale = Math.Max((float)Width / bitmap.Width, (float)Height / bitmap.Height);
        var scaledWidth = bitmap.Width * scale;
        var scaledHeight = bitmap.Height * scale;

        var destination = new SKRect(
            (Width - scaledWidth) / 2f,
            (Height - scaledHeight) / 2f,
            (Width + scaledWidth) / 2f,
            (Height + scaledHeight) / 2f);

        canvas.DrawBitmap(bitmap, destination, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
    }

    /// Metnin arkasına karartma.
    ///
    /// Şart: parlak bir görselin üstünde beyaz metin okunmuyor ve hangi
    /// görselin geleceğini önceden bilmiyoruz. Kontur tek başına
    /// yetmiyor — açık zeminde kontur da kayboluyor.
    private static void DrawScrim(SKCanvas canvas, ThumbnailRequest request)
    {
        if (request.BackgroundImage is not { Length: > 0 })
        {
            return;
        }

        using var paint = new SKPaint { Color = new SKColor(0, 0, 0, 130) };
        canvas.DrawRect(new SKRect(0, 0, Width, Height), paint);
    }

    private Result<int> DrawTitle(SKCanvas canvas, ThumbnailRequest request)
    {
        // Kalınlık AÇIKÇA isteniyor: kapak metni ince yüzle okunmuyor.
        var typeface = FontResolver.Resolve(fontStack, request.Title, bold: true, _fonts.Value);

        var maxWidth = Width * SafeWidthRatio;
        var fontSize = request.FontSize;

        List<string> lines;

        // Yazı tipi boyutu metne göre KÜÇÜLTÜLÜYOR.
        //
        // Sabit boyut, uzun bir başlıkta ya taşmaya ya da üç satırdan
        // fazlasına yol açıyordu. Küçültmek, başlığı kırpmaktan iyi:
        // kırpılmış başlık yarım cümle gösteriyor ve tıklanmıyor.
        while (true)
        {
            using var probe = new SKFont(typeface, fontSize);
            lines = WrapLines(request.Title, probe, maxWidth);

            if (lines.Count <= MaxLines || fontSize <= request.MinFontSize)
            {
                break;
            }

            fontSize -= 4;
        }

        if (lines.Count > MaxLines)
        {
            lines = [.. lines.Take(MaxLines)];
        }

        using var font = new SKFont(typeface, fontSize);
        using var fill = new SKPaint { Color = Parse(request.TextColor), IsAntialias = true };

        using var stroke = new SKPaint
        {
            Color = Parse(request.StrokeColor),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = request.StrokeWidth,
            StrokeJoin = SKStrokeJoin.Round,
        };

        var lineHeight = fontSize * 1.18f;
        var block = lineHeight * lines.Count;
        var y = (Height + block) / 2f - lineHeight * 0.28f - (lines.Count - 1) * lineHeight / 2f;

        foreach (var line in lines)
        {
            // Kontur ÖNCE, dolgu sonra. Ters sırada kontur harflerin
            // içine taşar ve ince yazı tiplerinde metni yer.
            canvas.DrawText(line, Width / 2f, y, SKTextAlign.Center, font, stroke);
            canvas.DrawText(line, Width / 2f, y, SKTextAlign.Center, font, fill);

            y += lineHeight;
        }

        typeface.Dispose();

        return Result.Success(lines.Count);
    }

    /// Satır kırma KELİME sınırında.
    ///
    /// Karakter sınırında kırmak Türkçe'de kelimeleri ortadan bölerdi
    /// ("arkeolo-jik") ve bu tireleme kuralı olmadan yapılınca okunmaz
    /// oluyor. Tek başına sığmayan bir kelime yine de kendi satırında
    /// duruyor — bölmektense taşmasına izin vermek daha az kötü.
    internal static List<string> WrapLines(string text, SKFont font, float maxWidth)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        var current = string.Empty;

        foreach (var word in words)
        {
            var candidate = current.Length == 0 ? word : current + " " + word;

            if (font.MeasureText(candidate) <= maxWidth || current.Length == 0)
            {
                current = candidate;
                continue;
            }

            lines.Add(current);
            current = word;
        }

        if (current.Length > 0)
        {
            lines.Add(current);
        }

        return lines;
    }

    private static SKColor Parse(string hex)
        => SKColor.TryParse(hex, out var color) ? color : SKColors.White;
}

/// Bir kapak isteği.
public sealed record ThumbnailRequest
{
    public required string Title { get; init; }

    public required LanguageTag Language { get; init; }

    /// Arka plan görseli (genellikle ilk sahnenin karesi). Null ise düz
    /// renk kullanılıyor.
    public byte[]? BackgroundImage { get; init; }

    public string BackgroundColor { get; init; } = "#101820";

    public string TextColor { get; init; } = "#FFFFFF";

    public string StrokeColor { get; init; } = "#000000";

    public float StrokeWidth { get; init; } = 10f;

    public float FontSize { get; init; } = 96f;

    /// Bu boyutun altına inilmiyor: daha küçüğü telefon ekranında
    /// okunmuyor ve kapağın tek işi okunmak.
    public float MinFontSize { get; init; } = 52f;

    public int JpegQuality { get; init; } = 88;
}
