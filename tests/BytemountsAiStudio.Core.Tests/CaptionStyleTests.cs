using System.Text.Json;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Execution;

namespace BytemountsAiStudio.Core.Tests;

/// Altyazı stili ve müzik seviyelerinin kanal ayarından okunması (P3-01).
///
/// ***BU DOSYANIN VAR OLMA SEBEBİ:*** stilin tamamı `TimelineBuilder`
/// içinde SABİTTİ. İki kanal aynı graftan koşunca altyazılar piksel
/// piksel aynı çıkıyordu — kanal kimliğinin en görünür parçası
/// değiştirilemiyordu. Üstelik `TextStyle`'ın kendi yorumu "bir kanalın
/// altyazı stilini değiştirmek tek satır olsun" diye söz veriyordu.
public sealed class CaptionStyleTests
{
    private static ChannelSettings Parse(string json) => ChannelSettings.Parse(json);

    /* ---- varsayılanlar ---- */

    /// ***AYAR YAZMAYAN KANAL DÜNKÜ VİDEOYU ÜRETİYOR.***
    ///
    /// BU DOSYANIN EN ÖNEMLİ TESTİ. Varsayılanlar eski sabit değerlerin
    /// AYNISI olmasaydı bu iş bir yapılandırma açması değil, sessiz bir
    /// davranış değişikliği olurdu: her kanalın altyazısı bir sürümde
    /// kendiliğinden değişirdi.
    [Fact]
    public void AyarYok_EskiSabitDegerler()
    {
        var settings = Parse("{}");

        Assert.Equal(5.5, settings.Captions.SizePercent);
        Assert.Equal("#FFFFFF", settings.Captions.Color);
        Assert.Equal("#FFD400", settings.Captions.HighlightColor);
        Assert.Equal("#000000", settings.Captions.StrokeColor);
        Assert.Equal(8, settings.Captions.StrokeWidth);
        Assert.Equal("#000000", settings.Captions.BoxColor);
        Assert.Equal(0.35, settings.Captions.BoxOpacity);
        Assert.Equal("bottom_center", settings.Captions.Position);
        Assert.Equal(22, settings.Captions.OffsetPercent);
        Assert.Equal(2, settings.Captions.MaxLines);
        Assert.True(settings.Captions.Bold);

        Assert.Equal(-22.0, settings.Music.GainDb);
        Assert.Equal(-30.0, settings.Music.DuckingDb);
        Assert.True(settings.Music.Ducking);
        Assert.Equal(1200, settings.Music.FadeInMs);
        Assert.Equal(2000, settings.Music.FadeOutMs);
    }

    /* ---- okuma ---- */

    /// KANAL AYARI OKUNUYOR.
    [Fact]
    public void KanalAyari_Okunuyor()
    {
        var settings = Parse("""
            {
              "caption_style": {
                "font_family": "Noto Sans Arabic",
                "size_percent": 7.5,
                "color": "#FFEE00",
                "position": "center",
                "max_lines": 3,
                "bold": false
              }
            }
            """);

        Assert.Equal("Noto Sans Arabic", settings.Captions.FontFamily);
        Assert.Equal(7.5, settings.Captions.SizePercent);
        Assert.Equal("#FFEE00", settings.Captions.Color);
        Assert.Equal("center", settings.Captions.Position);
        Assert.Equal(3, settings.Captions.MaxLines);
        Assert.False(settings.Captions.Bold);

        // ALTYAZI TARAFINDAN UYARI YOK. Belgenin tamamı için `Empty`
        // demek yanlış olurdu: ayarın başka blokları (`daily_target`)
        // kendi uyarılarını üretiyor ve bu testin konusu değil.
        Assert.DoesNotContain(settings.Warnings,
            w => w.Contains("caption_style", StringComparison.Ordinal));
    }

    /// MÜZİK SEVİYELERİ OKUNUYOR.
    ///
    /// Platform farkı böyle ifade edilebiliyor: YouTube -14 LUFS'a
    /// normalize ediyor, podcast çıktısı başka bir hedef istiyor.
    [Fact]
    public void MuzikSeviyeleri_Okunuyor()
    {
        var settings = Parse("""
            {"music": {"gain_db": -18, "ducking_db": -26, "fade_in_ms": 500, "fade_out_ms": 3000}}
            """);

        Assert.Equal(-18, settings.Music.GainDb);
        Assert.Equal(-26, settings.Music.DuckingDb);
        Assert.Equal(500, settings.Music.FadeInMs);
        Assert.Equal(3000, settings.Music.FadeOutMs);
        Assert.True(settings.Music.Ducking);
    }

    /// DUCKING KAPATILABİLİYOR — AMA AÇIK BİR KARARLA.
    [Fact]
    public void Ducking_Kapatilabiliyor()
        => Assert.False(Parse("""{"music": {"ducking": false}}""").Music.Ducking);

    /* ---- doğrulama ---- */

    /// ***TANINMAYAN KONUM SESSİZCE VARSAYILANA DÜŞMÜYOR.***
    ///
    /// "bottom-center" yazan biri (alt çizgi yerine tire) altyazısının
    /// neden yer değiştirmediğini asla anlayamazdı. Uyarı, ayar
    /// belgesinin okunduğu her yerde görünüyor.
    [Fact]
    public void TaninmayanKonum_UyariUretiyor()
    {
        var settings = Parse("""{"caption_style": {"position": "bottom-center"}}""");

        Assert.Equal("bottom_center", settings.Captions.Position);
        Assert.Contains(settings.Warnings, w => w.Contains("position", StringComparison.Ordinal));
    }

    /// GEÇERSİZ RENK UYARI ÜRETİYOR.
    ///
    /// Doğrulanmasaydı `"kirmizi"` yazan biri hiçbir uyarı almadan
    /// varsayılan beyaz altyazı görürdü.
    [Fact]
    public void GecersizRenk_UyariUretiyor()
    {
        var settings = Parse("""{"caption_style": {"color": "kirmizi"}}""");

        Assert.Equal("#FFFFFF", settings.Captions.Color);
        Assert.Contains(settings.Warnings, w => w.Contains("color", StringComparison.Ordinal));
    }

    /// ***MÜZİK KONUŞMANIN ÜSTÜNE ÇIKAMIYOR.***
    ///
    /// `gain_db: 6` yazan biri müziği konuşmanın üstüne çıkarırdı ve
    /// videoyu dinlemeden bunu kimse fark etmezdi. Üst sınır sıfır.
    [Fact]
    public void MuzikKazanci_SifirinUstune_Cikamiyor()
    {
        var settings = Parse("""{"music": {"gain_db": 6}}""");

        Assert.Equal(-22.0, settings.Music.GainDb);
        Assert.Contains(settings.Warnings, w => w.Contains("gain_db", StringComparison.Ordinal));
    }

    /// ARALIK DIŞI PUNTO VARSAYILANA DÜŞÜYOR VE UYARIYOR.
    [Fact]
    public void ArailkDisiPunto_UyariUretiyor()
    {
        var settings = Parse("""{"caption_style": {"size_percent": 90}}""");

        Assert.Equal(5.5, settings.Captions.SizePercent);
        Assert.NotEmpty(settings.Warnings);
    }

    /// BLOK NESNE DEĞİLSE VARSAYILAN — ÇÖKMÜYOR.
    [Fact]
    public void BlokNesneDegil_Varsayilan()
    {
        var settings = Parse("""{"caption_style": "buyuk", "music": 5}""");

        Assert.Equal(CaptionStyle.Default, settings.Captions);
        Assert.Equal(MusicLevels.Default, settings.Music);
    }

    /// RENK AÇIKÇA `null` YAZILIRSA KUTU KAPANIYOR.
    ///
    /// "Alan yok" ile "alan null" farklı: birincisi varsayılan kutu,
    /// ikincisi kutusuz altyazı. Ayırt edilmeseydi kutuyu kaldırmanın
    /// hiçbir yolu olmazdı.
    [Fact]
    public void RenkAcikcaNull_KutuKapaniyor()
    {
        var settings = Parse("""{"caption_style": {"box_color": null}}""");

        Assert.Null(settings.Captions.BoxColor);
    }

    /* ---- konumlar ---- */

    /// BÜTÜN KONUM ADLARI KABUL EDİLİYOR.
    ///
    /// Liste ile doğrulama aynı yerden geliyor; ayrışsalardı belgede
    /// yazan bir değer kodda reddedilirdi.
    [Fact]
    public void ButunKonumlar_KabulEdiliyor()
    {
        foreach (var position in CaptionStyle.Positions)
        {
            var json = JsonSerializer.Serialize(new { caption_style = new { position } });
            var settings = Parse(json);

            Assert.Equal(position, settings.Captions.Position);
            Assert.DoesNotContain(settings.Warnings,
                w => w.Contains("position", StringComparison.Ordinal));
        }
    }
}
