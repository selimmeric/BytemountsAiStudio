using BytemountsAiStudio.Core.Content;

namespace BytemountsAiStudio.Core.Tests;

/// Havuz doldurma kararının testleri (P2-01).
///
/// Kabul kriteri: **havuz hiç boşalmıyor; içerik koşusu konu
/// beklemiyor.** Karar saf olmak zorunda — havuz gerçekten boşalarak
/// öğrenilecek bir şey değil, çünkü boşaldığı an zaten geç kalınmış
/// an.
public sealed class TopicPoolPolicyTests
{
    [Fact]
    public void DoluHavuz_UretimTetiklemiyor()
    {
        var plan = TopicPoolPolicy.Decide(new PoolStatus(Ready: 20, Producing: 0, DailyTarget: 3));

        Assert.False(plan.ShouldRefill);
    }

    [Fact]
    public void EsikAlti_UretimTetikliyor()
    {
        // Günde 3 video, elde 3 konu = 1 günlük stok < 2 gün eşiği.
        var plan = TopicPoolPolicy.Decide(new PoolStatus(Ready: 3, Producing: 0, DailyTarget: 3));

        Assert.True(plan.ShouldRefill);

        // 5 güne çıkarılıyor: 15 hedef − 3 mevcut = 12.
        Assert.Equal(12, plan.Count);
    }

    /// EŞİK GÜN CİNSİNDEN, adet cinsinden değil.
    ///
    /// "En az 10 konu olsun" demek, günde bir video üreten kanalda on
    /// günlük stok, günde beş video üretende iki günlük stok demek —
    /// aynı sayı iki kanalda tamamen farklı anlamlar taşıyor.
    [Fact]
    public void Esik_GunCinsinden()
    {
        // Aynı adet (6 konu), farklı tempo.
        var slow = TopicPoolPolicy.Decide(new PoolStatus(Ready: 6, Producing: 0, DailyTarget: 1));
        var fast = TopicPoolPolicy.Decide(new PoolStatus(Ready: 6, Producing: 0, DailyTarget: 5));

        // Yavaş kanalda 6 günlük stok — yeterli.
        Assert.False(slow.ShouldRefill);

        // Hızlı kanalda 1,2 günlük stok — yetersiz.
        Assert.True(fast.ShouldRefill);
    }

    /// ÜRETİLMEKTE OLANLAR DA SAYILIYOR.
    ///
    /// Yalnızca hazır olanlara bakmak, arka arkaya çalışan iki
    /// doldurma turunun aynı eksiği iki kez kapatması demekti:
    /// birincisi üretimi başlatıyor ama henüz hazır konu yok,
    /// ikincisi "hâlâ boş" deyip bir tur daha başlatıyor.
    [Fact]
    public void UretilmekteOlanlar_TekrarUretimiEngelliyor()
    {
        var plan = TopicPoolPolicy.Decide(new PoolStatus(Ready: 0, Producing: 12, DailyTarget: 3));

        Assert.False(plan.ShouldRefill);
    }

    /// YÜKSEK EŞİK düşükten belirgin biçimde uzak: yoksa her
    /// üretimden sonra havuz hemen tekrar eşiğin altına düşüyor ve
    /// sistem sürekli küçük partiler üretiyor — her parti bir LLM
    /// çağrısı ve her çağrının sabit maliyeti var.
    [Fact]
    public void DoldurmaSonrasi_HemenTekrarTetiklenmiyor()
    {
        var status = new PoolStatus(Ready: 3, Producing: 0, DailyTarget: 3);
        var plan = TopicPoolPolicy.Decide(status);

        // Doldurma tamamlandıktan sonraki hâl.
        var after = new PoolStatus(status.Ready + plan.Count, 0, status.DailyTarget);

        Assert.False(TopicPoolPolicy.Decide(after).ShouldRefill);
    }

    /// Hedefi olmayan kanal konu istemiyor: üretmek, hiç
    /// kullanılmayacak konular için para harcamaktı.
    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void HedefsizKanal_UretimYok(int target)
    {
        Assert.False(TopicPoolPolicy.Decide(new PoolStatus(0, 0, target)).ShouldRefill);
    }

    /// Yanlış yapılandırılmış bir günlük hedef tek çağrıda yüzlerce
    /// konu üretmeye çalışırdı.
    [Fact]
    public void ParticBoyutu_Sinirli()
    {
        var plan = TopicPoolPolicy.Decide(new PoolStatus(Ready: 0, Producing: 0, DailyTarget: 500));

        Assert.Equal(TopicPoolPolicy.MaxBatch, plan.Count);
    }

    /// Tamamen boş havuz her zaman üretim tetikliyor.
    [Fact]
    public void BosHavuz_UretimTetikliyor()
    {
        var plan = TopicPoolPolicy.Decide(new PoolStatus(0, 0, 1));

        Assert.True(plan.ShouldRefill);
        Assert.True(plan.Count >= 1);
    }

    /// AÇLIK doldurma eşiğinden AYRI raporlanıyor: eşiğin altına
    /// düşmek normal işleyiş (doldurma tetiklenir), tamamen boşalmak
    /// bir arıza. İkisini aynı sayıya bakmak, arızayı normal
    /// işleyişin içinde gizlerdi.
    [Fact]
    public void Aclik_DoldurmaEsigindenAyri()
    {
        // Eşiğin altında ama boş değil: normal işleyiş.
        Assert.False(TopicPoolPolicy.IsStarved(new PoolStatus(Ready: 2, Producing: 0, DailyTarget: 5)));

        // Tamamen boş: arıza. Üretim sürüyor olsa bile bir sonraki
        // koşu konu bekleyecek.
        Assert.True(TopicPoolPolicy.IsStarved(new PoolStatus(Ready: 0, Producing: 10, DailyTarget: 5)));
    }

    [Fact]
    public void Gerekce_StokMiktariniIceriyor()
    {
        var plan = TopicPoolPolicy.Decide(new PoolStatus(Ready: 3, Producing: 0, DailyTarget: 3));

        Assert.Contains("1", plan.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void GunlukStok_Hesaplaniyor()
    {
        Assert.Equal(4, new PoolStatus(Ready: 8, Producing: 4, DailyTarget: 3).DaysOfSupply, 3);
        Assert.Equal(double.MaxValue, new PoolStatus(5, 0, 0).DaysOfSupply);
    }
}
