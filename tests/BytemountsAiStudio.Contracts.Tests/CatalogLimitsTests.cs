using BytemountsAiStudio.Contracts.Providers;

namespace BytemountsAiStudio.Contracts.Tests;

/// Katalogdaki sınırların gerçekten UYGULANDIĞININ sınanması (P0-17).
///
/// ***BU DOSYANIN VAR OLMA SEBEBİ:*** `config/providers.json` içindeki
/// `limits` bloğu okunuyordu ve hiçbir yere gitmiyordu. Katalogda "bu
/// servis dakikada 10 istek kaldırır" yazmak hiçbir şey yapmıyordu:
/// istekler doğrudan çıkıyor, sağlayıcı 429 dönünce ancak kuyruk geri
/// çekilmesi devreye giriyordu. Sınır yazıp güvende sanmak, gerçekte
/// sınırsız istek demekti.
///
/// Bu, düzeltilen `endpoint_env` hatasının aynısı: katalogdaki değer
/// sessizce atılıyor ve bunu ancak sağlayıcı hesabı kestiğinde fark
/// ediyorsun.
public sealed class CatalogLimitsTests
{
    private static ProviderCatalog Catalog()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "config", "providers.json");

            if (File.Exists(candidate))
            {
                var loaded = ProviderCatalog.Load(candidate);

                Assert.True(loaded.IsSuccess, loaded.IsFailure ? loaded.Error.Message : string.Empty);

                return loaded.Value;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("config/providers.json bulunamadı.");
    }

    /// ***KATALOGDAKİ HER SINIR BİR POLİTİKAYA DÖNÜŞÜYOR.***
    ///
    /// BU DOSYANIN EN ÖNEMLİ TESTİ. Katalogda sınırı yazılı olup
    /// politikaya dönüşmeyen bir sağlayıcı, sınırsız koşuyor demek.
    [Theory]
    [InlineData("pollinations-text", 10)]
    [InlineData("openai", 500)]
    [InlineData("openrouter", 200)]
    [InlineData("gemini", 15)]
    [InlineData("elevenlabs", 120)]
    public void DakikalikSinir_PolitikayaDonusuyor(string key, int permits)
    {
        var policies = Catalog().RateLimitPolicies();

        Assert.True(policies.TryGetValue(key, out var policy), $"'{key}' için politika yok.");
        Assert.Equal(permits, policy.PermitsPerWindow);
        Assert.Equal(TimeSpan.FromMinutes(1), policy.Window);
    }

    /// ***DÖRT PENCERE DE OKUNUYOR.***
    ///
    /// Yalnızca dakikayı okumak, üçünü sessizce atmak olurdu: Wikipedia
    /// saniye, Openverse saat, Brave ay bazında sınırlıyor ve üçü de
    /// katalogda yazılı.
    [Theory]
    [InlineData("wikipedia", 10, 1)]          // saniyede 10
    [InlineData("pexels", 200, 3600)]         // saatte 200
    [InlineData("brave-search", 2000, 2592000)] // ayda 2000
    public void FarkliPencereler_Okunuyor(string key, int permits, int windowSeconds)
    {
        var policies = Catalog().RateLimitPolicies();

        Assert.True(policies.TryGetValue(key, out var policy), $"'{key}' için politika yok.");
        Assert.Equal(permits, policy.PermitsPerWindow);
        Assert.Equal(TimeSpan.FromSeconds(windowSeconds), policy.Window);
    }

    /// SINIRSIZ SAĞLAYICI POLİTİKA ÜRETMİYOR.
    ///
    /// `requests_per_minute: null` yazan yerel servisler (Ollama,
    /// Piper, yan servis) için kova kurmak, olmayan bir sınırı
    /// uygulamak olurdu — yerel bir modeli dakikada 60 istekle
    /// boğazlamak, hiçbir şey kazandırmadan hattı yavaşlatırdı.
    [Theory]
    [InlineData("ollama")]
    [InlineData("piper")]
    [InlineData("tools-sidecar")]
    [InlineData("whisperx")]
    public void SinirsizSaglayici_PolitikaUretmiyor(string key)
        => Assert.False(Catalog().RateLimitPolicies().ContainsKey(key));

    /// ***YOUTUBE KOTASI KATALOGDAN OKUNUYOR.***
    ///
    /// Sayı kodda sabit olsaydı, Google kota artırımı verdiğinde
    /// (başvuruyla 10.000 → 1.000.000 mümkün) sistem yine günde altı
    /// videodan fazlasına izin vermezdi ve sebebi loglarda DOĞRU
    /// görünürdü: "kota tükendi". Değişmesi gereken tek şey katalog
    /// satırı olmalı.
    [Fact]
    public void YouTubeKotasi_KatalogdanOkunuyor()
    {
        var catalog = Catalog();

        Assert.Equal(10_000, catalog.Limit("youtube", "quota_units_per_day"));
        Assert.Equal(1_600, catalog.Limit("youtube", "quota_units_per_publish"));
    }

    /// TANIMSIZ SINIR NULL DÖNÜYOR, SIFIR DEĞİL.
    ///
    /// Sıfır dönmek "kota yok" ile "kota sıfır" arasındaki farkı yok
    /// ederdi: birincisinde varsayılana düşmek, ikincisinde hiç
    /// yayınlamamak doğru davranış.
    [Fact]
    public void TanimsizSinir_NullDonuyor()
    {
        var catalog = Catalog();

        Assert.Null(catalog.Limit("youtube", "boyle_bir_sinir_yok"));
        Assert.Null(catalog.Limit("boyle-bir-saglayici-yok", "quota_units_per_day"));

        // `requests_per_minute: null` yazan sağlayıcı da null dönüyor.
        Assert.Null(catalog.Limit("ollama", "requests_per_minute"));
    }

    /// SINIR GERÇEKTEN KOVAYA GİRİYOR.
    ///
    /// Politika üretmek yetmiyor: sınırlayıcı o politikayı KULLANMALI.
    /// İkisi arasındaki bağ kurulmasaydı politikalar sözlükte durur ve
    /// hiçbir istek sınırlanmazdı.
    [Fact]
    public async Task Politika_SinirlayiciyaGiriyor()
    {
        var limiter = ResilienceSelection.RateLimiter(null, Catalog().RateLimitPolicies());

        // Pollinations dakikada 10: onuncuya kadar geçiyor,
        // on birincisi ERTELENİYOR.
        for (var i = 0; i < 10; i++)
        {
            var permit = await limiter.AcquireAsync("pollinations-text", 1, CancellationToken.None);

            Assert.True(permit.IsSuccess, $"{i}. istek reddedildi.");
        }

        var refused = await limiter.AcquireAsync("pollinations-text", 1, CancellationToken.None);

        Assert.True(refused.IsFailure);

        // KAYNAK, HATA DEĞİL: iş düşmüyor, ertleniyor. Kalıcı olsaydı
        // sınıra takılan her video çöpe giderdi.
        Assert.Equal(Core.Errors.ErrorKind.Resource, refused.Error.Kind);
    }
}
