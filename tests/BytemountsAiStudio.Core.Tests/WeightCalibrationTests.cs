using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Learning;

namespace BytemountsAiStudio.Core.Tests;

/// Konu skorlama ağırlıklarının kalibrasyonu (P5-04).
///
/// ASIL TEHLİKE KALİBRASYONUN KENDİSİ. Altmış videoya ağırlık
/// uydurmak, o altmış videoyu mükemmel açıklayan ve sonraki altmış
/// video hakkında hiçbir şey bilmeyen katsayılar üretir — üstelik
/// çalışıyormuş gibi görünerek, çünkü eğitim verisindeki uyum HER
/// ZAMAN artar.
///
/// Bu dosyanın en önemli testi `SafGurultu_HicbirZamanBenimsenmiyor`:
/// yirmi ayrı gürültü kümesinde sıfır benimseme.
public sealed class WeightCalibrationTests
{
    /// Sabit kimlikler: eğitim/sınama bölmesi `run_id`'den türüyor,
    /// yani rastgele GUID testi koşudan koşuya değiştirirdi.
    private static Guid Id(int index) => new($"00000000-0000-0000-0000-{index:D12}");

    /* ---- ağırlık geçerliliği ---- */

    /// TOPLAM 1 OLMAYAN AĞIRLIK REDDEDİLİYOR.
    ///
    /// Olmasaydı `Overall` 0–100 aralığından çıkardı ve
    /// `AcceptThreshold = 65` sessizce başka bir anlama gelirdi: aynı
    /// eşik aynı konuyu bir kanalda kabul edip diğerinde reddederdi.
    [Fact]
    public void ToplamiBirOlmayanAgirlik_Reddediliyor()
    {
        var result = ScoreWeights.Validate(ScoreWeights.Default with { Demand = 0.50 });

        Assert.True(result.IsFailure);
        Assert.Equal("weights.not_normalized", result.Error.Code);
    }

    [Fact]
    public void NegatifAgirlik_Reddediliyor()
    {
        var result = ScoreWeights.Validate(
            ScoreWeights.Default with { Demand = -0.20, Fit = 0.55 });

        Assert.True(result.IsFailure);
        Assert.Equal("weights.negative", result.Error.Code);
    }

    [Fact]
    public void VarsayilanAgirliklar_Gecerli()
        => Assert.True(ScoreWeights.Validate(ScoreWeights.Default).IsSuccess);

    /* ---- sıra korelasyonu ---- */

    [Fact]
    public void ArtanIliski_TamKorelasyon()
        => Assert.Equal(1.0, Correlation.Spearman([1, 2, 3, 4, 5], [10, 20, 30, 40, 50]), 6);

    [Fact]
    public void AzalanIliski_TersKorelasyon()
        => Assert.Equal(-1.0, Correlation.Spearman([1, 2, 3, 4, 5], [50, 40, 30, 20, 10]), 6);

    /// DOĞRUSAL OLMAYAN AMA TEK YÖNLÜ İLİŞKİ DE TAM YAKALANIYOR.
    ///
    /// Spearman'ın Pearson yerine seçilme sebebi bu. Konu skoru
    /// uydurma bir ölçek: 80 ile 60 arasındaki farkın 40 ile 20
    /// arasındaki farkla aynı büyüklükte olduğunu iddia edemeyiz.
    /// Pearson tam olarak bunu iddia eder.
    [Fact]
    public void DogrusalOlmayanTekYonluIliski_TamKorelasyon()
        => Assert.Equal(1.0, Correlation.Spearman([1, 2, 3, 4, 5], [1, 4, 9, 100, 10_000]), 6);

    /// EŞİT DEĞERLER ORTALAMA SIRA ALIYOR.
    ///
    /// Rastgele sıralamak, modelin çoğu konuya 70 verdiği bir veri
    /// kümesinde uydurma bir korelasyon üretirdi.
    [Fact]
    public void EsitDegerler_OrtalamaSira()
        => Assert.Equal([2.0, 2.0, 2.0], Correlation.Ranks([5, 5, 5]));

    /// KÜÇÜK ÖRNEKLEMDE GÜÇLÜ KORELASYON BİLE ANLAMLI DEĞİL.
    [Fact]
    public void BesNokta_TamKorelasyonAnlamliDegil()
        => Assert.True(Correlation.PValue(0.9, 5) > 0.05);

    /// AYNI KORELASYON, BÜYÜK ÖRNEKLEMDE ANLAMLI.
    [Fact]
    public void YuzNokta_AyniKorelasyonAnlamli()
        => Assert.True(Correlation.PValue(0.9, 100) < 0.001);

    /* ---- kalibrasyon kapıları ---- */

    /// YETERSİZ VERİ "AĞIRLIKLAR DOĞRU" DEĞİL.
    [Fact]
    public void AzOrneklem_YeterliVeriYok()
    {
        var verdict = WeightCalibration.Evaluate(
            [.. Enumerable.Range(0, 20).Select(i => Sample(i, i, i))],
            ScoreWeights.Default);

        Assert.Equal(CalibrationOutcome.NotEnoughData, verdict.Outcome);
        Assert.Contains("henüz bilinmiyor", verdict.Reason, StringComparison.Ordinal);
        Assert.Equal(ScoreWeights.Default, verdict.Weights);
    }

    /// SAF GÜRÜLTÜ HİÇBİR ZAMAN BENİMSENMİYOR.
    ///
    /// BU DOSYANIN EN ÖNEMLİ TESTİ. Yirmi ayrı gürültü kümesi: konu
    /// boyutlarıyla performans arasında hiçbir ilişki yok. Kalibrasyon
    /// bunların hiçbirinde ağırlık değiştirmemeli.
    ///
    /// Beş boyut ve %5 eşikle, boyutlardan birinin tesadüfen "anlamlı"
    /// çıkması beklenen bir şey (~%23). Onu yakalayan şey görülmemiş
    /// veri kapısı: gürültüye uydurulan ağırlık, uydurulmadığı veride
    /// eskisini geçemiyor.
    [Fact]
    public void SafGurultu_HicbirZamanBenimsenmiyor()
    {
        var adopted = 0;

        for (var seed = 0; seed < 20; seed++)
        {
            var random = new Random(seed);

            var samples = Enumerable.Range(0, 100)
                .Select(i => new CalibrationSample(
                    Id((seed * 1000) + i),
                    new TopicScore
                    {
                        Demand = random.Next(0, 101),
                        Fit = random.Next(0, 101),
                        Sourceability = random.Next(0, 101),
                        Visualizability = random.Next(0, 101),
                        Freshness = random.Next(0, 101),
                        Risk = 0,
                    },
                    random.NextDouble() * 100))
                .ToList();

            if (WeightCalibration.Evaluate(samples, ScoreWeights.Default).Changed)
            {
                adopted++;
            }
        }

        Assert.True(adopted == 0, $"Gurultuye uyduruldu: {adopted}/20");
    }

    /// GERÇEK SİNYAL YAKALANIYOR.
    ///
    /// Performans yalnızca `sourceability` ile belirleniyor; diğer
    /// boyutlar gürültü. Kalibrasyon o boyutun ağırlığını artırmalı —
    /// yoksa çerçeve hiçbir işe yaramaz, sadece hep "hayır" der.
    [Fact]
    public void GercekSinyal_AgirlikArtiyor()
    {
        var verdict = WeightCalibration.Evaluate(SignalSamples(), ScoreWeights.Default);

        Assert.Equal(CalibrationOutcome.Adopt, verdict.Outcome);

        Assert.True(verdict.Weights.Sourceability > ScoreWeights.Default.Sourceability,
            $"Kaynak ağırlığı artmadı: {verdict.Weights.Sourceability:0.###}");

        // VE GÖRÜLMEMİŞ VERİDE GERÇEKTEN DAHA İYİ.
        Assert.True(verdict.ProposedRho > verdict.CurrentRho);
    }

    /// BENİMSENEN AĞIRLIKLAR HÂLÂ GEÇERLİ.
    ///
    /// Toplamı 1 olmayan bir ağırlık seti kabul eşiğini sessizce
    /// kaydırırdı — kalibrasyon, düzeltmesi gereken şeyi bozardı.
    [Fact]
    public void BenimsenenAgirliklar_Gecerli()
    {
        var verdict = WeightCalibration.Evaluate(SignalSamples(), ScoreWeights.Default);

        Assert.True(ScoreWeights.Validate(verdict.Weights).IsSuccess);
    }

    /// AĞIRLIK TEK ADIMDA SONUNA KADAR GİTMİYOR.
    ///
    /// Sinyal tamamen tek boyutta olsa bile ağırlık 1'e sıçramıyor:
    /// altmış videonun gürültüsünü strateji sanmamak için yarım adım.
    /// Aynı sonucun birkaç kez doğrulanması gerekiyor.
    [Fact]
    public void AgirlikAdimi_Sinirli()
    {
        var verdict = WeightCalibration.Evaluate(SignalSamples(), ScoreWeights.Default);

        Assert.True(verdict.Weights.Sourceability < 0.9,
            $"Ağırlık tek adımda uca gitti: {verdict.Weights.Sourceability:0.###}");
    }

    /// RİSK CEZASI KALİBRE EDİLMİYOR.
    ///
    /// Riskli konular iyi performans gösterebilir. Veriye "riski daha
    /// az önemse" dedirtmek, politika kararını izlenmeye devretmek
    /// olurdu. Risk bir performans boyutu değil, bir sınır.
    [Fact]
    public void RiskCezasi_Degismiyor()
    {
        var verdict = WeightCalibration.Evaluate(SignalSamples(), ScoreWeights.Default);

        Assert.Equal(ScoreWeights.Default.RiskPenalty, verdict.Weights.RiskPenalty);
    }

    /// TERS KORELASYON SIFIRLANIYOR, TERS ÇEVRİLMİYOR.
    ///
    /// "Talebi düşük konular daha iyi gidiyor" sonucu neredeyse her
    /// zaman gürültü; ona ağırlık vermek, sistemin kimsenin aramadığı
    /// konuları seçmesi demek olurdu.
    [Fact]
    public void TersKorelasyon_Sifirlaniyor()
    {
        var proposed = WeightCalibration.Propose(
            ScoreWeights.Default,
            new Dictionary<string, double>(StringComparer.Ordinal)
            {
                [ScoreWeights.Dimensions.Demand] = -0.9,
                [ScoreWeights.Dimensions.Fit] = 0.4,
                [ScoreWeights.Dimensions.Sourceability] = 0.4,
                [ScoreWeights.Dimensions.Visualizability] = 0.4,
                [ScoreWeights.Dimensions.Freshness] = 0.4,
            });

        // Sıfıra doğru gitti ama negatife düşmedi.
        Assert.True(proposed.Demand < ScoreWeights.Default.Demand);
        Assert.True(proposed.Demand >= 0);
        Assert.True(ScoreWeights.Validate(proposed).IsSuccess);
    }

    /* ---- ağırlıklar gerçekten karara giriyor ---- */

    /// FARKLI AĞIRLIK, FARKLI KARAR.
    ///
    /// Ağırlıkların ayarda durup karara girmemesi, bu depodaki en
    /// pahalı hata sınıfı olurdu: kanal ayarı değişir, hiçbir şey
    /// değişmez.
    [Fact]
    public void FarkliAgirlik_FarkliKarar()
    {
        var score = new TopicScore
        {
            Demand = 90, Fit = 90, Sourceability = 20,
            Visualizability = 90, Freshness = 90, Risk = 0,
        };

        var kaynakAgir = ScoreWeights.Normalize(
            new Dictionary<string, double>(StringComparer.Ordinal)
            {
                [ScoreWeights.Dimensions.Demand] = 0.05,
                [ScoreWeights.Dimensions.Fit] = 0.05,
                [ScoreWeights.Dimensions.Sourceability] = 0.80,
                [ScoreWeights.Dimensions.Visualizability] = 0.05,
                [ScoreWeights.Dimensions.Freshness] = 0.05,
            },
            ScoreWeights.Default.RiskPenalty);

        Assert.Equal(TopicDecision.Accept, TopicPolicy.Decide(score));
        Assert.NotEqual(TopicDecision.Accept, TopicPolicy.Decide(score, null, kaynakAgir));
    }

    /* ---- kanal ayarından okuma ---- */

    /// BİLİNMEYEN BOYUT UYARI ÜRETİYOR.
    ///
    /// `sourcability` yazan biri, kanalının kaynak boyutunu hiç
    /// önemsemediğini aylar sonra fark ederdi.
    [Fact]
    public void BilinmeyenBoyut_Uyariyor()
    {
        var warnings = new List<string>();

        Read("""{"score_weights":{"sourcability":0.9}}""", warnings);

        Assert.Contains(warnings, w => w.Contains("sourcability", StringComparison.Ordinal));
    }

    /// EKSİK BOYUT VARSAYILANDAN TAMAMLANMIYOR.
    ///
    /// Tamamlamak toplamı 1'in üstüne çıkarır ve kabul eşiğini
    /// sessizce kaydırırdı. Yarım uygulanmış bir ağırlık listesi, hiç
    /// uygulanmamış olandan daha yanıltıcı.
    [Fact]
    public void EksikBoyut_VarsayilanaDonuyor()
    {
        var warnings = new List<string>();

        var weights = Read("""{"score_weights":{"demand":1.0}}""", warnings);

        Assert.Equal(ScoreWeights.Default, weights);
        Assert.Contains(warnings, w => w.Contains("eksik boyut", StringComparison.Ordinal));
    }

    /// TAM AYAR OKUNUYOR.
    [Fact]
    public void TamAyar_Okunuyor()
    {
        var warnings = new List<string>();

        var weights = Read(
            """
            {"score_weights":{
              "demand":0.1,"fit":0.1,"sourceability":0.6,
              "visualizability":0.1,"freshness":0.1,"risk_penalty":0.4}}
            """,
            warnings);

        Assert.Equal(0.6, weights.Sourceability, 6);
        Assert.Equal(0.4, weights.RiskPenalty, 6);
        Assert.Empty(warnings);
    }

    /// TOPLAMI 1 OLMAYAN AYAR ÖLÇEKLENİYOR — ve söyleniyor.
    [Fact]
    public void ToplamiBirOlmayanAyar_Olcekleniyor()
    {
        var warnings = new List<string>();

        var weights = Read(
            """
            {"score_weights":{
              "demand":2,"fit":2,"sourceability":2,
              "visualizability":2,"freshness":2}}
            """,
            warnings);

        Assert.Equal(1.0, weights.PositiveSum, 6);
        Assert.Equal(0.2, weights.Demand, 6);
        Assert.Single(warnings);
    }

    /* ---- yardımcılar ---- */

    private static ScoreWeights Read(string json, List<string> warnings)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);

        return ScoreWeights.Read(document.RootElement, warnings);
    }

    private static CalibrationSample Sample(int index, int sourceability, double outcome)
        => new(
            Id(index),
            new TopicScore
            {
                Demand = 50, Fit = 50, Sourceability = sourceability,
                Visualizability = 50, Freshness = 50, Risk = 0,
            },
            outcome);

    /// Performansı yalnızca `sourceability` belirleyen veri kümesi.
    private static List<CalibrationSample> SignalSamples()
    {
        var random = new Random(7);

        return [.. Enumerable.Range(0, 120).Select(i =>
        {
            var sourceability = random.Next(0, 101);

            return new CalibrationSample(
                Id(i),
                new TopicScore
                {
                    Demand = random.Next(0, 101),
                    Fit = random.Next(0, 101),
                    Sourceability = sourceability,
                    Visualizability = random.Next(0, 101),
                    Freshness = random.Next(0, 101),
                    Risk = 0,
                },
                // Gürültü var ama sinyal baskın: gerçek veride de
                // ilişki hiçbir zaman kusursuz olmayacak.
                sourceability + (random.NextDouble() * 20));
        })];
    }
}
