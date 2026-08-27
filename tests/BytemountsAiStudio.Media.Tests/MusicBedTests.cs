using BytemountsAiStudio.Core.Time;
using BytemountsAiStudio.Media.Ir;
using BytemountsAiStudio.Media.Planning;
using BytemountsAiStudio.Media.Timeline;

namespace BytemountsAiStudio.Media.Tests;

/// Müzik yatağı ve ducking testleri (P1-19, §2.9).
///
/// Bu testler yazılmadan önce `AudioTrack.Music` modelde DOLU olabiliyor
/// ama filtre grafiğine hiç girmiyordu. Sessizce yok saymak en kötü
/// seçenekti: kanal ayarında müzik açık görünüyor, videoda müzik yok, ve
/// hiçbir şey hata vermiyor.
public sealed class MusicBedTests
{
    private static TimelineDocument WithMusic(MusicBed? music)
    {
        var timeline = TimelineFactory.Valid();

        return timeline with
        {
            Audio = timeline.Audio with { Music = music },
        };
    }

    private static Dictionary<string, string> Paths(TimelineDocument t, bool includeMusic = true)
    {
        var paths = t.Scenes.Select(s => s.Visual.Asset.Sha256)
            .Concat(t.Audio.VoiceSegments.Select(s => s.Asset.Sha256))
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(sha => sha, sha => $"/tmp/{sha[..8]}.bin", StringComparer.Ordinal);

        if (includeMusic && t.Audio.Music is { } music)
        {
            paths[music.Asset.Sha256] = "/tmp/music.mp3";
        }

        return paths;
    }

    private static RenderPlanner.Result Plan(MusicBed? music, bool includeMusicPath = true)
    {
        var timeline = WithMusic(music);

        return RenderPlanner.Plan(timeline, Paths(timeline, includeMusicPath));
    }

    private static MusicBed Bed(DuckingSpec? ducking = null) => new()
    {
        Asset = TimelineFactory.Asset('e'),
        GainDb = -22.0,
        Ducking = ducking,
    };

    private static IReadOnlyList<string> Filters(RenderPlanner.Result plan)
        => [.. plan.Plan!.Graph.Nodes.Select(n => n.Filter)];

    // ---- Müzik yok ----

    /// Müziksiz timeline eskisi gibi çalışmalı: yeni özellik mevcut
    /// davranışı değiştirmemeli.
    [Fact]
    public void MuzikYok_GrafDegismez()
    {
        var plan = Plan(null);

        Assert.True(plan.IsSuccess, string.Join(" | ", plan.Issues));
        Assert.DoesNotContain("aloop", Filters(plan), StringComparer.Ordinal);
        Assert.DoesNotContain("sidechaincompress", Filters(plan), StringComparer.Ordinal);
    }

    // ---- Müzik var ----

    /// ASIL TEST: müzik artık grafiğe GİRİYOR. Önceden modelde dolu olup
    /// grafikte hiç görünmüyordu.
    [Fact]
    public void MuzikVar_GrafigeGirer()
    {
        var plan = Plan(Bed());

        Assert.True(plan.IsSuccess, string.Join(" | ", plan.Issues));
        Assert.Contains(plan.Plan!.Graph.Inputs, i => i.Id == "music");
    }

    [Fact]
    public void MuzikVar_UretilenGrafGecerli()
    {
        var plan = Plan(Bed());

        var issues = GraphValidator.Validate(plan.Plan!.Graph);

        Assert.True(issues.Count == 0, string.Join(" | ", issues));
    }

    [Fact]
    public void Muzik_SeviyesiDusurulur()
    {
        var plan = Plan(Bed());

        Assert.Contains("volume", Filters(plan), StringComparer.Ordinal);
    }

    /// Döngü olmadan kısa bir müzik parçası videonun ortasında biter ve
    /// geri kalanı sessiz kalır.
    [Fact]
    public void DonguAcik_AloopEklenir()
    {
        Assert.Contains("aloop", Filters(Plan(Bed() with { Loop = true })), StringComparer.Ordinal);
    }

    [Fact]
    public void DonguKapali_AloopEklenmez()
    {
        Assert.DoesNotContain("aloop", Filters(Plan(Bed() with { Loop = false })), StringComparer.Ordinal);
    }

    [Fact]
    public void FadeVarsa_AfadeEklenir()
    {
        var plan = Plan(Bed() with { FadeIn = new Ms(1000), FadeOut = new Ms(1500) });

        Assert.Contains("afade", Filters(plan), StringComparer.Ordinal);
    }

    [Fact]
    public void FadeYoksa_AfadeEklenmez()
    {
        var plan = Plan(Bed() with { FadeIn = Ms.Zero, FadeOut = Ms.Zero });

        Assert.DoesNotContain("afade", Filters(plan), StringComparer.Ordinal);
    }

    // ---- Ducking ----

    [Fact]
    public void DuckingYok_SidechainEklenmez()
    {
        Assert.DoesNotContain("sidechaincompress", Filters(Plan(Bed())), StringComparer.Ordinal);
    }

    [Fact]
    public void DuckingVar_SidechainEklenir()
    {
        var plan = Plan(Bed(new DuckingSpec()));

        Assert.Contains("sidechaincompress", Filters(plan), StringComparer.Ordinal);
    }

    /// FFmpeg'de bir FİLTRE ÇIKIŞI yalnızca bir kez tüketilebiliyor
    /// (ham girdi pad'lerini FFmpeg kendisi çoğaltıyor — ayrım
    /// `DuckingFfmpegTests`'te gerçek FFmpeg'e karşı sabitlendi).
    /// Konuşma akışımız bir filtre çıktısı olduğu için ayrılmak zorunda.
    [Fact]
    public void DuckingVar_KonusmaIkiyeAyrilir()
    {
        var plan = Plan(Bed(new DuckingSpec()));

        Assert.Contains("asplit", Filters(plan), StringComparer.Ordinal);
    }

    /// Doğrulayıcı tekil tüketimi denetliyor; ducking grafiği ondan
    /// geçmeli.
    [Fact]
    public void DuckingGrafi_TekilTuketimKuralinaUyar()
    {
        var plan = Plan(Bed(new DuckingSpec()));

        Assert.True(plan.IsSuccess, string.Join(" | ", plan.Issues));

        var issues = GraphValidator.Validate(plan.Plan!.Graph);

        Assert.True(issues.Count == 0, string.Join(" | ", issues));
    }

    /// İlk girdi KISILAN (müzik), ikinci girdi TETİK (konuşma). Sıra ters
    /// olursa müzik konuşmayı kısar — teknik olarak geçerli bir grafik,
    /// ama tam tersi bir video.
    [Fact]
    public void Sidechain_MuzikOnceKonusmaSonra()
    {
        var plan = Plan(Bed(new DuckingSpec()));

        var node = plan.Plan!.Graph.Nodes.Single(n => n.Filter == "sidechaincompress");

        Assert.Equal(2, node.Inputs.Count);
        Assert.StartsWith("m_", node.Inputs[0].Id, StringComparison.Ordinal);
        Assert.StartsWith("v_", node.Inputs[1].Id, StringComparison.Ordinal);
    }

    /// Attack ve release doğrudan duyulan şeyi belirliyor: attack çok
    /// yüksekse müzik konuşmanın ilk hecesini yutuyor.
    [Fact]
    public void DuckingAyarlari_FiltreyeGecer()
    {
        var plan = Plan(Bed(new DuckingSpec { AttackMs = 120, ReleaseMs = 800 }));

        var node = plan.Plan!.Graph.Nodes.Single(n => n.Filter == "sidechaincompress");
        var args = node.Args.Select(a => $"{a.Key}={a.Value}").ToList();

        Assert.Contains(args, a => a.Contains("attack=120", StringComparison.Ordinal));
        Assert.Contains(args, a => a.Contains("release=800", StringComparison.Ordinal));
    }

    // ---- Eksik varlık ----

    /// Müzik dosyası çözümlenemezse video KAYBEDİLMEZ: konuşma tek
    /// başına geçerli bir ses. Ama sorun kayda geçiyor.
    [Fact]
    public void MuzikVarligiCozumlenemezse_KonusmaylaDevamEder()
    {
        var plan = Plan(Bed(), includeMusicPath: false);

        Assert.False(plan.IsSuccess);
        Assert.Contains(plan.Issues, i => i.Code == "plan.unresolved_asset");
    }

    // ---- Emitter ----

    /// Grafik metne çevrilebilmeli; çevrilemeyen bir grafik render
    /// aşamasında anlaşılmaz bir FFmpeg hatasına dönüşür.
    [Fact]
    public void DuckingGrafi_MetneCevrilebilir()
    {
        var plan = Plan(Bed(new DuckingSpec()));

        var command = FilterGraphEmitter.Emit(
            plan.Plan!.Graph, "/tmp/filters.txt", "/tmp/out.mp4", plan.Plan.Output);

        Assert.Contains("sidechaincompress", command.FilterComplex, StringComparison.Ordinal);
        Assert.Contains("asplit", command.FilterComplex, StringComparison.Ordinal);
    }
}
