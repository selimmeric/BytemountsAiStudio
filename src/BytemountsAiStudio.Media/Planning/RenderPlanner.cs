using System.Globalization;
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

    /// Görüntüsü HAZIR bir videodan gelen plan (P2-11).
    ///
    /// Bölüm bazlı render'ın son adımı: segmentler ayrı ayrı render
    /// edilip birleştiriliyor, sonra ses bu plana takılıyor.
    ///
    /// SES ZİNCİRİ AYNEN KULLANILIYOR (`BuildAudio`). Sesi ayrıca
    /// kurmak, ducking ve müzik mantığının iki yerde yaşaması ve
    /// zamanla ayrışması demekti — ve o ayrışmanın belirtisi, sesin
    /// yalnızca bir render yolunda doğru çıkması olurdu.
    public static Result PlanOverVideo(
        TimelineDocument timeline,
        IReadOnlyDictionary<string, string> resolvedPaths,
        string videoPath)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(resolvedPaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);

        var issues = new List<ValidationIssue>();

        var inputs = new List<InputDecl>
        {
            new() { Id = "prerendered", Path = videoPath, Kind = InputKind.Video },
        };

        var nodes = new List<FilterNode>();

        var videoOut = new StreamRef("vout", MediaKind.Video);

        // Biçim yine de uygulanıyor: birleştirilmiş dosya doğru piksel
        // biçiminde olsa bile, olduğunu VARSAYMAK bazı cihazlarda hiç
        // açılmayan bir video demekti.
        nodes.Add(FilterNode.Format(
            new StreamRef("prerendered", MediaKind.Video), videoOut, timeline.Output.PixelFormat));

        var audioOut = BuildAudio(timeline, resolvedPaths, inputs, nodes, issues);

        if (issues.Count > 0)
        {
            return new Result(null, issues);
        }

        return new Result(
            new PlanResult(
                new FilterGraph
                {
                    Inputs = inputs,
                    Nodes = nodes,
                    VideoOut = videoOut,
                    AudioOut = audioOut,
                },
                OptionsFor(timeline)),
            issues);
    }

    private static OutputOptions OptionsFor(TimelineDocument timeline) => new()
    {
        VideoCodec = timeline.Output.VideoCodec,
        Crf = timeline.Output.Crf,
        PresetSpeed = timeline.Output.PresetSpeed,
        PixelFormat = timeline.Output.PixelFormat,
        AudioCodec = timeline.Output.AudioCodec,
        AudioBitrate = timeline.Output.AudioBitrate,
        FrameRate = timeline.Canvas.Fps,
        DurationSeconds = timeline.Duration.TotalSeconds,
    };

    /// SESSİZ plan (P2-11): yalnızca görüntü.
    ///
    /// Bölüm bazlı render'da segmentler sessiz üretiliyor ve ses
    /// birleştirmeden sonra tek seferde biniyor. Sesi de bölmek,
    /// cümlelerin segment sınırlarında kesilmesi ve her sınırda duyulur
    /// bir tıklama demekti — konuşma sahne sınırlarına saygı duymuyor.
    public static Result PlanVideoOnly(
        TimelineDocument timeline,
        IReadOnlyDictionary<string, string> resolvedPaths,
        IReadOnlyList<TimedLayer>? overlays = null)
        => Plan(timeline, resolvedPaths, overlays, silent: true);

    public static Result Plan(
        TimelineDocument timeline,
        IReadOnlyDictionary<string, string> resolvedPaths,
        IReadOnlyList<TimedLayer>? overlays = null)
        => Plan(timeline, resolvedPaths, overlays, silent: false);

    private static Result Plan(
        TimelineDocument timeline,
        IReadOnlyDictionary<string, string> resolvedPaths,
        IReadOnlyList<TimedLayer>? overlays,
        bool silent)
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

        // KALICI KATMANLAR (filigran) EN ÜSTTE.
        //
        // Altyazıdan SONRA biniyor: filigranın altyazının altında kalması
        // onu kısmen görünmez yapardı ve filigranın tek işi görünmek.
        //
        // Bu blok da uzun süre eksikti: `PersistentLayers` modelde vardı,
        // render'a hiç girmiyordu — müzik yatağıyla aynı sessiz vaat.
        videoTail = AddPersistentLayers(
            timeline, resolvedPaths, inputs, nodes, issues, videoTail, fps);

        var videoOut = new StreamRef("vout", MediaKind.Video);
        nodes.Add(FilterNode.Format(videoTail, videoOut, timeline.Output.PixelFormat));

        // ---- ses ----
        StreamRef? audioOut = silent
            ? null
            : BuildAudio(timeline, resolvedPaths, inputs, nodes, issues);

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

        return new Result(new PlanResult(graph, OptionsFor(timeline)), issues);
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

        var seconds = timeline.Duration.TotalSeconds;

        // MÜZİK YATAĞI.
        //
        // Model uzun süre bunu VAAT ETTİ ama render yok saydı:
        // `AudioTrack.Music` doluydu, filtre grafiğine hiç girmiyordu.
        // Sessizce yok saymak en kötü seçenekti — kanal ayarında müzik
        // açık görünüyor, videoda müzik yok, ve hiçbir şey hata vermiyor.
        var withMusic = AddMusic(timeline, resolvedPaths, inputs, nodes, issues, mixed, seconds);

        // Sesi tam süreye oturt: kısaysa sessizlikle uzat, sonra kes.
        // İkisi birlikte olmazsa çıktı süresi videodan sapar.
        var padded = new StreamRef("apadded", MediaKind.Audio);
        nodes.Add(FilterNode.APadTrim(withMusic, padded, seconds));

        var trimmed = new StreamRef("atrim", MediaKind.Audio);
        nodes.Add(FilterNode.ATrim(padded, trimmed, seconds));

        // ---- SES SEVİYESİ YAYIN STANDARDINA ÇEKİLİYOR ----
        //
        // `ALoudNorm` düğümü ve `AudioTrack.TargetLufs` (−16) VARDI ama
        // planlayıcı ikisini hiç kullanmıyordu: timeline bir hedef
        // vaat ediyor, render onu yok sayıyordu.
        //
        // Bunu ancak ses ÖLÇÜLMEYE başlayınca gördük: ilk gerçek koşu
        // −24,8 LUFS çıktı, hedef −16. Sekiz desibel fark, izleyicinin
        // sesi açmak zorunda kalması demek — ve platform kendi
        // normalizasyonunu uygularken dinamikleri bozuyor.
        //
        // NORMALİZASYON EN SONDA: konuşma, müzik ve ducking
        // karıştıktan sonra. Önce uygulansaydı müzik eklenince seviye
        // yeniden kayardı.
        var audioOut = new StreamRef("aout", MediaKind.Audio);
        nodes.Add(FilterNode.ALoudNorm(trimmed, audioOut, timeline.Audio.TargetLufs));

        return audioOut;
    }

    /// Kalıcı katmanları (filigran, logo) videonun üstüne bindirir.
    ///
    /// Konum İFADE olarak veriliyor (`W-w-40` gibi), sabit piksel olarak
    /// değil: `W`/`w` FFmpeg'in ana ve katman genişlikleri. Sabit piksel
    /// yazsaydık filigranın kendi boyutunu bilmemiz gerekirdi ve o bilgi
    /// planlama anında yok — dosyayı açmadan öğrenilemiyor.
    private static StreamRef AddPersistentLayers(
        TimelineDocument timeline,
        IReadOnlyDictionary<string, string> resolvedPaths,
        List<InputDecl> inputs,
        List<FilterNode> nodes,
        List<ValidationIssue> issues,
        StreamRef videoTail,
        int fps)
    {
        if (timeline.PersistentLayers.Count == 0)
        {
            return videoTail;
        }

        var seconds = timeline.Duration.TotalSeconds;
        var index = 0;

        foreach (var layer in timeline.PersistentLayers)
        {
            var path = Resolve(layer.Asset, resolvedPaths, issues, $"'{layer.Role}' kalıcı katmanı");

            if (path is null)
            {
                continue;
            }

            var inputId = $"layer{index.ToString(CultureInfo.InvariantCulture)}";

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

            // Saydamlık için ÖNCE rgba: alfa kanalı olmayan bir görselde
            // `colorchannelmixer` saydamlık üretemiyor ve filigran tam
            // opak çıkıyor. PNG'de alfa var, JPEG'de yok, ve filigranın
            // hangi biçimde geleceğini önceden bilmiyoruz.
            var rgba = new StreamRef($"{inputId}_rgba", MediaKind.Video);
            nodes.Add(FilterNode.FormatRgba(source, rgba));

            var faded = new StreamRef($"{inputId}_a", MediaKind.Video);
            nodes.Add(FilterNode.Opacity(rgba, faded, layer.Opacity));

            var next = new StreamRef($"layered{index.ToString(CultureInfo.InvariantCulture)}", MediaKind.Video);

            var (x, y) = AnchorExpression(layer);
            nodes.Add(FilterNode.Overlay(videoTail, faded, next, x, y));

            videoTail = next;
            index++;
        }

        return videoTail;
    }

    /// Bağlantı noktasını FFmpeg overlay ifadesine çevirir.
    ///
    /// `W`/`H` ana videonun, `w`/`h` katmanın ölçüleri. Katmanın kendi
    /// boyutu planlama anında bilinmediği için hesap FFmpeg'e bırakılıyor.
    internal static (string X, string Y) AnchorExpression(PersistentLayer layer)
    {
        var mx = layer.MarginX.ToString(CultureInfo.InvariantCulture);
        var my = layer.MarginY.ToString(CultureInfo.InvariantCulture);

        return layer.Anchor switch
        {
            Anchor.TopLeft => (mx, my),
            Anchor.TopRight => ($"W-w-{mx}", my),
            Anchor.BottomLeft => (mx, $"H-h-{my}"),
            Anchor.BottomRight => ($"W-w-{mx}", $"H-h-{my}"),
            Anchor.BottomCenter => ("(W-w)/2", $"H-h-{my}"),
            _ => ("(W-w)/2", "(H-h)/2"),
        };
    }

    /// Müzik yatağını konuşmanın altına serer ve gerekiyorsa ducking uygular.
    ///
    /// Zincir: giriş → döngü → seviye → fade in/out → (ducking) → karışım
    ///
    /// Ducking varsa KONUŞMA İKİYE AYRILIYOR (`asplit`): bir kopya
    /// sidechain tetiği, bir kopya nihai karışım. FFmpeg'de bir akış
    /// yalnızca bir kez tüketilebiliyor; ayırmadan bağlamak "geçersiz
    /// filtre grafiği" hatası veriyor ve o mesaj sorunun nerede
    /// olduğunu hiç söylemiyor.
    private static StreamRef AddMusic(
        TimelineDocument timeline,
        IReadOnlyDictionary<string, string> resolvedPaths,
        List<InputDecl> inputs,
        List<FilterNode> nodes,
        List<ValidationIssue> issues,
        StreamRef voice,
        double seconds)
    {
        if (timeline.Audio.Music is not { } music)
        {
            return voice;
        }

        var path = Resolve(music.Asset, resolvedPaths, issues, "müzik yatağı");

        if (path is null)
        {
            // `Resolve` sorunu zaten kaydetti. Müziksiz devam etmek
            // yerine burada durmak videoyu tamamen kaybettirirdi;
            // konuşma tek başına geçerli bir ses.
            return voice;
        }

        inputs.Add(new InputDecl { Id = "music", Path = path, Kind = InputKind.Audio });

        var current = new StreamRef("music", MediaKind.Audio);

        if (music.Loop)
        {
            var looped = new StreamRef("m_loop", MediaKind.Audio);
            nodes.Add(FilterNode.ALoop(current, looped));
            current = looped;
        }

        var leveled = new StreamRef("m_gain", MediaKind.Audio);
        nodes.Add(FilterNode.Volume(current, leveled, music.GainDb));
        current = leveled;

        // Müzik VİDEO SÜRESİNE kırpılıyor; fade-out'un başlangıcı buna
        // göre hesaplanıyor. Kırpmadan fade koymak, sesin bittiği yerde
        // değil müziğin bittiği yerde solmasına yol açardı.
        var trimmed = new StreamRef("m_trim", MediaKind.Audio);
        nodes.Add(FilterNode.ATrim(current, trimmed, seconds));
        current = trimmed;

        if (music.FadeIn.Value > 0)
        {
            var faded = new StreamRef("m_fin", MediaKind.Audio);
            nodes.Add(FilterNode.AFadeIn(current, faded, music.FadeIn.TotalSeconds));
            current = faded;
        }

        if (music.FadeOut.Value > 0 && music.FadeOut.TotalSeconds < seconds)
        {
            var faded = new StreamRef("m_fout", MediaKind.Audio);
            nodes.Add(FilterNode.AFadeOut(
                current, faded, seconds - music.FadeOut.TotalSeconds, music.FadeOut.TotalSeconds));
            current = faded;
        }

        var voiceForMix = voice;

        if (music.Ducking is { } ducking)
        {
            var trigger = new StreamRef("v_trigger", MediaKind.Audio);
            var forMix = new StreamRef("v_mix", MediaKind.Audio);

            nodes.Add(FilterNode.ASplit(voice, [trigger, forMix]));
            voiceForMix = forMix;

            var ducked = new StreamRef("m_duck", MediaKind.Audio);

            nodes.Add(FilterNode.SidechainCompress(
                current, trigger, ducked,
                ducking.TargetGainDb - music.GainDb,
                ducking.AttackMs,
                ducking.ReleaseMs));

            current = ducked;
        }

        var mixedWithMusic = new StreamRef("a_with_music", MediaKind.Audio);
        nodes.Add(FilterNode.AMix([voiceForMix, current], mixedWithMusic));

        return mixedWithMusic;
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
