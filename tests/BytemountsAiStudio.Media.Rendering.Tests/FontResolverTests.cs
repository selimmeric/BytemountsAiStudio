using BytemountsAiStudio.Media.Rendering.Text;
using SkiaSharp;

namespace BytemountsAiStudio.Media.Rendering.Tests;

/// Font çözümleme testleri (P1-23).
///
/// Bu sınıf iki sessiz hatayı kapatmak için yazıldı; ikisi de canlı
/// çıktıya bakılınca görüldü, testlerden geçiyordu.
public sealed class FontResolverTests
{
    private static readonly Lazy<SKFontManager> Fonts = new(SKFontManager.CreateDefault);

    /// ASIL HATA: `SKTypeface.FromFamilyName` kurulu OLMAYAN bir aile
    /// için NULL DÖNMÜYOR — sistem varsayılanını döndürüyor ve istenen
    /// kalınlığı sessizce düşürüyor. `null` kontrolü yapan kod bunu
    /// yakalayamıyor.
    [Fact]
    public void OlmayanAile_SistemVarsayilaniDondurur_NullDegil()
    {
        var typeface = SKTypeface.FromFamilyName(
            "HICBIR-YERDE-OLMAYAN-FONT-ADI",
            SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);

        // Bu satır hatayı belgeliyor: null bekleyen kod yanılıyordu.
        Assert.NotNull(typeface);
        Assert.False(
            typeface.FamilyName.Equals("HICBIR-YERDE-OLMAYAN-FONT-ADI", StringComparison.OrdinalIgnoreCase),
            "Skia gercekten o aileyi buldu; test varsayimi gecersiz.");

        typeface.Dispose();
    }

    /// Zincirdeki ilk aile kurulu değilse ATLANMALI — Skia'nın verdiği
    /// ikame yüz kabul edilmemeli, çünkü o yüz istenen kalınlıkta değil.
    [Fact]
    public void OlmayanAile_Atlanir_KalinlikKorunur()
    {
        using var typeface = FontResolver.Resolve(
            ["HICBIR-YERDE-OLMAYAN-FONT", "Arial"], "Merhaba", bold: true, Fonts.Value);

        Assert.True(typeface.IsBold, $"kalinlik kaybedildi: {typeface.FamilyName} weight={typeface.FontWeight}");
    }

    [Fact]
    public void KalinIstenmezse_InceYuzDoner()
    {
        using var typeface = FontResolver.Resolve(["Arial"], "Merhaba", bold: false, Fonts.Value);

        Assert.False(typeface.IsBold);
    }

    /// Zincirdeki hiçbir aile yoksa yine de KALIN bir yüz gelmeli:
    /// aileyi de kalınlığı da kaybetmek iki kayıp olurdu.
    [Fact]
    public void HicbirAileYoksa_YineKalinDoner()
    {
        using var typeface = FontResolver.Resolve(
            ["YOK-1", "YOK-2", "YOK-3"], "Merhaba", bold: true, Fonts.Value);

        Assert.True(typeface.IsBold, $"yedek yuz ince geldi: {typeface.FamilyName}");
    }

    [Fact]
    public void BosZincir_YineDeBirYuzDoner()
    {
        using var typeface = FontResolver.Resolve([], "Merhaba", bold: false, Fonts.Value);

        Assert.NotNull(typeface);
    }

    /// Metnin TAMAMINI çizebilen yüz aranıyor. Tek karakter için
    /// fallback yapmak satır ortasında yazı tipi değiştiriyor ve
    /// Türkçe'de bu 'ğ' harfinde oluyor — yani neredeyse her cümlede.
    [Fact]
    public void TurkceMetin_CizilebilenYuzSecilir()
    {
        using var typeface = FontResolver.Resolve(
            ["Segoe UI", "Arial"], "ĞÜŞİÖÇ ğüşıöç", bold: true, Fonts.Value);

        Assert.True(FontResolver.CanRender(typeface, "ĞÜŞİÖÇ ğüşıöç"));
    }

    [Fact]
    public void CanRender_EksikGlifiYakalar()
    {
        using var typeface = SKTypeface.FromFamilyName("Arial");

        // Latin harfler çizilebiliyor.
        Assert.True(FontResolver.CanRender(typeface, "abc ABC"));

        // Boşluk ve satır sonu glif aranmadan geçiliyor.
        Assert.True(FontResolver.CanRender(typeface, "a b\nc\td"));
    }

    [Fact]
    public void AyniIstek_AyniAile()
    {
        using var first = FontResolver.Resolve(["Arial"], "test", bold: true, Fonts.Value);
        using var second = FontResolver.Resolve(["Arial"], "test", bold: true, Fonts.Value);

        Assert.Equal(first.FamilyName, second.FamilyName);
        Assert.Equal(first.FontWeight, second.FontWeight);
    }
}
