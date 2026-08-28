using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BytemountsAiStudio.Core.Content;

namespace BytemountsAiStudio.Core.Learning;

/// Bir yayınlanmış videonun kalibrasyon örneği (P5-04).
public readonly record struct CalibrationSample(
    Guid RunId,
    TopicScore Score,
    double Outcome);

public enum CalibrationOutcome
{
    /// Örneklem yetersiz — "ağırlıklar doğru" DEĞİL, "henüz bilinmiyor".
    NotEnoughData = 0,

    /// Hiçbir boyut performansla ilişkili çıkmadı.
    ///
    /// Bu durumda ağırlık oynamak, anlamsız bir skorun katsayılarını
    /// düzeltmeye çalışmak olurdu. Sorun ağırlıklarda değil.
    NoPredictivePower = 1,

    /// Yeni ağırlıklar GÖRMEDİĞİ veride eskisini geçemedi.
    KeepCurrent = 2,

    /// Yeni ağırlıklar benimsendi.
    Adopt = 3,
}

public sealed record CalibrationVerdict(
    CalibrationOutcome Outcome,
    string Reason,
    ScoreWeights Weights,
    double CurrentRho,
    double ProposedRho,
    IReadOnlyDictionary<string, double> DimensionCorrelations,
    int SampleCount)
{
    public bool Changed => Outcome == CalibrationOutcome.Adopt;
}

/// Konu skorlama ağırlıklarının gerçek performansla kalibrasyonu (P5-04).
///
/// ASIL TEHLİKE, KALİBRASYONUN KENDİSİ.
///
/// Otuz videoya ağırlık uydurmak, o otuz videoyu MÜKEMMEL açıklayan
/// ve sonraki otuz video hakkında hiçbir şey bilmeyen katsayılar
/// üretir. Üstelik bu, çalışıyormuş gibi görünür: eğitim verisindeki
/// uyum her zaman artar. "Yeni ağırlıklar veriye daha iyi uyuyor"
/// cümlesi garantili ve anlamsız.
///
/// Bu yüzden dört kapı var ve dördü de "hayır" diyebiliyor:
///
///   1. ÖRNEKLEM. Beş ağırlık için boyut başına en az on gözlem, artı
///      ayrı bir sınama kümesi. Altındaysa hiç hesaplanmıyor.
///   2. ÖNGÖRÜ GÜCÜ. Hiçbir boyut performansla ilişkili değilse sorun
///      ağırlıklarda değil; katsayı oynatmak gürültüyü ezberlemek olur.
///   3. GÖRÜLMEMİŞ VERİ. Yeni ağırlıklar, uydurulmadıkları veride
///      eskisini belirgin farkla geçmek zorunda.
///   4. KENDİ ANLAMLILIĞI. Yeni ağırlığın görülmemiş verideki
///      korelasyonu tesadüften ayırt edilebilmeli — gürültüde
///      eskisinden iyi olmak, gürültü olmamak demek değil.
///
/// Dördüncü kapı ÖLÇÜLEREK eklendi: ilk üçüyle yirmi saf gürültü
/// kümesinin biri benimseniyordu (tek bir %5 eşiğinden beklenen oran).
/// Dördüyle sıfır. Kalibrasyonun "değişiklik yok" diyebilmesi,
/// çalıştığının kanıtı.
public static class WeightCalibration
{
    /// Beş ağırlık × boyut başına en az on gözlem + sınama kümesi.
    ///
    /// Sayı keyfî değil ama kesin de değil; kesin olan yönü: beş
    /// katsayıyı otuz veriyle belirlemek, veriyi ezberlemek demek.
    public const int MinimumSamples = 60;

    /// Eğitim kümesinin oranı.
    public const double TrainFraction = 0.7;

    /// Yeni ağırlıkların benimsenmesi için gereken en az iyileşme.
    ///
    /// Sıfırdan büyük olması yetmiyor: kalibrasyon her hafta koşuyor ve
    /// her koşuda kıl payı bir iyileşme yakalamak, yeterince deneyince
    /// kaçınılmaz. Eşik, o kıl payı iyileşmelerin ağırlıkları
    /// sürüklemesini engelliyor.
    public const double MinimumImprovement = 0.05;

    /// Ağırlıkların tek seferde ne kadar hareket edebildiği.
    ///
    /// Yarı yol: 0,30'dan 0,05'e tek adımda inen bir ağırlık, altmış
    /// videonun gürültüsünü strateji sanmak demek. Yarım adım, aynı
    /// sonucun birkaç kez doğrulanmasını şart koşuyor.
    public const double StepFraction = 0.5;

    private const double Alpha = 0.05;

    public static CalibrationVerdict Evaluate(
        IReadOnlyList<CalibrationSample> samples, ScoreWeights current)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(current);

        if (samples.Count < MinimumSamples)
        {
            return new CalibrationVerdict(
                CalibrationOutcome.NotEnoughData,
                string.Create(CultureInfo.InvariantCulture,
                    $"Kalibrasyon için {MinimumSamples} ölçülmüş video gerekiyor; ")
                + string.Create(CultureInfo.InvariantCulture,
                    $"{samples.Count} var, {MinimumSamples - samples.Count} eksik. ")
                + "Bu 'ağırlıklar doğru' DEĞİL, 'henüz bilinmiyor'.",
                current, 0, 0,
                new Dictionary<string, double>(StringComparer.Ordinal),
                samples.Count);
        }

        // BÖLME DETERMİNİSTİK.
        //
        // Rastgele bölmek, aynı veriyle her koşuda farklı cevap
        // vermek demekti — ve yeterince koşulunca biri "benimse"
        // derdi. Aynı veri → aynı bölme → aynı karar.
        var train = new List<CalibrationSample>();
        var test = new List<CalibrationSample>();

        foreach (var sample in samples)
        {
            (InTrain(sample.RunId) ? train : test).Add(sample);
        }

        if (train.Count < 10 || test.Count < 10)
        {
            return new CalibrationVerdict(
                CalibrationOutcome.NotEnoughData,
                string.Create(CultureInfo.InvariantCulture,
                    $"Bölme dengesiz: {train.Count} eğitim, {test.Count} sınama."),
                current, 0, 0,
                new Dictionary<string, double>(StringComparer.Ordinal),
                samples.Count);
        }

        var outcomes = train.Select(s => s.Outcome).ToList();

        var correlations = new Dictionary<string, double>(StringComparer.Ordinal);
        var significant = new List<string>();

        foreach (var dimension in ScoreWeights.Dimensions.All)
        {
            var values = train.Select(s => Value(s.Score, dimension)).ToList();
            var rho = Correlation.Spearman(values, outcomes);

            correlations[dimension] = rho;

            if (Correlation.PValue(rho, train.Count) < Alpha)
            {
                significant.Add(dimension);
            }
        }

        if (significant.Count == 0)
        {
            return new CalibrationVerdict(
                CalibrationOutcome.NoPredictivePower,
                string.Create(CultureInfo.InvariantCulture,
                    $"{train.Count} videoda hiçbir boyut performansla ilişkili çıkmadı. ")
                + "Ağırlık oynatmak gürültüyü ezberlemek olurdu; sorun ağırlıklarda değil.",
                current,
                Rho(test, current), Rho(test, current),
                correlations, samples.Count);
        }

        var proposed = Propose(current, correlations);

        var currentRho = Rho(test, current);
        var proposedRho = Rho(test, proposed);

        if (proposedRho < currentRho + MinimumImprovement)
        {
            return new CalibrationVerdict(
                CalibrationOutcome.KeepCurrent,
                string.Create(CultureInfo.InvariantCulture,
                    $"Yeni ağırlıklar görülmemiş veride eskisini geçemedi ")
                + string.Create(CultureInfo.InvariantCulture,
                    $"({currentRho:0.###} → {proposedRho:0.###}, gereken artış {MinimumImprovement:0.##}). ")
                + "Eğitim verisine daha iyi uymak yetmiyor; bu her zaman oluyor.",
                current, currentRho, proposedRho, correlations, samples.Count);
        }

        // DÖRDÜNCÜ KAPI: yeni ağırlığın KENDİ korelasyonu da anlamlı olmalı.
        //
        // ÖLÇÜLEREK EKLENDİ. İlk üç kapıyla yirmi saf gürültü kümesinin
        // BİRİ benimseniyordu — tam olarak tek bir %5 eşiğinden
        // beklenen oran. "Eskisinden iyi" olmak yetmiyor: eski
        // ağırlıklar da gürültüde kötüyse, ondan biraz daha iyi olan
        // yeni ağırlıklar da gürültüdür.
        var proposedP = Correlation.PValue(proposedRho, test.Count);

        if (proposedP >= Alpha)
        {
            return new CalibrationVerdict(
                CalibrationOutcome.KeepCurrent,
                string.Create(CultureInfo.InvariantCulture,
                    $"Yeni ağırlıklar eskisini geçti ({currentRho:0.###} → {proposedRho:0.###}) ")
                + string.Create(CultureInfo.InvariantCulture,
                    $"ama kendi korelasyonu tesadüften ayırt edilemiyor (p = {proposedP:0.###}). ")
                + "Gürültüde eskisinden iyi olmak, gürültü olmamak demek değil.",
                current, currentRho, proposedRho, correlations, samples.Count);
        }

        return new CalibrationVerdict(
            CalibrationOutcome.Adopt,
            string.Create(CultureInfo.InvariantCulture,
                $"Görülmemiş {test.Count} videoda sıra korelasyonu ")
            + string.Create(CultureInfo.InvariantCulture, $"{currentRho:0.###} → {proposedRho:0.###}. ")
            + "İlişkili boyutlar: " + string.Join(", ", significant),
            proposed, currentRho, proposedRho, correlations, samples.Count);
    }

    /// Korelasyonlardan ağırlık önerir — ve YARI YOLDA duruyor.
    ///
    /// NEGATİF KORELASYON SIFIRLANIYOR, TERS ÇEVRİLMİYOR. "Talebi
    /// düşük konular daha iyi gidiyor" sonucu neredeyse her zaman
    /// gürültü; ona ağırlık vermek, sistemin kimsenin aramadığı
    /// konuları seçmesi demek olurdu.
    internal static ScoreWeights Propose(
        ScoreWeights current, IReadOnlyDictionary<string, double> correlations)
    {
        var positive = ScoreWeights.Dimensions.All.ToDictionary(
            d => d, d => Math.Max(0, correlations.GetValueOrDefault(d)), StringComparer.Ordinal);

        var fitted = ScoreWeights.Normalize(positive, current.RiskPenalty);
        var currentByDimension = current.ByDimension;
        var fittedByDimension = fitted.ByDimension;

        var stepped = ScoreWeights.Dimensions.All.ToDictionary(
            d => d,
            d => ((1 - StepFraction) * currentByDimension[d]) + (StepFraction * fittedByDimension[d]),
            StringComparer.Ordinal);

        // RİSK CEZASI KALİBRE EDİLMİYOR.
        //
        // Riskli konular iyi performans gösterebilir — muhtemelen
        // gösteriyor da. Veriye "riski daha az önemse" dedirtmek,
        // politika kararını izlenmeye devretmek olurdu. Risk bir
        // performans boyutu değil, bir sınır.
        return ScoreWeights.Normalize(stepped, current.RiskPenalty);
    }

    private static double Rho(IReadOnlyList<CalibrationSample> samples, ScoreWeights weights)
        => Correlation.Spearman(
            [.. samples.Select(s => s.Score.Weighted(weights))],
            [.. samples.Select(s => s.Outcome)]);

    private static double Value(TopicScore score, string dimension) => dimension switch
    {
        ScoreWeights.Dimensions.Demand => score.Demand,
        ScoreWeights.Dimensions.Fit => score.Fit,
        ScoreWeights.Dimensions.Sourceability => score.Sourceability,
        ScoreWeights.Dimensions.Visualizability => score.Visualizability,
        ScoreWeights.Dimensions.Freshness => score.Freshness,
        _ => 0,
    };

    /// Örnek eğitim kümesine mi düşüyor — `run_id`'den deterministik.
    internal static bool InTrain(Guid runId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"kalibrasyon:{runId:N}"));

        return BitConverter.ToUInt32(hash, 0) % 100 < TrainFraction * 100;
    }
}
