using System.Globalization;
using System.Text;
using System.Text.Json;
using BytemountsAiStudio.Contracts.Prompts;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Assets;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Core.Time;
using BytemountsAiStudio.Media.Planning;
using BytemountsAiStudio.Media.Rendering;
using BytemountsAiStudio.Media.Rendering.Text;
using BytemountsAiStudio.Media.Timeline;
using BytemountsAiStudio.Providers.Fake;
using BytemountsAiStudio.Workflow.Engine;

namespace BytemountsAiStudio.Nodes;

/// Node işleyicilerinin ortak yardımcıları.
///
/// §6.1'in kuralı burada görünüyor: işleyiciler İNCE. Konfigürasyonu okuyor,
/// bir servisi çağırıyor, sonucu JSON'a çeviriyor. İş mantığı Media ve
/// Providers katmanlarında; buraya taşınsaydı workflow motoru zamanla
/// uygulamanın kendisi hâline gelirdi.
internal static class NodeJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static JsonElement From(object value)
        => JsonSerializer.SerializeToElement(value, Options);

    public static string? Text(JsonElement element, string path)
    {
        var current = element;

        foreach (var part in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out var next))
            {
                return null;
            }

            current = next;
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : current.ToString();
    }
}

/// Konu seçimi. Gerçek hatta Topic Pool'dan en yüksek skorlu konuyu alacak;
/// burada run'ı başlatan komutun verdiği konuyu geçiriyor.
public sealed class TopicSelectHandler : INodeHandler
{
    public string NodeType => "topic.select";

    public QueueClass Queue => QueueClass.Llm;

    public Task<Result<JsonElement>> ExecuteAsync(NodeContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Once run girdisi, sonra node konfigurasyonu, sonra varsayilan.
        // Run girdisinin oncelikli olmasi kasitli: ayni workflow farkli
        // konularla kosulabilmeli, her konu icin yeni graf gerekmemeli.
        var topic = NodeJson.Text(context.RunContext, "input.topic")
                    ?? NodeJson.Text(context.Config, "topic")
                    ?? "Dunyanin En Tehlikeli 10 Yeri";

        var language = NodeJson.Text(context.RunContext, "input.language")
                       ?? NodeJson.Text(context.Config, "language")
                       ?? "tr-TR";

        return Task.FromResult(Result.Success(NodeJson.From(new { topic, language })));
    }
}

/// Senaryo üretimi.
///
/// Sahte LLM'i GERÇEK yoldan kullanıyor: zorunlu araç çağrısı + şema
/// doğrulaması (§7.2). Gerçek Script Agent aynı kodu koşacak, yalnızca
/// sağlayıcı değişecek.
///
/// İstem metni kayıt defterinden geliyor (P1-07). Kaynak dosyada gömülü
/// bir dizge olsaydı hangi videonun hangi metinle üretildiği kayda
/// girmezdi; şimdi damga (`script.generate@2#a1b2...`) çıktının içinde.
public sealed class ScriptGenerateHandler(ILlmProvider llm, PromptRegistry? prompts = null) : INodeHandler
{
    public string NodeType => "script.generate";

    public QueueClass Queue => QueueClass.Llm;

    public async Task<Result<JsonElement>> ExecuteAsync(NodeContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var topic = NodeJson.Text(context.RunContext, "topic.topic") ?? "konu";
        var language = NodeJson.Text(context.RunContext, "topic.language") ?? "tr-TR";
        var research = ResearchDigest(context.RunContext);

        // §2.2/8: senaryo knowledge base dışına çıkamaz. Araştırma varsa
        // "yalnızca bunları kullan" diyen v2 istemi, yoksa v1 seçiliyor.
        // Kaynaksız iddia üretmenin önündeki ilk engel bu.
        //
        // Sürüm burada AÇIKÇA seçiliyor, "en yeni" değil: iki sürüm iki
        // ayrı duruma ait, ve birine yeni bir sürüm eklemek diğerinin
        // davranışını sessizce değiştirmemeli.
        var registry = prompts is not null
            ? Result.Success(prompts)
            : PromptRegistry.Embedded;

        if (registry.IsFailure)
        {
            return Result.Failure<JsonElement>(registry.Error);
        }

        var template = registry.Value.Get("script.generate", research is null ? 1 : 2);

        if (template.IsFailure)
        {
            return Result.Failure<JsonElement>(template.Error);
        }

        var rendered = template.Value.Render(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["topic"] = topic,
            ["language"] = language,
            ["sentence_count"] = SentenceCount.ToString(CultureInfo.InvariantCulture),
            ["research"] = research ?? string.Empty,
        });

        if (rendered.IsFailure)
        {
            return Result.Failure<JsonElement>(rendered.Error);
        }

        var prompt = rendered.Value;

        var response = await llm.CompleteAsync(
            new LlmRequest
            {
                Tier = ModelTier.Strong,
                Temperature = 0.3,
                Messages =
                [
                    new(ChatRole.System, prompt.System ?? string.Empty),
                    new(ChatRole.User, prompt.User),
                ],
                ForcedTool = new ToolSchema("emit_script", "Senaryo cümleleri",
                    """{"type":"object","properties":{"sentences":{"type":"array","items":{"type":"string"}}}}"""),
            },
            Context(context),
            cancellationToken).ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result.Failure<JsonElement>(response.Error);
        }

        // Cevap ayrıştırılmaz, DOĞRULANIR.
        using var document = JsonDocument.Parse(response.Value.Value.ToolArguments!);
        var parsed = document.RootElement.GetProperty("sentences")
            .EnumerateArray()
            .Select(e => e.GetString() ?? string.Empty)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        if (parsed.Count == 0)
        {
            return Error.Permanent("script.empty", "Senaryo boş döndü.");
        }

        // Damga ve model kimliği çıktıya giriyor: "bu video hangi
        // istemle ve hangi modelle üretildi" sorusunun cevabı
        // `node_executions.output` içinde duruyor ve ayrı bir şema göçü
        // gerektirmiyor.
        //
        // Yedeğe düşüldüyse o da yazılıyor. Yazılmasaydı birincil
        // sağlayıcı sessizce ölür, kalite düşer ve hiçbir şey kırılmadığı
        // için kimse fark etmezdi.
        var route = (llm as TieredLlmProvider)?.LastRoute;

        return Result.Success(NodeJson.From(new
        {
            sentences = parsed,
            prompt = prompt.Stamp,
            model = response.Value.Value.ModelId,
            provider = route?.ProviderKey ?? llm.Key,
            fell_over_from = route?.FellOverFrom ?? [],
        }));
    }

    /// Varsayılan cümle sayısı. Sabit, çünkü sahne planlayıcısı (P1-16)
    /// devreye girene kadar süre bütçesini burası belirliyor.
    private const int SentenceCount = 3;

    /// Araştırma çıktısından modele verilecek özet.
    ///
    /// Null dönmesi normal: araştırma node'u olmayan bir grafta da senaryo
    /// üretilebilmeli. Zorunlu kılmak, sahte hattı da kırardı.
    private static string? ResearchDigest(JsonElement runContext)
    {
        if (!runContext.TryGetProperty("research", out var research)
            || !research.TryGetProperty("sources", out var sources)
            || sources.ValueKind != JsonValueKind.Array
            || sources.GetArrayLength() == 0)
        {
            return null;
        }

        var builder = new StringBuilder();

        foreach (var source in sources.EnumerateArray().Take(3))
        {
            var title = source.TryGetProperty("title", out var t) ? t.GetString() : "kaynak";
            var excerpt = source.TryGetProperty("excerpt", out var e) ? e.GetString() : null;

            if (string.IsNullOrWhiteSpace(excerpt))
            {
                continue;
            }

            builder.Append("--- ").AppendLine(title)
                .AppendLine(excerpt.Length > 700 ? excerpt[..700] : excerpt);
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    internal static ProviderContext Context(NodeContext context) => new()
    {
        IdempotencyKey = context.IdempotencyKey,
        CorrelationId = context.CorrelationId,
    };

    public static List<string> BuildSentences(string topic, string language) =>
        language.StartsWith("tr", StringComparison.OrdinalIgnoreCase)
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

/// Seslendirme + ÖLÇÜM.
///
/// ADR-006'nın uygulandığı yer: süre sağlayıcıdan alınmıyor, üretilen
/// dosyadan ffprobe ile ölçülüyor. Bu node'un çıktısı timeline'ın
/// zaman eksenini belirliyor.
public sealed class TtsSynthesizeHandler(
    ITtsProvider tts, IStorageProvider storage, string ffprobePath = "ffprobe") : INodeHandler
{
    public string NodeType => "tts.synthesize";

    public QueueClass Queue => QueueClass.Tts;

    public async Task<Result<JsonElement>> ExecuteAsync(NodeContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var language = LanguageTag.Create(NodeJson.Text(context.RunContext, "topic.language") ?? "tr-TR");
        var voiceId = NodeJson.Text(context.Config, "voice_id") ?? $"fake-{language.Primary}-f1";

        if (!context.RunContext.TryGetProperty("script", out var script)
            || !script.TryGetProperty("sentences", out var sentences))
        {
            return Error.Permanent("tts.no_script", "Senaryo bulunamadı.");
        }

        var segments = new List<object>();
        var cues = new List<object>();
        var cursor = Ms.Zero;
        var index = 0;

        foreach (var element in sentences.EnumerateArray())
        {
            var text = element.GetString() ?? string.Empty;

            var speech = await tts.SynthesizeAsync(
                new TtsRequest { SpeechText = text, VoiceId = voiceId, Language = language },
                ScriptGenerateHandler.Context(context),
                cancellationToken).ConfigureAwait(false);

            if (speech.IsFailure)
            {
                return Result.Failure<JsonElement>(speech.Error);
            }

            using var stream = new MemoryStream(speech.Value.Value.Audio.ToArray());
            var stored = await storage.PutAsync(
                stream,
                new AssetMetadata { Kind = AssetKind.Audio, MimeType = "audio/wav", SourceProvider = tts.Key },
                cancellationToken).ConfigureAwait(false);

            if (stored.IsFailure)
            {
                return Result.Failure<JsonElement>(stored.Error);
            }

            var path = await storage.GetLocalPathAsync(stored.Value.Ref, cancellationToken).ConfigureAwait(false);
            if (path.IsFailure)
            {
                return Result.Failure<JsonElement>(path.Error);
            }

            var probe = await MediaProbe.ProbeAsync(ffprobePath, path.Value, cancellationToken)
                .ConfigureAwait(false);

            if (probe.IsFailure)
            {
                return Result.Failure<JsonElement>(probe.Error);
            }

            var measured = Ms.FromSeconds(probe.Value.DurationSeconds);

            segments.Add(new
            {
                id = $"s{index}",
                asset = stored.Value.Ref.ToString(),
                start_ms = cursor.Value,
                duration_ms = measured.Value,
                speech_text = text,
            });

            // Kelime zamanları segment içinde 0'dan başlıyor; mutlak zamana
            // kaydırılıyor. Kaydırmayı unutmak tüm altyazıyı videonun başına
            // toplardı.
            foreach (var word in speech.Value.Value.WordTimings)
            {
                cues.Add(new
                {
                    text = word.Text,
                    start_ms = (cursor + word.Start).Value,
                    end_ms = (cursor + word.End).Value,
                    segment = $"s{index}",
                });
            }

            cursor += measured;
            index++;
        }

        return Result.Success(NodeJson.From(new
        {
            segments,
            cues,
            total_ms = cursor.Value,
        }));
    }
}

/// Sahne görselleri.
public sealed class VisualResolveHandler(
    IImageProvider images, IStorageProvider storage) : INodeHandler
{
    public string NodeType => "visual.resolve";

    public QueueClass Queue => QueueClass.ImageGeneration;

    public async Task<Result<JsonElement>> ExecuteAsync(NodeContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var topic = NodeJson.Text(context.RunContext, "topic.topic") ?? "konu";
        var canvas = Canvas.Shorts1080;

        if (!context.RunContext.TryGetProperty("tts", out var tts)
            || !tts.TryGetProperty("segments", out var segments))
        {
            return Error.Permanent("visual.no_segments", "Ses parçaları bulunamadı.");
        }

        var sceneCount = segments.GetArrayLength();

        // Gorsel uretimi PARALEL: her biri 20-40 saniye suruyor ve birbirinden
        // bagimsiz. Sirali yapildiginda uc gorsel 93 saniye aliyordu.
        //
        // Es zamanlilik sinirli: saglayicinin dakika basina istek siniri var
        // ve sinirsiz paralellik 429 aliyor. Rate limit dekoratoru zaten
        // koruyor ama gereksiz reddedilme uretmenin anlami yok.
        using var gate = new SemaphoreSlim(MaxParallelImages);

        var tasks = Enumerable.Range(0, sceneCount).Select(async index =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                if (index > 0)
                {
                    await Task.Delay(LaunchStagger, cancellationToken).ConfigureAwait(false);
                }

                return (Index: index, Result: await GenerateAndStoreAsync(
                    topic, index, canvas, context, cancellationToken).ConfigureAwait(false));
            }
            finally
            {
                gate.Release();
            }
        });

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        // Ilk hata tum node'u dusuruyor: eksik gorselle video uretmek
        // sessizce bozuk bir cikti demek.
        var failure = results.FirstOrDefault(r => r.Result.IsFailure);
        if (failure.Result.IsFailure)
        {
            return Result.Failure<JsonElement>(failure.Result.Error);
        }

        // Paralel calistiklari icin sira karisik gelebilir; sahne indeksine
        // gore siralaniyor.
        var assets = results
            .OrderBy(r => r.Index)
            .Select(r => (object)new { scene = r.Index, asset = r.Result.Value })
            .ToList();

        return Result.Success(NodeJson.From(new { images = assets }));
    }

    /// Es zamanli gorsel uretim siniri.
    ///
    /// 3 ile denendi ve Pollinations 429 dondurdu: ucretsiz servisin
    /// tolere ettigi es zamanlilik dusuk. 2 hem hizli hem guvenli.
    private const int MaxParallelImages = 2;

    /// Istekler arasi kucuk kayma. Ayni anda baslayan istekler ucretsiz
    /// servislerde patlama (burst) olarak algilaniyor.
    private static readonly TimeSpan LaunchStagger = TimeSpan.FromMilliseconds(400);

    private async Task<Result<string>> GenerateAndStoreAsync(
        string topic, int index, Canvas canvas, NodeContext context, CancellationToken cancellationToken)
    {
        var image = await images.GenerateAsync(
            new ImagePrompt
            {
                Text = $"{topic} — sahne {index.ToString(CultureInfo.InvariantCulture)}",
                Width = canvas.Width,
                Height = canvas.Height,
                Seed = index,
            },
            // Idempotency anahtarina sahne indeksi ekleniyor: eklenmezse uc
            // sahne ayni anahtari paylasir ve onbellek hepsine ayni gorseli
            // dondururdu.
            ScriptGenerateHandler.Context(context) with
            {
                IdempotencyKey = $"{context.IdempotencyKey}:scene{index.ToString(CultureInfo.InvariantCulture)}",
            },
            cancellationToken).ConfigureAwait(false);

        if (image.IsFailure)
        {
            return Result.Failure<string>(image.Error);
        }

        using var stream = new MemoryStream(image.Value.Value.Data.ToArray());
        var stored = await storage.PutAsync(
            stream,
            new AssetMetadata
            {
                Kind = AssetKind.Image,
                MimeType = image.Value.Value.MimeType,
                Width = image.Value.Value.Width,
                Height = image.Value.Value.Height,
                SourceProvider = images.Key,
                License = image.Value.Value.License,
            },
            cancellationToken).ConfigureAwait(false);

        return stored.IsFailure
            ? Result.Failure<string>(stored.Error)
            : Result.Success(stored.Value.Ref.ToString());
    }
}

/// Timeline derlemesi.
///
/// §11: timeline bir BELGE ve bir ARTEFAKT. Varlık deposuna yazılıyor,
/// context'e yalnızca referansı giriyor. Belgeyi context'e gömmek run
/// bağlamını şişirir ve "hangi timeline render edildi" sorusunun cevabını
/// kaybettirirdi.
public sealed class TimelineCompileHandler(IStorageProvider storage) : INodeHandler
{
    public string NodeType => "timeline.compile";

    public QueueClass Queue => QueueClass.Asset;

    public async Task<Result<JsonElement>> ExecuteAsync(NodeContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var build = TimelineBuilder.Build(context.RunContext);
        if (build.IsFailure)
        {
            return Result.Failure<JsonElement>(build.Error);
        }

        var timeline = build.Value;
        var issues = TimelineValidator.Validate(timeline);

        if (issues.Count > 0)
        {
            return Error.Permanent("timeline.invalid",
                "Timeline geçersiz: " + string.Join(" | ", issues));
        }

        var json = TimelineJson.Serialize(timeline);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var stored = await storage.PutAsync(
            stream,
            new AssetMetadata { Kind = AssetKind.Subtitle, MimeType = "application/json" },
            cancellationToken).ConfigureAwait(false);

        return stored.IsFailure
            ? Result.Failure<JsonElement>(stored.Error)
            : Result.Success(NodeJson.From(new
            {
                timeline_asset = stored.Value.Ref.ToString(),
                duration_ms = timeline.Duration.Value,
                scene_count = timeline.Scenes.Count,
                caption_count = timeline.Captions?.Cues.Count ?? 0,
            }));
    }
}

/// Render.
public sealed class MediaRenderHandler(
    IStorageProvider storage,
    string outputDirectory,
    string ffmpegPath = "ffmpeg",
    string ffprobePath = "ffprobe") : INodeHandler
{
    public string NodeType => "media.render";

    public QueueClass Queue => QueueClass.Render;

    public async Task<Result<JsonElement>> ExecuteAsync(NodeContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var reference = NodeJson.Text(context.RunContext, "timeline.timeline_asset");
        if (reference is null)
        {
            return Error.Permanent("render.no_timeline", "Timeline referansı yok.");
        }

        var assetRef = AssetRef.Create(reference);
        var timelinePath = await storage.GetLocalPathAsync(assetRef, cancellationToken).ConfigureAwait(false);

        if (timelinePath.IsFailure)
        {
            return Result.Failure<JsonElement>(timelinePath.Error);
        }

        var json = await File.ReadAllTextAsync(timelinePath.Value, cancellationToken).ConfigureAwait(false);
        var timeline = TimelineJson.Deserialize(json);

        if (timeline is null)
        {
            return Error.Permanent("render.bad_timeline", "Timeline okunamadı.");
        }

        // Varlıklar render ÖNCESİ yerelde hazır ediliyor (ADR-007).
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var refToResolve in timeline.Scenes.Select(s => s.Visual.Asset)
                     .Concat(timeline.Audio.VoiceSegments.Select(s => s.Asset)))
        {
            var path = await storage.GetLocalPathAsync(refToResolve, cancellationToken).ConfigureAwait(false);
            if (path.IsFailure)
            {
                return Result.Failure<JsonElement>(path.Error);
            }

            paths[refToResolve.Sha256] = path.Value;
        }

        var overlays = new List<RenderPlanner.TimedLayer>();

        if (timeline.Captions is { } captions && timeline.Styles.TryGetValue(captions.StyleRef, out var style))
        {
            var renderer = new CaptionRenderer(timeline.FontStack);
            var directory = Path.Combine(
                Path.GetTempPath(), "bmai-captions", Guid.CreateVersion7().ToString("N"));

            var rendered = renderer.RenderTrack(
                captions, style, timeline.Canvas, directory, timeline.RightToLeft);

            overlays.AddRange(rendered.Select(r => new RenderPlanner.TimedLayer(r.Path, r.Range)));
        }

        var plan = RenderPlanner.Plan(timeline, paths, overlays);

        if (!plan.IsSuccess)
        {
            return Error.Permanent("render.plan_failed",
                "Plan üretilemedi: " + string.Join(" | ", plan.Issues));
        }

        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, $"{context.RunId:N}.mp4");

        var executor = new FfmpegExecutor(ffmpegPath, ffprobePath);
        var render = await executor
            .RenderAsync(plan.Plan!.Graph, plan.Plan.Output, outputPath, null, cancellationToken)
            .ConfigureAwait(false);

        if (render.IsFailure)
        {
            return Result.Failure<JsonElement>(render.Error);
        }

        var probe = render.Value.Probe;

        return Result.Success(NodeJson.From(new
        {
            output_path = render.Value.OutputPath,
            width = probe.Width,
            height = probe.Height,
            duration_seconds = probe.DurationSeconds,
            size_bytes = probe.SizeBytes,
            video_codec = probe.VideoCodec,
            audio_codec = probe.AudioCodec,
            render_ms = (int)render.Value.RenderDuration.TotalMilliseconds,
        }));
    }
}

/// Araştırma — Faz 0'da yer tutucu.
///
/// Gerçek hatta arama + claim çıkarma + entailment zinciri koşacak (P1-09/10).
/// Şimdilik grafın şeklinin doğru olduğunu göstermek için var.
public sealed class ResearchHandler : INodeHandler
{
    public string NodeType => "research.deep";

    public QueueClass Queue => QueueClass.Search;

    public Task<Result<JsonElement>> ExecuteAsync(NodeContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        return Task.FromResult(Result.Success(NodeJson.From(new
        {
            sources = Array.Empty<string>(),
            claims = Array.Empty<string>(),
            note = "Faz 0 yer tutucusu; gercek arastirma P1-09'da.",
        })));
    }
}
