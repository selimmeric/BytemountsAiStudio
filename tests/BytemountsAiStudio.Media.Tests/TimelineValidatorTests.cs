using BytemountsAiStudio.Core.Time;
using BytemountsAiStudio.Media.Timeline;

namespace BytemountsAiStudio.Media.Tests;

/// Doğrulayıcının işi: render edilemeyecek bir belgeyi FFmpeg'e hiç
/// göndermemek. Aşağıdaki her test, gerçekte karşılaşılacak bir bozulmayı
/// temsil ediyor — hepsi milisaniyede yakalanıyor.
public sealed class TimelineValidatorTests
{
    private static string[] Codes(TimelineDocument t)
        => TimelineValidator.Validate(t).Select(i => i.Code).ToArray();

    [Fact]
    public void GecerliBelge_SorunUretmez()
    {
        var issues = TimelineValidator.Validate(TimelineFactory.Valid());

        Assert.True(issues.Count == 0,
            "Geçerli belge sorunsuz olmalıydı: " + string.Join(" | ", issues));
    }

    [Fact]
    public void CakisanSahneler_Yakalanir()
    {
        var t = TimelineFactory.Valid();
        var scenes = t.Scenes.ToList();
        scenes[0] = scenes[0] with { Range = new TimeRange(Ms.Zero, new Ms(6_000)) };

        Assert.Contains("scene.overlap", Codes(t with { Scenes = scenes }));
    }

    [Fact]
    public void SahnelerArasiBosluk_Yakalanir()
    {
        // Boşlukta ekranda hiçbir şey yok; FFmpeg siyah kare üretir ve bu
        // neredeyse her zaman bir hatadır.
        var t = TimelineFactory.Valid();
        var scenes = t.Scenes.ToList();
        scenes[0] = scenes[0] with { Range = new TimeRange(Ms.Zero, new Ms(4_000)) };

        Assert.Contains("scene.gap", Codes(t with { Scenes = scenes }));
    }

    [Fact]
    public void NegatifSure_Yakalanir()
    {
        var t = TimelineFactory.Valid() with { Duration = new Ms(0) };

        Assert.Contains("timeline.duration", Codes(t));
    }

    [Fact]
    public void SahnelerVideoyuKaplamiyorsa_Yakalanir()
    {
        var t = TimelineFactory.Valid() with { Duration = new Ms(20_000) };
        var codes = Codes(t);

        Assert.Contains("scene.short_coverage", codes);
    }

    [Fact]
    public void CakisanSesParcalari_Yakalanir()
    {
        // İki ses üst üste binerse izleyici iki kişiyi aynı anda duyar.
        var t = TimelineFactory.Valid();
        var segments = t.Audio.VoiceSegments.ToList();
        segments[1] = segments[1] with { Start = new Ms(4_000) };

        var codes = Codes(t with { Audio = t.Audio with { VoiceSegments = segments } });

        Assert.Contains("audio.overlap", codes);
    }

    [Fact]
    public void OlmayanSesParcasinaReferans_Yakalanir()
    {
        var t = TimelineFactory.Valid();
        var scenes = t.Scenes.ToList();
        scenes[0] = scenes[0] with { VoiceSegmentIds = ["yok-boyle-bir-parca"] };

        Assert.Contains("scene.unknown_segment", Codes(t with { Scenes = scenes }));
    }

    [Fact]
    public void TanimsizStileReferans_Yakalanir()
    {
        var t = TimelineFactory.Valid();
        var scenes = t.Scenes.ToList();
        scenes[0] = scenes[0] with
        {
            Overlays = [scenes[0].Overlays[0] with { StyleRef = "olmayan-stil" }],
        };

        Assert.Contains("style.missing", Codes(t with { Scenes = scenes }));
    }

    [Fact]
    public void SahneDisinaTasanOverlay_Yakalanir()
    {
        var t = TimelineFactory.Valid();
        var scenes = t.Scenes.ToList();
        scenes[0] = scenes[0] with
        {
            Overlays = [scenes[0].Overlays[0] with { Range = new TimeRange(new Ms(400), new Ms(9_000)) }],
        };

        Assert.Contains("overlay.outside_scene", Codes(t with { Scenes = scenes }));
    }

    [Fact]
    public void OlcekBirinAltinda_Yakalanir()
    {
        // Kadrajı dolduramaz, kenarlarda siyah şerit oluşur.
        var t = TimelineFactory.Valid();
        var scenes = t.Scenes.ToList();
        scenes[0] = scenes[0] with
        {
            Visual = scenes[0].Visual with
            {
                Motion = new KenBurns { FromScale = 0.9, ToScale = 1.1 },
            },
        };

        Assert.Contains("motion.scale_below_one", Codes(t with { Scenes = scenes }));
    }

    [Fact]
    public void AralikDisiPan_Yakalanir()
    {
        var t = TimelineFactory.Valid();
        var scenes = t.Scenes.ToList();
        scenes[0] = scenes[0] with
        {
            Visual = scenes[0].Visual with
            {
                Motion = new KenBurns { FromScale = 1.0, ToScale = 1.1, ToX = 1.8 },
            },
        };

        Assert.Contains("motion.pan_out_of_range", Codes(t with { Scenes = scenes }));
    }

    [Fact]
    public void CakisanAltyazilar_Yakalanir()
    {
        var t = TimelineFactory.Valid();
        var cues = t.Captions!.Cues.ToList();
        cues[1] = cues[1] with { Range = new TimeRange(new Ms(300), new Ms(980)) };

        Assert.Contains("caption.overlap", Codes(t with { Captions = t.Captions with { Cues = cues } }));
    }

    [Fact]
    public void SahneninGecisiSahnedenUzunsa_Yakalanir()
    {
        var t = TimelineFactory.Valid();
        var scenes = t.Scenes.ToList();
        scenes[0] = scenes[0] with { TransitionOut = new Transition(TransitionKind.Fade, new Ms(9_000)) };

        Assert.Contains("transition.too_long", Codes(t with { Scenes = scenes }));
    }

    [Fact]
    public void BosFontZinciri_Yakalanir()
    {
        // Zincir boşsa eksik glif yerine tofu çizilir ve bunu ancak izleyici görür.
        Assert.Contains("timeline.no_font", Codes(TimelineFactory.Valid() with { FontStack = [] }));
    }

    [Fact]
    public void DesteklenmeyenSemaSurumu_Yakalanir()
    {
        Assert.Contains("timeline.schema_version", Codes(TimelineFactory.Valid() with { SchemaVersion = 99 }));
    }

    [Fact]
    public void TekrarlananSesKimligi_Yakalanir()
    {
        var t = TimelineFactory.Valid();
        var segments = t.Audio.VoiceSegments.ToList();
        segments[1] = segments[1] with { Id = "s1" };

        var codes = Codes(t with { Audio = t.Audio with { VoiceSegments = segments } });

        Assert.Contains("audio.duplicate_id", codes);
    }
}
