using BytemountsAiStudio.Contracts.Prompts;

namespace BytemountsAiStudio.Contracts.Tests;

/// İstem kayıt defterinin testleri (P1-07).
public sealed class PromptRegistryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"bmai-prompts-{Guid.NewGuid():N}");

    private string WritePrompt(string key, int version, string body, string? description = null)
    {
        var directory = Path.Combine(_root, key);
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, $"v{version}.md");
        var meta = description is null ? string.Empty : $"description: {description}\n";

        File.WriteAllText(path, $"---\nkey: {key}\nversion: {version}\n{meta}---\n\n{body}\n");

        return path;
    }

    private static Dictionary<string, string> Values(params (string Key, string Value)[] pairs)
        => pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    [Fact]
    public void SurumlerOkunur_VarsayilanEnYuksek()
    {
        WritePrompt("script.generate", 1, "# user\nbirinci");
        WritePrompt("script.generate", 3, "# user\nucuncu");
        WritePrompt("script.generate", 2, "# user\nikinci");

        var registry = PromptRegistry.Load(_root);

        Assert.True(registry.IsSuccess, registry.IsFailure ? registry.Error.Message : string.Empty);
        Assert.Equal(3, registry.Value.Get("script.generate").Value.Version);
        Assert.Equal("ikinci", registry.Value.Get("script.generate", 2).Value.User);
        Assert.Equal(3, registry.Value.Versions("script.generate").Count);
    }

    [Fact]
    public void SistemVeKullaniciBolumleri_Ayrilir()
    {
        WritePrompt("x", 1, "# system\nsen bir yazarsin\n\n# user\nsunu yaz");

        var template = PromptRegistry.Load(_root).Value.Get("x").Value;

        Assert.Equal("sen bir yazarsin", template.System);
        Assert.Equal("sunu yaz", template.User);
    }

    /// Bölüm başlığı yoksa metnin tamamı kullanıcı bölümü — tek bölümlü
    /// basit istemler için tören istemiyoruz.
    [Fact]
    public void BolumBasligiYoksa_TumMetinKullaniciBolumu()
    {
        WritePrompt("x", 1, "dogrudan istem");

        var template = PromptRegistry.Load(_root).Value.Get("x").Value;

        Assert.Null(template.System);
        Assert.Equal("dogrudan istem", template.User);
    }

    [Fact]
    public void YerTutucular_Doldurulur()
    {
        WritePrompt("x", 1, "# user\n'{{topic}}' konusunda {{language}} dilinde yaz");

        var rendered = PromptRegistry.Load(_root).Value.Get("x").Value
            .Render(Values(("topic", "Göbeklitepe"), ("language", "tr-TR")));

        Assert.Equal("'Göbeklitepe' konusunda tr-TR dilinde yaz", rendered.Value.User);
    }

    /// Eksik yer tutucu HATA, boş değil. Boş bırakmak modele "'' konusunda
    /// yaz" diyen sessizce bozuk bir istem üretirdi ve teşhisi saatler
    /// alırdı.
    [Fact]
    public void EksikYerTutucu_HataVerir()
    {
        WritePrompt("x", 1, "# user\n{{topic}} ve {{language}}");

        var rendered = PromptRegistry.Load(_root).Value.Get("x").Value
            .Render(Values(("topic", "konu")));

        Assert.True(rendered.IsFailure);
        Assert.Equal("prompt.missing_value", rendered.Error.Code);
        Assert.Contains("language", rendered.Error.Message, StringComparison.Ordinal);
    }

    /// Fazladan değer serbest: aynı sözlük birden çok isteme verilebilsin.
    [Fact]
    public void FazladanDeger_SorunDegil()
    {
        WritePrompt("x", 1, "# user\n{{topic}}");

        var rendered = PromptRegistry.Load(_root).Value.Get("x").Value
            .Render(Values(("topic", "konu"), ("kullanilmayan", "deger")));

        Assert.True(rendered.IsSuccess);
        Assert.Equal("konu", rendered.Value.User);
    }

    /// Sürüm numarası yetmiyor: biri numarayı artırmadan metni
    /// düzeltebilir. Özet gerçek metni damgalıyor.
    [Fact]
    public void MetinDegisince_OzetDegisir()
    {
        var path = WritePrompt("x", 1, "# user\nbirinci metin");
        var first = PromptRegistry.Load(_root).Value.Get("x").Value.Hash;

        File.WriteAllText(path, File.ReadAllText(path).Replace("birinci", "ikinci", StringComparison.Ordinal));
        var second = PromptRegistry.Load(_root).Value.Get("x").Value.Hash;

        Assert.NotEqual(first, second);
    }

    /// Aynı dosya Windows ve Linux'ta aynı özeti vermeli, yoksa CI'daki
    /// damga geliştirme makinesindekiyle tutmaz.
    [Fact]
    public void SatirSonu_OzetiDegistirmez()
    {
        var directory = Path.Combine(_root, "x");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "v1.md");

        File.WriteAllText(path, "---\nkey: x\nversion: 1\n---\n\n# user\nmetin\n");
        var lf = PromptRegistry.Load(_root).Value.Get("x").Value.Hash;

        File.WriteAllText(path, "---\r\nkey: x\r\nversion: 1\r\n---\r\n\r\n# user\r\nmetin\r\n");
        var crlf = PromptRegistry.Load(_root).Value.Get("x").Value.Hash;

        Assert.Equal(lf, crlf);
    }

    [Fact]
    public void Damga_AnahtarSurumVeOzetIcerir()
    {
        WritePrompt("script.generate", 2, "# user\nmetin");

        var stamp = PromptRegistry.Load(_root).Value.Get("script.generate").Value.Stamp;

        Assert.StartsWith("script.generate@2#", stamp, StringComparison.Ordinal);
        Assert.Equal(16, stamp[stamp.IndexOf('#', StringComparison.Ordinal)..].Length - 1);
    }

    /// Dosya adı ile içerideki sürüm tutmazsa dizine bakan insan yanlış
    /// dosyayı düzenler.
    [Fact]
    public void DosyaAdiSurumleUyusmazsa_Reddedilir()
    {
        var directory = Path.Combine(_root, "x");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "v1.md"), "---\nkey: x\nversion: 7\n---\n\n# user\nmetin\n");

        var registry = PromptRegistry.Load(_root);

        Assert.True(registry.IsFailure);
        Assert.Equal("prompt.name_mismatch", registry.Error.Code);
    }

    [Fact]
    public void OnBilgisizDosya_Reddedilir()
    {
        var directory = Path.Combine(_root, "x");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "v1.md"), "# user\nmetin\n");

        var registry = PromptRegistry.Load(_root);

        Assert.True(registry.IsFailure);
        Assert.Equal("prompt.no_frontmatter", registry.Error.Code);
    }

    [Fact]
    public void BosKullaniciBolumu_Reddedilir()
    {
        var directory = Path.Combine(_root, "x");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "v1.md"), "---\nkey: x\nversion: 1\n---\n\n# system\nsadece sistem\n");

        var registry = PromptRegistry.Load(_root);

        Assert.True(registry.IsFailure);
        Assert.Equal("prompt.no_user", registry.Error.Code);
    }

    [Fact]
    public void BilinmeyenAnahtar_TanimlilariListeler()
    {
        WritePrompt("script.generate", 1, "# user\nmetin");

        var result = PromptRegistry.Load(_root).Value.Get("yok.boyle");

        Assert.True(result.IsFailure);
        Assert.Contains("script.generate", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OlmayanSurum_AcikHataVerir()
    {
        WritePrompt("x", 1, "# user\nmetin");

        var result = PromptRegistry.Load(_root).Value.Get("x", 9);

        Assert.True(result.IsFailure);
        Assert.Equal("prompt.unknown_version", result.Error.Code);
    }

    [Fact]
    public void BosDizin_Reddedilir()
    {
        Directory.CreateDirectory(_root);

        var registry = PromptRegistry.Load(_root);

        Assert.True(registry.IsFailure);
        Assert.Equal("prompts.empty", registry.Error.Code);
    }

    [Fact]
    public void OlmayanDizin_AcikHataVerir()
    {
        var registry = PromptRegistry.Load(_root);

        Assert.True(registry.IsFailure);
        Assert.Equal("prompts.missing", registry.Error.Code);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Geçici dizin silinemezse test sonucunu etkilemez.
        }
    }
}
