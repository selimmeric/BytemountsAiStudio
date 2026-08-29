using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Execution;

namespace BytemountsAiStudio.Core.Tests;

/// Koda gömülü kalmış parametrelerin ayarlanabilirliği.
///
/// ***BU DOSYANIN VAR OLMA SEBEBİ:*** sistemin bir yarısı ayarlanabilir,
/// diğer yarısı sabitti — ve sabit kalan yarı, ayarlanabilir olanı
/// işlevsiz kılıyordu:
///
///   - Ağırlıklar kanal ayarında, EŞİKLER koda gömülü. Ağırlığı kaydıran
///     kanalda skor dağılımı da kayıyor ve 65 eşiği başka bir anlama
///     geliyor: havuz ya hiçbir konuyu kabul ediyor ya hepsini.
///   - Ollama'nın adresi ve MODEL ADLARI okunuyor, ZAMAN AŞIMI
///     okunmuyordu. Katalogun kendi notu 14B model açılabileceğini
///     söylüyor; 14B model ilk çağrıda beş dakikadan uzun sürede
///     yükleniyor ve her istek zaman aşımına uğruyordu.
public sealed class ParametricSettingsTests
{
    /* ---- konu eşikleri ---- */

    /// EŞİKLER KANAL AYARINDAN OKUNUYOR.
    [Fact]
    public void KonuEsikleri_KanaldanOkunuyor()
    {
        var settings = ChannelSettings.Parse("""
            {"topic_thresholds": {"accept": 80, "reject": 30, "similarity": 0.95, "risk_veto": 50}}
            """);

        Assert.Equal(80, settings.TopicThresholds.Accept);
        Assert.Equal(30, settings.TopicThresholds.Reject);
        Assert.Equal(0.95, settings.TopicThresholds.Similarity);
        Assert.Equal(50, settings.TopicThresholds.RiskVeto);
    }

    /// ***EŞİKLER KARARI GERÇEKTEN DEĞİŞTİRİYOR.***
    ///
    /// BU DOSYANIN EN ÖNEMLİ TESTİ. Okunup kullanılmasaydı ayar yine
    /// "çalışıyor" görünürdü — bu depoda defalarca yaşanan durum.
    [Fact]
    public void Esikler_KarariDegistiriyor()
    {
        var score = new TopicScore
        {
            Demand = 70, Fit = 70, Sourceability = 70,
            Visualizability = 70, Freshness = 70, Risk = 10,
        };

        // Varsayılan eşiklerle KABUL.
        Assert.Equal(TopicDecision.Accept, TopicPolicy.Decide(score));

        // Kabul eşiği yükseltilince aynı skor BEKLEMEYE düşüyor.
        Assert.Equal(
            TopicDecision.Hold,
            TopicPolicy.Decide(score, null, null, new TopicThresholds { Accept = 95 }));

        // Risk vetosu düşürülünce aynı skor REDDEDİLİYOR.
        Assert.Equal(
            TopicDecision.Reject,
            TopicPolicy.Decide(score, null, null, new TopicThresholds { RiskVeto = 5 }));
    }

    /// BENZERLİK EŞİĞİ DAR ALANLI KANAL İÇİN GEVŞETİLEBİLİYOR.
    ///
    /// Yalnızca tarih içeren bir kanalda gömme vektörleri doğal olarak
    /// birbirine yakın ve 0,88 farklı konuları "tekrar" sayabiliyor.
    [Fact]
    public void BenzerlikEsigi_Gevsetilebiliyor()
    {
        var score = new TopicScore
        {
            Demand = 70, Fit = 70, Sourceability = 70,
            Visualizability = 70, Freshness = 70, Risk = 10,
        };

        Assert.Equal(TopicDecision.Reject, TopicPolicy.Decide(score, 0.90));

        Assert.Equal(
            TopicDecision.Accept,
            TopicPolicy.Decide(score, 0.90, null, new TopicThresholds { Similarity = 0.95 }));
    }

    /// ***KABUL EŞİĞİ RED EŞİĞİNİN ALTINA İNEMİYOR.***
    ///
    /// İnseydi "beklet" aralığı ters çevrilir ve karar tablosu
    /// anlamsızlaşırdı: kabul edilen bir skor aynı anda reddedilmiş de
    /// olurdu.
    [Fact]
    public void TersEsik_VarsayilanaDusuyor()
    {
        var settings = ChannelSettings.Parse("""
            {"topic_thresholds": {"accept": 20, "reject": 60}}
            """);

        Assert.Equal(TopicPolicy.AcceptThreshold, settings.TopicThresholds.Accept);
        Assert.Equal(TopicPolicy.RejectThreshold, settings.TopicThresholds.Reject);
        Assert.Contains(settings.Warnings, w => w.Contains("topic_thresholds", StringComparison.Ordinal));
    }

    /// AYAR YOKSA ESKİ SABİT DEĞERLER.
    [Fact]
    public void AyarYok_EskiEsikler()
    {
        var thresholds = ChannelSettings.Parse("{}").TopicThresholds;

        Assert.Equal(65.0, thresholds.Accept);
        Assert.Equal(40.0, thresholds.Reject);
        Assert.Equal(0.88, thresholds.Similarity);
        Assert.Equal(70, thresholds.RiskVeto);
    }
}
