using BytemountsAiStudio.Quality;

namespace BytemountsAiStudio.Quality.Tests;

/// Semantik kalite kontrolü (P2-06).
///
/// BURADAKİ EN ÖNEMLİ KURAL: **ölçülemeyen bir kontrol geçmiş
/// sayılmaz.** Model kapalıyken sessizce geçmek, kalite kontrolünün
/// hiç koşmadığı bir sistemde her videonun tam puan alması demekti —
/// ve bu depoda "model kapalı" hâli teorik değil (ana makinenin ekran
/// kartı model yüklenince sistemi çökertiyor).
public sealed class SemanticQcTests
{
    private static VisualRelevance Measured(int index, double score)
        => new(index, score, "gerekce");

    private static VisualRelevance Unmeasured(int index)
        => new(index, null, "model yok");

    private static CheckResult Find(IReadOnlyList<CheckResult> checks, string code)
        => checks.Single(c => c.Code == code);

    /// HİÇ ÖLÇÜLEMEDİ → KONTROL DÜŞÜYOR.
    [Fact]
    public void GormeModeliYok_AlakaKontroluDusuyor()
    {
        var checks = SemanticQc.Evaluate(
            [Unmeasured(0), Unmeasured(1)], new SemanticJudgement());

        var relevance = Find(checks, "qc.visual_relevance");

        Assert.False(relevance.Passed);
        Assert.Contains("ölçülemedi", relevance.Detail, StringComparison.Ordinal);
    }

    /// METİN YARGILARI DA ÖLÇÜLEMEDİĞİNDE DÜŞÜYOR.
    ///
    /// Varsayılan olarak `true` dönmek, politika kontrolünün hiç
    /// koşmadığı bir sistemde her videoyu "politika riski yok" diye
    /// işaretlemek olurdu.
    [Theory]
    [InlineData("qc.title_honest")]
    [InlineData("qc.tone")]
    [InlineData("qc.policy")]
    public void ModelYok_MetinKontrolleriDusuyor(string code)
    {
        var checks = SemanticQc.Evaluate([], new SemanticJudgement());

        Assert.False(Find(checks, code).Passed);
        Assert.Contains("Ölçülemedi", Find(checks, code).Detail, StringComparison.Ordinal);
    }

    /// "Kontrol düştü" ile "kontrol koşamadı" AYRI yazılıyor: triyaj
    /// eden insanın ikisini ayırt etmesi gerekiyor.
    [Fact]
    public void OlculemediVeDustu_FarkliGerekce()
    {
        var olculemedi = Find(SemanticQc.Evaluate([], new SemanticJudgement()), "qc.policy");

        var dustu = Find(
            SemanticQc.Evaluate([], new SemanticJudgement { PolicySafe = false, Rationale = "silah tarifi" }),
            "qc.policy");

        Assert.False(olculemedi.Passed);
        Assert.False(dustu.Passed);
        Assert.NotEqual(olculemedi.Detail, dustu.Detail);
        Assert.Contains("silah tarifi", dustu.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void TumKontrollerGecti_TamPuan()
    {
        var checks = SemanticQc.Evaluate(
            [Measured(0, 0.9), Measured(1, 0.8)],
            new SemanticJudgement { TitleMatchesContent = true, ToneAppropriate = true, PolicySafe = true });

        Assert.All(checks, c => Assert.True(c.Passed));
        Assert.Equal(100, new QualityReport { Checks = checks }.Score);
    }

    /// ORAN, ADET DEĞİL: üç sahnede bir alakasız kare videonun üçte
    /// biri, yirmi sahnede yirmide biri.
    [Fact]
    public void AzSayidaAlakasiz_Geciyor()
    {
        var checks = SemanticQc.Evaluate(
            [Measured(0, 0.9), Measured(1, 0.9), Measured(2, 0.9), Measured(3, 0.2)],
            new SemanticJudgement());

        // 4'te 1 = 0,25 ≤ 0,34
        Assert.True(Find(checks, "qc.visual_relevance").Passed);
    }

    [Fact]
    public void CokSayidaAlakasiz_Dusuyor()
    {
        var checks = SemanticQc.Evaluate(
            [Measured(0, 0.2), Measured(1, 0.1), Measured(2, 0.9)],
            new SemanticJudgement());

        var relevance = Find(checks, "qc.visual_relevance");

        Assert.False(relevance.Passed);

        // HANGİ sahneler alakasız, yazılıyor: "alaka düşük" tek başına
        // hangi kareyi değiştireceğini söylemiyor.
        Assert.Contains("#0", relevance.Detail, StringComparison.Ordinal);
        Assert.Contains("#1", relevance.Detail, StringComparison.Ordinal);
    }

    /// YARIM ÖLÇÜM YARIM SAYILIYOR: ölçülemeyen sahneler oranın
    /// paydasına girmiyor, ama kaç sahnenin ölçüldüğü yazılıyor.
    [Fact]
    public void KismenOlculdu_OlculenlerUzerindenKarar()
    {
        var checks = SemanticQc.Evaluate(
            [Measured(0, 0.9), Unmeasured(1), Unmeasured(2)],
            new SemanticJudgement());

        var relevance = Find(checks, "qc.visual_relevance");

        Assert.True(relevance.Passed);
        Assert.Contains("1 sahne örneklendi", relevance.Detail, StringComparison.Ordinal);
    }

    /// POLİTİKA BLOKLAYICI: yayınlanan bir ihlal kanalın tamamını
    /// riske atıyor ve geri alınamıyor — video silinse bile ihtar
    /// kalıyor.
    [Fact]
    public void PolitikaDustu_SkoruSifirlanir()
    {
        var checks = SemanticQc.Evaluate(
            [Measured(0, 0.9)],
            new SemanticJudgement { TitleMatchesContent = true, ToneAppropriate = true, PolicySafe = false });

        var report = new QualityReport { Checks = checks };

        Assert.True(report.HasBlockingFailure);
        Assert.Equal(0, report.Score);
    }

    /// Ton UYARI: kötü ton videoyu yayınlanamaz yapmıyor, puan
    /// düşürüyor. Bloklayıcı olsaydı üslup tercihi bir yayın engeline
    /// dönüşürdü.
    [Fact]
    public void TonDustu_YayiniEngellemiyor()
    {
        var checks = SemanticQc.Evaluate(
            [Measured(0, 0.9)],
            new SemanticJudgement { TitleMatchesContent = true, ToneAppropriate = false, PolicySafe = true });

        var report = new QualityReport { Checks = checks };

        Assert.False(report.HasBlockingFailure);
        Assert.InRange(report.Score, 1, 99);
    }

    /// ÖRNEKLEME EŞİT ARALIKLA: ilk N sahneyi almak videonun sonunu
    /// hiç görmemek demek ve alakasız görseller çoğu zaman sonda
    /// oluyor.
    [Fact]
    public void Ornekleme_SonuDaKapsiyor()
    {
        var indices = SemanticQc.SampleIndices(20, max: 5);

        Assert.Equal(5, indices.Count);
        Assert.Equal(0, indices[0]);

        // Son örnek videonun son beşte birinde.
        Assert.True(indices[^1] >= 16, $"son ornek {indices[^1]}");
    }

    [Fact]
    public void AzSahne_HepsiOrnekleniyor()
        => Assert.Equal([0, 1, 2], SemanticQc.SampleIndices(3, max: 6));

    [Fact]
    public void SahneYok_OrneklemeBos()
        => Assert.Empty(SemanticQc.SampleIndices(0));

    /// Örnekler TEKRARSIZ: aynı sahneyi iki kez modele sormak, iki kez
    /// ödeme yapıp aynı cevabı almak olurdu.
    [Theory]
    [InlineData(7, 6)]
    [InlineData(8, 6)]
    [InlineData(100, 6)]
    public void Ornekler_TekrarsizVeSirali(int sceneCount, int max)
    {
        var indices = SemanticQc.SampleIndices(sceneCount, max);

        Assert.Equal(indices.Distinct().Count(), indices.Count);
        Assert.Equal([.. indices.Order()], indices);
        Assert.All(indices, i => Assert.InRange(i, 0, sceneCount - 1));
    }

    /// Alaka hedefi GÖRSEL: alakasız bir kare senaryoyu yeniden
    /// üretmeyi değil, görseli yeniden seçmeyi gerektiriyor.
    [Fact]
    public void AlakaHedefi_Gorsel()
        => Assert.Equal(RetryTarget.Visuals,
            Find(SemanticQc.Evaluate([Measured(0, 0.1)], new SemanticJudgement()), "qc.visual_relevance").Target);

    /// Yanıltıcı başlık hedefi METADATA: başlığı düzeltmek için
    /// senaryoyu yeniden üretmek gerekmiyor.
    [Fact]
    public void BaslikHedefi_Metadata()
        => Assert.Equal(RetryTarget.Metadata,
            Find(SemanticQc.Evaluate([], new SemanticJudgement { TitleMatchesContent = false }), "qc.title_honest").Target);
}
