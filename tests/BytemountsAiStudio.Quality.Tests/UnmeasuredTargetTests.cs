using BytemountsAiStudio.Quality;

namespace BytemountsAiStudio.Quality.Tests;

/// Ölçülemeyen bir kontrol yeniden koşmanın HEDEFİNİ seçmemeli.
///
/// GERÇEK BİR KOŞUDAN ÇIKTI. İki kanal aynı anda üretim yaparken QC
/// iki kontrolü düşürdü:
///
///   - `qc.speech_ratio` — ÖLÇÜLDÜ (%100), hedefi `Timeline`
///   - `qc.topic_unique` — ÖLÇÜLEMEDİ (gömme sağlayıcısı yok),
///     hedefi `Script`
///
/// "En erken hedefi seç" kuralı `Script`'i seçti: senaryo,
/// seslendirme, görseller ve render yeniden koştu. Tur başına dört
/// dakika ve üç tur.
///
/// Oysa senaryoyu yeniden üretmek tekilliği ÖLÇÜLEBİLİR yapmıyor —
/// eksik olan bir ölçüm adımı, bir kalite kusuru değil. Düzelebilecek
/// tek düşüş `Timeline`'dı ve tam da o es geçildi.
public sealed class UnmeasuredTargetTests
{
    private static CheckResult Check(
        string code, RetryTarget target, bool measured, CheckSeverity severity)
        => new()
        {
            Code = code,
            Name = code,
            Passed = false,
            Severity = severity,
            Weight = 3,
            Target = target,
            Measured = measured,
        };

    /// ÖLÇÜLMÜŞ DÜŞÜŞ HEDEFİ SEÇİYOR, ölçülemeyen değil.
    ///
    /// `Script` boru hattında `Timeline`'dan ÖNCE geliyor, yani
    /// "en erken" kuralı onu seçerdi. Buradaki asıl iddia şu: erken
    /// olmak yetmiyor, ölçülmüş de olmak gerekiyor.
    [Fact]
    public void OlculemeyenKontrol_HedefSecmiyor()
    {
        var report = new QualityReport
        {
            Checks =
            [
                Check("qc.speech_ratio", RetryTarget.Timeline, measured: true, CheckSeverity.Warning),
                Check("qc.topic_unique", RetryTarget.Script, measured: false, CheckSeverity.Blocking),
            ],
        };

        Assert.Equal(RetryTarget.Timeline, report.Target);
    }

    /// Ve plan da o hedefe gidiyor: hedef seçimi kâğıt üstünde
    /// düzelip planda düzelmeseydi hiçbir şey kazanılmazdı.
    [Fact]
    public void Plan_OlculmusHedefeGidiyor()
    {
        var report = new QualityReport
        {
            Checks =
            [
                Check("qc.speech_ratio", RetryTarget.Timeline, measured: true, CheckSeverity.Warning),
                Check("qc.topic_unique", RetryTarget.Script, measured: false, CheckSeverity.Blocking),
            ],
        };

        var plan = RetryPlanner.Plan(report, completedLoops: 0);

        Assert.True(plan.ShouldRerun);
        Assert.Equal(RetryTarget.Timeline, plan.Target);
    }

    /// DÜŞENLERİN HEPSİ ÖLÇÜLEMEDİYSE hedef de yok — ve `RetryPlanner`
    /// zaten insana yönlendiriyor. Bu iki kural birbirini tamamlıyor:
    /// biri "ölçülemeyeni hedef sayma", diğeri "hepsi ölçülemediyse
    /// yeniden koşma".
    [Fact]
    public void HepsiOlculemedi_HedefYokVeInsanaGidiyor()
    {
        var report = new QualityReport
        {
            Checks =
            [
                Check("qc.topic_unique", RetryTarget.Script, measured: false, CheckSeverity.Blocking),
                Check("qc.thumbnail", RetryTarget.Render, measured: false, CheckSeverity.Blocking),
            ],
        };

        Assert.Equal(RetryTarget.None, report.Target);

        var plan = RetryPlanner.Plan(report, completedLoops: 0);

        Assert.False(plan.ShouldRerun);
    }

    /// Ölçülemeyen kontrol BLOKLAYICI olmayı sürdürüyor: hedef
    /// seçmemek "geçti saymak" değil.
    ///
    /// İkisini karıştırmak, tekilliği hiç ölçmeden videoyu yayına
    /// göndermek olurdu — ölçülmemiş bir kontrol geçmiş bir kontrol
    /// değil.
    [Fact]
    public void OlculemeyenKontrol_HalaBloklayici()
    {
        var report = new QualityReport
        {
            Checks =
            [
                Check("qc.topic_unique", RetryTarget.Script, measured: false, CheckSeverity.Blocking),
            ],
        };

        Assert.True(report.HasBlockingFailure);
        Assert.Equal(QualityDecision.Retry, report.Decision);
    }
}
