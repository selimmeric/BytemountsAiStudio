using System.Globalization;
using System.Text.Json;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Assets;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Core.Time;
using BytemountsAiStudio.Media.Planning;
using BytemountsAiStudio.Media.Rendering;
using BytemountsAiStudio.Media.Rendering.Text;
using BytemountsAiStudio.Media.Timeline;
using BytemountsAiStudio.Providers.Fake;

namespace BytemountsAiStudio.Cli;

public sealed record PipelineOutcome(string OutputPath, MediaProbe Probe, TimeSpan Duration, int SceneCount);

/// Faz 0'ın yürüyen iskeleti: konu → senaryo → ses → görsel → timeline → mp4.
///
/// Tamamen sahte sağlayıcılarla çalışır; ağa çıkmaz, para harcamaz, aynı
/// girdiye aynı çıktıyı verir. Amacı içerik üretmek değil, BORU HATTININ
/// KENDİSİNİ sınamak.
///
/// Sıra ADR-006'ya uyuyor ve bu kasıtlı: sahne süreleri senaryodan tahmin
/// EDİLMİYOR, üretilen sesten ÖLÇÜLÜYOR. Ters sırada kurulsaydı ses-görsel
/// kayması sahte veriyle görünmez, gerçek veriyle ortaya çıkardı.
public sealed class FakeShortsPipeline(
    IStorageProvider storage,
    string ffmpegPath = "ffmpeg",
    string ffprobePath = "ffprobe")
{
    private readonly FakeLlmProvider _llm = new();
    private readonly FakeTtsProvider _tts = new();
    private readonly FakeImageProvider _images = new(ImageProviderKind.Generative);
    private readonly FfmpegExecutor _executor = new(ffmpegPath, ffprobePath);

    private readonly List<CaptionCue> _cues = [];

    public async Task<Result<PipelineOutcome>> RunAsync(
        string topic,
        string outputPath,
        LanguageTag language,
        Action<string>? log = null,
        string? dotPath = null,
        CancellationToken cancellationToken = default)
    {
        log ??= _ => { };
        var canvas = Canvas.Shorts1080;

        // ---- 1. senaryo ----
        var script = await GenerateScriptAsync(topic, language, cancellationToken).ConfigureAwait(false);
        if (script.IsFailure)
        {
            return Result.Failure<PipelineOutcome>(script.Error);
        }

        var sentences = script.Value;
        log($"senaryo   : {sentences.Count} cümle");

        // ---- 2. ses: üret, DEPOLA, sonra ÖLÇ ----
        var segments = new List<VoiceSegment>();
        var scenes = new List<Scene>();
        var cursor = Ms.Zero;

        for (var i = 0; i < sentences.Count; i++)
        {
            var speech = await SynthesizeAsync(sentences[i], language, cancellationToken).ConfigureAwait(false);
            if (speech.IsFailure)
            {
                return Result.Failure<PipelineOutcome>(speech.Error);
            }

            var (assetRef, measured, wordTimings) = speech.Value;

            // Kelime zamanlari segment icinde 0'dan basliyor; timeline'da
            // mutlak zamana kaydiriliyor. Kaydirmayi unutmak butun altyazinin
            // videonun basinda toplanmasina yol acardi.
            foreach (var word in wordTimings)
            {
                _cues.Add(new CaptionCue
                {
                    Text = word.Text,
                    Range = new TimeRange(cursor + word.Start, cursor + word.End),
                    SegmentId = $"s{i}",
                });
            }

            segments.Add(new VoiceSegment
            {
                Id = $"s{i}",
                Asset = assetRef,
                Start = cursor,
                Duration = measured,
                SpeechText = sentences[i],
            });

            // ---- 3. görsel ----
            var image = await GenerateImageAsync(topic, i, canvas, cancellationToken).ConfigureAwait(false);
            if (image.IsFailure)
            {
                return Result.Failure<PipelineOutcome>(image.Error);
            }

            var isLast = i == sentences.Count - 1;
            var range = TimeRange.FromDuration(cursor, measured);

            scenes.Add(new Scene
            {
                Index = i,
                Range = range,
                VoiceSegmentIds = [$"s{i}"],
                Visual = new SceneVisual
                {
                    Asset = image.Value,
                    // Sahneler dönüşümlü yakınlaşıp uzaklaşıyor: hepsi aynı
                    // yönde olsaydı video tekdüze görünürdü.
                    Motion = i % 2 == 0
                        ? new KenBurns { FromScale = 1.0, ToScale = 1.12, ToX = 0.04 }
                        : new KenBurns { FromScale = 1.12, ToScale = 1.0, FromX = -0.04 },
                },
                TransitionOut = isLast ? null : new Transition(TransitionKind.Fade, new Ms(300)),
            });

            cursor += measured;
            log($"sahne {i}   : {measured.Value} ms ölçüldü");
        }

        // ---- 4. timeline ----
        var timeline = new TimelineDocument
        {
            Canvas = canvas,
            Language = language,
            Duration = cursor,
            FontStack = ["Inter", "Noto Sans", "Noto Color Emoji"],
            Audio = new AudioTrack { VoiceSegments = segments },
            Scenes = scenes,
            Captions = _cues.Count > 0
                ? new CaptionTrack { StyleRef = "caption", Cues = _cues }
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

        var timelineIssues = TimelineValidator.Validate(timeline);
        if (timelineIssues.Count > 0)
        {
            return Error.Permanent("pipeline.invalid_timeline",
                "Timeline geçersiz: " + string.Join(" | ", timelineIssues));
        }

        log($"timeline  : {timeline.Duration.Value} ms, {scenes.Count} sahne — doğrulandı");

        // ---- 5. plan ----
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var assetRef in scenes.Select(s => s.Visual.Asset).Concat(segments.Select(s => s.Asset)))
        {
            var path = await storage.GetLocalPathAsync(assetRef, cancellationToken).ConfigureAwait(false);
            if (path.IsFailure)
            {
                return Result.Failure<PipelineOutcome>(path.Error);
            }

            paths[assetRef.Sha256] = path.Value;
        }

        // Altyazi goruntuleri: her VURGU DURUMU icin bir PNG. Kare dizisi
        // degil - 50 saniyelik videoda 1.500 kare yerine ~120 kucuk gorsel.
        var overlays = new List<RenderPlanner.TimedLayer>();

        if (timeline.Captions is { } captions)
        {
            var renderer = new CaptionRenderer(timeline.FontStack);
            var directory = Path.Combine(Path.GetTempPath(), "bmai-captions", Guid.CreateVersion7().ToString("N"));

            var rendered = renderer.RenderTrack(
                captions, timeline.Styles[captions.StyleRef], canvas, directory, timeline.RightToLeft);

            overlays.AddRange(rendered.Select(r => new RenderPlanner.TimedLayer(r.Path, r.Range)));
            log($"altyazi   : {rendered.Count} görüntü");
        }

        var plan = RenderPlanner.Plan(timeline, paths, overlays);
        if (!plan.IsSuccess)
        {
            return Error.Permanent("pipeline.plan_failed",
                "Plan üretilemedi: " + string.Join(" | ", plan.Issues));
        }

        log($"plan      : {plan.Plan!.Graph.Inputs.Count} girdi, {plan.Plan.Graph.Nodes.Count} filtre düğümü");

        if (dotPath is not null)
        {
            // §12.3: render patladığında 12 KB'lık metne değil resme bakmak için.
            await File.WriteAllTextAsync(
                dotPath, Media.Ir.GraphDot.Render(plan.Plan.Graph), cancellationToken).ConfigureAwait(false);
            log($"graf      : {dotPath}");
        }

        // ---- 6. render ----
        var progress = new Progress<RenderProgress>(p =>
        {
            if (p.Percent >= 99.5 || (int)p.Percent % 25 == 0)
            {
                log($"render    : %{p.Percent:0}");
            }
        });

        var render = await _executor
            .RenderAsync(plan.Plan.Graph, plan.Plan.Output, outputPath, progress, cancellationToken)
            .ConfigureAwait(false);

        if (render.IsFailure)
        {
            return Result.Failure<PipelineOutcome>(render.Error);
        }

        return new PipelineOutcome(
            render.Value.OutputPath,
            render.Value.Probe,
            render.Value.RenderDuration,
            scenes.Count);
    }

    /// Sahte LLM'i GERÇEK yoldan kullanıyoruz: zorunlu araç çağrısı + şema
    /// doğrulaması. Gerçek ajan da aynı kodu koşacak; yalnızca sağlayıcı
    /// değişecek (§7.2).
    private async Task<Result<IReadOnlyList<string>>> GenerateScriptAsync(
        string topic, LanguageTag language, CancellationToken cancellationToken)
    {
        var sentences = BuildSentences(topic, language);

        _llm.SetToolResponse("emit_script", JsonSerializer.Serialize(new { sentences }));

        var request = new LlmRequest
        {
            Tier = ModelTier.Strong,
            Messages =
            [
                new(ChatRole.System, "Sen bir kısa video senaryo yazarısın."),
                new(ChatRole.User, $"'{topic}' konusunda {language} dilinde kısa bir senaryo yaz."),
            ],
            ForcedTool = new ToolSchema(
                "emit_script",
                "Senaryo cümlelerini döndürür.",
                """{"type":"object","properties":{"sentences":{"type":"array","items":{"type":"string"}}},"required":["sentences"]}"""),
        };

        var response = await _llm
            .CompleteAsync(request, Context("script"), cancellationToken)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result.Failure<IReadOnlyList<string>>(response.Error);
        }

        // Cevap ayrıştırılmaz, DOĞRULANIR: şemaya uymayan çıktı burada düşer.
        try
        {
            using var document = JsonDocument.Parse(response.Value.Value.ToolArguments!);
            var parsed = document.RootElement.GetProperty("sentences")
                .EnumerateArray()
                .Select(e => e.GetString() ?? string.Empty)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();

            return parsed.Count == 0
                ? Error.Permanent("pipeline.empty_script", "Senaryo boş döndü.")
                : parsed;
        }
        catch (JsonException ex)
        {
            return Error.Permanent("pipeline.script_schema", $"Senaryo şemaya uymuyor: {ex.Message}");
        }
    }

    private async Task<Result<(AssetRef Asset, Ms Duration, IReadOnlyList<WordTiming> Words)>> SynthesizeAsync(
        string sentence, LanguageTag language, CancellationToken cancellationToken)
    {
        var speech = await _tts.SynthesizeAsync(
            new TtsRequest
            {
                SpeechText = sentence,
                VoiceId = $"fake-{language.Primary}-f1",
                Language = language,
            },
            Context("tts"),
            cancellationToken).ConfigureAwait(false);

        if (speech.IsFailure)
        {
            return Result.Failure<(AssetRef, Ms, IReadOnlyList<WordTiming>)>(speech.Error);
        }

        using var stream = new MemoryStream(speech.Value.Value.Audio.ToArray());
        var stored = await storage.PutAsync(
            stream,
            new AssetMetadata { Kind = AssetKind.Audio, MimeType = "audio/wav", SourceProvider = _tts.Key },
            cancellationToken).ConfigureAwait(false);

        if (stored.IsFailure)
        {
            return Result.Failure<(AssetRef, Ms, IReadOnlyList<WordTiming>)>(stored.Error);
        }

        // ADR-006: sağlayıcının bildirdiği süre değil, DOSYADAN ölçülen süre.
        var path = await storage.GetLocalPathAsync(stored.Value.Ref, cancellationToken).ConfigureAwait(false);
        if (path.IsFailure)
        {
            return Result.Failure<(AssetRef, Ms, IReadOnlyList<WordTiming>)>(path.Error);
        }

        var probe = await MediaProbe.ProbeAsync(ffprobePath, path.Value, cancellationToken).ConfigureAwait(false);
        if (probe.IsFailure)
        {
            return Result.Failure<(AssetRef, Ms, IReadOnlyList<WordTiming>)>(probe.Error);
        }

        return (stored.Value.Ref, Ms.FromSeconds(probe.Value.DurationSeconds), speech.Value.Value.WordTimings);
    }

    private async Task<Result<AssetRef>> GenerateImageAsync(
        string topic, int index, Canvas canvas, CancellationToken cancellationToken)
    {
        var image = await _images.GenerateAsync(
            new ImagePrompt
            {
                Text = $"{topic} — sahne {index.ToString(CultureInfo.InvariantCulture)}",
                Width = canvas.Width,
                Height = canvas.Height,
                Seed = index,
            },
            Context($"image{index}"),
            cancellationToken).ConfigureAwait(false);

        if (image.IsFailure)
        {
            return Result.Failure<AssetRef>(image.Error);
        }

        using var stream = new MemoryStream(image.Value.Value.Data.ToArray());
        var stored = await storage.PutAsync(
            stream,
            new AssetMetadata
            {
                Kind = AssetKind.Image,
                MimeType = "image/png",
                Width = canvas.Width,
                Height = canvas.Height,
                SourceProvider = _images.Key,
                License = image.Value.Value.License,
            },
            cancellationToken).ConfigureAwait(false);

        return stored.IsFailure
            ? Result.Failure<AssetRef>(stored.Error)
            : Result.Success(stored.Value.Ref);
    }

    /// Konudan deterministik cümleler. Gerçek hatta bu, Script Agent'ın işi;
    /// burada yalnızca boru hattını besleyecek metin gerekiyor.
    private static List<string> BuildSentences(string topic, LanguageTag language)
    {
        var isTurkish = language.Primary.Equals("tr", StringComparison.Ordinal);

        return isTurkish
            ?
            [
                $"{topic} hakkında çoğu kişinin bilmediği bir şey var.",
                "Kayıtlar bunun sanılandan çok daha eskiye dayandığını gösteriyor.",
                "İşte bu yüzden konu bugün hâlâ tartışılıyor.",
            ]
            :
            [
                $"There is something about {topic} that most people never hear.",
                "The records show it goes back much further than anyone assumed.",
                "And that is exactly why it is still debated today.",
            ];
    }

    private static ProviderContext Context(string step) => new()
    {
        IdempotencyKey = $"fake-pipeline:{step}",
        CorrelationId = "fake-pipeline",
    };
}
