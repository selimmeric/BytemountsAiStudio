namespace BytemountsAiStudio.Core.Learning;

/// İki oranın karşılaştırılması (P5-02).
///
/// NEDEN İSTATİSTİK: "A varyantı %4,2, B varyantı %4,8 tıklanma aldı,
/// B kazandı" cümlesi ölçüme değil GÜRÜLTÜYE dayanabiliyor. Yüz
/// gösterimde %4 ile %5 arasındaki fark, madenî para atışıyla
/// üretilebilecek bir fark.
///
/// Kararı veriye dayandırmayan bir öğrenme döngüsü, öğrendiğini
/// SANIYOR: her hafta bir "kazanan" ilan ediyor, kanal stratejisini
/// ona göre değiştiriyor ve aslında rastgele yürüyor.
public static class Significance
{
    /// İki oran arasındaki farkın p-değeri (iki yönlü z-testi).
    ///
    /// `successes`/`trials`: tıklama/gösterim, ya da izlenme/erişim.
    ///
    /// İKİ YÖNLÜ, tek yönlü değil: "B daha iyi mi" diye sorup tek
    /// yönlü test kullanmak, B'nin DAHA KÖTÜ olduğu durumu görmezden
    /// gelmek ve eşiği fiilen yarıya indirmek demek. Bir varyantın
    /// zarar verdiğini öğrenmek de en az kazandırdığını öğrenmek kadar
    /// değerli.
    public static double PValue(int successesA, int trialsA, int successesB, int trialsB)
    {
        if (trialsA <= 0 || trialsB <= 0)
        {
            // Denenmemiş bir varyant hakkında söylenecek bir şey yok.
            // 1,0 döndürmek "fark kanıtlanamadı" demek — sıfır
            // döndürmek "kesin fark var" olurdu ve bu, veri yokken
            // en tehlikeli cevap.
            return 1.0;
        }

        var rateA = (double)successesA / trialsA;
        var rateB = (double)successesB / trialsB;

        // HAVUZLANMIŞ ORAN: sıfır hipotezi "iki oran aynı" diyor, o
        // yüzden standart hata ortak orandan hesaplanıyor. Ayrı
        // oranlardan hesaplamak testi kabul edilenden daha gevşek
        // yapardı.
        var pooled = (double)(successesA + successesB) / (trialsA + trialsB);

        if (pooled is <= 0 or >= 1)
        {
            // Hiç başarı yok ya da hepsi başarı: iki varyant da aynı
            // şeyi yaşadı, fark yok.
            return 1.0;
        }

        var standardError = Math.Sqrt(pooled * (1 - pooled) * ((1.0 / trialsA) + (1.0 / trialsB)));

        if (standardError <= 0)
        {
            return 1.0;
        }

        var z = (rateB - rateA) / standardError;

        return 2.0 * (1.0 - NormalCdf(Math.Abs(z)));
    }

    /// Bir varyantı ayırt etmek için varyant başına kaç deneme gerekir.
    ///
    /// SAYININ HESAPLANMASI ŞART, uydurulması değil. "Otuz video
    /// yeter" demek, ne kadar küçük bir farkı görebileceğini
    /// bilmemek demek — ve göremediği bir farkı "fark yok" diye
    /// raporlamak.
    ///
    /// `baseline`: mevcut oran (örneğin %4 tıklanma).
    /// `minimumDetectableEffect`: görmek istediğimiz MUTLAK fark
    /// (örneğin 0,01 = bir puan).
    ///
    /// Varsayımlar açık: α = 0,05 (yanlış alarm oranı), güç = 0,80
    /// (gerçek bir farkı yakalama olasılığı). İkisi de sektör
    /// teamülü; değiştirmek isteyen parametre verebilir.
    public static int RequiredSamplePerVariant(
        double baseline, double minimumDetectableEffect, double alpha = 0.05, double power = 0.80)
    {
        if (minimumDetectableEffect <= 0 || baseline is <= 0 or >= 1)
        {
            return int.MaxValue;
        }

        var other = Math.Clamp(baseline + minimumDetectableEffect, 0.0001, 0.9999);

        var zAlpha = InverseNormalCdf(1 - (alpha / 2));
        var zBeta = InverseNormalCdf(power);

        var variance = (baseline * (1 - baseline)) + (other * (1 - other));
        var n = Math.Pow(zAlpha + zBeta, 2) * variance / Math.Pow(other - baseline, 2);

        return (int)Math.Ceiling(n);
    }

    /// Standart normal dağılımın birikimli fonksiyonu.
    ///
    /// `erf` .NET'te yok; Abramowitz–Stegun 7.1.26 yaklaşımı
    /// kullanılıyor — mutlak hatası 1,5×10⁻⁷, yani p-değeri
    /// kararlarında görünmez.
    internal static double NormalCdf(double x)
        => 0.5 * (1.0 + Erf(x / Math.Sqrt(2.0)));

    private static double Erf(double x)
    {
        var sign = x < 0 ? -1.0 : 1.0;
        x = Math.Abs(x);

        const double A1 = 0.254829592;
        const double A2 = -0.284496736;
        const double A3 = 1.421413741;
        const double A4 = -1.453152027;
        const double A5 = 1.061405429;
        const double P = 0.3275911;

        var t = 1.0 / (1.0 + (P * x));
        var y = 1.0 - ((((((((A5 * t) + A4) * t) + A3) * t) + A2) * t) + A1) * t * Math.Exp(-x * x);

        return sign * y;
    }

    /// Normal dağılımın ters birikimli fonksiyonu (probit).
    ///
    /// Acklam yaklaşımı; örneklem hesabında kullanılan z değerleri
    /// için fazlasıyla yeterli.
    internal static double InverseNormalCdf(double p)
    {
        if (p is <= 0 or >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(p), p, "Olasılık (0,1) aralığında olmalı.");
        }

        double[] a = [-39.69683028665376, 220.9460984245205, -275.9285104469687,
                      138.3577518672690, -30.66479806614716, 2.506628277459239];
        double[] b = [-54.47609879822406, 161.5858368580409, -155.6989798598866,
                      66.80131188771972, -13.28068155288572];
        double[] c = [-0.007784894002430293, -0.3223964580411365, -2.400758277161838,
                      -2.549732539343734, 4.374664141464968, 2.938163982698783];
        double[] d = [0.007784695709041462, 0.3224671290700398, 2.445134137142996, 3.754408661907416];

        const double Low = 0.02425;
        const double High = 1 - Low;

        if (p < Low)
        {
            var q = Math.Sqrt(-2 * Math.Log(p));

            return ((((((c[0] * q) + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5])
                 / ((((((d[0] * q) + d[1]) * q + d[2]) * q + d[3]) * q) + 1);
        }

        if (p > High)
        {
            var q = Math.Sqrt(-2 * Math.Log(1 - p));

            return -((((((c[0] * q) + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5])
                  / ((((((d[0] * q) + d[1]) * q + d[2]) * q + d[3]) * q) + 1);
        }

        var r = p - 0.5;
        var s = r * r;

        return ((((((a[0] * s) + a[1]) * s + a[2]) * s + a[3]) * s + a[4]) * s + a[5]) * r
             / ((((((b[0] * s) + b[1]) * s + b[2]) * s + b[3]) * s + b[4]) * s + 1);
    }
}
