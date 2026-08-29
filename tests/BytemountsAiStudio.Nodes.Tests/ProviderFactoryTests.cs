using System.Text.Json;
using BytemountsAiStudio.Contracts.Providers;

namespace BytemountsAiStudio.Nodes.Tests;

/// Katalogdaki TARİFİN nesneye dönüşmesi (ADR-015).
///
/// ***BU DOSYANIN VAR OLMA SEBEBİ:*** `ProviderCatalog`'un kendi yorumu
/// *"nesne üretimi `ProviderFactory`'de kalıyor"* diyordu ve **öyle bir
/// sınıf hiç yazılmamıştı**. `routing` bloğu hiçbir kurulum kodundan
/// okunmuyordu — yalnızca `bmai providers` ekranı onu **gösteriyordu**.
///
/// Sonucu ADR-015'in merkezî iddiasının yanlış olmasıydı:
/// *"anahtar geldiğinde yapılacak tek şey `enabled` alanını açıp
/// yönlendirme listesinin başına almak"* — gerçekte gereken şey bir
/// **kod** değişikliğiydi.
///
/// Ölçülebilir bedeli: **beş adaptör hiçbir yerden kurulmuyordu**
/// (Searxng, DuckDuckGo, Gemini, ElevenLabs, Pexels). Hepsi yazılmış,
/// testlenmiş ve erişilemezdi.
public sealed class ProviderFactoryTests
{
    private static readonly JsonSerializerOptions Write = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static ProviderCatalog Catalog(object value)
    {
        var path = Path.Combine(Path.GetTempPath(), $"bmai-fabrika-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(value, Write));

            var loaded = ProviderCatalog.Load(path);

            Assert.True(loaded.IsSuccess, loaded.IsFailure ? loaded.Error.Message : string.Empty);

            return loaded.Value;
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// `display_name` ZORUNLU: katalog şeması onu istiyor. Testin
    /// kendi sahte kataloğu, gerçek şemayı sağlamalı — yoksa test
    /// gerçekte var olamayacak bir yapılandırmayı sınardı.
    private static object Provider(
        string key, string role, bool enabled = true, bool requiresKey = false, string? keyEnv = null)
        => new
        {
            key,
            role,
            display_name = key,
            enabled,
            requires_key = requiresKey,
            key_env = keyEnv,
        };

    private static ProviderFactory Factory(
        ProviderCatalog catalog, List<string>? warnings = null, ICredentialSource? credentials = null)
        => new(new HttpClient(), catalog, credentials, (warnings ?? []).Add);

    /* ---- yönlendirme sırası ---- */

    /// ***SIRA KATALOGDAN GELİYOR.***
    ///
    /// BU DOSYANIN EN ÖNEMLİ TESTİ. Gelmeseydi ADR-015'in iddiası
    /// yanlış kalırdı: sıra kodda sabit olurdu ve değiştirmek derleme
    /// gerektirirdi.
    [Fact]
    public void Sira_KatalogdanGeliyor()
    {
        var catalog = Catalog(new
        {
            schema_version = 1,
            providers = new[]
            {
                Provider("ollama", "llm"),
                Provider("pollinations-text", "llm"),
            },
            routing = new { llm = new[] { "pollinations-text", "ollama" } },
        });

        var chain = Factory(catalog).Llm();

        Assert.Equal(2, chain.Count);
        Assert.Equal("pollinations-text", chain[0].Key);
        Assert.Equal("ollama", chain[1].Key);
    }

    /// SIRA DEĞİŞİNCE ZİNCİR DE DEĞİŞİYOR — KOD DEĞİŞMEDEN.
    [Fact]
    public void SiraTersine_ZincirTersine()
    {
        var catalog = Catalog(new
        {
            schema_version = 1,
            providers = new[]
            {
                Provider("ollama", "llm"),
                Provider("pollinations-text", "llm"),
            },
            routing = new { llm = new[] { "ollama", "pollinations-text" } },
        });

        Assert.Equal("ollama", Factory(catalog).Llm()[0].Key);
    }

    /* ---- kullanılabilirlik ---- */

    /// ***KAPALI SAĞLAYICI ZİNCİRE GİRMİYOR.***
    ///
    /// `routing` ONU LİSTELEYEMİYOR ZATEN: katalog doğrulaması,
    /// yönlendirmenin kapalı ya da anahtarsız bir sağlayıcıya işaret
    /// etmesini reddediyor. Bu test rol yedeğinden gidiyor — orası
    /// doğrulamadan geçmiyor ve kendi süzgecine ihtiyacı var.
    [Fact]
    public void KapaliSaglayici_ZincireGirmiyor()
    {
        var catalog = Catalog(new
        {
            schema_version = 1,
            providers = new[]
            {
                Provider("ollama", "llm"),
                Provider("openai", "llm", enabled: false, requiresKey: true, keyEnv: "OPENAI_API_KEY"),
            },
            routing = new { llm = new[] { "ollama" } },
        });

        var chain = Factory(catalog).Llm();

        Assert.Single(chain);
        Assert.Equal("ollama", chain[0].Key);
    }

    /// ***ANAHTARI KAYBOLAN SAĞLAYICI SESSİZCE ATLANIYOR.***
    ///
    /// Katalog doğrulaması anahtarı YÜKLEME anında sınıyor; anahtar
    /// sonradan kaybolabiliyor (ortam değişkeni silinmiş bir kap,
    /// dönen bir jeton). Süzgeç bu yüzden ÇALIŞMA anında da gerekli.
    ///
    /// Uyarı üretilmiyor: bugünkü normal durum bu ve her açılışta
    /// gürültü yapmak, gerçek uyarıları görünmez kılardı.
    [Fact]
    public void AnahtariKaybolanSaglayici_Atlaniyor()
    {
        Environment.SetEnvironmentVariable("BMAI_TEST_PEXELS", "anahtar");

        ProviderCatalog catalog;

        try
        {
            catalog = Catalog(new
            {
                schema_version = 1,
                providers = new[]
                {
                    Provider("pexels", "image.stock", requiresKey: true, keyEnv: "BMAI_TEST_PEXELS"),
                    Provider("openverse", "image.stock"),
                },
                routing = new Dictionary<string, string[]>
                {
                    ["image.stock"] = ["pexels", "openverse"],
                },
            });
        }
        finally
        {
            // Anahtar KAYBOLDU: katalog yüklendi, ortam değişti.
            Environment.SetEnvironmentVariable("BMAI_TEST_PEXELS", null);
        }

        var warnings = new List<string>();
        var chain = Factory(catalog, warnings).StockImages();

        Assert.Single(chain);
        Assert.Equal("openverse", chain[0].Key);
        Assert.Empty(warnings);
    }

    /// ***ANAHTAR VARKEN SAĞLAYICI ZİNCİRE GİRİYOR — KOD DEĞİŞMEDEN.***
    ///
    /// ADR-015'in iddiası tam olarak bu. `PexelsImageProvider` yazılmış
    /// ve **hiçbir yerden kurulmuyordu**: anahtar gelse bile
    /// kullanılamazdı.
    [Fact]
    public void AnahtarVarken_ZincireGiriyor()
    {
        // Süreç ortamı DEĞİŞTİRİLİYOR ve hemen geri alınıyor:
        // `IsUsableNow` ortamı doğrudan okuyor, enjekte edilebilir bir
        // kaynağı yok. `finally` olmadan komşu testler kırılırdı.
        Environment.SetEnvironmentVariable("BMAI_TEST_PEXELS", "anahtar");

        try
        {
            var catalog = Catalog(new
            {
                schema_version = 1,
                providers = new[]
                {
                    Provider("pexels", "image.stock", requiresKey: true, keyEnv: "BMAI_TEST_PEXELS"),
                    Provider("openverse", "image.stock"),
                },
                routing = new Dictionary<string, string[]>
                {
                    ["image.stock"] = ["pexels", "openverse"],
                },
            });

            var chain = Factory(catalog).StockImages();

            Assert.Equal(2, chain.Count);
            Assert.Equal("pexels", chain[0].Key);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BMAI_TEST_PEXELS", null);
        }
    }

    /* ---- hata yolları ---- */

    /// ***KATALOGDA VAR, KODDA KARŞILIĞI YOK → UYARI.***
    ///
    /// Sessiz geçmek, `routing` içindeki bir yazım hatasının
    /// (`"pollinatons"`) o sağlayıcıyı kaybettirmesi ve kimsenin fark
    /// etmemesi demekti. Katalog veri olduğu için derleyici de
    /// yakalayamıyor.
    [Fact]
    public void TaninmayanAnahtar_UyariUretiyor()
    {
        // Katalogda TANIMLI ve acik, ama KODDA karsiligi yok: gercek
        // hayatta bu, katalog satirinin yazilip adaptorun yazilmamis
        // olmasi (ya da adin yanlis yazilmasi) demek.
        var catalog = Catalog(new
        {
            schema_version = 1,
            providers = new[] { Provider("pollinatons-text", "llm") },
            routing = new { llm = new[] { "pollinatons-text" } },
        });

        var warnings = new List<string>();

        Assert.Empty(Factory(catalog, warnings).Llm());
        Assert.Contains(warnings, w => w.Contains("pollinatons-text", StringComparison.Ordinal));
    }

    /// ***YÖNLENDİRME YOKSA ROLE DÜŞÜLÜYOR.***
    ///
    /// `routing` bloğunda karşılığı olmayan bir rol (bugün `music`
    /// böyle) aksi hâlde boş dönerdi ve o rolün bütün sağlayıcıları
    /// sessizce kaybolurdu — düzeltmeye çalıştığımız hatanın aynısı.
    [Fact]
    public void YonlendirmeYok_RoleDusuluyor()
    {
        var catalog = Catalog(new
        {
            schema_version = 1,
            providers = new[] { Provider("openverse-audio", "music") },
            routing = new Dictionary<string, string[]>(),
        });

        var music = Factory(catalog).Music();

        Assert.Single(music);
        Assert.Equal("openverse-audio", music[0].Key);
    }

    /* ---- gerçek katalog ---- */

    /// ***DEPODAKİ GERÇEK KATALOG BÜTÜN ROLLERİ DOLDURUYOR.***
    ///
    /// Bu test sahte bir katalogla değil, `config/providers.json` ile
    /// koşuyor: bir rol boş kalırsa hattın o adımı hiç çalışamaz ve
    /// bunu ancak koşu ortasında fark ederdik.
    [Theory]
    [InlineData(ProviderFactory.LlmRole)]
    [InlineData(ProviderFactory.SearchRole)]
    [InlineData(ProviderFactory.StockImageRole)]
    [InlineData(ProviderFactory.GenerativeImageRole)]
    [InlineData(ProviderFactory.TtsRole)]
    [InlineData(ProviderFactory.MusicRole)]
    public void GercekKatalog_RolleriDolduruyor(string role)
    {
        var warnings = new List<string>();

        Assert.NotEmpty(Factory(RealCatalog(), warnings).Describe(role));

        // TANINMAYAN ANAHTAR YOK: katalogdaki her kullanılabilir
        // sağlayıcının kodda karşılığı olmalı.
        Assert.Empty(warnings);
    }

    /// GERÇEK KATALOGDAN KURULAN ZİNCİRLER BOŞ DEĞİL.
    [Fact]
    public void GercekKatalog_ZincirleriKuruyor()
    {
        var factory = Factory(RealCatalog());

        Assert.NotEmpty(factory.Llm());
        Assert.NotEmpty(factory.Search());
        Assert.NotEmpty(factory.StockImages());
        Assert.NotEmpty(factory.GenerativeImages());
        Assert.NotEmpty(factory.Tts());
        Assert.NotEmpty(factory.Music());
    }

    /// YAYINCILAR BUGÜN BOŞ VE BU DOĞRU.
    ///
    /// Katalogda üçü de `enabled: false` (anahtar yok). Boş dönmesi
    /// bir hata değil, gerçeğin kendisi — kayıt tarafı sabit
    /// varsayılana düşüyor ve bunu söylüyor.
    [Fact]
    public void Yayincilar_AnahtarsizBos()
        => Assert.Empty(Factory(RealCatalog()).Publishers());

    private static ProviderCatalog RealCatalog()
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
}
