using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.TestSupport;

namespace BytemountsAiStudio.Worker.Tests;

/// Worker sağlık sinyali (P4-05).
///
/// NEDEN SÜREÇ CANLILIĞI YETMİYOR — bu depoda yaşandı. `restart:
/// unless-stopped` yalnızca ÇÖKEN kabı yeniden başlatıyor. Gerçekleşen
/// arıza ise şuydu: süreç ayaktaydı, bütün kuyruk döngüleri her turda
/// istisna atıyordu (EF yürütme stratejisi ile açık transaction
/// çakışması), saniyede bir hata satırı basılıyordu ve HİÇBİR VİDEO
/// ÜRETİLMİYORDU. Kap sağlıklı görünüyordu.
///
/// `QueueWorker` hatayı bilerek yutuyor — tek bir işin hatası o
/// kuyruğu durdurmamalı. Doğru karar; bedeli, dışarıdan bakan hiçbir
/// şeyin "bu döngü hiç iş bitiremiyor" diyememesiydi.
public sealed class WorkerHealthTests
{
    private static (WorkerHealth Health, FakeTimeProvider Time) Build()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-28T12:00:00Z", null));

        return (new WorkerHealth(time), time);
    }

    /// YENİ WORKER SAĞLIKLI: hiçbir şey yapmamış olmak hastalık değil.
    [Fact]
    public void BaslangicDurumu_Saglikli()
        => Assert.True(Build().Health.IsHealthy);

    /// TEK BİR HATA HASTALIK DEĞİL.
    ///
    /// Geçici veritabanı hatası, kilit çakışması ve ağ kesintisi
    /// normal. İlk hatada kabı öldürmek, sistemi her aksaklıkta
    /// yeniden başlatan bir şeye çevirirdi.
    [Fact]
    public void TekHata_HalaSaglikli()
    {
        var (health, _) = Build();

        health.RecordFailure(QueueClass.Llm);

        Assert.True(health.IsHealthy);
    }

    /// ARALIKSIZ DÜŞEN DÖNGÜ HASTALIK — bugünkü arızanın tam şekli.
    [Fact]
    public void AraliksizDusenDongu_Sagliksiz()
    {
        var (health, time) = Build();

        health.RecordFailure(QueueClass.Llm);
        time.Advance(WorkerHealth.FailureWindow + TimeSpan.FromSeconds(1));

        Assert.False(health.IsHealthy);

        // HANGİ KUYRUK VE NE KADAR SÜREDİR: "sağlıksız" tek başına
        // nereye bakılacağını söylemiyor ve kap yeniden başladıktan
        // sonra geriye kalan tek ipucu bu olabilir.
        var failing = health.Failing();

        Assert.Single(failing);
        Assert.Equal(QueueClass.Llm, failing[0].Queue);
        Assert.True(failing[0].For >= WorkerHealth.FailureWindow);
    }

    /// TOPARLAYAN DÖNGÜ SAĞLIKLI.
    ///
    /// Bir turda düşüp sonrakinde çalışan bir döngü sorunsuz: geçici
    /// hata beklenen şey. Sayaç sıfırlanmasaydı, günde bir kez hata
    /// alan bir worker sonsuza kadar hasta sayılırdı.
    [Fact]
    public void ToparlayanDongu_Saglikli()
    {
        var (health, time) = Build();

        health.RecordFailure(QueueClass.Render);
        time.Advance(WorkerHealth.FailureWindow + TimeSpan.FromSeconds(30));
        health.RecordSuccess(QueueClass.Render);

        Assert.True(health.IsHealthy);
        Assert.Empty(health.Failing());
    }

    /// BAŞARI "İŞ BULDU" DEMEK DEĞİL.
    ///
    /// Kuyruğu boş bir worker hiç iş yapmıyor ve tamamen sağlıklı.
    /// Ölçülen şey döngünün ÇALIŞABİLİYOR olması; "üretim var mı"
    /// sorusu başka bir ekranın işi (gece raporu).
    [Fact]
    public void BosKuyruk_Saglikli()
    {
        var (health, time) = Build();

        for (var i = 0; i < 50; i++)
        {
            health.RecordSuccess(QueueClass.Llm);
            time.Advance(TimeSpan.FromSeconds(10));
        }

        Assert.True(health.IsHealthy);
    }

    /// İLK HATANIN ZAMANI SAKLANIYOR, SAYISI DEĞİL.
    ///
    /// Hızlı dönen bir döngü dakikada yüzlerce hata üretir, yavaş
    /// dönen biri üç tane. Sayıya bakan bir eşik, döngü hızına göre
    /// farklı davranırdı — ve render döngüsü ile LLM döngüsü aynı
    /// hızda dönmüyor.
    [Fact]
    public void ArdisikHatalar_IlkZamandanSayiliyor()
    {
        var (health, time) = Build();

        health.RecordFailure(QueueClass.Render);

        for (var i = 0; i < 20; i++)
        {
            time.Advance(TimeSpan.FromSeconds(5));
            health.RecordFailure(QueueClass.Render);
        }

        // 100 saniye geçti, eşik 60: hasta.
        Assert.False(health.IsHealthy);
        Assert.True(health.Failing()[0].For >= TimeSpan.FromSeconds(100));
    }

    /// BİR KUYRUĞUN HASTALIĞI DİĞERİNİ ETKİLEMİYOR.
    ///
    /// Render döngüsü ffmpeg eksikliğinden düşerken LLM döngüsü
    /// çalışabiliyor. Kap düzeyinde sonuç yine "sağlıksız" ama
    /// KAYITTA hangisinin düştüğü yazılı olmalı: ikisini tek bayrakta
    /// toplamak, teşhisi kaybetmekti.
    [Fact]
    public void KuyruklarBagimsiz_AmaRaporTekil()
    {
        var (health, time) = Build();

        health.RecordFailure(QueueClass.Render);
        time.Advance(WorkerHealth.FailureWindow + TimeSpan.FromSeconds(1));
        health.RecordSuccess(QueueClass.Llm);

        Assert.False(health.IsHealthy);
        Assert.Equal([QueueClass.Render], health.Failing().Select(f => f.Queue));
    }

    /// KENDİNİ KAPATMA EŞİĞİ, RAPORLAMA EŞİĞİNDEN UZUN.
    ///
    /// Sıra kasıtlı: önce bildir, sonra harekete geç. Eşitseler,
    /// geçici bir aksaklıkta iş yapan bir süreç durup dururken
    /// öldürülürdü — ve devam eden bir render çöpe giderdi.
    [Fact]
    public void KapanmaEsigi_RaporlamaEsigindenUzun()
        => Assert.True(SelfRestartService.Threshold > WorkerHealth.FailureWindow);
}
