using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Time;
using BytemountsAiStudio.Media.Timeline;

namespace BytemountsAiStudio.Media.Tests;

/// Aynı içerikten başka oran ve süre için türev (P6-03).
///
/// TÜREV TIMELINE'DAN, BİTMİŞ VİDEODAN DEĞİL. Hazır mp4'ü kırpmak ucuz
/// görünüyor ve yanlış: 9:16'lık bir videodan 16:9 kesmek karenin
/// dörtte üçünü atıyor ve altyazının tam ortasından geçiyor.
///
/// Bu testlerin çoğu KIRPMANIN NEREDEN geçtiğini sınıyor — yanlış
/// yerden kesilen bir video, kırpılmamış olmasından kötü.
public sealed class RenditionTests
{
    private static readonly Canvas Square = new(1080, 1080, 30);

    private static readonly Canvas Landscape = new(1920, 1080, 30);

    private static TimelineDocument Derive(Canvas canvas, Ms? limit = null)
    {
        var result = Rendition.Derive(
            TimelineFactory.Valid(),
            new RenditionSpec { Canvas = canvas, MaxDuration = limit });

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);

        return result.Value;
    }

    /* ---- oran değişimi ---- */

    /// TUVAL VE ÖN AYAR BİRLİKTE DEĞİŞİYOR.
    ///
    /// Ön ayar adı tuvalden türüyor; ikisinin ayrışması "bu video
    /// hangi ayarla üretildi" sorusuna yanlış cevap vermek olurdu —
    /// P3-02'de bir kez ödenmiş hata.
    [Fact]
    public void OranDegisimi_OnAyarDaDegisiyor()
    {
        var derived = Derive(Landscape);

        Assert.Equal(1920, derived.Canvas.Width);
        Assert.Equal(1080, derived.Canvas.Height);
        Assert.Equal("video-1920x1080", derived.Output.Preset);
    }

    /// YATAY TÜREVDE ANAHTAR KARE SINIRI DEVREYE GİRİYOR.
    [Fact]
    public void YatayTurev_AnahtarKareSiniri()
        => Assert.Equal(
            RenderPreset.LandscapeKeyframeSeconds,
            Derive(Landscape).Output.KeyframeIntervalSeconds);

    /// KARE TUVALDE İÇERİK AYNEN KALIYOR.
    ///
    /// Süre sınırı yoksa kırpma da yok: oran değişimi tek başına
    /// içeriği kısaltmamalı.
    [Fact]
    public void KareTuval_IcerikAynen()
    {
        var source = TimelineFactory.Valid();
        var derived = Derive(Square);

        Assert.Equal(source.Duration, derived.Duration);
        Assert.Equal(source.Scenes.Count, derived.Scenes.Count);
        Assert.Equal(source.Audio.VoiceSegments.Count, derived.Audio.VoiceSegments.Count);
        Assert.Equal(source.Captions!.Cues.Count, derived.Captions!.Cues.Count);
    }

    /// METİN STİLLERİ DOKUNULMADAN GEÇİYOR.
    ///
    /// `SizePercent` tuval yüzdesi olduğu için kendi kendine
    /// ölçekleniyor. Piksel olsaydı burada elle dönüştürmek gerekirdi
    /// ve o dönüşümü unutmak, 16:9'da minicik altyazı demekti.
    [Fact]
    public void MetinStilleri_Degismiyor()
        => Assert.Equal(TimelineFactory.Valid().Styles, Derive(Landscape).Styles);

    /// KALICI KATMAN MARJLARI ORANSAL TAŞINIYOR.
    ///
    /// `PersistentLayer` marjı belgedeki TEK tuvale bağımlı alan
    /// (piksel). 1080 genişlikte 40 piksel olan boşluk, 1920
    /// genişlikte yarı yarıya daralmış görünürdü.
    [Fact]
    public void KaliciKatmanMarji_Olcekleniyor()
    {
        var source = TimelineFactory.Valid() with
        {
            PersistentLayers =
            [
                new() { Asset = TimelineFactory.Asset('c'), Role = "logo", MarginX = 40, MarginY = 40 },
            ],
        };

        var derived = Rendition.Derive(source, new RenditionSpec { Canvas = Landscape });

        Assert.True(derived.IsSuccess, derived.IsFailure ? derived.Error.Message : string.Empty);

        // 1080 -> 1920 genişlik: marj yaklaşık iki katına çıkıyor.
        Assert.Equal(71, derived.Value.PersistentLayers[0].MarginX);

        // 1920 -> 1080 yükseklik: dikey marj yarıya iniyor.
        Assert.Equal(23, derived.Value.PersistentLayers[0].MarginY);
    }

    /// KARE TUVAL YATAY SAYILMIYOR.
    ///
    /// İlk hâlinde `IsPortrait` yanlışsa "yatay" varsayılıyordu ve
    /// 1080x1080 çıktı `video-1080x1080` diye kaydediliyordu — ad yine
    /// yalan söylüyordu, bu sefer daha sessizce. Anahtar kare sınırı da
    /// gereksiz yere devreye giriyor, dosyayı karşılıksız büyütüyordu.
    [Fact]
    public void KareTuval_KendiAdiVar()
    {
        var derived = Derive(Square);

        Assert.Equal("kare-1080x1080", derived.Output.Preset);
        Assert.Null(derived.Output.KeyframeIntervalSeconds);
    }

    /* ---- süre kırpma ---- */

    /// KIRPMA CÜMLE SINIRINDA.
    ///
    /// Belgede iki ses parçası var: 0–5 sn ve 5–12 sn. Altı saniyelik
    /// sınır beş saniyede kesiyor, altıda değil — kelimenin ortasından
    /// kesilen bir video, kırpılmamış olmasından kötü.
    [Fact]
    public void Kirpma_CumleSinirinda()
    {
        var derived = Derive(Square, new Ms(6_000));

        Assert.Equal(5_000, derived.Duration.Value);
        Assert.Single(derived.Audio.VoiceSegments);
    }

    /// SAHNELER PENCEREYE KIRPILIYOR, ATILMIYOR.
    ///
    /// Kırpmadan atmak, son sahne ile video sonu arasında boşluk
    /// bırakırdı; ffmpeg orada siyah kare üretir ve doğrulayıcı
    /// `scene.gap` diye düşer.
    [Fact]
    public void Sahneler_PencereyeKirpiliyor()
    {
        var derived = Derive(Square, new Ms(12_000) - new Ms(1));

        // 12 sn'nin bir milisaniye altındaki sınır, 5 sn'lik parça
        // sınırına oturuyor.
        Assert.Equal(5_000, derived.Duration.Value);
        Assert.Single(derived.Scenes);
        Assert.Equal(5_000, derived.Scenes[0].Range.End.Value);
    }

    /// SAHNE NUMARALARI YENİDEN VERİLİYOR.
    ///
    /// Planner girdi kimliklerini (`scene0`) bu numaradan türetiyor;
    /// türev belge kendi başına tutarlı olmalı.
    [Fact]
    public void SahneNumaralari_SifirdanBasliyor()
        => Assert.Equal([0, 1], Derive(Square).Scenes.Select(s => s.Index));

    /// KIRPILAN VİDEO ANİDEN KESİLMİYOR.
    ///
    /// Aniden biten video izleyiciye "yüklenmedi mi" dedirtiyor.
    [Fact]
    public void KirpilanVideo_KapanisGecisiVar()
    {
        var derived = Derive(Square, new Ms(6_000));

        Assert.NotNull(derived.Scenes[^1].TransitionOut);
    }

    /// SINIRA SIĞMAYAN ALTYAZI KELİMESİ DÜŞÜYOR.
    ///
    /// Bir altyazı işareti tek kelime; ortasından kesmek yarım kelime
    /// göstermek demek.
    [Fact]
    public void SiniraSigmayanAltyazi_Dusuyor()
    {
        var source = TimelineFactory.Valid();

        var derived = Rendition.Derive(source, new RenditionSpec
        {
            Canvas = Square,
            MaxDuration = new Ms(6_000),
        });

        Assert.All(derived.Value.Captions!.Cues, c => Assert.True(c.Range.End.Value <= 5_000));
    }

    /// MÜZİK SÖNÜMÜ VİDEODAN UZUN OLAMIYOR.
    [Fact]
    public void MuzikSonumu_VideoyaSigiyor()
    {
        var source = TimelineFactory.Valid() with
        {
            Audio = TimelineFactory.Valid().Audio with
            {
                Music = new MusicBed
                {
                    Asset = TimelineFactory.Asset('d'),
                    FadeOut = new Ms(9_000),
                },
            },
        };

        var derived = Rendition.Derive(source, new RenditionSpec
        {
            Canvas = Square,
            MaxDuration = new Ms(6_000),
        });

        Assert.True(derived.IsSuccess, derived.IsFailure ? derived.Error.Message : string.Empty);
        Assert.Equal(5_000, derived.Value.Audio.Music!.FadeOut.Value);
    }

    /// İLK CÜMLE SINIRDAN UZUNSA RENDITION ÜRETİLMİYOR.
    ///
    /// Yapılacak doğru şey kelimenin ortasından kesmek değil, "bu
    /// içerikten bu sürede rendition çıkmıyor" demek. Sessizce üç
    /// saniyelik bir kırpma üretmek, yayınlanabilir görünen bozuk bir
    /// video demekti.
    [Fact]
    public void IlkCumleSinirdanUzun_Reddediliyor()
    {
        var result = Rendition.Derive(
            TimelineFactory.Valid(),
            new RenditionSpec { Canvas = Square, MaxDuration = new Ms(3_000) });

        Assert.True(result.IsFailure);
        Assert.Equal("rendition.no_boundary", result.Error.Code);
    }

    /* ---- kayıt ---- */

    /// KIRPILDIĞI KAYDA GEÇİYOR.
    ///
    /// Kırpılmış bir rendition videonun tamamı sanılırsa yanlış
    /// okunur: "izlenme oranı düşük" diye rapor edilen şey aslında
    /// videonun ilk dakikası olabilir.
    [Fact]
    public void Kirpilan_KayitTutuyor()
    {
        var derived = Derive(Square, new Ms(6_000));

        Assert.Equal("0-5s / 12s", derived.Provenance!.PromptVersions["rendition.excerpt"]);
        Assert.Equal("shorts-1080x1920", derived.Provenance.PromptVersions["rendition.source"]);
    }

    /// KIRPILMAYAN TÜREVDE ALINTI KAYDI YOK.
    ///
    /// Her rendition'a "alıntı" damgası basmak, gerçekten alıntı
    /// olanları görünmez kılardı.
    [Fact]
    public void Kirpilmayan_AlintiKaydiYok()
        => Assert.DoesNotContain("rendition.excerpt", Derive(Landscape).Provenance!.PromptVersions.Keys);

    /* ---- sonuç doğrulanıyor ---- */

    /// HER TÜREV DOĞRULAYICIDAN GEÇİYOR.
    ///
    /// Türetme sırasında sahne boşluğu ya da taşan katman bırakmak
    /// kolay; bunu render sırasında "Invalid argument" olarak görmek,
    /// dakikalar sonra ve nerede olduğunu söylemeden görmek demek.
    [Theory]
    [InlineData(1080, 1080, null)]
    [InlineData(1920, 1080, null)]
    [InlineData(1080, 1920, 6000)]
    [InlineData(1920, 1080, 6000)]
    public void HerTurev_Gecerli(int width, int height, int? limitMs)
    {
        var result = Rendition.Derive(
            TimelineFactory.Valid(),
            new RenditionSpec
            {
                Canvas = new Canvas(width, height, 30),
                MaxDuration = limitMs is { } ms ? new Ms(ms) : null,
            });

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);
        Assert.Empty(TimelineValidator.Validate(result.Value));
    }
}
