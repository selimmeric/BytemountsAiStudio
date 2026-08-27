using BytemountsAiStudio.Core.Assets;
using BytemountsAiStudio.Media.Ir;
using BytemountsAiStudio.Media.Timeline;

namespace BytemountsAiStudio.Media.Planning;

public sealed record PlanResult(FilterGraph Graph, OutputOptions Output);

/// Timeline → FilterGraph.
///
/// SAF: dosya sistemine, sürece ve ağa dokunmaz. Varlık yolları dışarıdan
/// çözümlenmiş hâlde verilir (`resolvedPaths`) — ADR-007'nin doğrudan sonucu.
/// Bu sayede planlayıcının tamamı milisaniyede, FFmpeg olmadan test edilebilir.
public static class RenderPlanner
{
    /// Zoom sırasında titremeyi önlemek için görsel tuvalin iki katına
    /// ölçekleniyor; zoompan sonra bu büyük kareden kırpıyor. Doğrudan tuval
    /// boyutunda zoom yapmak, piksel ızgarasına oturmayan ara kareler üretir.
    private const double ZoomOverscan = 2.0;

    /// Zamanli bir gorsel katman: altyazi ya da metin overlay'i.
    /// Yollar dosya sisteminden gelir ama planlayici onlari yalnizca
    /// tasir; okumaz. Saflik korunuyor.
    public sealed record TimedLayer(string Path, Core.Time.TimeRange Range);

    public static Result Plan(
        TimelineDocument timeline,
        IReadOnlyDictionary<string, string> resolvedPaths,
        IReadOnlyList<TimedLayer>? overlays = null)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(resolvedPaths);

        var issues = new List<ValidationIssue>();
        var inputs = new List<InputDecl>();
        var nodes = new List<FilterNode>();

        var canvas = timeline.Canvas;
        var fps = canvas.Fps;

        // ---- video: her sahne kendi zincirinde, sonra birleştirme ----
        var sceneOutputs = new List<StreamRef>();
        var orderedScenes = timeline.Scenes.OrderBy(s => s.Range.Start.Value).ToList();

        foreach (var scene in orderedScenes)
        {
            var path = Resolve(scene.Visual.Asset, resolvedPaths, issues, $"{scene.Index}. sahne görseli");
            if (path is null)
            {
                continue;
            }

            var inputId = $"scene{scene.Index}";
            var seconds = scene.Range.Duration.TotalSeconds;

            inputs.Add(new InputDecl
            {
                Id = inputId,
                Path = path,
                Kind = InputKind.Image,
                Loop = true,
                DurationSeconds = seconds,
                FrameRate = fps,
            });

            var source = new StreamRef(inputId, MediaKind.Video);
            var current = BuildSceneChain(scene, source, canvas, fps, nodes);
            sceneOutputs.Add(current);
        }

        if (sceneOutputs.Count == 0)
        {
            issues.Add(new("plan.no_scenes", "Planlanacak sahne kalmadı."));
            return new Result(null, issues);
        }

        StreamRef videoTail;

        if (sceneOutputs.Count == 1)
        {
            // Tek sahnede concat gereksiz; FFmpeg n=1 ile de çalışır ama
            // grafiği gereksiz düğümle şişirmenin anlamı yok.
            videoTail = sceneOutputs[0];
        }
        else
        {
            videoTail = new StreamRef("vcat", MediaKind.Video);
            nodes.Add(FilterNode.ConcatVideo(sceneOutputs, videoTail));
        }

        // Altyazi ve metin katmanlari birlestirilmis videonun uzerine biner.
        // Sahne bazinda bindirmek daha "dogru" gorunurdu ama altyazi sahne
        // sinirini asabiliyor; birlestirmeden sonra bindirmek bu sorunu
        // tamamen ortadan kaldiriyor.
        if (overlays is { Count: > 0 })
        {
            var totalSeconds = timeline.Duration.TotalSeconds;

            for (var i = 0; i < overlays.Count; i++)
            {
                var layer = overlays[i];
                var inputId = $"ovl{i}";

                inputs.Add(new InputDecl
                {
                    Id = inputId,
                    Path = layer.Path,
                    Kind = InputKind.Image,
                    Loop = true,
                    // Katman tum video boyunca girdi olarak duruyor; ne zaman
                    // GORUNECEGINI `enable` belirliyor. Girdiyi kendi araligina
                    // kirpmak, overlay'in zaman eksenini kaydirirdi.
                    DurationSeconds = totalSeconds,
                    FrameRate = fps,
                });

                var next = new StreamRef($"ovlout{i}", MediaKind.Video);

                nodes.Add(FilterNode.Overlay(
                    videoTail,
                    new StreamRef(inputId, MediaKind.Video),
                    next,
                    enable: (layer.Range.Start.TotalSeconds, layer.Range.End.TotalSeconds)));

                videoTail = next;
            }
        }

        var videoOut = new StreamRef("vout", MediaKind.Video);
        nodes.Add(FilterNode.Format(videoTail, videoOut, timeline.Output.PixelFormat));

        // ---- ses ----
        var audioOut = BuildAudio(timeline, resolvedPaths, inputs, nodes, issues);

        if (issues.Count > 0)
        {
            return new Result(null, issues);
        }

        var graph = new FilterGraph
        {
            Inputs = inputs,
            Nodes = nodes,
            VideoOut = videoOut,
            AudioOut = audioOut,
        };

        var options = new OutputOptions
        {
            VideoCodec = timeline.Output.VideoCodec,
            Crf = timeline.Output.Crf,
            PresetSpeed = timeline.Output.PresetSpeed,
            PixelFormat = timeline.Output.PixelFormat,
            AudioCodec = timeline.Output.AudioCodec,
            AudioBitrate = timeline.Output.AudioBitrate,
            FrameRate = fps,
            DurationSeconds = timeline.Duration.TotalSeconds,
        };

        return new Result(new PlanResult(graph, options), issues);
    }

    private static StreamRef BuildSceneChain(
        Scene scene, StreamRef source, Core.Content.Canvas canvas, int fps, List<FilterNode> nodes)
    {
        var prefix = $"s{scene.Index}";
        var hasMotion = scene.Visual.Motion is not null;
        var overscan = hasMotion ? ZoomOverscan : 1.0;

        var scaled = new StreamRef($"{prefix}scaled", MediaKind.Video);
        nodes.Add(FilterNode.ScaleCover(source, scaled, canvas.Width, canvas.Height, overscan));

        var cropped = new StreamRef($"{prefix}crop", MediaKind.Video);
        nodes.Add(FilterNode.Crop(scaled, cropped,
            (int)Math.Round(canvas.Width * overscan), (int)Math.Round(canvas.Height * overscan)));

        var current = cropped;

        if (scene.Visual.Motion is { } motion)
        {
            var frames = Math.Max(2, scene.Range.Duration.ToFrame(fps));

            // `d=1`: zoompan her GİRDİ karesi için bir çıktı karesi üretir ve
            // `on` sayacı sahne boyunca artar. Studio'nun kamera hareketinde
            // kullandığı yaklaşımın aynısı; ifadeler düz kalır.
            var zoom = ExprCompiler.Interpolate(motion.FromScale, motion.ToScale, frames, motion.Easing);
            var x = HasPan(motion)
                ? ExprCompiler.PanX(motion.FromX, motion.ToX, frames, motion.Easing)
                : ExprCompiler.CenterX();
            var y = HasPan(motion)
                ? ExprCompiler.PanY(motion.FromY, motion.ToY, frames, motion.Easing)
                : ExprCompiler.CenterY();

            var zoomed = new StreamRef($"{prefix}zoom", MediaKind.Video);
            nodes.Add(FilterNode.Zoompan(current, zoomed, zoom, x, y, 1, canvas.Width, canvas.Height, fps));
            current = zoomed;
        }

        if (scene.TransitionOut is { Kind: TransitionKind.Fade } transition)
        {
            var faded = new StreamRef($"{prefix}fade", MediaKind.Video);
            var duration = transition.Duration.TotalSeconds;
            var start = Math.Max(0, scene.Range.Duration.TotalSeconds - duration);
            nodes.Add(FilterNode.FadeOut(current, faded, start, duration));
            current = faded;
        }

        var normalized = new StreamRef($"{prefix}out", MediaKind.Video);
        nodes.Add(FilterNode.SetSar(current, normalized));

        return normalized;
    }

    private static StreamRef BuildAudio(
        TimelineDocument timeline,
        IReadOnlyDictionary<string, string> resolvedPaths,
        List<InputDecl> inputs,
        List<FilterNode> nodes,
        List<ValidationIssue> issues)
    {
        var delayed = new List<StreamRef>();

        foreach (var segment in timeline.Audio.VoiceSegments.OrderBy(s => s.Start.Value))
        {
            var path = Resolve(segment.Asset, resolvedPaths, issues, $"'{segment.Id}' ses parçası");
            if (path is null)
            {
                continue;
            }

            var inputId = $"voice_{segment.Id}";
            inputs.Add(new InputDecl { Id = inputId, Path = path, Kind = InputKind.Audio });

            var source = new StreamRef(inputId, MediaKind.Audio);
            var target = new StreamRef($"a_{segment.Id}", MediaKind.Audio);
            nodes.Add(FilterNode.ADelay(source, target, segment.Start.Value));
            delayed.Add(target);
        }

        if (delayed.Count == 0)
        {
            issues.Add(new("plan.no_audio", "En az bir ses parçası gerekli."));
            return new StreamRef("aout", MediaKind.Audio);
        }

        StreamRef mixed;

        if (delayed.Count == 1)
        {
            mixed = delayed[0];
        }
        else
        {
            mixed = new StreamRef("amixed", MediaKind.Audio);
            nodes.Add(FilterNode.AMix(delayed, mixed));
        }

        // Sesi tam süreye oturt: kısaysa sessizlikle uzat, sonra kes.
        // İkisi birlikte olmazsa çıktı süresi videodan sapar.
        var seconds = timeline.Duration.TotalSeconds;

        var padded = new StreamRef("apadded", MediaKind.Audio);
        nodes.Add(FilterNode.APadTrim(mixed, padded, seconds));

        var audioOut = new StreamRef("aout", MediaKind.Audio);
        nodes.Add(FilterNode.ATrim(padded, audioOut, seconds));

        return audioOut;
    }

    private static bool HasPan(KenBurns motion)
        => Math.Abs(motion.FromX - motion.ToX) > 1e-9
        || Math.Abs(motion.FromY - motion.ToY) > 1e-9
        || Math.Abs(motion.FromX) > 1e-9
        || Math.Abs(motion.FromY) > 1e-9;

    private static string? Resolve(
        AssetRef assetRef,
        IReadOnlyDictionary<string, string> resolvedPaths,
        List<ValidationIssue> issues,
        string description)
    {
        if (resolvedPaths.TryGetValue(assetRef.Sha256, out var path) && !string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        // ADR-007: timeline'a giren her varlık çözümlenmiş olmalı. Burada
        // eksik çıkması, boru hattının daha önceki bir adımının işini
        // yapmadığı anlamına gelir — render'ın düzeltebileceği bir şey değil.
        issues.Add(new("plan.unresolved_asset",
            $"{description} çözümlenmemiş: {assetRef}"));

        return null;
    }

    public sealed record Result(PlanResult? Plan, IReadOnlyList<ValidationIssue> Issues)
    {
        public bool IsSuccess => Plan is not null && Issues.Count == 0;
    }
}
