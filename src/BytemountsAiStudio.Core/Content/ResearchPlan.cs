using System.Globalization;

namespace BytemountsAiStudio.Core.Content;

/// Araştırma planı (P1-09, §7.3).
///
/// Planlama ile YÜRÜTME ayrı: plan bir kez üretiliyor, yürütme onu
/// adım adım tüketiyor. Ayırmak, "kaç arama yapacağız" sorusunun
/// cevabını çağrı başında bilinir kılıyor — bütçe ancak böyle
/// öngörülebilir.
public sealed record ResearchPlan
{
    public required IReadOnlyList<ResearchQuery> Queries { get; init; }

    /// Ajanın yapabileceği en fazla adım.
    ///
    /// Adım sayısı SINIRSIZ olamaz: araç döngüsü olan tek agent bu ve
    /// sınırsız bir döngü, kaynak bulamadıkça arama yapmaya devam edip
    /// bütçeyi tüketiyor. Sınır bir güvenlik kemeri, bir optimizasyon
    /// değil.
    public int MaxSteps { get; init; } = 6;

    /// Yeterli sayılacak kaynak sayısı. Buna ulaşınca döngü ERKEN
    /// bitiyor — plandaki her sorguyu koşturmak zorunlu değil.
    public int TargetSources { get; init; } = 3;

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture,
            $"{Queries.Count} sorgu, en fazla {MaxSteps} adım, hedef {TargetSources} kaynak");
}

/// Tek bir arama sorgusu.
public sealed record ResearchQuery
{
    public required string Text { get; init; }

    /// SORGU DİLİ, içerik dilinden FARKLI olabilir ve bu normal
    /// durumdur (§20.1): Türkçe içeriğin çoğu konuda İngilizce kaynağı
    /// daha zengin. Planlayıcının bunu seçebilmesi gerekiyor.
    public required string Language { get; init; }

    /// Bu sorgunun neden sorulduğu. Araştırma boş döndüğünde "hangi
    /// açıyı deneyip bulamadık" sorusunun cevabı.
    public string? Intent { get; init; }

    /// Tercih edilen kaynak türü. Zorunlu değil — bulunamazsa başka
    /// türden kaynak da kabul ediliyor.
    public string? PreferredSourceType { get; init; }
}

/// Araştırma döngüsünün bütçesi ve durumu (P1-09).
///
/// Ayrı ve SAF: döngünün ne zaman duracağı kararı ağa çıkmadan
/// sınanabilsin. Bütçe mantığındaki bir hata, gerçek koşuda para
/// harcayarak öğrenilecek bir şey olmamalı.
public sealed class ResearchBudget(int maxSteps, int targetSources)
{
    private int _steps;

    public int Steps => _steps;

    public int SourcesFound { get; private set; }

    public int MaxSteps { get; } = Math.Max(maxSteps, 1);

    public int TargetSources { get; } = Math.Max(targetSources, 1);

    /// Döngü neden durdu. "Durdu" yetmez: hedefe ulaşarak mı durdu,
    /// adım biterek mi? İkisi tamamen farklı sonuçlar ve ikincisi
    /// araştırmanın yetersiz olduğunu söylüyor.
    public ResearchStop Stop { get; private set; } = ResearchStop.Running;

    public bool CanContinue => Stop == ResearchStop.Running;

    /// Bir adım harcandığını bildirir.
    public void StepTaken()
    {
        _steps++;

        if (_steps >= MaxSteps && Stop == ResearchStop.Running)
        {
            Stop = ResearchStop.StepsExhausted;
        }
    }

    /// Kaynak bulunduğunu bildirir.
    ///
    /// Hedefe ulaşmak adım bitmesinden ÖNCE değerlendiriliyor: son
    /// adımda hedefe ulaşan bir araştırma "adım bitti" diye
    /// işaretlenmemeli, başarıyla bitti.
    public void SourceFound()
    {
        SourcesFound++;

        if (SourcesFound >= TargetSources)
        {
            Stop = ResearchStop.TargetReached;
        }
    }

    /// Sorgular tükendi ama hedefe ulaşılamadı.
    public void QueriesExhausted()
    {
        if (Stop == ResearchStop.Running)
        {
            Stop = ResearchStop.QueriesExhausted;
        }
    }

    /// KISMİ sonuç kabul edilebilir mi.
    ///
    /// En az bir kaynak varsa evet: eksik araştırmayla senaryo yazmak,
    /// hiç senaryo yazmamaktan iyi — iddia doğrulama zaten desteksiz
    /// olanı işaretleyecek. Sıfır kaynakla devam etmenin ise anlamı yok.
    public bool HasUsableResult => SourcesFound > 0;

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture,
            $"{Steps}/{MaxSteps} adım, {SourcesFound}/{TargetSources} kaynak, durum: {Stop}");
}

public enum ResearchStop
{
    Running = 0,

    /// Hedef kaynak sayısına ulaşıldı — istenen bitiş.
    TargetReached = 1,

    /// Adım bütçesi doldu. Kaynak sayısı hedefin altındaysa araştırma
    /// yetersiz demektir.
    StepsExhausted = 2,

    /// Plandaki bütün sorgular denendi, hedefe ulaşılamadı.
    QueriesExhausted = 3,
}
