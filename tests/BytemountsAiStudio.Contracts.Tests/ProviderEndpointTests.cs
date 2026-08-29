using BytemountsAiStudio.Contracts.Providers;
using Llm = BytemountsAiStudio.Providers.Llm;
using Open = BytemountsAiStudio.Providers.Open;

namespace BytemountsAiStudio.Contracts.Tests;

/// Katalogdaki adresler ile koddaki varsayılanların AYNI olması.
///
/// KATALOG BELGE DEĞİL, YAPILANDIRMA. `config/providers.json` bir
/// servisin adresini yazıyorsa, kod da o adresi kullanmak zorunda —
/// yoksa katalog süslü bir yorum satırı olur ve onu okuyup ayar yapan
/// kişi hiçbir şeyin değişmediğini ancak deneyerek anlar.
///
/// BU GERÇEKTEN OLDU: katalog `endpoint_env: BMAI_OLLAMA_URL` yazıyordu
/// ama `ProviderDescriptor`'da o alan hiç yoktu; `JsonSerializer`
/// tanımadığı alanı sessizce atıyordu.
public sealed class ProviderEndpointTests
{
    /// Katalog anahtarı → (koddaki varsayılan, koddaki ortam değişkeni).
    ///
    /// TABLO TESTTE, ÜRÜN KODUNDA DEĞİL: üçüncü bir "gerçeğin kaynağı"
    /// yaratmak, ikisi arasında da tutarsızlık üretirdi. Buradaki tek
    /// iş, iki tarafı karşı karşıya getirmek.
    private static readonly Dictionary<string, (string Endpoint, string Variable)> Wired =
        new(StringComparer.Ordinal)
        {
            ["gemini"] = (
                Llm.GeminiOptions.DefaultEndpoint.ToString(),
                Llm.GeminiOptions.EndpointVariable),

            ["openai"] = (
                Llm.OpenAiCompatibleOptions.OpenAiEndpoint,
                Llm.OpenAiCompatibleOptions.OpenAiEndpointVariable),

            ["openrouter"] = (
                Llm.OpenAiCompatibleOptions.OpenRouterEndpoint,
                Llm.OpenAiCompatibleOptions.OpenRouterEndpointVariable),

            ["ollama"] = (
                Llm.OllamaOptions.DefaultEndpoint.ToString(),
                Llm.OllamaOptions.EndpointVariable),

            ["wikidata"] = (
                Open.WikidataProvider.DefaultEndpoint.ToString(),
                Open.WikidataProvider.EndpointVariable),

            ["searxng"] = (
                Open.SearxngSearchProvider.DefaultEndpoint.ToString(),
                Open.SearxngSearchProvider.EndpointVariable),

            ["duckduckgo-ia"] = (
                Open.DuckDuckGoProvider.DefaultEndpoint.ToString(),
                Open.DuckDuckGoProvider.EndpointVariable),

            ["openverse"] = (
                Open.OpenverseImageProvider.DefaultEndpoint.ToString(),
                Open.OpenverseImageProvider.EndpointVariable),

            ["openverse-audio"] = (
                Open.OpenverseMusicProvider.DefaultEndpoint.ToString(),
                Open.OpenverseMusicProvider.EndpointVariable),

            ["pollinations"] = (
                Open.PollinationsImageProvider.DefaultEndpoint.ToString(),
                Open.PollinationsImageProvider.EndpointVariable),

            ["pexels"] = (
                Open.PexelsImageProvider.DefaultEndpoint.ToString(),
                Open.PexelsImageProvider.EndpointVariable),

            ["elevenlabs"] = (
                Open.ElevenLabsOptions.DefaultEndpoint.ToString(),
                Open.ElevenLabsOptions.EndpointVariable),

            ["tools-sidecar"] = (
                Open.ToolsSidecarOptions.DefaultEndpoint.ToString(),
                Open.ToolsSidecarOptions.EndpointVariable),

            ["youtube"] = (
                Open.YouTubeOptions.DefaultEndpoint.ToString(),
                Open.YouTubeOptions.EndpointVariable),

            ["tiktok"] = (
                Open.TikTokOptions.DefaultEndpoint.ToString(),
                Open.TikTokOptions.EndpointVariable),

            ["instagram"] = (
                Open.InstagramOptions.DefaultEndpoint.ToString(),
                Open.InstagramOptions.EndpointVariable),

            ["wikipedia"] = (
                Open.WikipediaProvider.DefaultApiTemplate,
                Open.WikipediaProvider.ApiVariable),
        };

    private static ProviderCatalog Catalog()
    {
        var path = CatalogPath();
        var loaded = ProviderCatalog.Load(path);

        Assert.True(loaded.IsSuccess, loaded.IsFailure ? loaded.Error.Message : string.Empty);

        return loaded.Value;
    }

    /// Katalog dosyasını depo kökünden bulur.
    private static string CatalogPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "config", "providers.json");

            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("config/providers.json bulunamadı.");
    }

    /// İki adres AYNI mı — `Uri` normalleştirmesiyle.
    ///
    /// Dizge karşılaştırması `http://localhost:8888` ile
    /// `http://localhost:8888/` arasında fark görüyor; `Uri` görmüyor
    /// ve haklı olan `Uri`. Kataloğa sırf sondaki eğik çizgi için
    /// düzeltme yaptırmak, insanı biçime uydurmak olurdu.
    ///
    /// Şablonlar (`{language}` içerenler) `Uri` olamıyor; onlar dizge
    /// olarak karşılaştırılıyor.
    private static bool SameAddress(string? catalog, string code)
    {
        if (catalog is null)
        {
            return false;
        }

        if (Uri.TryCreate(catalog, UriKind.Absolute, out var left)
            && Uri.TryCreate(code, UriKind.Absolute, out var right))
        {
            return left == right;
        }

        return string.Equals(catalog, code, StringComparison.Ordinal);
    }

    /// KATALOGDAKİ ADRES İLE KODDAKİ VARSAYILAN AYNI.
    [Fact]
    public void KatalogAdresleri_KoddakiVarsayilanlaAyni()
    {
        var catalog = Catalog();
        var mismatches = new List<string>();

        foreach (var (key, (endpoint, _)) in Wired)
        {
            var descriptor = catalog.Providers.FirstOrDefault(p => p.Key == key);

            if (descriptor is null)
            {
                mismatches.Add($"'{key}' katalogda yok ama kodda bağlı.");
                continue;
            }

            if (!SameAddress(descriptor.Endpoint, endpoint))
            {
                mismatches.Add($"'{key}': katalog '{descriptor.Endpoint}', kod '{endpoint}'.");
            }
        }

        Assert.True(mismatches.Count == 0, string.Join(" | ", mismatches));
    }

    /// ORTAM DEĞİŞKENİ ADLARI DA AYNI.
    ///
    /// Adı farklı olan bir değişken, kataloğu okuyup ayar yapan kişinin
    /// hiçbir şeyi değiştirememesi demek — ve bu sessizce oluyor.
    [Fact]
    public void KatalogDegiskenleri_KoddakiyleAyni()
    {
        var catalog = Catalog();
        var mismatches = new List<string>();

        foreach (var (key, (_, variable)) in Wired)
        {
            var descriptor = catalog.Providers.FirstOrDefault(p => p.Key == key);

            if (descriptor is not null
                && !string.Equals(descriptor.EndpointEnv, variable, StringComparison.Ordinal))
            {
                mismatches.Add($"'{key}': katalog '{descriptor.EndpointEnv}', kod '{variable}'.");
            }
        }

        Assert.True(mismatches.Count == 0, string.Join(" | ", mismatches));
    }

    /// ADRESİ OLAN HER SAĞLAYICININ ORTAM DEĞİŞKENİ DE VAR.
    ///
    /// Değişkeni olmayan bir adres, "bu servisin yerini değiştiremezsin"
    /// demek — ve bu sistemin parametrik olma iddiasıyla çelişiyor.
    [Fact]
    public void AdresiOlanHerSaglayici_DegiskeniDeVar()
    {
        var missing = Catalog().Providers
            .Where(p => !string.IsNullOrWhiteSpace(p.Endpoint))
            .Where(p => string.IsNullOrWhiteSpace(p.EndpointEnv))
            .Select(p => p.Key)
            .ToList();

        Assert.True(missing.Count == 0, "Ortam değişkeni yok: " + string.Join(", ", missing));
    }

    /// DEĞİŞKEN ADLARI TEKİL DEĞİL — ve olmamalı.
    ///
    /// `tools-sidecar`, `piper` ve `whisperx` aynı yan-servisi
    /// kullanıyor; üçü için ayrı değişken tanımlamak, birini
    /// değiştirip diğerlerini unutmak demekti.
    [Fact]
    public void AyniServis_AyniDegisken()
    {
        var catalog = Catalog();

        var sidecar = catalog.Providers
            .Where(p => p.Key is "tools-sidecar" or "piper" or "whisperx")
            .Select(p => p.EndpointEnv)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.Single(sidecar);
    }

    /* ---- çözümleme davranışı ---- */

    /// ORTAM DEĞİŞKENİ VARSAYILANI EZİYOR.
    [Fact]
    public void OrtamDegiskeni_VarsayilaniEziyor()
        => Assert.Equal(
            new Uri("https://ornek.test/api/"),
            Endpoints.Resolve("BMAI_TEST_URL", "https://varsayilan.test/", _ => "https://ornek.test/api/"));

    /// TANIMSIZ DEĞİŞKENDE VARSAYILAN KULLANILIYOR.
    [Fact]
    public void TanimsizDegisken_Varsayilan()
        => Assert.Equal(
            new Uri("https://varsayilan.test/"),
            Endpoints.Resolve("BMAI_TEST_URL", "https://varsayilan.test/", _ => null));

    /// BOZUK ADRES SESSİZCE YOK SAYILMIYOR.
    ///
    /// `htp://` yazan biri, sistemin hâlâ varsayılana gittiğini fark
    /// etmezdi ve "ayarım neden çalışmıyor" sorusunun cevabı hiçbir
    /// yerde olmazdı.
    [Fact]
    public void BozukAdres_Patliyor()
        => Assert.Throws<ArgumentException>(
            () => Endpoints.Resolve("BMAI_TEST_URL", "https://varsayilan.test/", _ => "bu bir adres degil"));

    /// ŞABLONDA YER TUTUCU ZORUNLU.
    ///
    /// `{language}` düşen bir ayar bütün dilleri tek bir dile bağlardı:
    /// Türkçe kanal İngilizce Wikipedia'dan okurdu ve bu sessizce
    /// olurdu.
    [Fact]
    public void SablondaYerTutucuYok_Patliyor()
        => Assert.Throws<ArgumentException>(
            () => Endpoints.ResolveTemplate(
                "BMAI_TEST_URL", "https://{language}.ornek.test/", "{language}",
                _ => "https://tr.ornek.test/"));

    [Fact]
    public void GecerliSablon_Kabul()
        => Assert.Equal(
            "https://{language}.ozel.test/",
            Endpoints.ResolveTemplate(
                "BMAI_TEST_URL", "https://{language}.ornek.test/", "{language}",
                _ => "https://{language}.ozel.test/"));
}
