using System.Text.Json;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Assets;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Core.Time;
using BytemountsAiStudio.Media.Timeline;

namespace BytemountsAiStudio.Nodes;

/// Run bağlamındaki ses ve görsel çıktılarından timeline derler.
///
/// ADR-006'nın somutlaştığı yer: sahne SINIRLARI senaryodan, sahne SÜRELERİ
/// ölçülen sesten geliyor. Ters kurulsaydı ses–görsel kayması sahte veriyle
/// görünmez, gerçek veriyle ortaya çıkardı.
public static class TimelineBuilder
{
    public static Result<TimelineDocument> Build(JsonElement runContext)
    {
        if (!runContext.TryGetProperty("tts", out var tts)
            || !tts.TryGetProperty("segments", out var segmentsJson))
        {
            return Error.Permanent("timeline.no_audio", "Ses parçaları bulunamadı.");
        }

        if (!runContext.TryGetProperty("visuals", out var visuals)
            || !visuals.TryGetProperty("images", out var imagesJson))
        {
            return Error.Permanent("timeline.no_visuals", "Sahne görselleri bulunamadı.");
        }

        var language = LanguageTag.Create(
            NodeJson.Text(runContext, "topic.language") ?? "tr-TR");

        var canvas = Canvas.Shorts1080;
        var images = imagesJson.EnumerateArray()
            .ToDictionary(
                e => e.GetProperty("scene").GetInt32(),
                e => AssetRef.Create(e.GetProperty("asset").GetString()!));

        var segments = new List<VoiceSegment>();
        var scenes = new List<Scene>();
        var index = 0;
        var total = Ms.Zero;

        foreach (var element in segmentsJson.EnumerateArray())
        {
            var id = element.GetProperty("id").GetString()!;
            var start = new Ms(element.GetProperty("start_ms").GetInt32());
            var duration = new Ms(element.GetProperty("duration_ms").GetInt32());

            segments.Add(new VoiceSegment
            {
                Id = id,
                Asset = AssetRef.Create(element.GetProperty("asset").GetString()!),
                Start = start,
                Duration = duration,
                SpeechText = element.TryGetProperty("speech_text", out var speech)
                    ? speech.GetString()
                    : null,
            });

            if (!images.TryGetValue(index, out var image))
            {
                return Error.Permanent("timeline.missing_visual",
                    $"{index}. sahne için görsel yok.");
            }

            var isLast = index == segmentsJson.GetArrayLength() - 1;

            scenes.Add(new Scene
            {
                Index = index,
                Range = TimeRange.FromDuration(start, duration),
                VoiceSegmentIds = [id],
                Visual = new SceneVisual
                {
                    Asset = image,
                    // Dönüşümlü yakınlaşma/uzaklaşma: hepsi aynı yönde
                    // olsaydı video tekdüze görünürdü.
                    Motion = index % 2 == 0
                        ? new KenBurns { FromScale = 1.0, ToScale = 1.12, ToX = 0.04 }
                        : new KenBurns { FromScale = 1.12, ToScale = 1.0, FromX = -0.04 },
                },
                TransitionOut = isLast ? null : new Transition(TransitionKind.Fade, new Ms(300)),
            });

            total = start + duration;
            index++;
        }

        var cues = new List<CaptionCue>();

        if (tts.TryGetProperty("cues", out var cuesJson))
        {
            foreach (var element in cuesJson.EnumerateArray())
            {
                cues.Add(new CaptionCue
                {
                    Text = element.GetProperty("text").GetString() ?? string.Empty,
                    Range = new TimeRange(
                        new Ms(element.GetProperty("start_ms").GetInt32()),
                        new Ms(element.GetProperty("end_ms").GetInt32())),
                    SegmentId = element.TryGetProperty("segment", out var segment)
                        ? segment.GetString()
                        : null,
                });
            }
        }

        return new TimelineDocument
        {
            Canvas = canvas,
            Language = language,
            RightToLeft = language.IsRightToLeft,
            Duration = total,
            FontStack = ["Inter", "Noto Sans", "Segoe UI", "Arial"],
            Audio = new AudioTrack { VoiceSegments = segments },
            Scenes = scenes,
            Captions = cues.Count > 0
                ? new CaptionTrack { StyleRef = "caption", Cues = cues }
                : null,
            Styles = new Dictionary<string, TextStyle>(StringComparer.Ordinal)
            {
                ["caption"] = new()
                {
                    FontFamily = "Inter",
                    SizePercent = 5.5,
                    Bold = true,
                    Color = "#FFFFFF",
                    HighlightColor = "#FFD400",
                    StrokeColor = "#000000",
                    StrokeWidth = 8,
                    BoxColor = "#000000",
                    BoxOpacity = 0.35,
                    Position = Anchor.BottomCenter,
                    OffsetPercent = 22,
                    MaxLines = 2,
                },
            },
            Output = new OutputSpec { Preset = "shorts-1080x1920" },
            Provenance = new Provenance
            {
                PromptVersions = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["script.generate"] = "fake-v1",
                },
                EngineMinVersion = "0.1.0",
            },
        };
    }
}
