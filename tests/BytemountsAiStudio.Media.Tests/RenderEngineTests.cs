using BytemountsAiStudio.Core.Time;
using BytemountsAiStudio.Media.Ir;
using BytemountsAiStudio.Media.Planning;
using BytemountsAiStudio.Media.Timeline;

namespace BytemountsAiStudio.Media.Tests;

/// Planner → IR → Validator → Emitter zincirinin testleri.
///
/// Hepsi FFmpeg olmadan, milisaniyede koşuyor. §12.8'de tarif edilen ilk üç
/// seviye bu: birim, topoloji ve emitter. Studio'da yalnızca sonuncusu vardı
/// ve o da 12 KB'lık bir metnin karşılaştırılmasıydı.
public sealed class RenderPlannerTests
{
    private static Dictionary<string, string> Paths(TimelineDocument t)
        => t.Scenes.Select(s => s.Visual.Asset.Sha256)
            .Concat(t.Audio.VoiceSegments.Select(s => s.Asset.Sha256))
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(sha => sha, sha => $"/tmp/{sha[..8]}.bin", StringComparer.Ordinal);

    [Fact]
    public void GecerliTimeline_PlanUretir()
    {
        var timeline = TimelineFactory.Valid();
        var plan = RenderPlanner.Plan(timeline, Paths(timeline));

        Assert.True(plan.IsSuccess, string.Join(" | ", plan.Issues));
        Assert.Equal(4, plan.Plan!.Graph.Inputs.Count);   // 2 sahne görseli + 2 ses
    }

    [Fact]
    public void CozumlenmemisVarlik_PlaniDusurur()
    {
        // ADR-007: timeline'a giren her varlık çözümlenmiş olmalı. Eksik
        // çıkması, önceki bir adımın işini yapmadığı anlamına gelir.
        var timeline = TimelineFactory.Valid();
        var plan = RenderPlanner.Plan(timeline, new Dictionary<string, string>(StringComparer.Ordinal));

        Assert.False(plan.IsSuccess);
        Assert.Contains(plan.Issues, i => i.Code == "plan.unresolved_asset");
    }

    [Fact]
    public void UretilenGraf_Gecerlidir()
    {
        var timeline = TimelineFactory.Valid();
        var plan = RenderPlanner.Plan(timeline, Paths(timeline));

        var issues = GraphValidator.Validate(plan.Plan!.Graph);

        Assert.True(issues.Count == 0, string.Join(" | ", issues));
    }

    [Fact]
    public void ZoompanYalnizcaHareketliSahneyeEklenir()
    {
        // Gereksiz zoompan hem CPU harcar hem de ölçekleme yüzünden görselin
        // netliğini düşürür. Fabrikadaki belgede yalnızca ilk sahne hareketli.
        var timeline = TimelineFactory.Valid();
        var withMotion = RenderPlanner.Plan(timeline, Paths(timeline));

        Assert.Equal(1, withMotion.Plan!.Graph.Nodes.Count(n => n.Filter == "zoompan"));

        var scenes = timeline.Scenes.ToList();
        scenes[0] = scenes[0] with { Visual = scenes[0].Visual with { Motion = null } };
        var still = timeline with { Scenes = scenes };

        var withoutMotion = RenderPlanner.Plan(still, Paths(still));

        Assert.Equal(0, withoutMotion.Plan!.Graph.Nodes.Count(n => n.Filter == "zoompan"));
    }

    [Fact]
    public void SesParcalari_BaslangicZamanlarinaGoreGeciktirilir()
    {
        var timeline = TimelineFactory.Valid();
        var plan = RenderPlanner.Plan(timeline, Paths(timeline));

        var delays = plan.Plan!.Graph.Nodes
            .Where(n => n.Filter == "adelay")
            .Select(n => n.Args.First(a => a.Key == "delays").Value)
            .ToList();

        Assert.Equal(["0", "5000"], delays);
    }

    [Fact]
    public void CiktiSuresi_TimelineSuresineEsit()
    {
        // Süreyi açıkça sabitlemezsek ses ya da video bir kare uzun biter
        // ve QC'nin süre kontrolü düşer.
        var timeline = TimelineFactory.Valid();
        var plan = RenderPlanner.Plan(timeline, Paths(timeline));

        Assert.Equal(12.0, plan.Plan!.Output.DurationSeconds, 3);
    }
}

public sealed class FilterGraphEmitterTests
{
    private static FilterGraph BuildGraph(params string[] inputIds)
    {
        var inputs = inputIds
            .Select(id => new InputDecl { Id = id, Path = $"/tmp/{id}.png", Kind = InputKind.Image })
            .ToList();

        var video = new StreamRef("vout", MediaKind.Video);
        var audioIn = new InputDecl { Id = "voice", Path = "/tmp/voice.wav", Kind = InputKind.Audio };
        inputs.Add(audioIn);

        var nodes = new List<FilterNode>
        {
            FilterNode.ScaleCover(new StreamRef(inputIds[0], MediaKind.Video),
                new StreamRef("scaled", MediaKind.Video), 1080, 1920),
            FilterNode.Format(new StreamRef("scaled", MediaKind.Video), video, "yuv420p"),
            FilterNode.ADelay(new StreamRef("voice", MediaKind.Audio),
                new StreamRef("aout", MediaKind.Audio), 0),
        };

        return new FilterGraph
        {
            Inputs = inputs,
            Nodes = nodes,
            VideoOut = video,
            AudioOut = new StreamRef("aout", MediaKind.Audio),
        };
    }

    [Fact]
    public void GirdiKimlikleri_IndeksleCevrilir()
    {
        var graph = BuildGraph("bg");
        var text = FilterGraphEmitter.EmitFilterComplex(graph);

        // "bg" girdisi 0. sırada -> [0:v]
        Assert.Contains("[0:v]scale=", text, StringComparison.Ordinal);
        // ses girdisi 1. sırada -> [1:a]
        Assert.Contains("[1:a]adelay=", text, StringComparison.Ordinal);
    }

    [Fact]
    public void GirdiSirasiDegisince_GrafBozulmaz()
    {
        // §12.1/L2'nin regresyon testi. Studio'da girdi indeksleri elle
        // korunan bir teamüle bağlıydı; yeni bir girdi tipi eklemek tüm
        // indeksleri kaydırıyordu. Burada isimler taşındığı için sıralama
        // değişse de bağlantılar aynı kalıyor.
        var graph = BuildGraph("bg");

        var reordered = graph with
        {
            Inputs = graph.Inputs.Reverse().ToList(),
        };

        var text = FilterGraphEmitter.EmitFilterComplex(reordered);

        // Artık ses 0., görsel 1. sırada — ve emitter bunu doğru yansıtıyor.
        Assert.Contains("[0:a]adelay=", text, StringComparison.Ordinal);
        Assert.Contains("[1:v]scale=", text, StringComparison.Ordinal);

        // Grafiğin kendisi hâlâ geçerli: bağlantılar isimle kurulduğu için
        // sıralama bir şey bozmadı.
        Assert.Empty(GraphValidator.Validate(reordered));
    }

    [Fact]
    public void OzelKarakterliIfadeler_TirnaklanirVeKacirilir()
    {
        // İfadeler `,` ve `/` içeriyor; kaçırılmazsa FFmpeg bunları
        // argüman ayırıcısı sanar ve anlaşılmaz bir hata verir.
        var expr = ExprCompiler.Interpolate(1.0, 1.12, 30, Easing.EaseInOut);
        var node = FilterNode.Zoompan(
            new StreamRef("in", MediaKind.Video), new StreamRef("out", MediaKind.Video),
            expr, ExprCompiler.CenterX(), ExprCompiler.CenterY(), 1, 1080, 1920, 30);

        var graph = new FilterGraph
        {
            Inputs = [new() { Id = "in", Path = "/tmp/a.png", Kind = InputKind.Image }],
            Nodes = [node],
            VideoOut = new StreamRef("out", MediaKind.Video),
            AudioOut = new StreamRef("out", MediaKind.Video),
        };

        var text = FilterGraphEmitter.EmitFilterComplex(graph);

        Assert.Contains("z='", text, StringComparison.Ordinal);
        Assert.DoesNotContain("z=(1+", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ArgumanListesi_GirdileriDogruSiralar()
    {
        var graph = BuildGraph("bg");
        var command = FilterGraphEmitter.Emit(
            graph, "/tmp/f.txt", "/tmp/out.mp4",
            new OutputOptions { FrameRate = 30, DurationSeconds = 5 });

        var args = command.Arguments;

        // Görsel girdilerde `-loop 1` ve `-t` girdiden ÖNCE gelmeli;
        // sonra gelirse çıktıya uygulanır ve tüm videoyu kırpar.
        Assert.Contains("-filter_complex_script", args, StringComparer.Ordinal);
        Assert.Equal("/tmp/out.mp4", args[^1]);
        Assert.Contains("+faststart", args, StringComparer.Ordinal);
    }
}

public sealed class GraphValidatorTests
{
    private static FilterGraph Minimal(IReadOnlyList<FilterNode> nodes, StreamRef v, StreamRef a)
        => new()
        {
            Inputs =
            [
                new() { Id = "img", Path = "/tmp/a.png", Kind = InputKind.Image },
                new() { Id = "snd", Path = "/tmp/a.wav", Kind = InputKind.Audio },
            ],
            Nodes = nodes,
            VideoOut = v,
            AudioOut = a,
        };

    [Fact]
    public void UretilmemisAkisaBasvuru_Yakalanir()
    {
        var graph = Minimal(
            [FilterNode.Format(new StreamRef("yok", MediaKind.Video), new StreamRef("v", MediaKind.Video), "yuv420p"),
             FilterNode.ADelay(new StreamRef("snd", MediaKind.Audio), new StreamRef("a", MediaKind.Audio), 0)],
            new StreamRef("v", MediaKind.Video), new StreamRef("a", MediaKind.Audio));

        Assert.Contains(GraphValidator.Validate(graph), i => i.Code == "graph.unknown_stream");
    }

    [Fact]
    public void AyniAkisIkiKezTuketilirse_Yakalanir()
    {
        // Filter graph'ta bir pad'i iki yerde kullanmak `split` gerektirir;
        // sessizce yapılırsa FFmpeg anlaşılmaz bir hata verir.
        var source = new StreamRef("img", MediaKind.Video);

        var graph = Minimal(
            [FilterNode.Format(source, new StreamRef("v1", MediaKind.Video), "yuv420p"),
             FilterNode.Format(source, new StreamRef("v", MediaKind.Video), "yuv420p"),
             FilterNode.ADelay(new StreamRef("snd", MediaKind.Audio), new StreamRef("a", MediaKind.Audio), 0)],
            new StreamRef("v", MediaKind.Video), new StreamRef("a", MediaKind.Audio));

        Assert.Contains(GraphValidator.Validate(graph), i => i.Code == "graph.multiple_consumers");
    }

    [Fact]
    public void KullanilmayanAraAkis_Yakalanir()
    {
        // FFmpeg buna "Output pad not connected" der ve render hiç başlamaz.
        var graph = Minimal(
            [FilterNode.Format(new StreamRef("img", MediaKind.Video), new StreamRef("v", MediaKind.Video), "yuv420p"),
             FilterNode.ADelay(new StreamRef("snd", MediaKind.Audio), new StreamRef("a", MediaKind.Audio), 0),
             FilterNode.Volume(new StreamRef("a", MediaKind.Audio), new StreamRef("bosta", MediaKind.Audio), -3)],
            new StreamRef("v", MediaKind.Video), new StreamRef("a", MediaKind.Audio));

        var issues = GraphValidator.Validate(graph);

        Assert.Contains(issues, i => i.Code == "graph.dangling_stream" || i.Code == "graph.output_consumed");
    }

    [Fact]
    public void CiktiUretilmiyorsa_Yakalanir()
    {
        var graph = Minimal(
            [FilterNode.ADelay(new StreamRef("snd", MediaKind.Audio), new StreamRef("a", MediaKind.Audio), 0)],
            new StreamRef("hicyok", MediaKind.Video), new StreamRef("a", MediaKind.Audio));

        Assert.Contains(GraphValidator.Validate(graph), i => i.Code == "graph.output_missing");
    }

    [Fact]
    public void YanlisMedyaTipi_Yakalanir()
    {
        var graph = Minimal(
            [FilterNode.Format(new StreamRef("img", MediaKind.Video), new StreamRef("v", MediaKind.Video), "yuv420p")],
            new StreamRef("v", MediaKind.Video), new StreamRef("v", MediaKind.Video));

        Assert.Contains(GraphValidator.Validate(graph), i => i.Code == "graph.output_kind");
    }
}

public sealed class ExprCompilerTests
{
    [Fact]
    public void CokKeyframeliIfade_DerinlesmezDuzKalir()
    {
        // Studio'nun zor kazanılmış dersi: iç içe ifadeler FFmpeg'in
        // ayrıştırıcı derinlik sınırına takılıyordu. Bu üretim, kare sayısı
        // ne olursa olsun sabit derinlikte kalır.
        var short_ = ExprCompiler.Interpolate(1.0, 1.2, 30, Easing.EaseInOut).Text;
        var long_ = ExprCompiler.Interpolate(1.0, 1.2, 3000, Easing.EaseInOut).Text;

        Assert.Equal(Depth(short_), Depth(long_));
    }

    [Fact]
    public void AyniDegerlerdeInterpolasyon_SabitDoner()
    {
        // Değişmeyen bir değer için ifade üretmek boşuna hesap yükü.
        Assert.Equal("1.5", ExprCompiler.Interpolate(1.5, 1.5, 30, Easing.Linear).Text);
    }

    [Fact]
    public void IfadelerNoktaliOndalikKullanir()
    {
        // Türkçe kültürde virgüllü sayı üretilirse FFmpeg onu argüman
        // ayırıcısı sanar. Kültürden bağımsız biçimlendirme şart.
        var text = ExprCompiler.Interpolate(1.0, 1.125, 30, Easing.Linear).Text;

        Assert.Contains("0.125", text, StringComparison.Ordinal);
        Assert.DoesNotContain("0,125", text, StringComparison.Ordinal);
    }

    private static int Depth(string expression)
    {
        int depth = 0, max = 0;

        foreach (var c in expression)
        {
            if (c == '(')
            {
                max = Math.Max(max, ++depth);
            }
            else if (c == ')')
            {
                depth--;
            }
        }

        return max;
    }
}

public sealed class GraphDotTests
{
    [Fact]
    public void DotDokumu_GecerliDigraphUretir()
    {
        var timeline = TimelineFactory.Valid();
        var paths = timeline.Scenes.Select(s => s.Visual.Asset.Sha256)
            .Concat(timeline.Audio.VoiceSegments.Select(s => s.Asset.Sha256))
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(sha => sha, sha => $"/tmp/{sha[..8]}.bin", StringComparer.Ordinal);

        var plan = RenderPlanner.Plan(timeline, paths);
        var dot = GraphDot.Render(plan.Plan!.Graph);

        Assert.StartsWith("digraph", dot, StringComparison.Ordinal);
        Assert.Contains("out_video", dot, StringComparison.Ordinal);
        Assert.Contains("out_audio", dot, StringComparison.Ordinal);
        Assert.Equal(dot.Count(c => c == '{'), dot.Count(c => c == '}'));
    }
}
