using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Time;
using BytemountsAiStudio.Media.Rendering.Text;
using BytemountsAiStudio.Media.Timeline;
using SkiaSharp;

namespace BytemountsAiStudio.Media.Rendering.Tests;

/// Altyazı katmanının testleri.
///
/// Bunlar FFmpeg gerektirmiyor: Skia doğrudan PNG üretiyor, biz de pikselleri
/// okuyoruz. §12.8'in "piksel" seviyesi — gözle bakmadan "metin gerçekten
/// çizildi mi" sorusunu cevaplıyor.
public sealed class CaptionRendererTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "bmai-caption-test-" + Guid.NewGuid().ToString("N")[..8]);

    private static readonly Canvas Canvas = new(360, 640, 30);

    private static TextStyle Style() => new()
    {
        FontFamily = "Inter",
        SizePercent = 6,
        Color = "#FFFFFF",
        HighlightColor = "#FFD400",
        StrokeColor = "#000000",
        StrokeWidth = 4,
        MaxLines = 2,
    };

    private static CaptionTrack Track(params string[] words) => new()
    {
        StyleRef = "caption",
        Cues = words.Select((w, i) => new CaptionCue
        {
            Text = w,
            Range = new TimeRange(new Ms(i * 500), new Ms((i + 1) * 500)),
        }).ToList(),
    };

    private static SKBitmap Load(string path)
    {
        using var stream = File.OpenRead(path);
        return SKBitmap.Decode(stream);
    }

    [Fact]
    public void HerVurguDurumuIcin_BirGoruntuUretilir()
    {
        // §12.4: kare dizisi DEĞİL. 5 kelimelik satır = 5 görüntü,
        // saniyede 30 kare değil.
        var renderer = new CaptionRenderer(["Inter", "Arial"]);

        var images = renderer.RenderTrack(Track("bir", "iki", "uc"), Style(), Canvas, _directory);

        Assert.Equal(3, images.Count);
        Assert.All(images, i => Assert.True(File.Exists(i.Path)));
    }

    [Fact]
    public void UretilenGoruntu_SeffafArkaPlanliVeTuvalBoyutunda()
    {
        // Arka plan opak olsaydı altyazı videoyu tamamen kapatırdı.
        var renderer = new CaptionRenderer(["Inter"]);
        var images = renderer.RenderTrack(Track("merhaba"), Style(), Canvas, _directory);

        using var bitmap = Load(images[0].Path);

        Assert.Equal(Canvas.Width, bitmap.Width);
        Assert.Equal(Canvas.Height, bitmap.Height);
        Assert.Equal(0, bitmap.GetPixel(5, 5).Alpha);
    }

    [Fact]
    public void MetinGercektenCizilir()
    {
        var renderer = new CaptionRenderer(["Inter", "Arial"]);
        var images = renderer.RenderTrack(Track("MERHABA"), Style(), Canvas, _directory);

        using var bitmap = Load(images[0].Path);

        var opaque = 0;
        for (var y = 0; y < bitmap.Height; y += 2)
        {
            for (var x = 0; x < bitmap.Width; x += 2)
            {
                if (bitmap.GetPixel(x, y).Alpha > 128)
                {
                    opaque++;
                }
            }
        }

        Assert.True(opaque > 50, $"Çizilen piksel çok az ({opaque}); metin görünmüyor olabilir.");
    }

    [Fact]
    public void VurgulananKelime_FarkliRenkte()
    {
        // Karaoke altyazının tamamı bu davranışa dayanıyor.
        var renderer = new CaptionRenderer(["Inter", "Arial"]);
        var images = renderer.RenderTrack(Track("aaa", "bbb"), Style(), Canvas, _directory);

        using var first = Load(images[0].Path);
        using var second = Load(images[1].Path);

        var differing = 0;
        for (var y = 0; y < first.Height; y += 3)
        {
            for (var x = 0; x < first.Width; x += 3)
            {
                if (first.GetPixel(x, y) != second.GetPixel(x, y))
                {
                    differing++;
                }
            }
        }

        Assert.True(differing > 10,
            "İki vurgu durumu aynı görünüyor; vurgu rengi uygulanmamış olabilir.");
    }

    [Fact]
    public void TurkceKarakterler_Cizilir()
    {
        // "ğüşıöç" için glif yoksa tofu çıkar ve bunu ancak izleyici görür.
        var renderer = new CaptionRenderer(["Inter", "Arial", "Segoe UI"]);
        var images = renderer.RenderTrack(Track("ğüşıöçİĞ"), Style(), Canvas, _directory);

        using var bitmap = Load(images[0].Path);

        var opaque = 0;
        for (var y = 0; y < bitmap.Height; y += 2)
        {
            for (var x = 0; x < bitmap.Width; x += 2)
            {
                if (bitmap.GetPixel(x, y).Alpha > 128)
                {
                    opaque++;
                }
            }
        }

        Assert.True(opaque > 50, "Türkçe karakterler çizilmemiş olabilir.");
    }

    [Fact]
    public void CumleSonu_SatiriBitirir()
    {
        // Zamana göre bölmek daha basit olurdu ama cümleyi ortasından kesip
        // iki ekrana yayardı.
        var cues = new[] { "Bir", "cümle.", "Yeni", "cümle" }
            .Select((w, i) => new CaptionCue
            {
                Text = w,
                Range = new TimeRange(new Ms(i * 400), new Ms((i + 1) * 400)),
            }).ToList();

        var lines = CaptionRenderer.GroupIntoLines(cues, maxLines: 2);

        Assert.Equal(2, lines.Count);
        Assert.Equal(2, lines[0].Count);
    }

    [Fact]
    public void GoruntulerZamanAraliklariniKorur()
    {
        var renderer = new CaptionRenderer(["Inter"]);
        var images = renderer.RenderTrack(Track("bir", "iki"), Style(), Canvas, _directory);

        Assert.Equal(0, images[0].Range.Start.Value);
        Assert.Equal(500, images[0].Range.End.Value);
        Assert.Equal(500, images[1].Range.Start.Value);
    }

    [Fact]
    public void OlmayanFontZinciri_YineDeCizer()
    {
        // Zincirde uygun yüz yoksa sistem fallback'ine düşülüyor:
        // yanlış fontla çizmek, hiç çizmemekten iyi.
        var renderer = new CaptionRenderer(["HicBoyleBirFontYok-12345"]);
        var images = renderer.RenderTrack(Track("test"), Style(), Canvas, _directory);

        using var bitmap = Load(images[0].Path);

        var opaque = 0;
        for (var y = 0; y < bitmap.Height; y += 4)
        {
            for (var x = 0; x < bitmap.Width; x += 4)
            {
                if (bitmap.GetPixel(x, y).Alpha > 128)
                {
                    opaque++;
                }
            }
        }

        Assert.True(opaque > 5, "Font bulunamadığında hiçbir şey çizilmemiş.");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Geçici dizin temizlenemezse test sonucunu etkilememeli.
        }
    }
}
