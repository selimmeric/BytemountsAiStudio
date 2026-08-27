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
        // "yalnızca bunları kullan" diyen biçimli istem, yoksa sade olan
        // seçiliyor. Kaynaksız iddia üretmenin önündeki ilk engel bu.
        //
        // Sürüm AÇIKÇA seçiliyor, "en yeni" değil: sürümler farklı
        // durumlara ait ve birine yeni sürüm eklemek diğerinin
        // davranışını sessizce değiştirmemeli.
        var registry = prompts is not null
            ? Result.Success(prompts)
            : PromptRegistry.Embedded;

        if (registry.IsFailure)
        {
            return Result.Failure<JsonElement>(registry.Error);
        }

        // Biçim şablonu (P1-12). Yapıyı İSTEM taşıyor, kod değil: yeni
        // bir biçim eklemek bir kayıt yazmak olmalı, `switch` koluna
        // dokunmak değil.
        var format = ScriptFormat.Get(
            NodeJson.Text(context.RunContext, "input.format") ?? NodeJson.Text(context.Config, "format"));

        // Araştırma yoksa biçimli v3 istemi kullanılamıyor: kaynağı
        // olmayan bir yapıyı doldurmak modeli uydurmaya iter.
        var version = research is null ? 1 : 3;
        var template = registry.Value.Get("script.generate", version);

        if (template.IsFailure)
        {
            return Result.Failure<JsonElement>(template.Error);
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["topic"] = topic,
            ["language"] = language,
            ["sentence_count"] = format.TargetSentences.ToString(CultureInfo.InvariantCulture),
            ["format_structure"] = format.Structure,
            ["research"] = research ?? string.Empty,
            // Olgu yoksa bölüm başlığı da girmiyor: boş bir "OLGULAR:"
            // başlığı modele "burada bir şey olmalıydı" diye okunuyor
            // ve uydurmayı davet ediyor.
            ["facts"] = FactsDigest(context.RunContext) is { } digest
                ? "DOGRULANMIS OLGULAR (bunlar kaynaktan OKUNMUS degerler, yorumlama):" + Environment.NewLine + digest
                : string.Empty,
        };

        var rendered = template.Value.Render(values);

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

        // Cümle sayısı biçimin sınırları içinde mi.
        //
        // GEÇİCİ hata olarak sınıflandırılıyor, kalıcı değil: istek
        // geçerli, model bu sefer uymadı. Aynı istemle ikinci deneme
        // farklı bir cevap veriyor ve genellikle uyuyor. Kalıcı desek
        // run düşerdi; hiç denetlemesek biçim şablonu yalnızca bir
        // temenni olurdu.
        if (!format.Accepts(parsed.Count))
        {
            return Error.Transient(
                "script.wrong_length",
                FormattableString.Invariant(
                    $"'{format.Name}' bicimi {format.MinSentences}-{format.MaxSentences} cumle bekliyor, model {parsed.Count} dondu."));
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
            format = format.Name,
            model = response.Value.Value.ModelId,
            provider = route?.ProviderKey ?? llm.Key,
            fell_over_from = route?.FellOverFrom ?? [],
        }));
    }

    /// Araştırma çıktısından modele verilecek özet.
    ///
    /// Null dönmesi normal: araştırma node'u olmayan bir grafta da senaryo
    /// üretilebilmeli. Zorunlu kılmak, sahte hattı da kırardı.
    /// Wikidata olgularının isteme giren hâli.
    ///
    /// AYRI bir bölüm olarak veriliyor, kaynak metnine karıştırılmadan.
    /// Sebep: bunlar metinden çıkarılmış değil, okunmuş değerler ve
    /// modele bunu söylemek gerekiyor. Karıştırsaydık model olguyu da
    /// yorumlanacak bir metin sanardı; oysa tarih ve sayı tam da
    /// yorumlanmaması gereken şeyler.
    private static string? FactsDigest(JsonElement runContext)
    {
        if (!runContext.TryGetProperty("research", out var research)
            || !research.TryGetProperty("facts", out var facts)
            || facts.ValueKind != JsonValueKind.Array
            || facts.GetArrayLength() == 0)
        {
            return null;
        }

        var builder = new StringBuilder();

        foreach (var fact in facts.EnumerateArray())
        {
            var label = fact.TryGetProperty("label", out var l) ? l.GetString() : null;
            var value = fact.TryGetProperty("value", out var v) ? v.GetString() : null;

            if (!string.IsNullOrWhiteSpace(label) && !string.IsNullOrWhiteSpace(value))
            {
                builder.Append("- ").Append(label).Append(": ").AppendLine(value);
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

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

    /// Dil başına konuşma normalizasyonu (P1-13).
    ///
    /// Kayıt uzun süre YAZILI ama BAĞLANMAMIŞ durdu: ham cümle doğrudan
    /// TTS'e gidiyordu, yani "1453" harf harf okunuyordu. Bağlantı burada.
    private static readonly SpeechNormalizerRegistry Speech = SpeechNormalizerRegistry.Default();

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
        var estimated = false;

        foreach (var element in sentences.EnumerateArray())
        {
            // §20.3'ün ayrımı: EKRANDA görünen metin ile SESLENDİRİLEN
            // metin aynı değil. "1453" ekranda öyle yazılır, "bin dört
            // yüz elli üç" diye okunur.
            var displayText = element.GetString() ?? string.Empty;
            var speechText = Speech.Normalize(language, displayText);

            var speech = await tts.SynthesizeAsync(
                new TtsRequest { SpeechText = speechText, VoiceId = voiceId, Language = language },
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
                // İkisi de yazılıyor: sahne planlayıcı ekranda görüneni,
                // sorun giderme okunanı istiyor.
                display_text = displayText,
                speech_text = speechText,
            });

            // ALTYAZI EKRANDAKİ METNİ GÖSTERİYOR, okunanı değil.
            //
            // Sağlayıcının kelime zamanlaması SESLENDİRİLEN metne ait.
            // Normalizasyon metni değiştirdiyse o zamanlamalar başka
            // sözcüklere işaret ediyor: "1453" tek kelime, karşılığı beş
            // kelime. Bire bir eşlemeye kalkmak altyazıyı kaydırırdı, ve
            // ekranda "bin dört yüz elli üç" yazması da yanlış olurdu.
            var normalizationChangedText = !string.Equals(displayText, speechText, StringComparison.Ordinal);

            var timings = speech.Value.Value.WordTimings.Count > 0 && !normalizationChangedText
                ? speech.Value.Value.WordTimings
                : WordTimingEstimator.Distribute(displayText, measured);

            // Kelime zamanları segment içinde 0'dan başlıyor; mutlak zamana
            // kaydırılıyor. Kaydırmayı unutmak tüm altyazıyı videonun başına
            // toplardı.
            foreach (var word in timings)
            {
                cues.Add(new
                {
                    text = word.Text,
                    start_ms = (cursor + word.Start).Value,
                    end_ms = (cursor + word.End).Value,
                    segment = $"s{index}",
                });
            }

            // Zamanlamanın ÖLÇÜLDÜĞÜ mü DAĞITILDIĞI mı kayda giriyor.
            // Bir altyazı kayması araştırılırken ilk bakılacak şey bu.
            estimated |= speech.Value.Value.WordTimings.Count == 0 || normalizationChangedText;

            cursor += measured;
            index++;
        }

        return Result.Success(NodeJson.From(new
        {
            segments,
            cues,
            total_ms = cursor.Value,
            // true = kelime zamanlari OLCULMEDI, dagitildi (P1-15 ara
            // cozum). Gercek hizalama TTS'in kendi zamanlamasindan ya da
            // ASR yan servisinden gelir.
            timings_estimated = estimated,
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

        // P1-16: görsel yönergesi artık sahne planından geliyor.
        //
        // Önceden istem `"{konu} — sahne {n}"` idi ve üretilen kareler
        // cümleyle hiç ilgili değildi — konu doğru, sahne rastgeleydi.
        // Plan her sahneye kendi cümlesinden türetilmiş bir istem veriyor.
        var plan = BuildPlan(context.RunContext, tts, topic);

        if (plan.IsFailure)
        {
            return Result.Failure<JsonElement>(plan.Error);
        }

        var scenes = plan.Value.Scenes;
        var sceneCount = scenes.Count;

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
                    scenes[index].Direction, canvas, context, cancellationToken).ConfigureAwait(false));
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
        // SAHNE LİSTESİ çıktıya giriyor, yalnızca görseller değil.
        //
        // Sahne sayısı ses parçası sayısıyla aynı OLMAYABİLİR: kısa
        // cümleler birleşiyor. Timeline birebir varsayarsa birleşen
        // sahnelerde eşleşme kayar ve ses ile görsel ayrışır — üstelik
        // yalnızca kısa cümle içeren senaryolarda, yani seyrek ve zor
        // fark edilen bir hata olurdu.
        //
        // Yönerge de yazılıyor: bir görsel konuyla ilgisiz çıktığında
        // "hangi istemle üretildi" sorusu kayıttan cevaplanabilsin.
        var assets = results
            .OrderBy(r => r.Index)
            .Select(r => (object)new
            {
                scene = r.Index,
                asset = r.Result.Value,
                start_ms = scenes[r.Index].Start.Value,
                duration_ms = scenes[r.Index].Duration.Value,
                segments = scenes[r.Index].SourceSegments
                    .Select(i => $"s{i.ToString(CultureInfo.InvariantCulture)}")
                    .ToList(),
                query = scenes[r.Index].Direction.SearchQuery,
                prompt = scenes[r.Index].Direction.ImagePrompt,
            })
            .ToList();

        return Result.Success(NodeJson.From(new { images = assets, style = plan.Value.Style }));
    }

    /// Ses parçalarının ÖLÇÜLEN sürelerinden sahne planı kurar.
    ///
    /// Süre buradan geliyor, senaryodan tahmin edilmiyor (ADR-006).
    /// Sahne metni de ses parçasının kendi metninden okunuyor: senaryoyu
    /// ikinci kez bölmek, iki bölmenin ayrışma riskini getirirdi.
    private static Result<ScenePlan> BuildPlan(JsonElement runContext, JsonElement tts, string topic)
    {
        var language = LanguageTag.Create(NodeJson.Text(runContext, "topic.language") ?? "tr-TR");
        var style = VisualStyle.Get(NodeJson.Text(runContext, "input.style"));

        var sentences = new List<string>();
        var durations = new List<Ms>();

        foreach (var segment in tts.GetProperty("segments").EnumerateArray())
        {
            // EKRANDAKİ metin isteniyor, okunan değil: görsel yönergesi
            // "bin dört yüz elli üç"ten değil "1453"ten türemeli — sayının
            // harfe açılmış hâli anahtar kelime çıkarımını bozar.
            var text = segment.TryGetProperty("display_text", out var display)
                ? display.GetString()
                : segment.TryGetProperty("speech_text", out var speech)
                    ? speech.GetString()
                    : null;

            sentences.Add(text ?? string.Empty);
            durations.Add(new Ms(segment.GetProperty("duration_ms").GetInt32()));
        }

        return ScenePlanner.Plan(sentences, durations, topic, language, style);
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
        VisualDirection direction, Canvas canvas, NodeContext context, CancellationToken cancellationToken)
    {
        var index = direction.SceneIndex;

        var image = await images.GenerateAsync(
            new ImagePrompt
            {
                Text = direction.ImagePrompt,
                Width = canvas.Width,
                Height = canvas.Height,
                Seed = direction.Seed,
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
                // GERCEK saglayici kaydediliyor, zincirin adi degil.
                // "stock-first" yazsaydik, gorselin stoktan mi uretimden
                // mi geldigini kayittan hic ogrenemezdik - oysa stok hic
                // tutmuyorsa arama terimleri kotu demektir.
                SourceProvider = (images as StockFirstImageProvider)?.LastRoute ?? images.Key,
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
