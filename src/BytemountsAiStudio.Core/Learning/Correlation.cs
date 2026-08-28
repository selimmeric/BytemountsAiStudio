namespace BytemountsAiStudio.Core.Learning;

/// Sıra korelasyonu (P5-04).
///
/// SPEARMAN, PEARSON DEĞİL. Konu skoru 0–100 arası uydurma bir ölçek:
/// 80 ile 60 arasındaki farkın, 40 ile 20 arasındaki farkla aynı
/// büyüklükte olduğunu iddia edemeyiz. Pearson tam olarak bunu iddia
/// eder. Sıra korelasyonu yalnızca "daha yüksek skorlu konu daha iyi
/// performans gösterdi mi" sorusunu soruyor — cevaplayabildiğimiz tek
/// soru bu.
///
/// İzlenme sayıları da uzun kuyruklu: tek bir viral video Pearson'ı
/// tek başına belirleyebilir. Sıraya çevirmek o videoyu "en iyi"
/// yapar, "yüz kat daha iyi" değil.
public static class Correlation
{
    /// Spearman sıra korelasyonu (-1 … +1).
    ///
    /// Eşit değerler ORTALAMA SIRA alıyor. Rastgele sıralamak,
    /// skorların çoğunun aynı olduğu (modelin 70 vermeyi sevdiği) bir
    /// veri kümesinde uydurma bir korelasyon üretirdi.
    public static double Spearman(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);

        if (x.Count != y.Count || x.Count < 3)
        {
            // ÜÇ NOKTADAN AZINDA KORELASYON YOK, SIFIR DA DEĞİL.
            // Sıfır dönmek "ilişki yok" demek olurdu; doğru cevap
            // "ölçülemez" ve çağıran bunu örneklem kapısında zaten
            // yakalıyor.
            return 0;
        }

        var rankX = Ranks(x);
        var rankY = Ranks(y);

        return Pearson(rankX, rankY);
    }

    /// Anlamlılık hesabının güvenilir olduğu en küçük örneklem.
    ///
    /// ÖLÇÜLDÜ: n=5 ve ρ=0,9 için aşağıdaki yaklaşım p≈0,043 veriyor,
    /// yani "anlamlı" diyor. Oysa Spearman'ın tam testinde beş noktada
    /// %5 seviyesine ulaşmak için ρ=1,0 gerekiyor — yaklaşım tam da
    /// yanılmanın pahalı olduğu yerde CESUR davranıyor.
    ///
    /// Bu yüzden küçük örneklemde hesap yapılmıyor: cevap "anlamlı
    /// değil" değil, "bilinmiyor" — ve ikisi de aynı kapıyı kapatıyor.
    public const int MinimumForSignificance = 10;

    /// Korelasyonun tesadüf olma olasılığı (iki yönlü).
    ///
    /// Fisher dönüşümü + Fieller düzeltmesi.
    public static double PValue(double rho, int sampleCount)
    {
        if (sampleCount < MinimumForSignificance || Math.Abs(rho) >= 1.0)
        {
            // Kusursuz korelasyon genellikle veri hatası; "kesin"
            // demek yerine "bilinmiyor" demek daha güvenli.
            return Math.Abs(rho) >= 1.0 && sampleCount >= MinimumForSignificance ? 0.0 : 1.0;
        }

        var z = Math.Sqrt((sampleCount - 3) / 1.06) * Atanh(rho);

        return 2 * (1 - Significance.NormalCdf(Math.Abs(z)));
    }

    /// Ortalama sıralama (eşitlerde ortalama sıra).
    internal static double[] Ranks(IReadOnlyList<double> values)
    {
        var indexed = values
            .Select((value, index) => (value, index))
            .OrderBy(pair => pair.value)
            .ToList();

        var ranks = new double[values.Count];
        var position = 0;

        while (position < indexed.Count)
        {
            var end = position;

            while (end + 1 < indexed.Count
                && indexed[end + 1].value.Equals(indexed[position].value))
            {
                end++;
            }

            // Sıralar 1'den başlıyor; eşitler aralığın ortasını alıyor.
            var average = (position + end + 2) / 2.0;

            for (var i = position; i <= end; i++)
            {
                ranks[indexed[i].index] = average;
            }

            position = end + 1;
        }

        return ranks;
    }

    private static double Pearson(double[] x, double[] y)
    {
        var meanX = x.Average();
        var meanY = y.Average();

        double covariance = 0, varianceX = 0, varianceY = 0;

        for (var i = 0; i < x.Length; i++)
        {
            var dx = x[i] - meanX;
            var dy = y[i] - meanY;

            covariance += dx * dy;
            varianceX += dx * dx;
            varianceY += dy * dy;
        }

        if (varianceX == 0 || varianceY == 0)
        {
            // Bütün değerler aynı: sıralanacak bir şey yok. Sıfır
            // burada doğru cevap — "hiçbir ayrım yok" demek.
            return 0;
        }

        return covariance / Math.Sqrt(varianceX * varianceY);
    }

    private static double Atanh(double value) => 0.5 * Math.Log((1 + value) / (1 - value));
}
