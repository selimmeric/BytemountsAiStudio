using System.Globalization;

namespace BytemountsAiStudio.Quality;

/// Bir sahnenin görsel alaka yargısı (P2-06).
///
/// `Score` 0–1: görselin o sahnenin metniyle ne kadar ilgili olduğu.
/// `null` = **ÖLÇÜLEMEDİ** ve bu "geçti" değil; ikisini eşitlemek,
/// görme modeli kapalıyken her videonun alaka kontrolünü tam puanla
/// geçmesi demekti.
public sealed record VisualRelevance(int SceneIndex, double? Score, string? Reason)
{
    public bool Measured => Score is not null;
}

/// Metin üzerinden semantik yargılar (P2-06).
///
/// Hepsi nullable ve sebebi aynı: ölçülemeyen bir kontrol geçmiş
/// sayılmamalı. Model kapalıysa video insana gidiyor — sessizce
/// yayınlanmıyor.
public sealed record SemanticJudgement
{
    /// Başlık içeriği KARŞILIYOR mu (yanıltıcı başlık kontrolü).
    public bool? TitleMatchesContent { get; init; }

    /// Anlatım tonu kanalın tonuna uygun mu.
    public bool? ToneAppropriate { get; init; }

    /// Politika ihlali riski taşıyan içerik var mı.
    public bool? PolicySafe { get; init; }

    /// Modelin gerekçesi. Skordan çok işe yarıyor: eşik ayarlanırken
    /// bakılan şey bu.
    public string? Rationale { get; init; }
}

/// Semantik kalite kontrolü (P2-06, §14.2).
///
/// SAF: model çağrısı yok, veritabanı yok. Model yargılarını ALIYOR ve
/// kontrol sonucuna çeviriyor. Ayrım, eşikleri ve "ölçülemedi"
/// davranışını bir görme modeli koşturmadan sınayabilmek için — ve bu
/// depoda o ayrım pratik bir zorunluluk: ana makinenin ekran kartı şu
/// an model yükleyince sistemi çökertiyor (bkz. DONANIM-VE-MODEL.md).
///
/// MEKANİK QC'DEN AYRI BİR SINIF. Mekanik kontroller ölçüyor (süre,
/// çözünürlük, ses seviyesi); semantik kontroller YORUMLUYOR. İkisini
/// aynı yere koymak, model kapalıyken çalışan kontrollerin de
/// susmasıydı.
public static class SemanticQc
{
    /// Bir sahnenin alakalı sayılması için gereken en düşük skor.
    ///
    /// 0,5: yarıdan azı "bu görsel bu cümleyle ilgisiz" demek. Daha
    /// yüksek bir eşik (0,8) soyut cümlelerde neredeyse her görseli
    /// düşürürdü — belgesel anlatıda "o dönemde ekonomi çökmüştü"
    /// cümlesinin birebir görseli yok ve olamaz.
    public const double RelevanceThreshold = 0.5;

    /// Kaç sahnenin alakasız olması kontrolü düşürüyor.
    ///
    /// ORAN, ADET DEĞİL: üç sahnelik bir videoda bir alakasız kare
    /// videonun üçte biri, yirmi sahnelikte yirmide biri. Aynı adet iki
    /// videoda tamamen farklı anlam taşıyor.
    public const double MaxIrrelevantRatio = 0.34;

    /// ÖRNEKLEME: her sahne değil, en fazla bu kadarı.
    ///
    /// Her kareyi modele sormak, yirmi sahnelik bir videoda yirmi
    /// çağrı demek ve görme modeli hattın en yavaş adımı. Örnekleme
    /// kaçırma riski taşıyor ama tamamen ölçmemekten iyi — ve kaç
    /// sahnenin ölçüldüğü çıktıya yazılıyor, yani "hepsi kontrol
    /// edildi" izlenimi hiç doğmuyor.
    public const int MaxSampledScenes = 6;

    /// Hangi sahneler örneklenecek.
    ///
    /// EŞİT ARALIKLA, baştan değil: ilk N sahneyi almak videonun
    /// sonunu hiç görmemek demek ve alakasız görseller çoğu zaman
    /// sonda oluyor — görsel seçici ilerledikçe sözlükten uzaklaşıyor.
    public static IReadOnlyList<int> SampleIndices(int sceneCount, int max = MaxSampledScenes)
    {
        if (sceneCount <= 0)
        {
            return [];
        }

        if (sceneCount <= max)
        {
            return [.. Enumerable.Range(0, sceneCount)];
        }

        var step = (double)sceneCount / max;

        return [.. Enumerable.Range(0, max)
            .Select(i => (int)Math.Floor(i * step))
            .Distinct()];
    }

    /// Yargıları kontrol sonucuna çevirir.
    public static IReadOnlyList<CheckResult> Evaluate(
        IReadOnlyList<VisualRelevance> relevance, SemanticJudgement judgement)
    {
        ArgumentNullException.ThrowIfNull(relevance);
        ArgumentNullException.ThrowIfNull(judgement);

        return
        [
            RelevanceCheck(relevance),
            Judged("qc.title_honest", "Başlık içeriği karşılıyor",
                judgement.TitleMatchesContent, CheckSeverity.Blocking, 12, RetryTarget.Metadata,
                "Başlık içerikte olmayan bir şey vadediyor.",
                judgement.Rationale),
            Judged("qc.tone", "Anlatım tonu uygun",
                judgement.ToneAppropriate, CheckSeverity.Warning, 6, RetryTarget.Script,
                "Ton kanalın tonuna uymuyor.",
                judgement.Rationale),
            // POLİTİKA BLOKLAYICI: yayınlanan bir ihlal kanalın
            // tamamını riske atıyor ve geri alınamıyor — video
            // silinse bile ihtar kalıyor.
            Judged("qc.policy", "Politika riski yok",
                judgement.PolicySafe, CheckSeverity.Blocking, 15, RetryTarget.Script,
                "İçerik politika ihlali riski taşıyor.",
                judgement.Rationale),
        ];
    }

    private static CheckResult RelevanceCheck(IReadOnlyList<VisualRelevance> relevance)
    {
        var measured = relevance.Where(r => r.Measured).ToList();

        if (measured.Count == 0)
        {
            // HİÇ ÖLÇÜLEMEDİ: kontrol DÜŞÜYOR, geçmiyor.
            //
            // Geçseydi görme modeli kapalıyken her video alaka
            // kontrolünü tam puanla geçerdi — ve kimse bu kontrolün
            // hiç koşmadığını fark etmezdi.
            return new CheckResult
            {
                Code = "qc.visual_relevance",
                Name = "Görsel alaka",
                Passed = false,
                Severity = CheckSeverity.Warning,
                Weight = 10,
                Target = RetryTarget.Visuals,
                Detail = "Görsel alaka ölçülemedi (görme modeli yok); insan bakmalı.",
            };
        }

        var irrelevant = measured.Where(r => r.Score < RelevanceThreshold).ToList();
        var ratio = (double)irrelevant.Count / measured.Count;

        var detail = string.Create(CultureInfo.InvariantCulture,
            $"{measured.Count} sahne örneklendi, {irrelevant.Count} tanesi alakasız (oran {ratio:0.##}).");

        if (irrelevant.Count > 0)
        {
            detail += " Alakasız sahneler: "
                      + string.Join(", ", irrelevant.Select(r => string.Create(
                          CultureInfo.InvariantCulture, $"#{r.SceneIndex} ({r.Score:0.##})")));
        }

        return new CheckResult
        {
            Code = "qc.visual_relevance",
            Name = "Görsel alaka",
            Passed = ratio <= MaxIrrelevantRatio,
            Severity = CheckSeverity.Warning,
            Weight = 10,
            Target = RetryTarget.Visuals,
            Detail = detail,
        };
    }

    /// Model yargısını kontrol sonucuna çevirir.
    ///
    /// `null` (ölçülemedi) DÜŞÜYOR ama gerekçesi ayrı yazılıyor:
    /// "kontrol düştü" ile "kontrol koşamadı" farklı şeyler ve triyaj
    /// eden insanın ikisini ayırt etmesi gerekiyor.
    private static CheckResult Judged(
        string code, string name, bool? value, CheckSeverity severity,
        int weight, RetryTarget target, string failureDetail, string? rationale)
        => new()
        {
            Code = code,
            Name = name,
            Passed = value == true,
            Severity = severity,
            Weight = weight,
            Target = target,
            Detail = value switch
            {
                true => rationale ?? "Geçti.",
                false => rationale is null ? failureDetail : $"{failureDetail} {rationale}",
                null => "Ölçülemedi (model yok); insan bakmalı.",
            },
        };
}
