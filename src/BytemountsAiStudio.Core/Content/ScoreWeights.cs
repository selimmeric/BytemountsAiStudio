using System.Globalization;
using System.Text.Json;
using BytemountsAiStudio.Core.Errors;

namespace BytemountsAiStudio.Core.Content;

/// Konu skorunun ağırlıkları (P5-04).
///
/// AĞIRLIKLAR ELLE KONDU VE ELLE KONDUĞU BELLİ. `Sourceability` en
/// ağır boyut çünkü hattın kırılma noktası orası — ama bu bir HİPOTEZ,
/// ölçüm değil. P5-04'ün işi hipotezi ölçülebilir kılmak: ağırlıklar
/// koddan çıkıp kanal ayarına taşındı, böylece değiştirilebilir ve
/// değiştirmenin sonucu ölçülebilir.
///
/// TOPLAM 1 OLMAK ZORUNDA. Olmasaydı `Overall` 0–100 aralığından
/// çıkardı ve `AcceptThreshold = 65` sessizce başka bir anlama
/// gelirdi: aynı eşik, aynı konuyu bir kanalda kabul edip diğerinde
/// reddederdi.
public sealed record ScoreWeights
{
    public required double Demand { get; init; }

    public required double Fit { get; init; }

    public required double Sourceability { get; init; }

    public required double Visualizability { get; init; }

    public required double Freshness { get; init; }

    /// Risk CEZASI — ağırlık değil.
    ///
    /// Ağırlıklı ortalamaya katsaydık yüksek riskli bir konu diğer
    /// boyutlardan telafi edebilirdi. Politika ihlali riski telafi
    /// edilebilir bir şey değil, o yüzden toplamdan düşülüyor ve bu
    /// sayı toplamın dışında duruyor.
    public required double RiskPenalty { get; init; }

    /// Bugünkü ağırlıklar: P1-08'de elle kondu.
    public static ScoreWeights Default { get; } = new()
    {
        Demand = 0.20,
        Fit = 0.15,
        Sourceability = 0.30,
        Visualizability = 0.20,
        Freshness = 0.15,
        RiskPenalty = 0.5,
    };

    /// Pozitif boyutların toplamı.
    public double PositiveSum => Demand + Fit + Sourceability + Visualizability + Freshness;

    /// Boyut adı → ağırlık. Kalibrasyon boyutlar üzerinde döndüğü için
    /// isimle erişim gerekiyor.
    public IReadOnlyDictionary<string, double> ByDimension
        => new Dictionary<string, double>(StringComparer.Ordinal)
        {
            [Dimensions.Demand] = Demand,
            [Dimensions.Fit] = Fit,
            [Dimensions.Sourceability] = Sourceability,
            [Dimensions.Visualizability] = Visualizability,
            [Dimensions.Freshness] = Freshness,
        };

    /// Pozitif boyutların adları — sıra sabit.
    public static class Dimensions
    {
        public const string Demand = "demand";
        public const string Fit = "fit";
        public const string Sourceability = "sourceability";
        public const string Visualizability = "visualizability";
        public const string Freshness = "freshness";

        public static IReadOnlyList<string> All { get; }
            = [Demand, Fit, Sourceability, Visualizability, Freshness];
    }

    /// Ağırlık sözlüğünden ağırlık nesnesi kurar ve DOĞRULAR.
    public static Result<ScoreWeights> FromDimensions(
        IReadOnlyDictionary<string, double> values, double riskPenalty)
    {
        ArgumentNullException.ThrowIfNull(values);

        var missing = Dimensions.All.Where(d => !values.ContainsKey(d)).ToList();

        if (missing.Count > 0)
        {
            return Error.Permanent("weights.missing_dimension",
                "Eksik boyut: " + string.Join(", ", missing));
        }

        return Validate(new ScoreWeights
        {
            Demand = values[Dimensions.Demand],
            Fit = values[Dimensions.Fit],
            Sourceability = values[Dimensions.Sourceability],
            Visualizability = values[Dimensions.Visualizability],
            Freshness = values[Dimensions.Freshness],
            RiskPenalty = riskPenalty,
        });
    }

    /// Ağırlıkların geçerli olduğunu doğrular.
    ///
    /// NEGATİF AĞIRLIK REDDEDİLİYOR: "talebi düşük olan konu daha iyi"
    /// gibi bir sonuç, gürültüye uydurmanın en açık işareti. Kalibrasyon
    /// böyle bir sonuca varırsa boyutu SIFIRLIYOR, ters çevirmiyor.
    public static Result<ScoreWeights> Validate(ScoreWeights weights)
    {
        ArgumentNullException.ThrowIfNull(weights);

        foreach (var (name, value) in weights.ByDimension)
        {
            if (double.IsNaN(value) || value < 0)
            {
                return Error.Permanent("weights.negative",
                    string.Create(CultureInfo.InvariantCulture, $"'{name}' ağırlığı negatif: {value}"));
            }
        }

        if (Math.Abs(weights.PositiveSum - 1.0) > 0.001)
        {
            return Error.Permanent("weights.not_normalized",
                string.Create(CultureInfo.InvariantCulture,
                    $"Ağırlıklar toplamı 1 değil: {weights.PositiveSum:0.###}. ")
                + "Toplam 1 olmazsa skor 0–100 aralığından çıkar ve kabul eşiği anlamını yitirir.");
        }

        if (weights.RiskPenalty is < 0 or > 1)
        {
            return Error.Permanent("weights.bad_risk",
                string.Create(CultureInfo.InvariantCulture,
                    $"Risk cezası 0–1 aralığında olmalı: {weights.RiskPenalty:0.###}"));
        }

        return Result.Success(weights);
    }

    /// Ağırlıkları toplamı 1 olacak şekilde ölçekler.
    ///
    /// Hepsi sıfırsa varsayılana dönüyor: sıfır ağırlıklı bir skor her
    /// konuya 0 verir ve kanal hiçbir konuyu kabul etmez.
    public static ScoreWeights Normalize(IReadOnlyDictionary<string, double> values, double riskPenalty)
    {
        ArgumentNullException.ThrowIfNull(values);

        var clamped = Dimensions.All.ToDictionary(
            d => d, d => Math.Max(0, values.GetValueOrDefault(d)), StringComparer.Ordinal);

        var sum = clamped.Values.Sum();

        if (sum <= 0)
        {
            return Default;
        }

        return new ScoreWeights
        {
            Demand = clamped[Dimensions.Demand] / sum,
            Fit = clamped[Dimensions.Fit] / sum,
            Sourceability = clamped[Dimensions.Sourceability] / sum,
            Visualizability = clamped[Dimensions.Visualizability] / sum,
            Freshness = clamped[Dimensions.Freshness] / sum,
            RiskPenalty = Math.Clamp(riskPenalty, 0, 1),
        };
    }

    /// Kanal ayarındaki `score_weights` bloğunu okur.
    ///
    /// TANINMAYAN AYAR SESSİZCE DÜŞMÜYOR (P5-03'teki aynı ders):
    /// `sourcability` yazan biri, kanalının kaynak boyutunu hiç
    /// önemsemediğini aylar sonra fark ederdi.
    public static ScoreWeights Read(JsonElement root, List<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(warnings);

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("score_weights", out var block)
            || block.ValueKind != JsonValueKind.Object)
        {
            return Default;
        }

        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        var riskPenalty = Default.RiskPenalty;

        foreach (var property in block.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Number)
            {
                warnings.Add($"`score_weights.{property.Name}` sayı değil; yok sayıldı");
                continue;
            }

            if (property.NameEquals("risk_penalty"))
            {
                riskPenalty = property.Value.GetDouble();
                continue;
            }

            if (!Dimensions.All.Contains(property.Name, StringComparer.Ordinal))
            {
                warnings.Add(
                    $"`score_weights.{property.Name}` bilinmeyen boyut; tanımlılar: "
                    + string.Join(", ", Dimensions.All));

                continue;
            }

            values[property.Name] = property.Value.GetDouble();
        }

        if (values.Count == 0)
        {
            return Default with { RiskPenalty = Math.Clamp(riskPenalty, 0, 1) };
        }

        var missing = Dimensions.All.Where(d => !values.ContainsKey(d)).ToList();

        if (missing.Count > 0)
        {
            // EKSİK BOYUT VARSAYILANDAN TAMAMLANMIYOR.
            //
            // Tamamlamak, toplamı 1'in üstüne çıkarır ve kabul eşiğini
            // sessizce kaydırırdı. Ayarın tamamı reddediliyor: yarım
            // uygulanmış bir ağırlık listesi, hiç uygulanmamış olandan
            // daha yanıltıcı.
            warnings.Add(
                "`score_weights` eksik boyut içeriyor (" + string.Join(", ", missing)
                + "); varsayılan ağırlıklar kullanılıyor");

            return Default;
        }

        var normalized = Normalize(values, riskPenalty);

        if (Math.Abs(values.Values.Sum() - 1.0) > 0.001)
        {
            warnings.Add(string.Create(CultureInfo.InvariantCulture,
                $"`score_weights` toplamı 1 değil ({values.Values.Sum():0.###}); ölçeklendi"));
        }

        return normalized;
    }
}
