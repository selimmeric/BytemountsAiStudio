using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Time;
using BytemountsAiStudio.Media.Ir;
using BytemountsAiStudio.Media.Planning;
using BytemountsAiStudio.Media.Timeline;

namespace BytemountsAiStudio.Media.Tests;

/// Yalnızca ses türevi — podcast (P6-05).
///
/// PODCAST "VİDEONUN SESİ" DEĞİL. Videoda ekranda yazan ama
/// seslendirilmeyen her şey dinleyici için YOK — ve bunu kimse fark
/// etmiyor, çünkü ses dosyası kusursuz çalıyor.
public sealed class PodcastRenditionTests
{
    private static Dictionary<string, string> Paths()
        => TimelineFactory.Valid().Audio.VoiceSegments
            .Select(s => s.Asset.Sha256)
            .Concat(TimelineFactory.Valid().Scenes.Select(s => s.Visual.Asset.Sha256))
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(sha => sha, sha => $"C:/tmp/{sha}.wav", StringComparer.Ordinal);

    /* ---- plan ---- */

    /// GÖRÜNTÜ HİÇ ÜRETİLMİYOR.
    ///
    /// Videoyu render edip sesini ayıklamak, aynı sesi iki kez
    /// kodlamak (birincinin kaybı ikinciye miras kalır) ve
    /// dakikalarca boşuna ffmpeg çalıştırmak demekti.
    [Fact]
    public void SesPlani_VideoCikisiYok()
    {
        var plan = RenderPlanner.PlanAudioOnly(TimelineFactory.Valid(), Paths());

        Assert.True(plan.IsSuccess, string.Join(" | ", plan.Issues));
        Assert.Null(plan.Plan!.Graph.VideoOut);
        Assert.NotNull(plan.Plan.Graph.AudioOut);
    }

    /// KOMUTTA `-vn` VAR.
    ///
    /// Olmadan ffmpeg girdi dosyalarındaki video akışlarını kendi
    /// seçimiyle kopyalayabiliyor ve "yalnızca ses" dosyası içinde bir
    /// görsel akışıyla çıkabiliyordu.
    [Fact]
    public void SesKomutu_VnIceriyor()
    {
        var plan = RenderPlanner.PlanAudioOnly(TimelineFactory.Valid(), Paths());

        var command = FilterGraphEmitter.Emit(
            plan.Plan!.Graph, "graf.txt", "cikti.m4a", plan.Plan.Output);

        Assert.Contains("-vn", command.Arguments, StringComparer.Ordinal);

        // VE VIDEO KODEK BAYRAKLARI HİÇ YOK: görüntüsüz çıktıda
        // `-c:v` vermek ffmpeg'i uyarıya, bazı sürümlerde hataya
        // sokuyor.
        Assert.DoesNotContain("-c:v", command.Arguments, StringComparer.Ordinal);
        Assert.DoesNotContain("-crf", command.Arguments, StringComparer.Ordinal);
    }

    /// SES KODEĞİ VE BİT HIZI PODCAST İÇİN AYARLANIYOR.
    [Fact]
    public void SesPlani_PodcastKodegi()
    {
        var plan = RenderPlanner.PlanAudioOnly(TimelineFactory.Valid(), Paths());

        Assert.Equal("aac", plan.Plan!.Output.AudioCodec);
        Assert.Null(plan.Plan.Output.KeyframeInterval);
    }

    /// SES ZİNCİRİ VİDEODAKİYLE AYNI.
    ///
    /// Ducking, müzik seviyesi ve LUFS normalizasyonu video ile
    /// podcast'te farklı olsaydı, aynı içeriğin iki sürümü farklı
    /// duyulurdu.
    [Fact]
    public void SesZinciri_VideodakiyleAyni()
    {
        var audioOnly = RenderPlanner.PlanAudioOnly(TimelineFactory.Valid(), Paths());
        var full = RenderPlanner.Plan(TimelineFactory.Valid(), Paths());

        Assert.Equal(full.Plan!.Graph.AudioOut, audioOnly.Plan!.Graph.AudioOut);
    }

    /// SESSİZ BİR PODCAST ÜRETİLMİYOR.
    ///
    /// Görüntüsüz VE sessiz bir dosya, boyutu birkaç kilobayt olan ve
    /// hiçbir şey içermeyen bir "başarı" demekti — sessiz başarının en
    /// saf hâli.
    [Fact]
    public void SesParcasiYok_PlanUretilmiyor()
    {
        var source = TimelineFactory.Valid();

        var plan = RenderPlanner.PlanAudioOnly(
            source with { Audio = source.Audio with { VoiceSegments = [] } },
            Paths());

        Assert.False(plan.IsSuccess);
        Assert.Contains(plan.Issues, i => i.Code == "podcast.no_audio");
    }

    /// ÇIKIŞSIZ GRAFİK DOĞRULAYICIDA DÜŞÜYOR.
    ///
    /// Video ve ses birlikte nullable olunca "ikisi de yok" hâli
    /// derleyiciye geçerli görünüyor; ffmpeg ise böyle bir komutta
    /// hiçbir şey üretmeden başarıyla çıkabiliyor.
    [Fact]
    public void CikissizGrafik_Reddediliyor()
    {
        var issues = GraphValidator.Validate(new FilterGraph
        {
            Inputs = [],
            Nodes = [],
            VideoOut = null,
            AudioOut = null,
        });

        Assert.Contains(issues, i => i.Code == "graph.no_output");
    }

    /* ---- ekranda kalan bilgi ---- */

    /// SESLENDİRİLMEYEN METİN KATMANI YAKALANIYOR.
    ///
    /// Belgede "1453" diye bir katman var ve anlatım metni boş —
    /// dinleyici o bilgiyi hiç almıyor.
    [Fact]
    public void SeslendirilmeyenKatman_Yakalaniyor()
        => Assert.Equal(["1453"], PodcastRendition.VisualOnlyText(TimelineFactory.Valid()));

    /// SESLENDİRİLEN KATMAN UYARI ÜRETMİYOR.
    [Fact]
    public void SeslendirilenKatman_UyariYok()
    {
        var source = TimelineFactory.Valid();

        var spoken = source with
        {
            Audio = source.Audio with
            {
                VoiceSegments =
                [
                    .. source.Audio.VoiceSegments.Select(s =>
                        s with { SpeechText = "yil bin dort yuz elli uc, yani 1453 yilinda." }),
                ],
            },
        };

        Assert.Empty(PodcastRendition.VisualOnlyText(spoken));
    }

    /// NOKTALAMA FARKI UYDURMA UYARI ÜRETMİYOR.
    ///
    /// Ekrandaki "1453." ile söylenen "1453" aynı bilgi; farklı saymak
    /// her videoda uydurma bir uyarı üretirdi ve uyarılar okunmaz
    /// hâle gelirdi.
    [Fact]
    public void NoktalamaFarki_UyariUretmiyor()
    {
        var source = TimelineFactory.Valid();

        var timeline = source with
        {
            Scenes =
            [
                source.Scenes[0] with
                {
                    Overlays =
                    [
                        new()
                        {
                            Text = "1453.",
                            StyleRef = "big",
                            Range = new TimeRange(new Ms(400), new Ms(2_200)),
                        },
                    ],
                },
                source.Scenes[1],
            ],
            Audio = source.Audio with
            {
                VoiceSegments =
                [
                    .. source.Audio.VoiceSegments.Select(s => s with { SpeechText = "1453 yilinda" }),
                ],
            },
        };

        Assert.Empty(PodcastRendition.VisualOnlyText(timeline));
    }

    /// BÜYÜK/KÜÇÜK HARF KARŞILAŞTIRMASI DİLE DUYARLI.
    ///
    /// `ToLowerInvariant` Türkçe'de "İSTANBUL"u noktalı i ile
    /// bırakıyor ve karşılaştırma tutmuyor — kapak metninde ödenen
    /// dersin aynısı (P5-03).
    [Fact]
    public void BuyukKucukHarf_DileDuyarli()
    {
        var source = TimelineFactory.Valid();

        var timeline = source with
        {
            Scenes =
            [
                source.Scenes[0] with
                {
                    Overlays =
                    [
                        new()
                        {
                            Text = "İSTANBUL",
                            StyleRef = "big",
                            Range = new TimeRange(new Ms(400), new Ms(2_200)),
                        },
                    ],
                },
                source.Scenes[1],
            ],
            Audio = source.Audio with
            {
                VoiceSegments =
                [
                    .. source.Audio.VoiceSegments.Select(s => s with { SpeechText = "istanbul sehri" }),
                ],
            },
        };

        Assert.Empty(PodcastRendition.VisualOnlyText(timeline));
    }

    /// ANLATIM METNİ YOKSA HİÇBİR KATMAN KAPSANMIŞ SAYILMIYOR.
    ///
    /// Boş konuşma metnini "her şeyi içeriyor" gibi ele almak,
    /// kontrolü sessizce kapatırdı — ve kontrolün kapalı olduğu tek
    /// yer, en çok gerektiği yer olurdu.
    [Fact]
    public void AnlatimMetniYok_KatmanBildiriliyor()
        => Assert.NotEmpty(PodcastRendition.VisualOnlyText(TimelineFactory.Valid()));
}
