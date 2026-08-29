using System.Globalization;

namespace BytemountsAiStudio.Core.Learning;

/// Bir deneyin sonucu (P5-02).
public enum ExperimentOutcome
{
    /// Henüz karar verilemez — örneklem yetersiz.
    ///
    /// "FARK YOK" DEĞİL ve ikisini karıştırmak bu çerçevenin
    /// engellemek için var olduğu hatanın kendisi. Üç videodan sonra
    /// "fark yok" demek, denemeyi bırakmak demek; oysa henüz hiçbir
    /// şey ölçülmemiş.
    NotEnoughData = 0,

    /// Yeterli veri var ve fark istatistiksel olarak anlamlı değil.
    NoDifference = 1,

    /// Varyant kontrolden anlamlı ölçüde İYİ.
    VariantWins = 2,

    /// Varyant kontrolden anlamlı ölçüde KÖTÜ.
    ///
    /// Ayrı bir sonuç, çünkü yapılacak şey ayrı: "fark yok" denemeye
    /// devam etmeyi düşündürür, "daha kötü" o varyantı kapatmayı
    /// gerektirir.
    ControlWins = 3,
}

/// Bir varyantın ölçülen sonucu.
public readonly record struct VariantResult(string Name, int Successes, int Trials)
{
    public double Rate => Trials > 0 ? (double)Successes / Trials : 0;
}

/// Deney kararı ve GEREKÇESİ.
public sealed record ExperimentVerdict(
    ExperimentOutcome Outcome,
    string Reason,
    double PValue,
    int RequiredPerVariant)
{
    public bool IsDecided => Outcome is ExperimentOutcome.VariantWins or ExperimentOutcome.ControlWins;
}

/// Tek değişkenli deney değerlendirmesi (P5-02).
///
/// ÜÇ KURAL, ÜÇÜ DE BİR HATADAN GELİYOR:
///
/// 1. TEK DEĞİŞKEN. Aynı anda hem kapağı hem başlığı değiştiren bir
///    deney kazanırsa hangisinin kazandırdığı bilinmiyor — ve bir
///    sonraki videoda yanlış olanı taşımak mümkün.
///
/// 2. ÖNCE ÖRNEKLEM, SONRA BAKIŞ. Gerekli örneklem ÖNCEDEN
///    hesaplanıyor ve o sayıya ulaşmadan karar verilmiyor.
///
/// 3. ARADA BAKIP DURMA YOK. Her gün "anlamlı oldu mu" diye bakıp
///    p &lt; 0,05'te durmak, yanlış alarm oranını %5'ten %20'nin
///    üzerine çıkarıyor. Bu, ev yapımı A/B sistemlerinin en yaygın
///    hatası ve sonucu, gerçekte hiçbir şey yapmayan değişikliklerin
///    "kanıtlanmış" sayılması.
public static class ExperimentEvaluator
{
    /// Anlamlılık eşiği.
    public const double Alpha = 0.05;

    /// Kararı verir.
    ///
    /// `minimumDetectableEffect`: görmek istediğimiz MUTLAK fark.
    /// Küçük bir fark görmek istemek, çok daha büyük örneklem
    /// istiyor — bu takas açıkta duruyor.
    public static ExperimentVerdict Evaluate(
        VariantResult control, VariantResult variant, double minimumDetectableEffect)
    {
        var required = Significance.RequiredSamplePerVariant(
            control.Trials > 0 ? control.Rate : minimumDetectableEffect,
            minimumDetectableEffect,
            Alpha);

        // ÖRNEKLEM YETMİYORSA HİÇ TESTE GİRİLMİYOR.
        //
        // Testi koşturup "anlamlı değil" demek teknik olarak doğru
        // ama YANLIŞ ANLAŞILIR: okuyan kişi "fark yok" diye anlar,
        // oysa doğru cümle "henüz bilmiyoruz".
        if (control.Trials < required || variant.Trials < required)
        {
            var missing = Math.Max(required - control.Trials, required - variant.Trials);

            return new ExperimentVerdict(
                ExperimentOutcome.NotEnoughData,
                FormattableString.Invariant(
                    $"Varyant başına {required} deneme gerekiyor; {missing} eksik. ")
                    + "Bu 'fark yok' DEĞİL, 'henüz bilinmiyor'.",
                PValue: 1.0,
                required);
        }

        var p = Significance.PValue(
            control.Successes, control.Trials, variant.Successes, variant.Trials);

        if (p >= Alpha)
        {
            return new ExperimentVerdict(
                ExperimentOutcome.NoDifference,
                FormattableString.Invariant(
                    $"Yeterli veri var ({control.Trials} / {variant.Trials}) ve fark anlamlı değil ")
                    + FormattableString.Invariant($"({Format(p)}). Varyant taşınmıyor."),
                p,
                required);
        }

        var better = variant.Rate > control.Rate;

        return new ExperimentVerdict(
            better ? ExperimentOutcome.VariantWins : ExperimentOutcome.ControlWins,
            (better ? "Varyant" : "Kontrol") + " kazandı: "
                + FormattableString.Invariant($"{control.Rate:P2} → {variant.Rate:P2} ({Format(p)})."),
            p,
            required);
    }

    /// p-değerini YUVARLAMADAN yazar.
    ///
    /// `{p:0.###}` biçimi 0,00001'i "0" diye yazıyordu ve "p = 0"
    /// KESİNLİK iddiası — istatistikte olmayan bir şey. Kabul
    /// koşusunda gerçek çıktıda görüldü: "Varyant kazandı: %4,00 →
    /// %6,00 (p = 0)". Küçük değerler artık eşikle yazılıyor.
    private static string Format(double p)
        => p < 0.001
            ? "p < 0,001"
            : FormattableString.Invariant($"p = {p:0.###}");

    /// İki varyantın TEK bir boyutta ayrıştığını doğrular.
    ///
    /// `dimensions`: her varyantın alanları (kapak istemi, başlık
    /// istemi, ses…). Aynı anahtar iki varyantta farklıysa o boyut
    /// değişmiş sayılıyor.
    ///
    /// Bu kontrol olmadan "tek değişkenli deney" bir niyet beyanı
    /// olurdu — ve bu depoda niyet beyanları defalarca koddan
    /// ayrıştı.
    public static Result<string> SingleChangedDimension(
        IReadOnlyDictionary<string, string> control,
        IReadOnlyDictionary<string, string> variant)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(variant);

        var changed = control.Keys
            .Union(variant.Keys, StringComparer.Ordinal)
            .Where(key => !string.Equals(
                control.GetValueOrDefault(key), variant.GetValueOrDefault(key), StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

        return changed.Count switch
        {
            1 => Result.Success(changed[0]),

            0 => Errors.Error.Permanent("experiment.no_change",
                "Varyant kontrolden farklı değil; ölçülecek bir şey yok."),

            _ => Errors.Error.Permanent("experiment.multiple_changes",
                $"Deney {changed.Count} boyutta ayrışıyor ({string.Join(", ", changed)}). "
                + "Kazandığında hangisinin kazandırdığı bilinemez."),
        };
    }
}
