using BytemountsAiStudio.Core.Execution;

namespace BytemountsAiStudio.Core.Tests;

/// Birden fazla hesap arasında kota havuzu (P4-04).
///
/// YouTube günlük 10.000 birim veriyor ve bir yükleme 1.600 birim —
/// yani PROJE BAŞINA GÜNDE ALTI VİDEO. Faz 4'ün hedefi günde 100 video
/// ve tek proje bunun on altıda birini bile karşılamıyor. Ölçek sorunu
/// burada bir performans sorunu değil, bir MUHASEBE sorunu.
public sealed class QuotaPoolTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    private static QuotaAccountState Account(string name, int spent)
        => new(name, spent, QuotaLedger.DailyUnits);

    /* ---- seçim ---- */

    /// ***EN ÇOK KALANI SEÇİLİYOR.***
    ///
    /// BU DOSYANIN EN ÖNEMLİ TESTİ. Sırayla dağıtmak "adil" görünüyor
    /// ve kapasiteyi MAHSUR BIRAKIYOR: harcamayı yaymak, hiçbir
    /// hesapta büyük bir iş için yer bırakmamak demek.
    [Fact]
    public void EnCokKalan_Seciliyor()
    {
        var decision = QuotaPool.Select(
            [Account("a", 8_000), Account("b", 1_000), Account("c", 5_000)],
            QuotaLedger.UploadCost,
            Now);

        Assert.True(decision.Granted);
        Assert.Equal("b", decision.Account);
        Assert.Equal(9_000 - QuotaLedger.UploadCost, decision.RemainingAfter);
    }

    /// EŞİTLİKTE AD SIRASI — RASTGELE DEĞİL.
    ///
    /// Aynı girdiye farklı cevap vermek, bir hatayı yeniden
    /// üretilemez kılardı: "dün hangi hesaptan yükledi" sorusunun
    /// cevabı kayda bakmadan bilinemezdi.
    [Fact]
    public void Esitlikte_AdSirasi()
    {
        var first = QuotaPool.Select([Account("z", 0), Account("a", 0)], 1_600, Now);
        var second = QuotaPool.Select([Account("a", 0), Account("z", 0)], 1_600, Now);

        Assert.Equal("a", first.Account);
        Assert.Equal(first.Account, second.Account);
    }

    /// SIĞMAYAN HESAP SEÇİLMİYOR.
    ///
    /// Kısmi yükleme diye bir şey yok: yarım kotayla başlanan bir
    /// yükleme ortasında reddediliyor ve harcanan kısım geri gelmiyor.
    [Fact]
    public void Sigmayan_Secilmiyor()
    {
        var decision = QuotaPool.Select(
            [Account("dolu", 9_000), Account("bos", 8_500)],
            QuotaLedger.UploadCost,
            Now);

        // İkisinde de 1.600 yok (1.000 ve 1.500).
        Assert.Equal(PoolOutcome.Exhausted, decision.Outcome);
        Assert.Null(decision.Account);
    }

    /// ***PARÇALANMA GÖRÜNÜR: TOPLAM VAR AMA KAPASİTE SIFIR.***
    ///
    /// Üç hesapta 1.500'er birim "toplam 4.500" görünüyor ama
    /// 1.600'lük bir iş hiçbirine sığmıyor. Toplamı tek havuz gibi
    /// bölmek, olmayan bir kapasiteyi raporlamak olurdu.
    [Fact]
    public void Parcalanma_KapasiteSifir()
    {
        QuotaAccountState[] accounts =
        [
            Account("a", 8_500),
            Account("b", 8_500),
            Account("c", 8_500),
        ];

        Assert.Equal(4_500, accounts.Sum(a => a.Remaining));
        Assert.Equal(0, QuotaPool.Capacity(accounts, QuotaLedger.UploadCost));
        Assert.Equal(PoolOutcome.Exhausted, QuotaPool.Select(accounts, QuotaLedger.UploadCost, Now).Outcome);
    }

    /// TÜKENDİĞİNDE GEREKÇE SAYIYLA YAZILIYOR.
    ///
    /// "Kota bitti" tek başına kaç hesap eklemek gerektiğini
    /// söylemiyor.
    [Fact]
    public void Tukendiginde_GerekceSayili()
    {
        var decision = QuotaPool.Select([Account("a", 9_500)], QuotaLedger.UploadCost, Now);

        Assert.Contains("500", decision.Reason, StringComparison.Ordinal);
        Assert.Contains("1600", decision.Reason.Replace(".", string.Empty, StringComparison.Ordinal),
            StringComparison.Ordinal);
    }

    /// ***HESAP YOKLUĞU KOTA BİTMESİ DEĞİL.***
    ///
    /// İkisini aynı saymak, hiç yapılandırılmamış bir sistemin "yarın
    /// dolar" diye beklemesi ve hiç uyarmaması demekti — fabrika
    /// sessizce hiçbir şey yayınlamazdı.
    [Fact]
    public void HesapYok_KotaBitmesiDegil()
    {
        var decision = QuotaPool.Select([], QuotaLedger.UploadCost, Now);

        Assert.Equal(PoolOutcome.NoAccounts, decision.Outcome);
        Assert.NotEqual(PoolOutcome.Exhausted, decision.Outcome);
        Assert.Contains("hesap tanımlanmamış", decision.Reason, StringComparison.Ordinal);
    }

    /* ---- kapasite ---- */

    /// KAPASİTE HESAP BAŞINA BÖLÜNÜYOR.
    [Fact]
    public void Kapasite_HesapBasina()
    {
        // Her hesapta 10.000 -> 6 yayın (1.600 x 6 = 9.600).
        Assert.Equal(
            18,
            QuotaPool.Capacity(
                [Account("a", 0), Account("b", 0), Account("c", 0)],
                QuotaLedger.UploadCost));
    }

    /// FAZ 4 HEDEFİ İÇİN KAÇ HESAP GEREKTİĞİ ÖLÇÜLÜYOR.
    ///
    /// "Günde 100 video" hedefi tek projeyle imkânsız ve bu sayı
    /// tahmin değil, hesap: 100 / 6 = 17 proje.
    [Fact]
    public void YuzVideo_OnYediHesapIstiyor()
    {
        var perAccount = QuotaLedger.DailyUnits / QuotaLedger.UploadCost;

        Assert.Equal(6, perAccount);

        var needed = (int)Math.Ceiling(100.0 / perAccount);

        Assert.Equal(17, needed);

        var pool = Enumerable.Range(0, needed)
            .Select(i => Account($"proje-{i:D2}", 0))
            .ToList();

        Assert.True(QuotaPool.Capacity(pool, QuotaLedger.UploadCost) >= 100);
    }

    /// SIFIR MALİYET KAPASİTE ÜRETMİYOR.
    ///
    /// Sıfıra bölme yerine sıfır kapasite: "sonsuz yayın sığar"
    /// cevabı, yapılandırma hatasını gizlerdi.
    [Fact]
    public void SifirMaliyet_KapasiteSifir()
        => Assert.Equal(0, QuotaPool.Capacity([Account("a", 0)], 0));

    /* ---- gün anahtarı ---- */

    /// ***GÜN ANAHTARI PASİFİK TARİHİ.***
    ///
    /// YouTube kotayı Pasifik saatiyle gece yarısı sıfırlıyor.
    /// Anahtarı UTC tarihinden üretmek, günün yedi–sekiz saatinde
    /// YANLIŞ GÜNE yazmak demekti: sabaha karşı yapılan bir yükleme
    /// dünün kotasına düşer ve bugünün havuzu olduğundan dolu
    /// görünürdü.
    [Fact]
    public void GunAnahtari_PasifikTarihi()
    {
        // 29 Ağustos 03:00 UTC = 28 Ağustos 20:00 Pasifik.
        var key = QuotaPool.DayKey(new DateTimeOffset(2026, 8, 29, 3, 0, 0, TimeSpan.Zero));

        Assert.Equal("2026-08-28", key);
    }

    /// AYNI PASİFİK GÜNÜ AYNI ANAHTAR.
    [Fact]
    public void AyniPasifikGunu_AyniAnahtar()
    {
        var morning = QuotaPool.DayKey(new DateTimeOffset(2026, 8, 29, 16, 0, 0, TimeSpan.Zero));
        var evening = QuotaPool.DayKey(new DateTimeOffset(2026, 8, 30, 5, 0, 0, TimeSpan.Zero));

        // 29 Ağustos 09:00 ve 29 Ağustos 22:00 Pasifik — aynı gün.
        Assert.Equal(morning, evening);
    }

    /// GÜN ANAHTARI KOTA SIFIRLANMASIYLA TUTARLI.
    ///
    /// İki ayrı hesaplama olsaydı, sıfırlama anıyla anahtarın değişme
    /// anı ayrışır ve arada kalan işler ya iki kez sayılır ya hiç
    /// sayılmazdı.
    [Fact]
    public void GunAnahtari_SifirlanmaylaTutarli()
    {
        var now = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

        var before = QuotaPool.DayKey(now);
        var after = QuotaPool.DayKey(QuotaLedger.NextReset(now));

        Assert.NotEqual(before, after);
    }
}
