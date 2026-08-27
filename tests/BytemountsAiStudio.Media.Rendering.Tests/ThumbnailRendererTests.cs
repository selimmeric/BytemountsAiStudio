using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Media.Rendering.Text;
using SkiaSharp;

namespace BytemountsAiStudio.Media.Rendering.Tests;

/// Kapak görseli testleri (P1-23).
///
/// Kapak, izlenme oranını en çok belirleyen tek görsel; bu yüzden
/// testler "çalıştı mı"ya değil ÖLÇÜLEBİLİR ÖZELLİKLERE bakıyor:
/// boyut, oran, dosya büyüklüğü, metnin gerçekten çizilmiş olması.
public sealed class ThumbnailRendererTests
{
    private static readonly string[] FontStack = ["Inter", "Noto Sans", "Segoe UI", "Arial"];

    private static ThumbnailRenderer Renderer() => new(FontStack);

    private static ThumbnailRequest Request(string title, byte[]? background = null) => new()
    {
        Title = title,
        Language = LanguageTag.Create("tr-TR"),
        BackgroundImage = background,
    };

    private static SKBitmap Decode(byte[] bytes)
    {
        var bitmap = SKBitmap.Decode(bytes);
        Assert.NotNull(bitmap);

        return bitmap;
    }

    /// Düz renk arka plan üretir — "görsel var" yolunu sınamak için.
    private static byte[] SolidImage(SKColor color, int width = 800, int height = 600)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        surface.Canvas.Clear(color);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);

        return data.ToArray();
    }

    /// Platform daha küçüğünü büyütüp bulanıklaştırıyor.
    [Fact]
    public void Olcu_1280x720()
    {
        var result = Renderer().Render(Request("Göbeklitepe"));

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);

        using var bitmap = Decode(result.Value);

        Assert.Equal(1280, bitmap.Width);
        Assert.Equal(720, bitmap.Height);
    }

    /// Kısa video dikey olsa bile kapak YATAY: platform onu arama
    /// sonuçlarında yatay gösteriyor ve dikey bir kapağı kendisi
    /// kırpıyor — kırptığı yer genellikle metnin ortası.
    [Fact]
    public void Oran_OnAltiDokuz()
    {
        using var bitmap = Decode(Renderer().Render(Request("Test")).Value);

        Assert.Equal(16.0 / 9.0, (double)bitmap.Width / bitmap.Height, 3);
    }

    [Fact]
    public void DosyaBoyutu_SinirinAltinda()
    {
        var result = Renderer().Render(Request("Göbeklitepe: Dünyanın En Eski Tapınağı"));

        Assert.True(result.Value.Length < ThumbnailRenderer.MaxBytes);
        Assert.True(result.Value.Length > 1024, "kapak supheli derecede kucuk");
    }

    [Fact]
    public void BosBaslik_Reddedilir()
    {
        var result = Renderer().Render(Request("   "));

        Assert.True(result.IsFailure);
        Assert.Equal("thumbnail.no_title", result.Error.Code);
    }

    /// Metin GERÇEKTEN çizilmiş mi.
    ///
    /// Piksel sayarak bakılıyor: düz zeminde beyaz metin çizildiyse
    /// açık piksel olmak zorunda. "Hata vermedi" testi, metni hiç
    /// çizmeyen bir kapağı geçirirdi.
    [Fact]
    public void Metin_GercektenCizilir()
    {
        using var withText = Decode(Renderer().Render(Request("MERHABA DUNYA")).Value);

        var light = 0;

        for (var y = 0; y < withText.Height; y += 4)
        {
            for (var x = 0; x < withText.Width; x += 4)
            {
                if (withText.GetPixel(x, y).Red > 200)
                {
                    light++;
                }
            }
        }

        Assert.True(light > 300, $"beyaz piksel sayisi cok dusuk: {light}");
    }

    /// Türkçe harfler kutu olarak çizilmemeli.
    ///
    /// Aynı uzunlukta Türkçe ve İngilizce başlık benzer sayıda açık
    /// piksel üretmeli; Türkçe tarafta çok daha az piksel çıkması
    /// harflerin çizilemediği anlamına gelir.
    [Fact]
    public void TurkceHarfler_Cizilebiliyor()
    {
        static int Light(byte[] bytes)
        {
            using var bitmap = SKBitmap.Decode(bytes);
            var count = 0;

            for (var y = 0; y < bitmap.Height; y += 2)
            {
                for (var x = 0; x < bitmap.Width; x += 2)
                {
                    if (bitmap.GetPixel(x, y).Red > 200)
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        var turkish = Light(Renderer().Render(Request("ĞÜŞİÖÇ ğüşıöç")).Value);
        var latin = Light(Renderer().Render(Request("GUSIOC gusioc")).Value);

        Assert.True(turkish > latin * 0.6,
            $"Turkce metin cok az piksel uretti: {turkish} vs {latin}");
    }

    /// Uzun başlık KIRPILMIYOR, yazı tipi küçültülüyor: kırpılmış
    /// başlık yarım cümle gösterir ve tıklanmaz.
    [Fact]
    public void UzunBaslik_SigdirilirKirpilmaz()
    {
        var title = "Dünyanın En Tehlikeli On Yeri ve Oralara Neden Kimsenin Gitmediğinin Gerçek Sebepleri";

        var result = Renderer().Render(Request(title));

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);

        using var bitmap = Decode(result.Value);
        Assert.Equal(1280, bitmap.Width);
    }

    /// Görsel KAPLAYARAK yerleşiyor: sığdırmak kenarlarda boş bant
    /// bırakır ve kapak amatör görünür.
    [Fact]
    public void ArkaPlanGorseli_KenarBirakmadanKaplar()
    {
        // Kaynak 4:3; 16:9'a sığdırılsaydı yanlarda düz renk bant kalırdı.
        var background = SolidImage(new SKColor(200, 40, 40), 800, 600);

        using var bitmap = Decode(Renderer().Render(Request("Test", background)).Value);

        // Sol ve sağ kenar ortası: görsel kapladıysa kırmızımsı,
        // bant kaldıysa arka plan rengi (koyu lacivert) olurdu.
        var left = bitmap.GetPixel(4, bitmap.Height / 2);
        var right = bitmap.GetPixel(bitmap.Width - 5, bitmap.Height / 2);

        Assert.True(left.Red > left.Blue, $"sol kenarda gorsel yok: {left}");
        Assert.True(right.Red > right.Blue, $"sag kenarda gorsel yok: {right}");
    }

    /// Karartma şart: parlak bir görselin üstünde beyaz metin
    /// okunmuyor ve hangi görselin geleceğini önceden bilmiyoruz.
    [Fact]
    public void ParlakGorsel_Karartilir()
    {
        var white = SolidImage(SKColors.White, 1280, 720);

        using var bitmap = Decode(Renderer().Render(Request("Test", white)).Value);

        // Metnin olmadığı üst köşe: beyaz kalsaydı metin okunmazdı.
        var corner = bitmap.GetPixel(20, 20);

        Assert.True(corner.Red < 200, $"karartma uygulanmamis: {corner}");
    }

    /// Bozuk görsel kapağı düşürmüyor: düz renk zaten geçerli bir
    /// kapak, ve görsel yüzünden metni de kaybetmek yanlış olurdu.
    [Fact]
    public void BozukArkaPlan_KapagiDusurmez()
    {
        var result = Renderer().Render(Request("Test", [1, 2, 3, 4, 5]));

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);
        Assert.True(result.Value.Length > 1024);
    }

    /// Aynı girdi aynı çıktıyı vermeli — kapak da render önbelleğinin
    /// parçası.
    [Fact]
    public void AyniGirdi_AyniCikti()
    {
        var first = Renderer().Render(Request("Göbeklitepe")).Value;
        var second = Renderer().Render(Request("Göbeklitepe")).Value;

        Assert.Equal(first, second);
    }

    // ---- Satır kırma ----

    /// Karakter sınırında kırmak Türkçe'de kelimeleri ortadan bölerdi
    /// ("arkeolo-jik") ve tireleme kuralı olmadan okunmaz olurdu.
    [Fact]
    public void SatirKirma_KelimeSinirinda()
    {
        using var typeface = SKTypeface.Default;
        using var font = new SKFont(typeface, 40);

        var lines = ThumbnailRenderer.WrapLines("bir iki üç dört beş altı yedi sekiz", font, 200);

        Assert.True(lines.Count > 1);
        Assert.All(lines, line => Assert.DoesNotContain("  ", line, StringComparison.Ordinal));

        // Hiçbir kelime bölünmemiş: birleştirince özgün metin çıkıyor.
        Assert.Equal("bir iki üç dört beş altı yedi sekiz", string.Join(' ', lines));
    }

    /// Tek başına sığmayan kelime kendi satırında duruyor —
    /// bölmektense taşmasına izin vermek daha az kötü.
    [Fact]
    public void SigmayanTekKelime_Bolunmez()
    {
        using var typeface = SKTypeface.Default;
        using var font = new SKFont(typeface, 40);

        var lines = ThumbnailRenderer.WrapLines("muvaffakiyetsizleştiricileştiriveremeyebileceklerimizdenmişsinizcesine", font, 100);

        Assert.Single(lines);
    }

    [Fact]
    public void TekSatirlikMetin_TekSatirKalir()
    {
        using var typeface = SKTypeface.Default;
        using var font = new SKFont(typeface, 20);

        Assert.Single(ThumbnailRenderer.WrapLines("kısa", font, 500));
    }
}
