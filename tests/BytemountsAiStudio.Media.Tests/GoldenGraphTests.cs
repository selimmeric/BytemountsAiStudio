using System.Text;
using System.Text.Json;
using BytemountsAiStudio.Media.Ir;
using BytemountsAiStudio.Media.Planning;
using BytemountsAiStudio.Media.Timeline;

namespace BytemountsAiStudio.Media.Tests;

/// Golden testler — ama Studio'daki gibi 12 KB'lık metin karşılaştırması değil.
///
/// §12.8: iki ayrı seviye var. TOPOLOJİ altın kaydı grafın yapısını tutuyor
/// (hangi filtre, hangi akıştan hangi akışa); EMITTER altın kaydı ise metnin
/// kendisini. Ayırmanın sebebi şu: bir filtrenin argüman biçimi değiştiğinde
/// yalnızca ikincisi kırılır ve diff okunabilir kalır. Tek bir dev metin
/// karşılaştırmasında "ne değişti" sorusu cevapsız kalıyordu.
public sealed class GoldenGraphTests
{
    private static PlanResult Plan()
    {
        var timeline = TimelineFactory.Valid();

        var paths = timeline.Scenes.Select(s => s.Visual.Asset.Sha256)
            .Concat(timeline.Audio.VoiceSegments.Select(s => s.Asset.Sha256))
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(sha => sha, sha => $"/assets/{sha[..8]}.bin", StringComparer.Ordinal);

        var plan = RenderPlanner.Plan(timeline, paths);
        Assert.True(plan.IsSuccess, string.Join(" | ", plan.Issues));

        return plan.Plan!;
    }

    /// Grafın kanonik topolojisi. Okunabilir olması kasıtlı: bu dosya
    /// değiştiğinde diff'e bakan kişi ne olduğunu anlamalı.
    private static string Topology(FilterGraph graph)
    {
        var builder = new StringBuilder();

        foreach (var input in graph.Inputs)
        {
            builder.Append("input ").Append(input.Id).Append(' ')
                .Append(input.Kind)
                .Append(input.Loop ? " loop" : string.Empty)
                .AppendLine();
        }

        foreach (var node in graph.Nodes)
        {
            builder
                .Append(string.Join(',', node.Inputs.Select(i => i.ToString())))
                .Append(" -> ").Append(node.Filter).Append(" -> ")
                .AppendLine(string.Join(',', node.Outputs.Select(o => o.ToString())));
        }

        builder.Append("video_out ").AppendLine(graph.VideoOut.ToString());
        builder.Append("audio_out ").AppendLine(graph.AudioOut.ToString());

        return builder.ToString();
    }

    [Fact]
    public void GrafTopolojisi_AltinKayitlaAyni()
    {
        var expected = """
            input scene0 Image loop
            input scene1 Image loop
            input voice_s1 Audio
            input voice_s2 Audio
            scene0:v -> scale -> s0scaled:v
            s0scaled:v -> crop -> s0crop:v
            s0crop:v -> zoompan -> s0zoom:v
            s0zoom:v -> fade -> s0fade:v
            s0fade:v -> setsar -> s0out:v
            scene1:v -> scale -> s1scaled:v
            s1scaled:v -> crop -> s1crop:v
            s1crop:v -> setsar -> s1out:v
            s0out:v,s1out:v -> concat -> vcat:v
            vcat:v -> format -> vout:v
            voice_s1:a -> adelay -> a_s1:a
            voice_s2:a -> adelay -> a_s2:a
            a_s1:a,a_s2:a -> amix -> amixed:a
            amixed:a -> apad -> apadded:a
            apadded:a -> atrim -> atrim:a
            atrim:a -> loudnorm -> aout:a
            video_out vout:v
            audio_out aout:a

            """;

        Assert.Equal(expected.ReplaceLineEndings("\n"), Topology(Plan().Graph).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void EmitterCiktisi_AltinKayitlaAyni()
    {
        var text = FilterGraphEmitter.EmitFilterComplex(Plan().Graph).ReplaceLineEndings("\n");

        // Tam metni sabitlemek yerine değişmemesi gereken parçaları
        // sabitliyoruz: kaçış kuralları, indeks ataması ve argüman biçimi.
        Assert.Contains("[0:v]scale=w=2160:h=3840:force_original_aspect_ratio=increase[s0scaled]",
            text, StringComparison.Ordinal);
        Assert.Contains("[s0scaled]crop=2160:3840[s0crop]", text, StringComparison.Ordinal);
        Assert.Contains("d=1:s=1080x1920:fps=30", text, StringComparison.Ordinal);
        Assert.Contains("[2:a]adelay=delays=0:all=1[a_s1]", text, StringComparison.Ordinal);
        Assert.Contains("[3:a]adelay=delays=5000:all=1[a_s2]", text, StringComparison.Ordinal);
        Assert.Contains("amix=inputs=2:normalize=0:dropout_transition=0", text, StringComparison.Ordinal);
        Assert.Contains("[vcat]format=yuv420p[vout]", text, StringComparison.Ordinal);

        // SES SEVİYESİ YAYIN STANDARDINA ÇEKİLİYOR (−16 LUFS).
        //
        // `ALoudNorm` düğümü ve `AudioTrack.TargetLufs` vardı ama
        // planlayıcı ikisini hiç kullanmıyordu: timeline bir hedef
        // vaat ediyor, render onu yok sayıyordu. İlk gerçek ölçüm
        // −24,8 LUFS gösterdi.
        Assert.Contains("loudnorm=I=-16:TP=-1.5:LRA=11", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AyniTimeline_AyniGrafiUretir()
    {
        // Planlayıcı saf: aynı girdi her zaman aynı graf. Bu bozulursa
        // render önbelleği ve determinizm garantisi de bozulur.
        Assert.Equal(Topology(Plan().Graph), Topology(Plan().Graph));
    }

    [Fact]
    public void TimelineJson_GidipGelmedeKorunur()
    {
        // §11: timeline bir BELGE. Serileştirme kaybı olsaydı varlık
        // deposundan okunan timeline render edilenden farklı olurdu.
        var original = TimelineFactory.Valid();
        var json = TimelineJson.Serialize(original);
        var restored = TimelineJson.Deserialize(json);

        Assert.NotNull(restored);
        Assert.Equal(original.Duration, restored.Duration);
        Assert.Equal(original.Canvas.Width, restored.Canvas.Width);
        Assert.Equal(original.Language.Value, restored.Language.Value);
        Assert.Equal(original.Scenes.Count, restored.Scenes.Count);
        Assert.Equal(original.Scenes[0].Range, restored.Scenes[0].Range);
        Assert.Equal(original.Scenes[0].Visual.Asset, restored.Scenes[0].Visual.Asset);
        Assert.Equal(original.Audio.VoiceSegments[1].Start, restored.Audio.VoiceSegments[1].Start);
        Assert.Equal(original.Captions!.Cues.Count, restored.Captions!.Cues.Count);
        Assert.Equal(original.Styles["caption"].SizePercent, restored.Styles["caption"].SizePercent);
    }

    [Fact]
    public void TimelineJson_SurelerDuzSayiOlarakYazilir()
    {
        // Nesne olarak yazmak (`{"value":4820}`) belgeyi hem şişirir hem
        // insan tarafından okunmaz yapardı.
        var json = TimelineJson.Serialize(TimelineFactory.Valid());
        using var document = JsonDocument.Parse(json);

        Assert.Equal(12000, document.RootElement.GetProperty("duration").GetInt32());
        Assert.Equal(JsonValueKind.Array,
            document.RootElement.GetProperty("scenes")[0].GetProperty("range").ValueKind);
    }
}
