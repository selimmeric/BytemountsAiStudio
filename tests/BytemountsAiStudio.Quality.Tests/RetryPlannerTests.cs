using BytemountsAiStudio.Media.Timeline;
using BytemountsAiStudio.Quality;

namespace BytemountsAiStudio.Quality.Tests;

/// Hedefli yeniden koşmanın testleri (P2-07).
///
/// Kabul kriteri: **QC retry'ı tüm boru hattını yeniden koşturmuyor**
/// ve bunun kanıtı maliyet ölçümü — burada node sayısı olarak.
public sealed class RetryPlannerTests
{
    private static CheckResult Check(bool passed, RetryTarget target, CheckSeverity severity, int weight = 10)
        => new()
        {
            Code = "test",
            Name = "Test kontrolü",
            Passed = passed,
            Severity = severity,
            Weight = weight,
            Target = target,
        };

    private static QualityReport Report(params CheckResult[] checks)
        => new() { Checks = checks };

    [Fact]
    public void GecenRapor_YenidenKosmuyor()
    {
        var plan = RetryPlanner.Plan(Report(Check(true, RetryTarget.None, CheckSeverity.Blocking)), 0);

        Assert.False(plan.ShouldRerun);
        Assert.Equal(RetryDecision.None, plan.Decision);
    }

    /// `NeedsApproval` bir düşüş DEĞİL, bir yönlendirme: video sınırda
    /// ve insan bakacak (P2-08). Yeniden koşturmak, insanın zaten kabul
    /// edeceği bir videoyu bir kez daha üretmek olurdu.
    [Fact]
    public void OnayaGidenRapor_YenidenKosmuyor()
    {
        // Skor 70–85 arası: bir kontrol düşüyor ama bloklayıcı değil.
        var report = Report(
            Check(true, RetryTarget.None, CheckSeverity.Warning, weight: 80),
            Check(false, RetryTarget.Visuals, CheckSeverity.Warning, weight: 20));

        Assert.Equal(QualityDecision.NeedsApproval, report.Decision);

        var plan = RetryPlanner.Plan(report, 0);

        Assert.False(plan.ShouldRerun);
    }

    [Fact]
    public void BloklayiciDusus_HedeftenKosuyor()
    {
        var plan = RetryPlanner.Plan(Report(Check(false, RetryTarget.Render, CheckSeverity.Blocking)), 0);

        Assert.True(plan.ShouldRerun);
        Assert.Equal(RetryTarget.Render, plan.Target);
        Assert.Equal(1, plan.Loop);
    }

    /// KABUL KRİTERİ, sayı olarak: render'a dönmek senaryoyu yeniden
    /// üretmiyor.
    [Fact]
    public void RenderRetry_SenaryoyuYenidenUretmiyor()
    {
        var nodes = RetryPlanner.NodesFrom(RetryTarget.Render);

        Assert.DoesNotContain("script.generate", nodes);
        Assert.DoesNotContain("visual.resolve", nodes);
        Assert.Contains("media.render", nodes);
    }

    /// Hedefin KENDİSİ ve sonrası koşuyor; öncesi hiç dokunulmuyor.
    [Fact]
    public void HedefVeSonrasi_Kosuyor()
    {
        var nodes = RetryPlanner.NodesFrom(RetryTarget.Visuals);

        Assert.DoesNotContain("script.generate", nodes);
        Assert.Contains("visual.resolve", nodes);
        Assert.Contains("timeline.compile", nodes);
        Assert.Contains("media.render", nodes);
    }

    /// Maliyet farkı ölçülebilir: baştan koşmakla hedeften koşmak
    /// arasındaki fark sayı olarak görülüyor.
    [Fact]
    public void Tasarruf_Olculebiliyor()
    {
        Assert.Equal(0, RetryPlanner.Saved(RetryTarget.Script));
        Assert.True(RetryPlanner.Saved(RetryTarget.Render) > 0);
        Assert.True(RetryPlanner.Saved(RetryTarget.Metadata) > RetryPlanner.Saved(RetryTarget.Render));
    }

    [Fact]
    public void HicHedefYok_BosListe()
    {
        Assert.Empty(RetryPlanner.NodesFrom(RetryTarget.None));
    }

    /// Hedefi olmayan bir düşüş yeniden koşmayla DÜZELMİYOR: örneğin
    /// ölçülemeyen bir süre, aynı adımı tekrarlayınca yine ölçülemez.
    [Fact]
    public void HedefsizDusus_YenidenKosmuyor()
    {
        var plan = RetryPlanner.Plan(Report(Check(false, RetryTarget.None, CheckSeverity.Blocking)), 0);

        Assert.False(plan.ShouldRerun);
        Assert.Contains("hedef yok", plan.Reason, StringComparison.Ordinal);
    }

    /// Sınırsız bir döngü aynı hatayı sonsuza kadar para harcayarak
    /// tekrarlıyor.
    [Fact]
    public void DonguSiniri_Duruyor()
    {
        var report = Report(Check(false, RetryTarget.Script, CheckSeverity.Blocking));

        Assert.True(RetryPlanner.Plan(report, completedLoops: 2).ShouldRerun);

        var stopped = RetryPlanner.Plan(report, completedLoops: 3);

        Assert.False(stopped.ShouldRerun);
        Assert.Equal(RetryDecision.LoopLimitReached, stopped.Decision);
    }

    /// Sınır dolunca run BAŞARISIZ değil: hedef korunuyor ki insan
    /// nereye bakacağını bilsin. Başarısız saymak, üç turdur
    /// düzelmeyen ama belki kabul edilebilir bir videoyu çöpe atmaktı.
    [Fact]
    public void SinirDoldugunda_HedefKoruniyor()
    {
        var plan = RetryPlanner.Plan(
            Report(Check(false, RetryTarget.Visuals, CheckSeverity.Blocking)), completedLoops: 3);

        Assert.Equal(RetryTarget.Visuals, plan.Target);
    }

    [Fact]
    public void SinirSifir_HicDenemiyor()
    {
        var plan = RetryPlanner.Plan(
            Report(Check(false, RetryTarget.Script, CheckSeverity.Blocking)), 0, maxLoops: 0);

        Assert.False(plan.ShouldRerun);
    }

    /// Birden çok hedef varsa EN ERKEN olan seçiliyor (QualityReport'un
    /// kuralı) ve plan da ona uyuyor: senaryo bozukken görseli yeniden
    /// üretmenin anlamı yok.
    [Fact]
    public void CokluHedef_EnErkeniSeciyor()
    {
        var plan = RetryPlanner.Plan(
            Report(
                Check(false, RetryTarget.Render, CheckSeverity.Blocking),
                Check(false, RetryTarget.Script, CheckSeverity.Blocking)),
            0);

        Assert.Equal(RetryTarget.Script, plan.Target);
        Assert.Contains("script.generate", RetryPlanner.NodesFrom(plan.Target));
    }

    [Fact]
    public void Ozet_TuruVeHedefiIceriyor()
    {
        var text = RetryPlanner.Plan(
            Report(Check(false, RetryTarget.Timeline, CheckSeverity.Blocking)), 1).ToString();

        Assert.Contains("Timeline", text, StringComparison.Ordinal);
        Assert.Contains("2", text, StringComparison.Ordinal);
    }
}

/// Müzik lisans kontrolünün testleri (P2-09).
///
/// Kabul kriteri: **lisanssız müzik varlığı yayına giremiyor
/// (bloklayıcı kontrol).**
public sealed class MusicLicenseCheckTests
{
    private static MusicLicense Complete() => new()
    {
        Name = "CC BY 3.0",
        Author = "Zeropage",
        RequiresAttribution = true,
        CapturedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void TamKanit_Yeterli()
    {
        Assert.True(Complete().IsComplete);
    }

    /// Atıf gerekiyorsa YAZAR ADI ŞART: "CC BY" deyip yazarı bilmemek,
    /// atfı yapılamaz kılıyor ve lisansı ihlal ediyor.
    [Fact]
    public void AtifGerekiyorAmaYazarYok_Yetersiz()
    {
        Assert.False((Complete() with { Author = null }).IsComplete);
        Assert.False((Complete() with { Author = "   " }).IsComplete);
    }

    /// Atıf gerekmiyorsa yazar da gerekmiyor: `cc0` ve `pdm` böyle.
    [Fact]
    public void AtifGerekmiyorsa_YazarSartDegil()
    {
        var cc0 = new MusicLicense
        {
            Name = "CC0",
            RequiresAttribution = false,
            CapturedAt = DateTimeOffset.UtcNow,
        };

        Assert.True(cc0.IsComplete);
    }

    [Fact]
    public void LisansAdiBos_Yetersiz()
    {
        Assert.False((Complete() with { Name = "  " }).IsComplete);
    }
}
