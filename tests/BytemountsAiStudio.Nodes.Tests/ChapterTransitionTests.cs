using System.Text.Json;
using BytemountsAiStudio.Media.Timeline;
using BytemountsAiStudio.Nodes;

namespace BytemountsAiStudio.Nodes.Tests;

/// Bölüm geçişleri timeline'a ulaşıyor mu (P3-04).
///
/// SORUN GÖRÜNMEZLİKTİ: on dakikalık bir videoda her sahne geçişi 300
/// ms'ydi ve beş bölümlük bir belgesel, kırk sahnelik tek bir akış
/// gibi izleniyordu. Bölüm planı vardı, chapter işaretleri vardı —
/// ama İZLEYEN kişi için yapı yoktu, çünkü ekranda hiçbir şey konunun
/// değiştiğini söylemiyordu.
///
/// Bölüm sınırında geçişin uzaması, o tek görsel işaret.
public sealed class ChapterTransitionTests
{
    private const string Asset = "sha256:0000000000000000000000000000000000000000000000000000000000000002";

    private static JsonElement Context(object value) => JsonSerializer.SerializeToElement(value);

    private static object Segment(int index, int startMs, int durationMs)
        => new { id = $"s{index}", asset = Asset, start_ms = startMs, duration_ms = durationMs };

    private static object Scene(int index, int startMs, int durationMs)
        => new
        {
            scene = index,
            asset = Asset,
            start_ms = startMs,
            duration_ms = durationMs,
            segments = new[] { $"s{index}" },
            query = "sorgu",
            prompt = "istem",
        };

    /// Dört eşit sahne; bölüm ikinci sahnenin sonunda değişiyor.
    private static JsonElement FourScenes(object? chapters)
    {
        var tts = new { segments = new[] { Segment(0, 0, 5000), Segment(1, 5000, 5000), Segment(2, 10000, 5000), Segment(3, 15000, 5000) } };
        var visuals = new { images = new[] { Scene(0, 0, 5000), Scene(1, 5000, 5000), Scene(2, 10000, 5000), Scene(3, 15000, 5000) } };

        return chapters is null
            ? Context(new { topic = new { language = "tr-TR" }, tts, visuals })
            : Context(new { topic = new { language = "tr-TR" }, tts, visuals, chapters });
    }

    /// BÖLÜM SINIRINDAKİ GEÇİŞ, SIRADAN SAHNE GEÇİŞİNDEN UZUN.
    ///
    /// Testin ölçtüğü şey bir sayı değil, bir FARK: iki değer eşit
    /// olsaydı bölüm sınırı ekranda görünmezdi ve "bölüm geçişleri
    /// eklendi" iddiası doğrulanamazdı.
    [Fact]
    public void BolumSiniri_SahneGecisindenUzun()
    {
        var context = FourScenes(new
        {
            chapters = new[]
            {
                new { index = 0, title = "Birinci", start_ms = 0 },
                new { index = 1, title = "Ikinci", start_ms = 10_000 },
            },
        });

        var scenes = TimelineBuilder.Build(context).Value.Scenes;

        // Bölüm 10.000 ms'de başlıyor: 1. sahnenin sonu.
        var boundary = scenes[1].TransitionOut!.Duration;
        var ordinary = scenes[0].TransitionOut!.Duration;

        Assert.True(boundary > ordinary,
            $"Bölüm sınırı sıradan geçişle aynı: {boundary} = {ordinary}");
    }

    /// PLAN TAM TUTMASA DA SINIR BULUNUYOR.
    ///
    /// Bölüm planı bir HEDEF; sahneler gerçek seslendirme
    /// sürelerinden doğuyor ve ikisi asla tam tutmuyor. Eşitlik
    /// arayan bir kod hiçbir sınır bulamaz, her geçiş 300 ms kalır ve
    /// hata sessizce "hiçbir şey olmadı" diye görünürdü.
    [Fact]
    public void HedefKaysaBile_SinirIsaretleniyor()
    {
        var context = FourScenes(new
        {
            chapters = new[]
            {
                new { index = 0, title = "Birinci", start_ms = 0 },

                // 10.000 yerine 10.850: plandaki hedef, gerçek sahne
                // sınırına 850 ms uzakta.
                new { index = 1, title = "Ikinci", start_ms = 10_850 },
            },
        });

        var scenes = TimelineBuilder.Build(context).Value.Scenes;

        Assert.True(scenes[1].TransitionOut!.Duration > scenes[0].TransitionOut!.Duration);
    }

    /// BÖLÜM YOKSA BÜTÜN GEÇİŞLER AYNI.
    ///
    /// Kısa videoda bölüm diye bir şey yok; kod yine de bir sınır
    /// uydursaydı rastgele bir sahne uzun geçiş alırdı ve sebebi
    /// hiçbir yerde yazılı olmazdı.
    [Fact]
    public void BolumYok_GecislerEsit()
    {
        var scenes = TimelineBuilder.Build(FourScenes(null)).Value.Scenes;

        // Son sahne hariç (o kapanış), hepsi aynı.
        var middle = scenes.Take(scenes.Count - 1).Select(s => s.TransitionOut!.Duration.Value).Distinct();

        Assert.Single(middle);
    }

    /// SON SAHNENİN GEÇİŞİ KAPANIŞ, BÖLÜM SINIRI DEĞİL.
    ///
    /// Bölüm planının son bölümü videonun sonuna yakın başlıyor ve
    /// eşleştirme son sahneyi işaretleseydi kapanış, bölüm geçişi
    /// uzunluğuna KISALIRDI — yani bir iyileştirme başka bir şeyi
    /// bozardı.
    [Fact]
    public void SonSahne_KapanisUzunluguKoruyor()
    {
        var context = FourScenes(new
        {
            chapters = new[]
            {
                new { index = 0, title = "Birinci", start_ms = 0 },
                new { index = 1, title = "Son", start_ms = 20_000 },
            },
        });

        var scenes = TimelineBuilder.Build(context).Value.Scenes;

        Assert.Equal(TimelineBuilder.Closing, scenes[^1].TransitionOut!.Duration);
    }

    /// ÜRETİLEN BELGE GEÇERLİ KALIYOR.
    ///
    /// Geçiş uzunlukları sahne sürelerine bağlı; uzun bir bölüm
    /// geçişi kısa bir sahneye sığmayabilir ve doğrulayıcı bunu hata
    /// sayıyor. Bir görsel iyileştirme, belgeyi reddedilir hâle
    /// getirmemeli.
    [Fact]
    public void BolumGecisliBelge_Gecerli()
    {
        var context = FourScenes(new
        {
            chapters = new[]
            {
                new { index = 0, title = "Birinci", start_ms = 0 },
                new { index = 1, title = "Ikinci", start_ms = 10_000 },
            },
        });

        Assert.Empty(TimelineValidator.Validate(TimelineBuilder.Build(context).Value));
    }
}
