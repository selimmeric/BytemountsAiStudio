using BytemountsAiStudio.Persistence;

namespace BytemountsAiStudio.Persistence.Tests;

/// Graf sürümleme kararının testleri.
///
/// VERİTABANI GEREKTİRMİYOR — ve bu bilinçli. İlk denemede aynı
/// davranış veritabanına bağlı bir testle sınandı; test paylaşılan
/// tohum verisini bozup komşu testi düşürdü ve CI kırmızı yandı.
/// Karar saf bir fonksiyon olduğu için testi de saf olmalıydı.
public sealed class SeederVersioningTests
{
    [Fact]
    public void SurumYoksa_YeniSurumGerekir()
    {
        Assert.True(DatabaseSeeder.NeedsNewVersion(null, DatabaseSeeder.FakeGraphJson));
    }

    [Fact]
    public void AyniGraf_YeniSurumGerektirmez()
    {
        Assert.False(DatabaseSeeder.NeedsNewVersion(
            DatabaseSeeder.FakeGraphJson, DatabaseSeeder.FakeGraphJson));
    }

    /// Koddaki grafı değiştirmek MEVCUT bir veritabanında bir şey
    /// yapmalı. Yapmazsa CI (boş veritabanı) yeşil yanar, geliştirme
    /// makinesi (tohumlanmış veritabanı) eski grafla koşar ve fark
    /// hiçbir yerde görünmez.
    [Fact]
    public void FarkliGraf_YeniSurumGerektirir()
    {
        Assert.True(DatabaseSeeder.NeedsNewVersion(
            """{ "nodes": [] }""", DatabaseSeeder.FakeGraphJson));
    }

    /// Aynı graf Windows ve Linux'ta farklı sayılmamalı, yoksa her
    /// makinede bir sürüm daha eklenir ve sürüm numarası anlamını
    /// yitirir.
    [Fact]
    public void SatirSonuFarki_YeniSurumGerektirmez()
    {
        // Önce LF'e indirgeniyor: kaynak dosya zaten CRLF ile
        // saklanıyorsa doğrudan çevirmek çift taşıma karakteri üretir.
        var lf = DatabaseSeeder.FakeGraphJson.Replace("\r\n", "\n", StringComparison.Ordinal);
        var crlf = lf.Replace("\n", "\r\n", StringComparison.Ordinal);

        Assert.False(DatabaseSeeder.NeedsNewVersion(crlf, DatabaseSeeder.FakeGraphJson));
        Assert.False(DatabaseSeeder.NeedsNewVersion(lf, DatabaseSeeder.FakeGraphJson));
    }

    [Fact]
    public void BastaSondaBosluk_YeniSurumGerektirmez()
    {
        Assert.False(DatabaseSeeder.NeedsNewVersion(
            "\n\n  " + DatabaseSeeder.FakeGraphJson + "  \n", DatabaseSeeder.FakeGraphJson));
    }

    /// Tek bir node eklemek sürüm gerektirmeli — asıl kullanım durumu.
    [Fact]
    public void TekNodeFarki_YakalanIr()
    {
        var changed = DatabaseSeeder.FakeGraphJson.Replace(
            "seo.generate", "baska.node.tipi", StringComparison.Ordinal);

        Assert.NotEqual(DatabaseSeeder.FakeGraphJson, changed);
        Assert.True(DatabaseSeeder.NeedsNewVersion(changed, DatabaseSeeder.FakeGraphJson));
    }
}
