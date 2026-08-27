using System.Text.Json;
using BytemountsAiStudio.Contracts.Providers;

namespace BytemountsAiStudio.Contracts.Tests;

/// Sağlayıcı kataloğunun testleri.
///
/// Katalog veri olduğu için hatası derleme zamanında yakalanmaz — bir
/// yönlendirme satırının olmayan bir sağlayıcıya işaret etmesi, ancak o rol
/// çağrıldığında ortaya çıkardı. Bu testler o hatayı öne çekiyor.
public sealed class ProviderCatalogTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"bmai-catalog-{Guid.NewGuid():N}.json");

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private string Write(object catalog)
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(catalog, WriteOptions));
        return _path;
    }

    private static object Provider(string key, string role, bool enabled = true,
        bool requiresKey = false, string? keyEnv = null)
        => new
        {
            key,
            role,
            display_name = key,
            enabled,
            requires_key = requiresKey,
            key_env = keyEnv,
        };

    [Fact]
    public void GecerliKatalog_Okunur()
    {
        var path = Write(new
        {
            schema_version = 1,
            providers = new[] { Provider("ollama", "llm") },
            routing = new Dictionary<string, string[]> { ["llm"] = ["ollama"] },
        });

        var catalog = ProviderCatalog.Load(path);

        Assert.True(catalog.IsSuccess, catalog.IsFailure ? catalog.Error.Message : string.Empty);
        Assert.Single(catalog.Value.For("llm"));
    }

    [Fact]
    public void OlmayanSaglayiciyaYonlendirme_Reddedilir()
    {
        // Çalışma zamanında "sağlayıcı bulunamadı" hatası verirdi.
        var path = Write(new
        {
            schema_version = 1,
            providers = new[] { Provider("ollama", "llm") },
            routing = new Dictionary<string, string[]> { ["llm"] = ["hicboyle-bir-saglayici"] },
        });

        var catalog = ProviderCatalog.Load(path);

        Assert.True(catalog.IsFailure);
        Assert.Contains("hicboyle", catalog.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void KapaliSaglayiciYonlendirmede_Reddedilir()
    {
        var path = Write(new
        {
            schema_version = 1,
            providers = new[] { Provider("kapali", "llm", enabled: false) },
            routing = new Dictionary<string, string[]> { ["llm"] = ["kapali"] },
        });

        Assert.True(ProviderCatalog.Load(path).IsFailure);
    }

    [Fact]
    public void AnahtarsizYonlendirmedeAnahtarIsteyenSaglayici_Reddedilir()
    {
        // Anahtar isteyen bir sağlayıcıyı yönlendirmeye koymak, ilk çağrıda
        // beklenmedik bir kimlik hatası demek. Kayıt anında yakalanıyor.
        var path = Write(new
        {
            schema_version = 1,
            providers = new[]
            {
                Provider("openai", "llm", requiresKey: true, keyEnv: "BMAI_TEST_YOK_BOYLE_ANAHTAR"),
            },
            routing = new Dictionary<string, string[]> { ["llm"] = ["openai"] },
        });

        var catalog = ProviderCatalog.Load(path);

        Assert.True(catalog.IsFailure);
        Assert.Contains("BMAI_TEST_YOK_BOYLE_ANAHTAR", catalog.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TekrarlananAnahtar_Reddedilir()
    {
        var path = Write(new
        {
            schema_version = 1,
            providers = new[] { Provider("ayni", "llm"), Provider("ayni", "tts") },
            routing = new Dictionary<string, string[]>(),
        });

        Assert.True(ProviderCatalog.Load(path).IsFailure);
    }

    [Fact]
    public void AnahtarsizVeAnahtarBekleyen_Ayrilir()
    {
        var path = Write(new
        {
            schema_version = 1,
            providers = new[]
            {
                Provider("ollama", "llm"),
                Provider("openai", "llm", enabled: false, requiresKey: true, keyEnv: "OPENAI_API_KEY"),
            },
            routing = new Dictionary<string, string[]> { ["llm"] = ["ollama"] },
        });

        var catalog = ProviderCatalog.Load(path).Value;

        Assert.Single(catalog.KeyFree());
        Assert.Single(catalog.AwaitingKeys());
    }

    [Fact]
    public void OlmayanDosya_AcikHataVerir()
    {
        var result = ProviderCatalog.Load(Path.Combine(Path.GetTempPath(), "yok-boyle.json"));

        Assert.True(result.IsFailure);
        Assert.Equal("catalog.missing", result.Error.Code);
    }

    /// Depodaki gerçek katalog geçerli olmalı; değilse sistem hiç açılmaz.
    [Fact]
    public void DepodakiKatalog_Gecerlidir()
    {
        var path = FindRepositoryFile("config/providers.json");

        if (path is null)
        {
            // Test tek başına (paket olarak) koşuyorsa depo yapısı yok.
            return;
        }

        var result = ProviderCatalog.Load(path);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);

        var catalog = result.Value;

        // En azından LLM, arama, görsel ve ses rollerinde anahtarsız bir
        // sağlayıcı olmalı — sistemin anahtarsız çalışabilmesinin şartı.
        foreach (var role in new[] { "llm", "search", "image.generative", "tts" })
        {
            Assert.NotEmpty(catalog.For(role));
        }
    }

    private static string? FindRepositoryFile(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relative);

            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (IOException)
        {
            // Geçici dosya silinemezse test sonucunu etkilemez.
        }
    }
}
