using BytemountsAiStudio.Core.Execution;

namespace BytemountsAiStudio.Core.Tests;

/// Sürekli modun testleri (P2-12).
///
/// Tür karışımı gerekli çünkü tek türe kilitlenen kanal, o tür
/// tükendiğinde duruyor. Ama karışım rastgele de olamaz: "%60 liste,
/// %30 tarih, %10 gizem" dendiğinde gerçekten o oranda üretilmeli.
public sealed class ContinuousStrategyTests
{
    private static readonly ContentGenre[] Mix =
    [
        new("liste", 0.6),
        new("tarih", 0.3),
        new("gizem", 0.1),
    ];

    private static Dictionary<string, int> Produced(int liste = 0, int tarih = 0, int gizem = 0)
        => new(StringComparer.Ordinal) { ["liste"] = liste, ["tarih"] = tarih, ["gizem"] = gizem };

    /// İlk video: en büyük paylı tür. Rastgele seçmek, aynı
    /// yapılandırmanın iki koşuda farklı başlaması demekti ve bir
    /// sorunun tekrarlanabilirliğini bozardı.
    [Fact]
    public void IlkVideo_EnBuyukPay()
    {
        Assert.Equal("liste", ContinuousStrategy.Next(Mix, Produced()));
    }

    /// EN ÇOK GERİDE KALAN seçiliyor.
    [Fact]
    public void EnCokGerideKalan_Seciliyor()
    {
        // 10 videonun 10'u liste: tarih ve gizem çok geride.
        Assert.Equal("tarih", ContinuousStrategy.Next(Mix, Produced(liste: 10)));
    }

    /// Uzun koşuda oran GERÇEKTEN tutuyor.
    ///
    /// Rastgele seçim (paya göre zar atmak) uzun vadede doğru orana
    /// yakınsıyor ama kısa vadede sapıyor — günde beş video üreten bir
    /// kanalda "uzun vade" haftalar demek.
    [Fact]
    public void YirmiVideo_OranTutuyor()
    {
        var produced = Produced();

        for (var i = 0; i < 20; i++)
        {
            var next = ContinuousStrategy.Next(Mix, produced);

            Assert.NotNull(next);
            produced[next]++;
        }

        // %60/%30/%10 → 12/6/2. En fazla bir video sapma kabul
        // edilebilir.
        Assert.InRange(produced["liste"], 11, 13);
        Assert.InRange(produced["tarih"], 5, 7);
        Assert.InRange(produced["gizem"], 1, 3);
        Assert.True(ContinuousStrategy.LargestDrift(Mix, produced) < 6);
    }

    /// Karar KARARLI: aynı durum iki kez farklı tür üretirse "neden bu
    /// sırayla üretildi" sorusu cevapsız kalır.
    [Fact]
    public void Karar_Kararli()
    {
        var produced = Produced(liste: 3, tarih: 3, gizem: 3);
        var first = ContinuousStrategy.Next(Mix, produced);

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(first, ContinuousStrategy.Next(Mix, produced));
        }
    }

    /// Toplam 1 olmak ZORUNDA DEĞİL: "%60, %30, %10" ile "6, 3, 1"
    /// aynı anlama gelmeli.
    [Fact]
    public void Paylar_NormallesiyOr()
    {
        var raw = new ContentGenre[] { new("a", 6), new("b", 3), new("c", 1) };

        var normalized = ContinuousStrategy.Normalize(raw);

        Assert.Equal(1.0, normalized.Sum(g => g.Share), 6);
        Assert.Equal(0.6, normalized.Single(g => g.Name == "a").Share, 6);
    }

    /// Payı sıfır olan tür hiç üretilmiyor: bir türü kapatmanın yolu.
    [Fact]
    public void SifirPay_Uretilmiyor()
    {
        var mix = new ContentGenre[] { new("acik", 1), new("kapali", 0) };

        var produced = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i < 5; i++)
        {
            var next = ContinuousStrategy.Next(mix, produced);
            Assert.Equal("acik", next);
            produced[next!] = produced.TryGetValue(next!, out var count) ? count + 1 : 1;
        }
    }

    [Fact]
    public void HicTurYok_NullDonuyor()
    {
        Assert.Null(ContinuousStrategy.Next([], Produced()));
        Assert.Null(ContinuousStrategy.Next([new("x", 0)], Produced()));
    }

    /// NEGATİF OLAMIYOR: hedefin üstüne çıkmış bir kanalda "eksi iki
    /// video" anlamsız ve o sayı bir döngüde geriye sayarsa sonsuza
    /// kadar üretim tetikler.
    [Theory]
    [InlineData(5, 0, 5)]
    [InlineData(5, 3, 2)]
    [InlineData(5, 5, 0)]
    [InlineData(5, 9, 0)]
    [InlineData(-3, 0, 0)]
    public void KalanHedef_NegatifOlmuyor(int target, int produced, int expected)
    {
        Assert.Equal(expected, ContinuousStrategy.Remaining(target, produced));
    }

    /// Sapma panelde görünmeli: büyükse ya hedefler yeni değişmiş ya
    /// da bir tür sürekli üretilemiyor (konu havuzu boş olabilir) ve
    /// ikincisi sessizce olabilecek bir arıza.
    [Fact]
    public void Sapma_Olculebiliyor()
    {
        Assert.Equal(0, ContinuousStrategy.LargestDrift(Mix, Produced()));

        // Hepsi liste: gizem %10 hedefliyken %0 üretilmiş.
        Assert.True(ContinuousStrategy.LargestDrift(Mix, Produced(liste: 10)) > 30);
    }
}
