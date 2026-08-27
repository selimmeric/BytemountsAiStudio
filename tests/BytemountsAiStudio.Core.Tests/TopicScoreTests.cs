using BytemountsAiStudio.Core.Content;

namespace BytemountsAiStudio.Core.Tests;

/// Konu skorlama ve havuz kararı testleri (P1-08).
///
/// Mimari "skor açıklanabilir olmalı; tek sayı yetmez" diyor. Buradaki
/// testler ağırlıkların ve cezanın davranışını sabitliyor: bir gün
/// eşik ayarlanacak ve o zaman hangi boyutun ne kadar etkili olduğunu
/// bilmek gerekecek.
public sealed class TopicScoreTests
{
    private static TopicScore Score(
        int demand = 70, int fit = 70, int sourceability = 70,
        int visualizability = 70, int freshness = 70, int risk = 0)
        => new()
        {
            Demand = demand,
            Fit = fit,
            Sourceability = sourceability,
            Visualizability = visualizability,
            Freshness = freshness,
            Risk = risk,
        };

    // ---- Ağırlıklar ----

    [Fact]
    public void TumBoyutlarTam_YuzPuan()
    {
        var score = Score(100, 100, 100, 100, 100, risk: 0);

        Assert.Equal(100, score.Overall, 1);
    }

    [Fact]
    public void TumBoyutlarSifir_SifirPuan()
    {
        Assert.Equal(0, Score(0, 0, 0, 0, 0).Overall, 1);
    }

    /// Kaynak bulunabilirliği EN AĞIR boyut: hattımızın kırılma noktası
    /// orası. Kaynağı olmayan konu iddia doğrulama aşamasında düşüyor
    /// ve o noktaya kadar harcanan her şey boşa gidiyor.
    [Fact]
    public void KaynakBulunabilirligi_EnAgirBoyut()
    {
        var withSources = Score(0, 0, sourceability: 100, visualizability: 0, freshness: 0);
        var withDemand = Score(demand: 100, fit: 0, sourceability: 0, visualizability: 0, freshness: 0);
        var withFreshness = Score(0, 0, 0, 0, freshness: 100);

        Assert.True(withSources.Overall > withDemand.Overall);
        Assert.True(withSources.Overall > withFreshness.Overall);
    }

    /// Risk CEZA olarak uygulanıyor, boyut olarak değil. Ağırlıklı
    /// ortalamaya katsaydık, yüksek riskli bir konu diğer boyutlardan
    /// telafi edebilirdi — oysa politika ihlali riski telafi edilebilir
    /// bir şey değil.
    [Fact]
    public void Risk_CezaOlarakUygulanir()
    {
        var clean = Score(risk: 0);
        var risky = Score(risk: 60);

        Assert.True(risky.Overall < clean.Overall);
        Assert.Equal(clean.Overall - 30, risky.Overall, 1);
    }

    [Fact]
    public void AsiriRisk_SkoruSifirinAltinaIndirmez()
    {
        Assert.Equal(0, Score(10, 10, 10, 10, 10, risk: 100).Overall, 1);
    }

    // ---- Geçerlilik ----

    /// Model uydurma değer verebiliyor. Sıkıştırmak yerine REDDETMEK
    /// doğru: 120 veren bir model muhtemelen boyutu da yanlış anlamış
    /// demektir ve sessizce 100'e çekmek o hatayı gizler.
    [Theory]
    [InlineData(120, 70, 70, 70, 70, 0)]
    [InlineData(70, -5, 70, 70, 70, 0)]
    [InlineData(70, 70, 70, 70, 70, 101)]
    public void AralikDisiDeger_Gecersiz(int d, int f, int s, int v, int fr, int r)
    {
        Assert.False(Score(d, f, s, v, fr, r).IsValid);
    }

    [Fact]
    public void SinirlardakiDegerler_Gecerli()
    {
        Assert.True(Score(0, 0, 0, 0, 0, 0).IsValid);
        Assert.True(Score(100, 100, 100, 100, 100, 100).IsValid);
    }

    [Fact]
    public void GecersizSkor_Reddedilir()
    {
        Assert.Equal(TopicDecision.Reject, TopicPolicy.Decide(Score(demand: 500)));
    }

    // ---- Karar eşikleri ----

    [Fact]
    public void YuksekSkor_KabulEdilir()
    {
        Assert.Equal(TopicDecision.Accept, TopicPolicy.Decide(Score(85, 85, 85, 85, 85)));
    }

    [Fact]
    public void OrtaSkor_Bekletilir()
    {
        // ~50 puan: eşiğin altında ama reddetmeye değmeyecek kadar iyi.
        Assert.Equal(TopicDecision.Hold, TopicPolicy.Decide(Score(50, 50, 50, 50, 50)));
    }

    [Fact]
    public void DusukSkor_Reddedilir()
    {
        Assert.Equal(TopicDecision.Reject, TopicPolicy.Decide(Score(20, 20, 20, 20, 20)));
    }

    /// Risk vetosu DİĞER BOYUTLARDAN BAĞIMSIZ: politika ihlali riski
    /// yüksek bir konu, ne kadar iyi olursa olsun üretilmemeli.
    [Fact]
    public void YuksekRisk_DigerBoyutlardanBagimsizReddeder()
    {
        var excellent = Score(100, 100, 100, 100, 100, risk: TopicPolicy.RiskVeto);

        Assert.Equal(TopicDecision.Reject, TopicPolicy.Decide(excellent));
    }

    [Fact]
    public void VetoAltindakiRisk_TekBasinaReddetmez()
    {
        var score = Score(90, 90, 90, 90, 90, risk: TopicPolicy.RiskVeto - 1);

        Assert.NotEqual(TopicDecision.Reject, TopicPolicy.Decide(score));
    }

    // ---- Tekillik ----

    /// Tekrar REDDEDİLİYOR, beklemeye alınmıyor: bir konu daha önce
    /// yayınlandıysa bekleyerek tekrar olmaktan çıkmıyor.
    [Fact]
    public void CokBenzerKonu_BeklemeyeAlinmazReddedilir()
    {
        var decision = TopicPolicy.Decide(Score(90, 90, 90, 90, 90), highestSimilarity: 0.95);

        Assert.Equal(TopicDecision.Reject, decision);
    }

    [Fact]
    public void EsikAltiBenzerlik_KabuluEngellemez()
    {
        var decision = TopicPolicy.Decide(
            Score(90, 90, 90, 90, 90), highestSimilarity: TopicPolicy.SimilarityThreshold - 0.01);

        Assert.Equal(TopicDecision.Accept, decision);
    }

    [Fact]
    public void BenzerlikBilinmiyor_KaraiEtkilemez()
    {
        Assert.Equal(
            TopicPolicy.Decide(Score(90, 90, 90, 90, 90)),
            TopicPolicy.Decide(Score(90, 90, 90, 90, 90), highestSimilarity: null));
    }

    // ---- Kosinüs benzerliği ----

    [Fact]
    public void AyniVektor_TamBenzer()
    {
        float[] v = [1f, 2f, 3f];

        Assert.Equal(1.0, TopicPolicy.CosineSimilarity(v, v), 6);
    }

    [Fact]
    public void DikVektorler_SifirBenzer()
    {
        Assert.Equal(0.0, TopicPolicy.CosineSimilarity([1f, 0f], [0f, 1f]), 6);
    }

    [Fact]
    public void ZitVektorler_EksiBirBenzer()
    {
        Assert.Equal(-1.0, TopicPolicy.CosineSimilarity([1f, 0f], [-1f, 0f]), 6);
    }

    /// Ölçek benzerliği DEĞİŞTİRMEMELİ: kosinüs yön ölçüyor, uzunluk
    /// değil. Gömme modelleri normalize edilmemiş vektör dönebiliyor.
    [Fact]
    public void OlcekBenzerligiDegistirmez()
    {
        var small = TopicPolicy.CosineSimilarity([1f, 2f, 3f], [2f, 4f, 6f]);

        Assert.Equal(1.0, small, 6);
    }

    /// Farklı boyutlu vektörler karşılaştırılamaz. Sıfır dönmek "hiç
    /// benzemiyor" demek olurdu ve bu YANLIŞ bir güvence; -1
    /// "karşılaştırılamadı" anlamında ve eşiğin altında.
    [Fact]
    public void FarkliBoyut_KarsilastirilamazDoner()
    {
        Assert.Equal(-1.0, TopicPolicy.CosineSimilarity([1f, 2f], [1f, 2f, 3f]), 6);
        Assert.Equal(-1.0, TopicPolicy.CosineSimilarity([], []), 6);
    }

    [Fact]
    public void SifirVektor_KarsilastirilamazDoner()
    {
        Assert.Equal(-1.0, TopicPolicy.CosineSimilarity([0f, 0f], [1f, 1f]), 6);
    }

    /// Karşılaştırılamayan bir benzerlik kabulü engellememeli: veri
    /// eksikliği yüzünden iyi bir konuyu reddetmek yanlış.
    [Fact]
    public void KarsilastirilamazBenzerlik_KabuluEngellemez()
    {
        var decision = TopicPolicy.Decide(Score(90, 90, 90, 90, 90), highestSimilarity: -1.0);

        Assert.Equal(TopicDecision.Accept, decision);
    }

    // ---- Açıklanabilirlik ----

    /// "Skor açıklanabilir olmalı; tek sayı yetmez." Özet bütün
    /// boyutları göstermeli.
    [Fact]
    public void Ozet_TumBoyutlariIcerir()
    {
        var text = Score(10, 20, 30, 40, 50, 60).ToString();

        foreach (var part in new[] { "talep 10", "uygunluk 20", "kaynak 30", "görsel 40", "tazelik 50", "risk 60" })
        {
            Assert.Contains(part, text, StringComparison.Ordinal);
        }
    }
}
