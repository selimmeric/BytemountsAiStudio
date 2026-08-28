using System.Globalization;

namespace BytemountsAiStudio.Quality;

/// Hedefli yeniden koşma kararı (P2-07, §14.3).
public enum RetryDecision
{
    /// Yeniden koşulmuyor — ya her şey yolunda ya da düzelme ihtimali yok.
    None = 0,

    /// Hedef node'dan itibaren yeniden koşuluyor.
    Rerun = 1,

    /// Döngü sınırı doldu; run insana ya da başarısızlığa gidiyor.
    LoopLimitReached = 2,
}

public sealed record RetryPlan(RetryDecision Decision, RetryTarget Target, int Loop, string Reason)
{
    public bool ShouldRerun => Decision == RetryDecision.Rerun;

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{Decision} ({Target}), tur {Loop}: {Reason}");
}

/// QC sonucundan yeniden koşma planı çıkarır (P2-07).
///
/// SAF: veritabanı ve model yok. "Hangi node'dan dönülecek" kararı,
/// gerçek bir koşu para harcayarak yapılmadan sınanabilmeli.
///
/// KABUL KRİTERİ: QC retry'ı TÜM boru hattını yeniden koşturmuyor.
/// Senaryo iyiyken render bozuksa senaryoyu yeniden üretmenin bedeli
/// var, faydası yok — ve o bedel her turda tekrarlanıyor.
public static class RetryPlanner
{
    /// Varsayılan döngü sınırı.
    ///
    /// ÜÇ: iki tur düzeltmeyen bir kusur genelde üçüncüde de
    /// düzelmiyor, ve sınırsız bir döngü aynı hatayı sonsuza kadar
    /// para harcayarak tekrarlıyor. Sınır bir güvenlik kemeri, bir
    /// optimizasyon değil — araştırma bütçesiyle (P1-09) aynı mantık.
    public const int DefaultMaxLoops = 3;

    /// Boru hattındaki node'lar, SIRAYLA.
    ///
    /// Sıra `RetryTarget` değerleriyle aynı: hedefin kendisi ve
    /// SONRASINDAKİ her şey yeniden koşuyor. Öncesi korunuyor —
    /// hedefli retry'ın tanımı bu.
    private static readonly (RetryTarget Stage, string[] Nodes)[] Pipeline =
    [
        // ANLATIM SENARYOYA BAĞLI.
        //
        // Senaryo yeniden üretilip seslendirme yenilenmezse video,
        // ESKİ metni okuyan bir sesle YENİ metnin altyazılarını
        // taşır. Kulakla gözün farklı şeyler söylediği bir video,
        // hiç düzeltilmemiş olandan daha kötü — ve mekanik QC bunu
        // yakalayamaz, çünkü her iki parça da tek başına geçerli.
        (RetryTarget.Script, ["script.generate", "claim.check", "tts.synthesize"]),
        (RetryTarget.Visuals, ["visual.resolve"]),
        (RetryTarget.Timeline, ["timeline.compile"]),
        (RetryTarget.Render, ["media.render"]),
        (RetryTarget.Metadata, ["seo.generate"]),
    ];

    public static RetryPlan Plan(QualityReport report, int completedLoops, int maxLoops = DefaultMaxLoops)
    {
        ArgumentNullException.ThrowIfNull(report);

        // YALNIZCA `Retry` kararı yeniden koşuyor.
        //
        // `NeedsApproval` bir düşüş değil, bir yönlendirme: video
        // sınırda ve insan bakacak (P2-08). Onu da yeniden koşturmak,
        // insanın zaten kabul edeceği bir videoyu bir kez daha
        // üretmek olurdu — hem para hem gecikme.
        if (report.Decision != QualityDecision.Retry)
        {
            return new RetryPlan(RetryDecision.None, RetryTarget.None, completedLoops,
                $"QC kararı: {report.Decision}");
        }

        // ÖLÇÜLEMEYEN KONTROL RETRY'I TETİKLEMİYOR.
        //
        // Bu kural gerçek bir kayıptan doğdu: ilk uçtan uca koşuda beş
        // kontrol "ölçülmedi" diye düştü (ses seviyesi, kırpılma,
        // konuşma oranı, kapak, tekillik) çünkü hat o ölçümleri hiç
        // üretmiyor. QC bunu kalite sorunu sanıp senaryodan yeniden
        // koşma istedi; sistem ÜÇ TUR aynı videoyu render etti (her
        // tur ~4 dakika) ve hiçbir şey değişmedi — değişemezdi de.
        //
        // Yeniden koşmak eksik bir ÖLÇÜM ADIMINI eklemiyor. Düşen
        // kontrollerin HEPSİ ölçülememişse yapılacak şey insana
        // gitmek: eksik olan hattın kendisi ve onu bir insan
        // tamamlayacak.
        //
        // Ölçülmüş bir düşüş varsa retry yine koşuyor — o düşüş
        // gerçek ve düzelebilir.
        var failures = report.Failures;

        if (failures.Count > 0 && failures.All(c => !c.Measured))
        {
            return new RetryPlan(RetryDecision.LoopLimitReached, report.Target, completedLoops,
                $"{failures.Count} kontrol ölçülemedi; yeniden koşmak ölçüm adımı eklemiyor, insan bakmalı");
        }

        if (report.Target == RetryTarget.None)
        {
            // Hedefi olmayan bir düşüş yeniden koşmayla DÜZELMİYOR.
            // Örneğin ölçülemeyen bir süre: aynı adımı tekrarlamak
            // aynı ölçülemezliği üretiyor.
            return new RetryPlan(RetryDecision.None, RetryTarget.None, completedLoops,
                "hedef yok; yeniden koşmak düzeltmez");
        }

        var limit = Math.Max(maxLoops, 0);

        if (completedLoops >= limit)
        {
            // SINIR DOLDU: run başarısız DEĞİL, insana gidiyor.
            // Başarısız saymak, üç turdur düzelmeyen ama belki insan
            // gözüyle kabul edilebilir bir videoyu çöpe atmak olurdu.
            return new RetryPlan(RetryDecision.LoopLimitReached, report.Target, completedLoops,
                string.Create(CultureInfo.InvariantCulture,
                    $"{limit} tur denendi, hala geçmiyor"));
        }

        return new RetryPlan(RetryDecision.Rerun, report.Target, completedLoops + 1,
            string.Create(CultureInfo.InvariantCulture,
                $"{report.Target} aşamasından yeniden koşuluyor"));
    }

    /// Hedeften itibaren yeniden koşacak node'lar.
    ///
    /// Hedefin KENDİSİ dâhil: "senaryoya dön" demek senaryoyu yeniden
    /// üretmek demek. Sonrasındaki her şey de dâhil, çünkü senaryo
    /// değişince ses, görsel ve render'ın hepsi geçersiz.
    ///
    /// Öncesi HİÇ dokunulmuyor — araştırma yeniden yapılmıyor,
    /// kaynaklar yeniden çekilmiyor. Kabul kriterinin ölçülebilir
    /// karşılığı bu.
    public static IReadOnlyList<string> NodesFrom(RetryTarget target)
    {
        if (target == RetryTarget.None)
        {
            return [];
        }

        var nodes = new List<string>();

        foreach (var (stage, stageNodes) in Pipeline)
        {
            if (stage >= target)
            {
                nodes.AddRange(stageNodes);
            }
        }

        return nodes;
    }

    /// Bir turda kaç node yeniden koşacak — maliyet karşılaştırması
    /// için.
    ///
    /// Kabul kriteri "maliyet ölçümü kanıt" diyor: baştan koşmak ile
    /// hedeften koşmak arasındaki farkın sayı olarak görülmesi
    /// gerekiyor.
    public static int NodeCount(RetryTarget target) => NodesFrom(target).Count;

    /// Baştan koşmaya göre kaç node atlanıyor.
    public static int Saved(RetryTarget target)
        => NodesFrom(RetryTarget.Script).Count - NodesFrom(target).Count;
}
