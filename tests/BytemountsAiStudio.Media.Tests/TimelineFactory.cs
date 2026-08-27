using BytemountsAiStudio.Core.Assets;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Time;
using BytemountsAiStudio.Media.Timeline;

namespace BytemountsAiStudio.Media.Tests;

/// Testler için geçerli bir timeline üretir; her test yalnızca bozmak
/// istediği alanı değiştirir.
///
/// Bunun alternatifi her testte 60 satırlık kurulum olurdu ve testin ne
/// sınadığı o kurulumun içinde kaybolurdu.
internal static class TimelineFactory
{
    public static AssetRef Asset(char seed) => AssetRef.Create(new string(seed, 64));

    /// İki sahneli, iki ses parçalı, altyazılı geçerli belge.
    public static TimelineDocument Valid()
    {
        var styles = new Dictionary<string, TextStyle>(StringComparer.Ordinal)
        {
            ["caption"] = new() { FontFamily = "Inter", SizePercent = 6.5, Bold = true },
            ["big"] = new() { FontFamily = "Inter", SizePercent = 14 },
        };

        return new TimelineDocument
        {
            Canvas = Canvas.Shorts1080,
            Language = LanguageTag.Create("tr-TR"),
            Duration = new Ms(12_000),
            Audio = new AudioTrack
            {
                VoiceSegments =
                [
                    new() { Id = "s1", Asset = Asset('1'), Start = Ms.Zero, Duration = new Ms(5_000) },
                    new() { Id = "s2", Asset = Asset('2'), Start = new Ms(5_000), Duration = new Ms(7_000) },
                ],
            },
            Scenes =
            [
                new()
                {
                    Index = 0,
                    Range = new TimeRange(Ms.Zero, new Ms(5_000)),
                    VoiceSegmentIds = ["s1"],
                    Visual = new SceneVisual
                    {
                        Asset = Asset('a'),
                        Motion = new KenBurns { FromScale = 1.0, ToScale = 1.12, ToX = 0.03 },
                    },
                    Overlays =
                    [
                        new() { Text = "1453", StyleRef = "big", Range = new TimeRange(new Ms(400), new Ms(2_200)) },
                    ],
                    TransitionOut = new Transition(TransitionKind.Fade, new Ms(300)),
                },
                new()
                {
                    Index = 1,
                    Range = new TimeRange(new Ms(5_000), new Ms(12_000)),
                    VoiceSegmentIds = ["s2"],
                    Visual = new SceneVisual { Asset = Asset('b') },
                },
            ],
            Captions = new CaptionTrack
            {
                StyleRef = "caption",
                Cues =
                [
                    new() { Text = "Bin", Range = new TimeRange(new Ms(120), new Ms(460)), SegmentId = "s1" },
                    new() { Text = "dört", Range = new TimeRange(new Ms(460), new Ms(980)), SegmentId = "s1", Emphasis = true },
                ],
            },
            Styles = styles,
            Output = new OutputSpec { Preset = "shorts-1080x1920" },
        };
    }
}
