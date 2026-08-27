using BytemountsAiStudio.Contracts.Prompts;

namespace BytemountsAiStudio.Contracts.Tests;

/// Fixture koşucusunun testleri (P1-07).
///
/// Ayrıca DEPODAKİ gerçek istemleri ve fixture'ları koşuyor: bir istem
/// dosyası düzenlendiğinde kırılması gereken yer burası. Bu test olmadan
/// bir yer tutucunun düşürülmesi ancak canlı bir koşuda fark edilirdi.
public sealed class PromptEvaluatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"bmai-evals-{Guid.NewGuid():N}");

    private void WritePrompt(string key, int version, string body)
    {
        var directory = Path.Combine(_root, key);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"v{version}.md"),
            $"---\nkey: {key}\nversion: {version}\n---\n\n{body}\n");
    }

    private void WriteFixture(string key, string name, string json)
    {
        var directory = Path.Combine(_root, key, "evals");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"{name}.json"), json);
    }

    private PromptRegistry Registry() => PromptRegistry.Load(_root).Value;

    [Fact]
    public void GecenFixture_Raporda()
    {
        WritePrompt("x", 1, "# user\n'{{topic}}' hakkinda yaz, kaynak disina cikma");
        WriteFixture("x", "temel", """
            { "prompt_key": "x", "values": { "topic": "Göbeklitepe" },
              "expect": { "contains": ["Göbeklitepe", "kaynak disina cikma"] } }
            """);

        var report = PromptEvaluator.RunAll(Registry(), _root).Value;

        Assert.True(report.AllPassed, string.Join("; ", report.Results.SelectMany(r => r.Failures)));
        Assert.Equal(1, report.Passed);
        Assert.Equal("temel", report.Results[0].Name);
    }

    /// P1-07'nin asıl amacı: bir kural silinince fixture kırılsın.
    [Fact]
    public void KuralSilinince_FixtureKirilir()
    {
        WritePrompt("x", 1, "# user\n{{topic}} hakkinda yaz");
        WriteFixture("x", "kural", """
            { "prompt_key": "x", "values": { "topic": "konu" },
              "expect": { "contains": ["kaynak disina cikma"] } }
            """);

        var report = PromptEvaluator.RunAll(Registry(), _root).Value;

        Assert.False(report.AllPassed);
        Assert.Contains("kaynak disina cikma", report.Results[0].Failures[0], StringComparison.Ordinal);
    }

    /// Yer tutucu düşürülünce konu isteme hiç girmez — modelsiz yakalanıyor.
    [Fact]
    public void YerTutucuDusurulunce_FixtureKirilir()
    {
        WritePrompt("x", 1, "# user\nbir konu hakkinda yaz");
        WriteFixture("x", "konu", """
            { "prompt_key": "x", "values": { "topic": "Göbeklitepe" },
              "expect": { "contains": ["Göbeklitepe"] } }
            """);

        Assert.False(PromptEvaluator.RunAll(Registry(), _root).Value.AllPassed);
    }

    [Fact]
    public void EksikDeger_FixtureKirilir()
    {
        WritePrompt("x", 1, "# user\n{{topic}} ve {{language}}");
        WriteFixture("x", "eksik", """
            { "prompt_key": "x", "values": { "topic": "konu" } }
            """);

        var report = PromptEvaluator.RunAll(Registry(), _root).Value;

        Assert.False(report.AllPassed);
        Assert.Contains("language", report.Results[0].Failures[0], StringComparison.Ordinal);
    }

    /// Bağlam sınırını taşıran bir istem, sağlayıcıda anlamsız bir hataya
    /// dönüşürdü. Karakter sınırı bunu önden yakalıyor.
    [Fact]
    public void SinirAsilinca_FixtureKirilir()
    {
        WritePrompt("x", 1, "# user\n{{metin}}");
        WriteFixture("x", "sinir", """
            { "prompt_key": "x", "values": { "metin": "aaaaaaaaaaaaaaaaaaaa" },
              "expect": { "max_chars": 10 } }
            """);

        var report = PromptEvaluator.RunAll(Registry(), _root).Value;

        Assert.False(report.AllPassed);
        Assert.Contains("sinir 10", report.Results[0].Failures[0], StringComparison.Ordinal);
    }

    [Fact]
    public void OlmamasiGerekenMetin_Yakalanir()
    {
        WritePrompt("x", 1, "# user\nTODO: burayi doldur");
        WriteFixture("x", "todo", """
            { "prompt_key": "x", "expect": { "not_contains": ["TODO"] } }
            """);

        Assert.False(PromptEvaluator.RunAll(Registry(), _root).Value.AllPassed);
    }

    [Fact]
    public void SurumSabitlenebilir()
    {
        WritePrompt("x", 1, "# user\nbirinci");
        WritePrompt("x", 2, "# user\nikinci");
        WriteFixture("x", "sabit", """
            { "prompt_key": "x", "version": 1, "expect": { "contains": ["birinci"] } }
            """);

        var report = PromptEvaluator.RunAll(Registry(), _root).Value;

        Assert.True(report.AllPassed);
        Assert.Contains("@1#", report.Results[0].Stamp!, StringComparison.Ordinal);
    }

    [Fact]
    public void BozukFixture_KosuyuDusurmezRaporlanir()
    {
        WritePrompt("x", 1, "# user\nmetin");
        WriteFixture("x", "bozuk", "{ bu gecerli json degil ");

        var report = PromptEvaluator.RunAll(Registry(), _root).Value;

        Assert.False(report.AllPassed);
        Assert.Equal("bozuk", report.Results[0].Name);
    }

    /// Depodaki gerçek istemler ve fixture'lar. Bir istem dosyası
    /// düzenlendiğinde CI'da kırılacak yer burası.
    [Fact]
    public void DepodakiIstemler_FixturelariGeciyor()
    {
        var directory = FindRepositoryDirectory("prompts");

        if (directory is null)
        {
            // Test tek başına (paket olarak) koşuyorsa depo yapısı yok.
            return;
        }

        var registry = PromptRegistry.Load(directory);
        Assert.True(registry.IsSuccess, registry.IsFailure ? registry.Error.Message : string.Empty);

        var report = PromptEvaluator.RunAll(registry.Value, directory);
        Assert.True(report.IsSuccess, report.IsFailure ? report.Error.Message : string.Empty);

        var failures = report.Value.Results
            .Where(r => !r.Passed)
            .Select(r => $"{r.Name}: {string.Join(", ", r.Failures)}");

        Assert.True(report.Value.AllPassed, string.Join(" | ", failures));
        Assert.True(report.Value.Passed > 0, "Depoda hic fixture bulunamadi.");
    }

    /// Gömülü kopya diskteki dosyaların AYNISI olmalı.
    ///
    /// İkisi ayrışırsa yayınlanmış araç, depoda okunandan farklı bir
    /// istemle çalışır — ve bunun teşhisi neredeyse imkânsızdır: kod
    /// doğru, dosya doğru, çıktı yanlış. Özetler karşılaştırılıyor.
    [Fact]
    public void GomuluIstemler_DiskteakilerleAyni()
    {
        var directory = FindRepositoryDirectory("prompts");

        if (directory is null)
        {
            return;
        }

        var fromDisk = PromptRegistry.Load(directory);
        var embedded = PromptRegistry.Embedded;

        Assert.True(embedded.IsSuccess, embedded.IsFailure ? embedded.Error.Message : string.Empty);
        Assert.True(fromDisk.IsSuccess, fromDisk.IsFailure ? fromDisk.Error.Message : string.Empty);
        Assert.Equal(fromDisk.Value.Count, embedded.Value.Count);

        foreach (var key in fromDisk.Value.Keys)
        {
            foreach (var template in fromDisk.Value.Versions(key))
            {
                var match = embedded.Value.Get(key, template.Version);

                Assert.True(match.IsSuccess, $"gomulu kopyada yok: {template.Stamp}");
                Assert.Equal(template.Stamp, match.Value.Stamp);
            }
        }
    }

    private static string? FindRepositoryDirectory(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relative);

            if (Directory.Exists(candidate))
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
