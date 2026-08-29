using BytemountsAiStudio.Core.Assets;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Time;
using BytemountsAiStudio.Media.Rendering;
using BytemountsAiStudio.Media.Timeline;

namespace BytemountsAiStudio.Media.Rendering.Tests;

/// Bölüm bazlı render (P2-11).
///
/// KABUL KRİTERİ SAYI OLARAK SINANIYOR: "tek sahne değişince yalnız o
/// segment yeniden render ediliyor" iddiası, kaç segmentin render
/// edildiği ve kaçının önbellekten geldiği görülmediği sürece bir
/// iddia. Bu testler GERÇEK ffmpeg koşuyor — planın geçerli olduğunu
/// söylemek, videonun üretildiğini söylemekle aynı şey değil.
[Collection("ffmpeg")]
public sealed class SegmentRendererTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "bmai-segment-" + Guid.CreateVersion7().ToString("N")[..8]);

    private static bool FfmpegAvailable => FfmpegProbe.Available;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    /// Tek renkli bir PNG üretir — sahne görseli yerine.
    private string Image(string name, string color)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name + ".png");

        var result = FfmpegProbe.Run(
            ["-y", "-v", "error", "-f", "lavfi", "-i", $"color=c={color}:s=540x960", "-frames:v", "1", path]);

        Assert.True(result, "gorsel uretilemedi: " + path);

        return path;
    }

    /// Sessiz bir WAV üretir — seslendirme yerine.
    private string Silence(string name, double seconds)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name + ".wav");

        var result = FfmpegProbe.Run(
            ["-y", "-v", "error", "-f", "lavfi", "-i", "anullsrc=r=48000:cl=mono",
             "-t", seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), path]);

        Assert.True(result, "ses uretilemedi: " + path);

        return path;
    }

    /// Varlık referansı İÇERİKTEN türüyor — gerçekteki gibi.
    ///
    /// İlk yazımda sahne sırasından türetmiştim ve test kırıldı: görsel
    /// değişmesine rağmen referans aynı kaldığı için önbellek üç
    /// segmenti de yeniden kullandı. Kırılan test doğruydu — depoda
    /// adres İÇERİĞİN sha256'sı (§10.1), yani farklı görsel farklı
    /// referans demek. Sahte referansı sıraya bağlamak, önbelleği
    /// gerçekte olmadığı bir şeyle sınamaktı.
    private static AssetRef Ref(string content)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(content));

        return AssetRef.Create("sha256:" + Convert.ToHexStringLower(hash));
    }

    private (TimelineDocument Timeline, Dictionary<string, string> Paths) Build(params string[] colors)
    {
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        var scenes = new List<Scene>();
        var segments = new List<VoiceSegment>();

        var voice = Ref("ses");
        paths[voice.Sha256] = Silence("ses", colors.Length * 2.0);

        segments.Add(new VoiceSegment
        {
            Id = "v0",
            Asset = voice,
            Start = new Ms(0),
            Duration = new Ms(colors.Length * 2000),
            SpeechText = "test",
        });

        for (var i = 0; i < colors.Length; i++)
        {
            var image = Ref(colors[i]);
            paths[image.Sha256] = Image($"gorsel{i}-{colors[i]}", colors[i]);

            scenes.Add(new Scene
            {
                Index = i,
                Range = new TimeRange(new Ms(i * 2000), new Ms((i + 1) * 2000)),
                VoiceSegmentIds = i == 0 ? ["v0"] : [],
                Visual = new SceneVisual { Asset = image },
            });
        }

        return (new TimelineDocument
        {
            Language = LanguageTag.Create("tr-TR"),
            Duration = new Ms(colors.Length * 2000),
            Canvas = new Canvas(540, 960, 24),
            Audio = new AudioTrack { VoiceSegments = segments },
            Scenes = scenes,
            Output = new OutputSpec { Preset = "test", PresetSpeed = "ultrafast", Crf = 30 },
        }, paths);
    }

    /// KABUL KRİTERİ: ortadaki sahnenin görseli değişince YALNIZ o
    /// segment yeniden render ediliyor.
    [Fact]
    public async Task OrtaSahneDegisti_YalnizOSegmentYenidenRenderEdiliyor()
    {
        Assert.True(FfmpegAvailable, "ffmpeg yok — bu test gerçek render koşuyor.");

        var cache = Path.Combine(_root, "onbellek");
        var renderer = new SegmentRenderer(cache, FfmpegProbe.FfmpegPath, FfmpegProbe.FfprobePath);

        var (first, paths) = Build("red", "green", "blue");

        var run1 = await renderer.RenderAsync(
            first, paths, Path.Combine(_root, "ilk.mp4"), null, CancellationToken.None);

        Assert.True(run1.IsSuccess, run1.IsFailure ? run1.Error.Message : string.Empty);
        Assert.Equal(3, run1.Value.Rendered);
        Assert.Equal(0, run1.Value.Reused);

        // İkinci koşu: yalnızca ORTADAKİ sahnenin görseli değişiyor.
        var (second, paths2) = Build("red", "yellow", "blue");

        var run2 = await renderer.RenderAsync(
            second, paths2, Path.Combine(_root, "ikinci.mp4"), null, CancellationToken.None);

        Assert.True(run2.IsSuccess, run2.IsFailure ? run2.Error.Message : string.Empty);

        // SAYI OLARAK: 1 yeniden, 2 önbellekten.
        Assert.Equal(1, run2.Value.Rendered);
        Assert.Equal(2, run2.Value.Reused);
    }

    /// Hiçbir şey değişmediyse HİÇBİR segment yeniden render edilmiyor.
    [Fact]
    public async Task DegisiklikYok_HicRenderEdilmiyor()
    {
        Assert.True(FfmpegAvailable, "ffmpeg yok — bu test gerçek render koşuyor.");

        var cache = Path.Combine(_root, "onbellek2");
        var renderer = new SegmentRenderer(cache, FfmpegProbe.FfmpegPath, FfmpegProbe.FfprobePath);

        var (timeline, paths) = Build("red", "green");

        await renderer.RenderAsync(timeline, paths, Path.Combine(_root, "a.mp4"), null, CancellationToken.None);

        var again = await renderer.RenderAsync(
            timeline, paths, Path.Combine(_root, "b.mp4"), null, CancellationToken.None);

        Assert.True(again.IsSuccess, again.IsFailure ? again.Error.Message : string.Empty);
        Assert.Equal(0, again.Value.Rendered);
        Assert.Equal(2, again.Value.Reused);
    }

    /// ÇIKTI GERÇEKTEN VİDEO VE SESİ VAR.
    ///
    /// Segmentler sessiz üretiliyor; ses birleştirmeden sonra biniyor.
    /// Bu adım atlanmış olsaydı elimizde sessiz bir video kalırdı ve
    /// "render başarılı" derdi — sessiz başarının tam tanımı.
    [Fact]
    public async Task Cikti_SesliVeDogruSurede()
    {
        Assert.True(FfmpegAvailable, "ffmpeg yok — bu test gerçek render koşuyor.");

        var cache = Path.Combine(_root, "onbellek3");
        var renderer = new SegmentRenderer(cache, FfmpegProbe.FfmpegPath, FfmpegProbe.FfprobePath);

        var (timeline, paths) = Build("red", "green", "blue");
        var output = Path.Combine(_root, "sesli.mp4");

        var result = await renderer.RenderAsync(timeline, paths, output, null, CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);
        Assert.True(File.Exists(output));

        var probe = FfmpegProbe.Probe(output);

        Assert.NotNull(probe);

        // Toplam süre 6 saniye (3 sahne × 2 sn); kodlama toleransı.
        Assert.InRange(probe.Duration, 5.5, 6.6);
        Assert.True(probe.HasAudio, "cikti sessiz");
        Assert.Equal(540, probe.Width);
    }

    /// Önbellek TEMİZLENEBİLİYOR: her koşu yeni anahtarlar üretiyor ve
    /// eski segmentler bir daha hiç kullanılmıyor. Temizlemeyi
    /// unutmak, diskin sessizce dolması demek.
    [Fact]
    public void EskiSegmentler_Temizleniyor()
    {
        var cache = Path.Combine(_root, "onbellek4");
        Directory.CreateDirectory(cache);

        var eski = Path.Combine(cache, "eski.mp4");
        var yeni = Path.Combine(cache, "yeni.mp4");

        File.WriteAllBytes(eski, [1]);
        File.WriteAllBytes(yeni, [1]);
        File.SetLastWriteTimeUtc(eski, DateTime.UtcNow.AddDays(-10));

        var renderer = new SegmentRenderer(cache);

        Assert.Equal(1, renderer.Prune(TimeSpan.FromDays(7)));
        Assert.False(File.Exists(eski));
        Assert.True(File.Exists(yeni));
    }

    /// Sahnesiz timeline KALICI hata: yeniden denemek sahne
    /// üretmiyor.
    [Fact]
    public async Task SahneYok_KaliciHata()
    {
        var renderer = new SegmentRenderer(Path.Combine(_root, "onbellek5"));

        var timeline = new TimelineDocument
        {
            Language = LanguageTag.Create("tr-TR"),
            Duration = new Ms(1000),
            Canvas = Canvas.Shorts1080,
            Audio = new AudioTrack { VoiceSegments = [] },
            Scenes = [],
            Output = new OutputSpec { Preset = "test" },
        };

        var result = await renderer.RenderAsync(
            timeline, new Dictionary<string, string>(StringComparer.Ordinal),
            Path.Combine(_root, "yok.mp4"), null, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("segment.no_scenes", result.Error.Code);
    }
}
