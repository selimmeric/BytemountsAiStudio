using BytemountsAiStudio.Core.Time;
using BytemountsAiStudio.Media.Ir;
using BytemountsAiStudio.Media.Planning;
using BytemountsAiStudio.Media.Timeline;

namespace BytemountsAiStudio.Media.Tests;

/// Açılma geçişinin filtre grafına gerçekten ULAŞMASI (P3-04).
///
/// Bu depoda tekrar eden hata sınıfı: bir alan modele ekleniyor,
/// yazılıyor, ve hiçbir şey onu okumuyor. Timeline'a `TransitionIn`
/// yazmak tek başına hiçbir kare değiştirmiyor — planlayıcı onu
/// `fade` filtresine çevirmezse video yine sert başlar ve kimse
/// sebebini aramaz, çünkü belgede geçiş "var" görünür.
public sealed class OpeningFadeTests
{
    private static Dictionary<string, string> Paths(TimelineDocument t)
        => t.Scenes.Select(s => s.Visual.Asset.Sha256)
            .Concat(t.Audio.VoiceSegments.Select(s => s.Asset.Sha256))
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(sha => sha, sha => $"/tmp/{sha[..8]}.bin", StringComparer.Ordinal);

    private static TimelineDocument WithOpening(Ms? opening)
    {
        var timeline = TimelineFactory.Valid();
        var scenes = timeline.Scenes.ToList();

        scenes[0] = scenes[0] with
        {
            TransitionIn = opening is { } value ? new Transition(TransitionKind.Fade, value) : null,
        };

        return timeline with { Scenes = scenes };
    }

    /// AÇILMA `fade t=in` OLARAK GRAFA GİRİYOR.
    [Fact]
    public void Acilma_FiltreyeCevriliyor()
    {
        var plan = RenderPlanner.Plan(WithOpening(new Ms(500)), Paths(TimelineFactory.Valid()));

        Assert.True(plan.IsSuccess, string.Join(" | ", plan.Issues));

        var fadeIn = plan.Plan!.Graph.Nodes
            .Where(n => n.Filter == "fade")
            .Where(n => n.Args.Any(a => a.Key == "t" && a.Value == "in"))
            .ToList();

        Assert.Single(fadeIn);

        // Süre de taşınıyor: filtre var ama süresi yanlışsa açılma
        // ya göz kırpması ya da yarım sahne olurdu.
        Assert.Contains(fadeIn[0].Args, a => a.Key == "d" && a.Value == "0.5");
    }

    /// AÇILMA YOKSA FİLTRE DE YOK.
    ///
    /// Her sahneye açılma eklemek, videoyu sürekli kararıp aydınlanan
    /// bir şeye çevirirdi — ve o hatanın belirtisi de tam olarak
    /// "grafta fazladan fade" olurdu.
    [Fact]
    public void AcilmaYok_FiltreYok()
    {
        var plan = RenderPlanner.Plan(WithOpening(null), Paths(TimelineFactory.Valid()));

        Assert.True(plan.IsSuccess, string.Join(" | ", plan.Issues));

        Assert.DoesNotContain(plan.Plan!.Graph.Nodes,
            n => n.Filter == "fade" && n.Args.Any(a => a.Key == "t" && a.Value == "in"));
    }

    /// AÇILMA, KARARMADAN ÖNCE UYGULANIYOR.
    ///
    /// İkisi de aynı akışa `fade` ekliyor. Ters sırada yazılsalardı
    /// açılma kararmanın ÜSTÜNE uygulanır ve videonun sonu önce
    /// kararıp yeniden açılırdı — ffmpeg çalıştırmadan görülmesi zor,
    /// çalıştırınca da ancak son saniyeye bakan biri fark eder.
    [Fact]
    public void Acilma_KararmadanOnce()
    {
        var timeline = TimelineFactory.Valid();
        var scenes = timeline.Scenes.ToList();

        scenes[0] = scenes[0] with
        {
            TransitionIn = new Transition(TransitionKind.Fade, new Ms(400)),
            TransitionOut = new Transition(TransitionKind.Fade, new Ms(300)),
        };

        var plan = RenderPlanner.Plan(timeline with { Scenes = scenes }, Paths(timeline));

        Assert.True(plan.IsSuccess, string.Join(" | ", plan.Issues));

        var nodes = plan.Plan!.Graph.Nodes.ToList();

        var inIndex = nodes.FindIndex(n => n.Filter == "fade"
            && n.Args.Any(a => a.Key == "t" && a.Value == "in"));

        var outIndex = nodes.FindIndex(n => n.Filter == "fade"
            && n.Args.Any(a => a.Key == "t" && a.Value == "out"));

        Assert.True(inIndex >= 0 && outIndex >= 0);
        Assert.True(inIndex < outIndex,
            "Açılma kararmadan sonra uygulanıyor: videonun sonu kararıp yeniden açılır.");
    }
}
