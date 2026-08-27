using BytemountsAiStudio.Persistence.Providers;

namespace BytemountsAiStudio.Persistence.Tests;

/// Kaynak güven skorunun testleri (P1-11).
///
/// Veritabanı gerektirmiyor — skor saf bir eşleme. Kaba ve BİLEREK
/// öyle: gerçek kalibrasyon performans verisiyle yapılacak (P5-04).
/// Şimdilik amaç ansiklopedi ile blog arasında bir sıralama olması;
/// hiç ayrım yapmamak, QC'nin kaynak kalitesine hiç bakamaması demekti.
public sealed class TrustScoreTests
{
    [Theory]
    [InlineData("academic", 0.95)]
    [InlineData("official", 0.90)]
    [InlineData("encyclopedia", 0.85)]
    [InlineData("news", 0.70)]
    [InlineData("community", 0.45)]
    [InlineData("blog", 0.35)]
    public void BilinenTurler_BeklenenSkor(string type, double expected)
    {
        Assert.Equal(expected, KnowledgeBase.TrustFor(type), 3);
    }

    [Fact]
    public void BuyukKucukHarf_Onemsiz()
    {
        Assert.Equal(KnowledgeBase.TrustFor("encyclopedia"), KnowledgeBase.TrustFor("Encyclopedia"), 3);
    }

    /// Bilinmeyen tür ORTA skor alıyor, sıfır değil. Sıfır vermek, tür
    /// tanınmadığı için kaynağı tamamen değersiz saymak olurdu.
    [Fact]
    public void BilinmeyenTur_OrtaSkor()
    {
        var score = KnowledgeBase.TrustFor("boyle-bir-tur-yok");

        Assert.InRange(score, 0.4, 0.6);
    }

    /// Sıralamanın kendisi, tek tek değerlerden daha önemli: değerler
    /// kalibre edilecek, sıralama kalacak.
    [Fact]
    public void Siralama_AnsiklopediBlogdanYuksek()
    {
        Assert.True(KnowledgeBase.TrustFor("academic") > KnowledgeBase.TrustFor("news"));
        Assert.True(KnowledgeBase.TrustFor("encyclopedia") > KnowledgeBase.TrustFor("community"));
        Assert.True(KnowledgeBase.TrustFor("community") > KnowledgeBase.TrustFor("blog"));
    }

    [Fact]
    public void TumSkorlar_SifirBirArasinda()
    {
        foreach (var type in new[] { "academic", "official", "encyclopedia", "news", "community", "blog", "?" })
        {
            Assert.InRange(KnowledgeBase.TrustFor(type), 0.0, 1.0);
        }
    }
}
