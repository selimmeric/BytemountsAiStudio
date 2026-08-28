using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Learning;
using BytemountsAiStudio.Media.Rendering.Text;
using SkiaSharp;

namespace BytemountsAiStudio.Media.Rendering.Tests;

/// Kapak varyantları GERÇEKTEN farklı kapak üretiyor mu (P5-03).
///
/// A/B çerçevesinin sessiz ölüm biçimi şu: iki kol aynı görüntüyü
/// üretir, deney haftalarca koşar, örneklemi doldurur ve "fark yok"
/// der. Cümle doğrudur; ölçülen şey hiçbir şeydir.
///
/// Bu yüzden burada "ayar okundu mu" DEĞİL, PİKSELLER değişti mi
/// ölçülüyor. Ayarın node'a ulaşıp renderer'a ulaşmadığı bir durumda
/// bayt karşılaştırması geçer görünmez.
public sealed class ThumbnailVariantRenderTests
{
    private static readonly string[] FontStack = ["Inter", "Noto Sans", "Segoe UI", "Arial"];

    private const string Title = "Göbeklitepe neden bu kadar önemli";

    private static byte[] Render(ThumbnailVariantSettings style, byte[]? background = null)
    {
        var result = new ThumbnailRenderer(FontStack).Render(new ThumbnailRequest
        {
            Title = ThumbnailVariant.ApplyCase(Title, LanguageTag.Create("tr-TR"), style.Uppercase),
            Language = LanguageTag.Create("tr-TR"),
            BackgroundImage = background,
            TextPosition = style.Position,
            ScrimAlpha = style.ScrimAlpha,
            FontSize = style.FontSize,
        });

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);

        return result.Value;
    }

    private static ThumbnailVariantSettings Style(string config)
    {
        var parsed = ThumbnailVariant.Parse(config);

        Assert.True(parsed.IsSuccess, parsed.IsFailure ? parsed.Error.Message : string.Empty);

        return parsed.Value;
    }

    /// Metnin dikey ağırlık merkezi (satır numarası).
    ///
    /// Beyaz piksel sayısı değil KONUMU ölçülüyor: "metin taşındı"
    /// iddiasını ancak konum sınayabilir.
    private static double TextCenterRow(byte[] jpeg)
    {
        using var bitmap = SKBitmap.Decode(jpeg);
        Assert.NotNull(bitmap);

        double weighted = 0;
        long count = 0;

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);

                // Metin beyaz, zemin koyu. JPEG sıkıştırması kenarları
                // yumuşattığı için eşik yüksek tutuldu.
                if (pixel.Red > 200 && pixel.Green > 200 && pixel.Blue > 200)
                {
                    weighted += y;
                    count++;
                }
            }
        }

        Assert.True(count > 0, "Kapakta hiç metin pikseli yok.");

        return weighted / count;
    }

    private static long WhitePixels(byte[] jpeg)
    {
        using var bitmap = SKBitmap.Decode(jpeg);
        Assert.NotNull(bitmap);

        long count = 0;

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);

                if (pixel.Red > 200 && pixel.Green > 200 && pixel.Blue > 200)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static double MeanBrightness(byte[] jpeg)
    {
        using var bitmap = SKBitmap.Decode(jpeg);
        Assert.NotNull(bitmap);

        double total = 0;

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                total += (pixel.Red + pixel.Green + pixel.Blue) / 3.0;
            }
        }

        return total / (bitmap.Width * bitmap.Height);
    }

    private static byte[] SolidImage(SKColor color)
    {
        using var surface = SKSurface.Create(new SKImageInfo(800, 600));
        surface.Canvas.Clear(color);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);

        return data.ToArray();
    }

    /// KONTROL KOLU = BUGÜNKÜ KAPAK.
    ///
    /// Kontrolün varsayılandan farklı çıkması, deneyin tabanının
    /// kanalın gerçek tabanı olmaması demekti: kazanan varyant
    /// yayına alındığında ölçülen fark tekrar etmezdi.
    [Fact]
    public void KontrolKolu_VarsayilanKapakla_AyniBaytlar()
    {
        var control = Render(Style("{}"));

        var plain = new ThumbnailRenderer(FontStack).Render(new ThumbnailRequest
        {
            Title = Title,
            Language = LanguageTag.Create("tr-TR"),
        });

        Assert.True(plain.IsSuccess);
        Assert.Equal(plain.Value, control);
    }

    /// ALT KONUM METNİ GERÇEKTEN AŞAĞI TAŞIYOR.
    [Fact]
    public void AltKonum_MetinAsagida()
    {
        var center = TextCenterRow(Render(Style("{}")));
        var lower = TextCenterRow(Render(Style("""{"konum":"alt"}""")));

        // Yüz pikselden fazla fark: gözle görülür bir taşınma. Daha
        // küçük bir eşik, hiç taşımayan bir kolu da geçirirdi.
        Assert.True(lower > center + 100,
            $"Metin aşağı taşınmadı: orta={center:F0}, alt={lower:F0}");

        // VE KADRAJIN İÇİNDE KALIYOR: platform sağ alta süre rozeti
        // basıyor; metnin oraya girmesi okunmaması demek.
        Assert.True(lower < ThumbnailRenderer.Height, $"Metin kadraj dışına taştı: {lower:F0}");
    }

    /// BÜYÜK PUNTO DAHA ÇOK MÜREKKEP.
    [Fact]
    public void BuyukPunto_DahaCokMetinPikseli()
    {
        var normal = WhitePixels(Render(Style("{}")));
        var large = WhitePixels(Render(Style("""{"punto":"buyuk"}""")));

        Assert.True(large > normal * 1.1,
            $"Punto büyümedi: normal={normal}, büyük={large}");
    }

    /// BÜYÜK HARF KOLU FARKLI BİR KAPAK ÜRETİYOR.
    [Fact]
    public void BuyukHarf_FarkliKapak()
        => Assert.NotEqual(
            Render(Style("{}")),
            Render(Style("""{"harf":"buyuk"}""")));

    /// AĞIR KARARTMA GERÇEKTEN KARARTIYOR.
    ///
    /// Arka plan görseli AÇIK RENK seçildi: karartmanın etkisi ancak
    /// açık bir zeminde ölçülebilir. Koyu zeminde iki kol da koyu
    /// çıkar ve test hiçbir şey sınamazdı.
    [Fact]
    public void AgirKarartma_DahaKoyu()
    {
        var background = SolidImage(new SKColor(230, 230, 230));

        var light = MeanBrightness(Render(Style("""{"karartma":"hafif"}"""), background));
        var heavy = MeanBrightness(Render(Style("""{"karartma":"agir"}"""), background));

        Assert.True(heavy < light - 20,
            $"Karartma değişmedi: hafif={light:F1}, ağır={heavy:F1}");
    }

    /// ARKA PLAN YOKSA KARARTMA KOLU GÖRÜNMEZ — VE BU BİR TUZAK.
    ///
    /// Düz renk kapakta karartma hiç çizilmiyor; o kanalda karartma
    /// deneyi açmak, iki kolda da aynı kapağı üretmek demek. Test
    /// bunu KANIT olarak tutuyor ki davranış sessizce değişirse
    /// (ya da böyle bir deney açılırsa) burada görünsün.
    [Fact]
    public void ArkaPlansizKapak_KarartmaKoluEtkisiz()
        => Assert.Equal(
            Render(Style("""{"karartma":"hafif"}""")),
            Render(Style("""{"karartma":"agir"}""")));
}
