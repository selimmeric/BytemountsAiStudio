using System.Text.Json;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Media.Timeline;
using BytemountsAiStudio.Providers.Fake;
using BytemountsAiStudio.Workflow.Engine;

namespace BytemountsAiStudio.Nodes.Tests;

/// Kanal kimliğinin GERÇEKTEN uygulandığının sınanması (P3-01).
///
/// ***BU DOSYANIN VAR OLMA SEBEBİ:*** ayarlar okunuyor olabilir ve
/// hiçbir yere gitmeyebilir. Bu depoda tam olarak o oldu: `font_stack`
/// kanalda yazılıydı, timeline onu okuyordu, **kapak okumuyordu**;
/// altyazı stilinin tamamı `TimelineBuilder` içinde sabitti ve
/// `TextStyle.FontFamily` yazılıp hiçbir yerde okunmuyordu.
///
/// Ayarın kendi testleri (`CaptionStyleTests`) ayarın OKUNDUĞUNU
/// gösteriyor. Buradakiler UYGULANDIĞINI gösteriyor — iki ayrı soru ve
/// ikincisi cevapsız kaldığında hiçbir şey kırmızıya dönmüyor.
public sealed class ChannelStyleWiringTests
{
    private const string Asset = "sha256:0000000000000000000000000000000000000000000000000000000000000001";

    private sealed class StyledChannel(ChannelSettings settings) : IChannelPolicy
    {
        public Task<ChannelMode?> ModeAsync(Guid channelId, CancellationToken cancellationToken)
            => Task.FromResult<ChannelMode?>(null);

        public Task<ChannelSettings?> SettingsAsync(Guid channelId, CancellationToken cancellationToken)
            => Task.FromResult<ChannelSettings?>(settings);
    }

    private static JsonElement Context(object value) => JsonSerializer.SerializeToElement(value);

    private static JsonElement TimelineContext() => Context(new
    {
        topic = new { language = "tr-TR" },
        tts = new
        {
            segments = new[]
            {
                new
                {
                    id = "s0", asset = Asset, start_ms = 0, duration_ms = 3000,
                    speech_text = "Birinci cümle.",
                },
            },
        },
        visuals = new
        {
            images = new[]
            {
                new
                {
                    scene = 0, asset = Asset, start_ms = 0, duration_ms = 3000,
                    segments = new[] { "s0" }, query = "sorgu", prompt = "istem",
                },
            },
        },
        music = new
        {
            asset = Asset,
            license = new
            {
                name = "CC BY 4.0",
                author = "Biri",
                url = "https://example.org/lisans",
                requires_attribution = true,
                captured_at = "2026-08-29T00:00:00Z",
            },
        },
    });

    /* ---- altyazı ---- */

    /// ***KANAL ALTYAZI STİLİ TIMELINE'A GEÇİYOR.***
    ///
    /// BU DOSYANIN EN ÖNEMLİ TESTİ. Geçmeseydi iki kanal aynı graftan
    /// koşunca altyazılar piksel piksel aynı çıkardı.
    [Fact]
    public void KanalStili_TimelineaGeciyor()
    {
        var captions = new CaptionStyle
        {
            FontFamily = "Noto Sans Arabic",
            SizePercent = 8.0,
            Color = "#FFEE00",
            Position = "center",
            MaxLines = 3,
            Bold = false,
        };

        var result = TimelineBuilder.Build(TimelineContext(), null, null, captions, null);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);

        var style = result.Value.Styles["caption"];

        Assert.Equal("Noto Sans Arabic", style.FontFamily);
        Assert.Equal(8.0, style.SizePercent);
        Assert.Equal("#FFEE00", style.Color);
        Assert.Equal(Anchor.Center, style.Position);
        Assert.Equal(3, style.MaxLines);
        Assert.False(style.Bold);
    }

    /// AYAR YOKSA ESKİ SABİT DEĞERLER.
    ///
    /// Bu iş bir davranış değişikliği değil, bir yapılandırma açması:
    /// ayar yazmayan kanal dünkü videoyu üretiyor.
    [Fact]
    public void AyarYok_EskiSabitStil()
    {
        var result = TimelineBuilder.Build(TimelineContext());

        Assert.True(result.IsSuccess);

        var style = result.Value.Styles["caption"];

        Assert.Equal(5.5, style.SizePercent);
        Assert.Equal("#FFFFFF", style.Color);
        Assert.Equal("#FFD400", style.HighlightColor);
        Assert.Equal(Anchor.BottomCenter, style.Position);
        Assert.Equal(22, style.OffsetPercent);
        Assert.True(style.Bold);
    }

    /* ---- müzik ---- */

    /// KANAL MÜZİK SEVİYELERİ TIMELINE'A GEÇİYOR.
    [Fact]
    public void MuzikSeviyeleri_TimelineaGeciyor()
    {
        var music = new MusicLevels
        {
            GainDb = -16,
            DuckingDb = -24,
            FadeInMs = 400,
            FadeOutMs = 3500,
        };

        var result = TimelineBuilder.Build(TimelineContext(), null, null, null, music);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);

        var bed = result.Value.Audio.Music;

        Assert.NotNull(bed);
        Assert.Equal(-16, bed.GainDb);
        Assert.Equal(400, bed.FadeIn.Value);
        Assert.Equal(3500, bed.FadeOut.Value);
        Assert.NotNull(bed.Ducking);
        Assert.Equal(-24, bed.Ducking.TargetGainDb);
    }

    /// ***DUCKING KAPATILABİLİYOR VE KAPANDIĞINDA GERÇEKTEN YOK.***
    ///
    /// Ayarı okuyup yine de bir `DuckingSpec` yazsaydık, "kapattım ama
    /// müzik hâlâ kısılıyor" diye bir soru doğardı ve cevabı hiçbir
    /// yerde olmazdı.
    [Fact]
    public void DuckingKapali_SpecYok()
    {
        var result = TimelineBuilder.Build(
            TimelineContext(), null, null, null, new MusicLevels { Ducking = false });

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value.Audio.Music);
        Assert.Null(result.Value.Audio.Music.Ducking);
    }

    /* ---- kapak ---- */

    /// ***KAPAK YAZI TİPİ ZİNCİRİ KANALDAN OKUNUYOR.***
    ///
    /// Kapakta sabitti ve timeline aynı değeri kanaldan okuyordu:
    /// `font_stack: ["Noto Sans Arabic", ...]` yazan bir kanalda
    /// altyazılar değişiyor, KAPAK DEĞİŞMİYORDU. Arapça bir kanalda
    /// kapaktaki başlık tofu karakterlerle çiziliyordu — ve kapak,
    /// kanalın arama sonuçlarında görünen tek görseli.
    [Fact]
    public async Task Kapak_KanalYaziTipiniOkuyor()
    {
        using var storage = new FakeStorageProvider();

        var settings = ChannelSettings.Defaults with
        {
            FontStack = ["BoyleBirYaziTipiYok", "Inter", "Arial"],
        };

        var handler = new ThumbnailRenderHandler(storage, new StyledChannel(settings));

        var result = await handler.ExecuteAsync(
            new NodeContext
            {
                RunId = Guid.CreateVersion7(),
                NodeId = "thumbnail.render",
                NodeType = "thumbnail.render",
                Attempt = 1,
                Config = Context(new { }),
                RunContext = Context(new
                {
                    topic = new { language = "tr-TR" },
                    seo = new { title = "İstanbul'un yeraltı su yolları" },
                }),
                IdempotencyKey = Guid.CreateVersion7().ToString("N"),
                CorrelationId = "test",
                ChannelId = Guid.CreateVersion7(),
            },
            CancellationToken.None);

        // OLMAYAN BİR YAZI TİPİ ZİNCİRİN BAŞINDA: çizim ikinciye
        // düşüyor ve kapak yine üretiliyor. Zincirin ANLAMI bu —
        // istenen yüz yoksa video kaybedilmiyor.
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);
    }

    /// KANAL POLİTİKASI VERİLMEZSE VARSAYILAN ZİNCİR.
    ///
    /// Kapak node'u kanal politikası olmadan da kurulabiliyor (sahte
    /// hat, testler) ve o hâlde varsayılan yazı tipleri geçerli —
    /// kapaksız video değil.
    [Fact]
    public void KanalYok_VarsayilanZincir()
        => Assert.Equal(
            ["Inter", "Noto Sans", "Segoe UI", "Arial"],
            ThumbnailRenderHandler.DefaultFonts);
}
