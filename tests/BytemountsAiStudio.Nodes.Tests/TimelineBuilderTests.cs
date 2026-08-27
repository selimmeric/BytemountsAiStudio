using System.Text.Json;
using BytemountsAiStudio.Nodes;

namespace BytemountsAiStudio.Nodes.Tests;

/// Timeline derleyici testleri (P1-19, ADR-006).
///
/// `TimelineBuilder.Build` saf bir fonksiyon: JSON alıyor, belge
/// döndürüyor. Buna rağmen uzun süre yalnızca veritabanı gerektiren
/// uçtan uca testlerle kapsanıyordu — yani veritabanı ayaktayken.
/// Buradaki testler o bağımlılığı kaldırıyor.
///
/// Asıl korunması gereken şey ses ile görselin AYRIŞMAMASI: sahne
/// sayısı ses parçası sayısından az olabiliyor (kısa cümleler
/// birleşiyor) ve birebir varsayan her kod bu durumda sessizce kayıyor.
public sealed class TimelineBuilderTests
{
    private const string Asset = "sha256:0000000000000000000000000000000000000000000000000000000000000001";

    private static JsonElement Context(object value)
        => JsonSerializer.SerializeToElement(value);

    private static object Segment(int index, int startMs, int durationMs, string text)
        => new
        {
            id = $"s{index}",
            asset = Asset,
            start_ms = startMs,
            duration_ms = durationMs,
            speech_text = text,
        };

    private static object Scene(int index, int startMs, int durationMs, params string[] segments)
        => new
        {
            scene = index,
            asset = Asset,
            start_ms = startMs,
            duration_ms = durationMs,
            segments,
            query = "sorgu",
            prompt = "istem",
        };

    [Fact]
    public void BirebirEslesme_TimelineUretir()
    {
        var context = Context(new
        {
            topic = new { language = "tr-TR" },
            script = new { prompt = "script.generate@2#abcdef" },
            tts = new
            {
                segments = new[]
                {
                    Segment(0, 0, 3000, "Birinci."),
                    Segment(1, 3000, 4000, "İkinci."),
                },
            },
            visuals = new { images = new[] { Scene(0, 0, 3000, "s0"), Scene(1, 3000, 4000, "s1") } },
        });

        var result = TimelineBuilder.Build(context);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);
        Assert.Equal(2, result.Value.Scenes.Count);
        Assert.Equal(7000, result.Value.Duration.Value);
        Assert.Equal(2, result.Value.Audio.VoiceSegments.Count);
    }

    /// ASIL TEST: üç ses parçası, iki sahne.
    ///
    /// Eskiden her ses parçası bir sahne varsayılıyordu ve sahne
    /// planlayıcı kısa cümleleri birleştirmeye başlayınca bu varsayım
    /// kırıldı. Kırılması yalnızca kısa cümle içeren senaryolarda
    /// görülecekti — seyrek ve teşhisi zor bir ses–görsel kayması.
    [Fact]
    public void BirlesmisSahne_SesiKaydirmaz()
    {
        var context = Context(new
        {
            topic = new { language = "tr-TR" },
            tts = new
            {
                segments = new[]
                {
                    Segment(0, 0, 500, "Kısa."),
                    Segment(1, 500, 4000, "Bu uzun bir cümle."),
                    Segment(2, 4500, 4000, "Bu da uzun."),
                },
            },
            visuals = new
            {
                images = new[]
                {
                    // İlk iki ses parçası tek sahnede birleşti.
                    Scene(0, 0, 4500, "s0", "s1"),
                    Scene(1, 4500, 4000, "s2"),
                },
            },
        });

        var result = TimelineBuilder.Build(context);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);

        // Sahne sayısı sesten AZ, ve bu bir hata değil.
        Assert.Equal(2, result.Value.Scenes.Count);
        Assert.Equal(3, result.Value.Audio.VoiceSegments.Count);

        // Birleşen sahne iki ses parçasını da sahipleniyor.
        Assert.Equal(["s0", "s1"], result.Value.Scenes[0].VoiceSegmentIds);
        Assert.Equal(["s2"], result.Value.Scenes[1].VoiceSegmentIds);

        // Ve zamanlama kaymamış: ikinci sahne, ikinci sesin bittiği yerde.
        Assert.Equal(4500, result.Value.Scenes[1].Range.Start.Value);
        Assert.Equal(8500, result.Value.Duration.Value);
    }

    /// Sahnelerin toplamı sesin toplamına eşit olmalı; eşit değilse
    /// bir yerde süre uydurulmuş ya da düşürülmüş demektir.
    [Fact]
    public void SahneSureleriToplami_SesToplamiylaAyni()
    {
        var context = Context(new
        {
            topic = new { language = "tr-TR" },
            tts = new
            {
                segments = new[]
                {
                    Segment(0, 0, 700, "a"),
                    Segment(1, 700, 800, "b"),
                    Segment(2, 1500, 5000, "c"),
                },
            },
            visuals = new { images = new[] { Scene(0, 0, 1500, "s0", "s1"), Scene(1, 1500, 5000, "s2") } },
        });

        var result = TimelineBuilder.Build(context);

        Assert.Equal(6500, result.Value.Duration.Value);
        Assert.Equal(6500, result.Value.Scenes.Sum(s => s.Range.Duration.Value));
    }

    /// Sahneler boşluksuz ard arda gelmeli; boşluk siyah kare demek.
    [Fact]
    public void Sahneler_BosluksuzArdArda()
    {
        var context = Context(new
        {
            topic = new { language = "tr-TR" },
            tts = new { segments = new[] { Segment(0, 0, 3000, "a"), Segment(1, 3000, 3000, "b") } },
            visuals = new { images = new[] { Scene(0, 0, 3000, "s0"), Scene(1, 3000, 3000, "s1") } },
        });

        var scenes = TimelineBuilder.Build(context).Value.Scenes;

        Assert.Equal(scenes[0].Range.End, scenes[1].Range.Start);
    }

    /// Eski bir run bağlamı yeniden derlendiğinde kırılmasın: plan
    /// bilgisi yoksa birebir eşleşmeye düşülüyor.
    [Fact]
    public void PlanBilgisiYoksa_BirebirEslesmeyeDusulur()
    {
        var context = Context(new
        {
            topic = new { language = "tr-TR" },
            tts = new { segments = new[] { Segment(0, 0, 3000, "a"), Segment(1, 3000, 4000, "b") } },
            visuals = new
            {
                images = new[]
                {
                    new { scene = 0, asset = Asset },
                    new { scene = 1, asset = Asset },
                },
            },
        });

        var result = TimelineBuilder.Build(context);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);
        Assert.Equal(2, result.Value.Scenes.Count);
        Assert.Equal(7000, result.Value.Duration.Value);
        Assert.Equal(["s0"], result.Value.Scenes[0].VoiceSegmentIds);
    }

    [Fact]
    public void SesiOlmayanSahne_Reddedilir()
    {
        var context = Context(new
        {
            topic = new { language = "tr-TR" },
            tts = new { segments = new[] { Segment(0, 0, 3000, "a") } },
            visuals = new
            {
                images = new[]
                {
                    new { scene = 0, asset = Asset },
                    new { scene = 1, asset = Asset },
                },
            },
        });

        var result = TimelineBuilder.Build(context);

        Assert.True(result.IsFailure);
        Assert.Equal("timeline.scene_without_audio", result.Error.Code);
    }

    [Fact]
    public void SesYoksa_Reddedilir()
    {
        var result = TimelineBuilder.Build(Context(new { topic = new { language = "tr-TR" } }));

        Assert.True(result.IsFailure);
        Assert.Equal("timeline.no_audio", result.Error.Code);
    }

    [Fact]
    public void GorselYoksa_Reddedilir()
    {
        var context = Context(new
        {
            topic = new { language = "tr-TR" },
            tts = new { segments = new[] { Segment(0, 0, 3000, "a") } },
        });

        var result = TimelineBuilder.Build(context);

        Assert.True(result.IsFailure);
        Assert.Equal("timeline.no_visuals", result.Error.Code);
    }

    /// Altyazı ipuçları MUTLAK zamanda geliyor; kaydırmayı unutmak tüm
    /// altyazıyı videonun başına toplardı.
    [Fact]
    public void AltyaziIpuclari_Aktarilir()
    {
        var context = Context(new
        {
            topic = new { language = "tr-TR" },
            tts = new
            {
                segments = new[] { Segment(0, 0, 3000, "a") },
                cues = new[]
                {
                    new { text = "Merhaba", start_ms = 0, end_ms = 500 },
                    new { text = "dünya", start_ms = 500, end_ms = 1200 },
                },
            },
            visuals = new { images = new[] { Scene(0, 0, 3000, "s0") } },
        });

        var captions = TimelineBuilder.Build(context).Value.Captions;

        Assert.NotNull(captions);
        Assert.Equal(2, captions.Cues.Count);
        Assert.Equal(500, captions.Cues[1].Range.Start.Value);
    }

    /// P1-07: hangi istem sürümüyle üretildiği belgede duruyor.
    [Fact]
    public void IstemDamgasi_BelgeyeTasinir()
    {
        var context = Context(new
        {
            topic = new { language = "tr-TR" },
            script = new { prompt = "script.generate@2#759d10ffd75efc0c" },
            tts = new { segments = new[] { Segment(0, 0, 3000, "a") } },
            visuals = new { images = new[] { Scene(0, 0, 3000, "s0") } },
        });

        var provenance = TimelineBuilder.Build(context).Value.Provenance;

        Assert.NotNull(provenance);
        Assert.Equal("script.generate@2#759d10ffd75efc0c", provenance.PromptVersions["script.generate"]);
    }

    /// Dil belgeye taşınmalı: yazı yönü ve font seçimi buna bağlı.
    [Fact]
    public void Dil_BelgeyeTasinir()
    {
        var context = Context(new
        {
            topic = new { language = "en-US" },
            tts = new { segments = new[] { Segment(0, 0, 3000, "a") } },
            visuals = new { images = new[] { Scene(0, 0, 3000, "s0") } },
        });

        var document = TimelineBuilder.Build(context).Value;

        Assert.Equal("en-US", document.Language.Value);
        Assert.False(document.RightToLeft);
    }

    /// Son sahnede geçiş olmamalı: bir yere geçmiyor.
    [Fact]
    public void SonSahnede_GecisYok()
    {
        var context = Context(new
        {
            topic = new { language = "tr-TR" },
            tts = new { segments = new[] { Segment(0, 0, 3000, "a"), Segment(1, 3000, 3000, "b") } },
            visuals = new { images = new[] { Scene(0, 0, 3000, "s0"), Scene(1, 3000, 3000, "s1") } },
        });

        var scenes = TimelineBuilder.Build(context).Value.Scenes;

        Assert.NotNull(scenes[0].TransitionOut);
        Assert.Null(scenes[1].TransitionOut);
    }

    /// Görseller paralel üretildiği için sıra karışık gelebiliyor.
    [Fact]
    public void SahneSirasiKarisikGelirse_Siralanir()
    {
        var context = Context(new
        {
            topic = new { language = "tr-TR" },
            tts = new { segments = new[] { Segment(0, 0, 3000, "a"), Segment(1, 3000, 4000, "b") } },
            visuals = new { images = new[] { Scene(1, 3000, 4000, "s1"), Scene(0, 0, 3000, "s0") } },
        });

        var scenes = TimelineBuilder.Build(context).Value.Scenes;

        Assert.Equal(0, scenes[0].Range.Start.Value);
        Assert.Equal(3000, scenes[1].Range.Start.Value);
    }
}
