using System.Globalization;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Time;
using BytemountsAiStudio.Media.Timeline;
using SkiaSharp;

namespace BytemountsAiStudio.Media.Rendering.Text;

/// Tek bir altyazı karesi: hangi görüntü, ne zaman gösterilecek.
public sealed record CaptionImage(string Path, TimeRange Range);

/// Altyazı ve metin katmanı — SkiaSharp ile.
///
/// ADR-005r: `drawtext` HİÇ kullanılmıyor. Üç sebep (§12.4):
///   1. Dizgi (shaping) yok — Arapça bitişik yazı, Hint dilleri, CJK satır
///      kırma doğru çıkmaz. Çok dilli hedef bunu doğrudan dışlıyor.
///   2. Escape cehennemi — metindeki `:` `'` `\` `%` filtre sözdizimiyle çakışır.
///   3. Kelime vurgusu için satırın tamamını çizip yalnızca o kelimeyi
///      boyamak gerekir; `drawtext` bunu yapamaz.
///
/// KARE DİZİSİ TUZAĞINDAN KAÇINMA (§12.4): saniyede 30 PNG üretmiyoruz.
/// Bir altyazı satırındaki her VURGU DURUMU tek bir PNG'dir — 5 kelimelik
/// satır = 5 görüntü, her biri `enable=between(t,a,b)` ile gösterilir.
/// 50 saniyelik Shorts'ta ~120 küçük PNG; 1.500 kare yerine.
public sealed class CaptionRenderer(IReadOnlyList<string> fontStack)
{
    /// Bir metnin çizilebildiği ilk yazı tipini bulur.
    ///
    /// §20.4: tek font tüm dilleri kapsamaz. Zincir olmadan eksik glif
    /// yerine tofu (□□□) çizilir ve bunu ancak izleyici fark eder.
    private readonly Lazy<SKFontManager> _fonts = new(SKFontManager.Default);

    /// Altyazı ipuçlarını satırlara toplar ve her vurgu durumu için bir
    /// görüntü üretir.
    ///
    /// Satırlama kelime kelime değil CÜMLE bazlı: ekranda tek kelime
    /// göstermek okunabilirliği düşürür, izleyici bağlamı kaybeder.
    public IReadOnlyList<CaptionImage> RenderTrack(
        CaptionTrack track, TextStyle style, Canvas canvas, string outputDirectory, bool rightToLeft = false)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(style);

        Directory.CreateDirectory(outputDirectory);

        var lines = GroupIntoLines(track.Cues, style.MaxLines);
        var images = new List<CaptionImage>();
        var index = 0;

        foreach (var line in lines)
        {
            var words = line.ConvertAll(c => c.Text);

            for (var highlighted = 0; highlighted < line.Count; highlighted++)
            {
                var path = Path.Combine(
                    outputDirectory,
                    $"caption_{index.ToString("D4", CultureInfo.InvariantCulture)}.png");

                Draw(words, highlighted, style, canvas, rightToLeft, path);
                images.Add(new CaptionImage(path, line[highlighted].Range));
                index++;
            }
        }

        return images;
    }

    /// Tek bir metin katmanı (başlık, sayı vurgusu) için görüntü üretir.
    public string RenderOverlay(
        string text, TextStyle style, Canvas canvas, string outputPath, bool rightToLeft = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        Draw([text], -1, style, canvas, rightToLeft, outputPath);
        return outputPath;
    }

    /// Ardışık ipuçlarını okunabilir satırlara böler.
    ///
    /// Satır sınırı iki şeyden biri: cümle sonu noktalama ya da kelime
    /// sayısı sınırı. Zamana göre bölmek daha basit olurdu ama cümleyi
    /// ortasından kesip iki ekrana yayardı.
    internal static List<List<CaptionCue>> GroupIntoLines(
        IReadOnlyList<CaptionCue> cues, int maxLines)
    {
        var wordsPerLine = Math.Max(3, maxLines * 3);
        var lines = new List<List<CaptionCue>>();
        var current = new List<CaptionCue>();

        foreach (var cue in cues.OrderBy(c => c.Range.Start.Value))
        {
            current.Add(cue);

            var endsSentence = cue.Text.EndsWith('.') || cue.Text.EndsWith('!')
                            || cue.Text.EndsWith('?') || cue.Text.EndsWith(':');

            if (endsSentence || current.Count >= wordsPerLine)
            {
                lines.Add(current);
                current = [];
            }
        }

        if (current.Count > 0)
        {
            lines.Add(current);
        }

        return lines;
    }

    private void Draw(
        List<string> words, int highlightedIndex, TextStyle style,
        Canvas canvas, bool rightToLeft, string outputPath)
    {
        var fontSize = (float)(canvas.Height * style.SizePercent / 100.0);

        using var surface = SKSurface.Create(new SKImageInfo(
            canvas.Width, canvas.Height, SKColorType.Rgba8888, SKAlphaType.Premul));

        var skCanvas = surface.Canvas;
        skCanvas.Clear(SKColors.Transparent);

        // Kalınlık artık GERÇEKTEN uygulanıyor.
        //
        // `TextStyle.Bold` timeline'da true olduğu hâlde çizim ince
        // yüzle yapılıyordu: ayar vardı, etkisi yoktu. Kapak üretimi
        // yazılırken aynı hatanın izi sürülüp buraya kadar geldi.
        // ***`style.FontFamily` ARTIK OKUNUYOR.***
        //
        // Alan `TextStyle` içinde yazılıyor ve HİÇBİR YERDE
        // OKUNMUYORDU: çizim yalnızca belge düzeyindeki `FontStack`'e
        // bakıyordu. Yani timeline JSON'unda yazı tipi adını
        // değiştiren biri hiçbir fark görmüyordu — bu depoda tekrar
        // eden "kaydediliyor, okunmuyor" sınıfının bir örneği daha.
        //
        // ZİNCİRİN BAŞINA EKLENİYOR, ZİNCİRİ DEĞİŞTİRMİYOR: tek bir
        // yazı tipi adı bir yedek zinciri değil. İstenen yüz sistemde
        // yoksa ya da metindeki karakterleri kapsamıyorsa (Arapça,
        // Japonca) çizim zincirin geri kalanına düşüyor. Zinciri
        // tamamen değiştirmek, o kanalda hiçbir altyazının
        // çizilememesi demek olabilirdi.
        var fonts = string.IsNullOrWhiteSpace(style.FontFamily)
            ? fontStack
            : new[] { style.FontFamily }.Concat(fontStack).ToList();

        using var typeface = FontResolver.Resolve(
            fonts, string.Concat(words), style.Bold, _fonts.Value);
        using var font = new SKFont(typeface, fontSize);

        // Kelimeler tek tek ölçülüp yerleştiriliyor: vurgulanan kelimenin
        // konumu böyle kendiliğinden doğru çıkıyor. Satırın tamamını çizip
        // sonra vurgulanan kısmı bulmaya çalışmak, dizgi sonrası konum
        // hesabı gerektirirdi (§12.4/3).
        var spaceWidth = font.MeasureText(" ");
        var widths = words.Select(w => font.MeasureText(w)).ToArray();
        var totalWidth = widths.Sum() + (spaceWidth * Math.Max(0, words.Count - 1));

        var y = PositionY(style, canvas, fontSize);
        var x = (canvas.Width - totalWidth) / 2f;

        if (style.BoxColor is { } boxColor && style.BoxOpacity > 0)
        {
            DrawBox(skCanvas, x, y, totalWidth, fontSize, boxColor, style.BoxOpacity);
        }

        var order = rightToLeft
            ? Enumerable.Range(0, words.Count).Reverse().ToArray()
            : Enumerable.Range(0, words.Count).ToArray();

        var cursor = x;

        foreach (var i in order)
        {
            var isHighlighted = i == highlightedIndex;
            var color = isHighlighted && style.HighlightColor is { } highlight
                ? Parse(highlight)
                : Parse(style.Color);

            if (style.StrokeWidth > 0 && style.StrokeColor is { } strokeColor)
            {
                // Kontur ÖNCE çiziliyor: sonra çizilseydi harflerin içini
                // yer ve ince fontlarda metni okunmaz hâle getirirdi.
                using var stroke = new SKPaint
                {
                    Color = Parse(strokeColor),
                    IsStroke = true,
                    StrokeWidth = style.StrokeWidth,
                    IsAntialias = true,
                    StrokeJoin = SKStrokeJoin.Round,
                };

                skCanvas.DrawText(words[i], cursor, y, SKTextAlign.Left, font, stroke);
            }

            using var paint = new SKPaint { Color = color, IsAntialias = true };
            skCanvas.DrawText(words[i], cursor, y, SKTextAlign.Left, font, paint);

            cursor += widths[i] + spaceWidth;
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(outputPath);
        data.SaveTo(stream);
    }

    private static void DrawBox(
        SKCanvas canvas, float x, float y, float width, float fontSize, string color, double opacity)
    {
        var padding = fontSize * 0.35f;

        using var paint = new SKPaint
        {
            Color = Parse(color).WithAlpha((byte)(opacity * 255)),
            IsAntialias = true,
        };

        canvas.DrawRoundRect(
            new SKRect(x - padding, y - fontSize, x + width + padding, y + (fontSize * 0.35f)),
            padding / 2, padding / 2, paint);
    }

    private static float PositionY(TextStyle style, Canvas canvas, float fontSize) => style.Position switch
    {
        Anchor.TopLeft or Anchor.TopRight => fontSize * 1.5f,
        Anchor.Center => canvas.Height / 2f,
        _ => (float)(canvas.Height * (1 - (style.OffsetPercent / 100.0))),
    };

    internal static SKColor Parse(string hex)
        => SKColor.TryParse(hex, out var color) ? color : SKColors.White;
}
