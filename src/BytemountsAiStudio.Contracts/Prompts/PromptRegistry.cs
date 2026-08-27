using System.Globalization;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;

namespace BytemountsAiStudio.Contracts.Prompts;

/// Dosya tabanlı istem kayıt defteri (P1-07).
///
/// Dizin yapısı:
///   prompts/&lt;anahtar&gt;/v&lt;N&gt;.md      — istem sürümleri
///   prompts/&lt;anahtar&gt;/evals/*.json   — fixture'lar (bkz. PromptEvaluator)
///
/// Neden veritabanı değil de dosya: istemler kodla birlikte gözden
/// geçirilmeli. Veritabanında olsaydı bir istem değişikliği kod
/// incelemesine hiç girmez, `git diff` ile görünmez ve bir sürüm geri
/// alınamazdı. Kod deposunda durunca üçü de kendiliğinden geliyor.
public sealed class PromptRegistry
{
    private readonly Dictionary<string, List<PromptTemplate>> _byKey;

    private PromptRegistry(Dictionary<string, List<PromptTemplate>> byKey) => _byKey = byKey;

    public IReadOnlyCollection<string> Keys => _byKey.Keys;

    public int Count => _byKey.Values.Sum(v => v.Count);

    /// Derlemeye gömülü istemler.
    ///
    /// Her zaman çalışıyor — çalışma dizininden, yayın biçiminden ve
    /// dosya sisteminden bağımsız. Kaynak dosyalar diskteki `prompts/`
    /// dizininin AYNISI (csproj'daki `EmbeddedResource`), dolayısıyla iki
    /// kaynak arasında kayma oluşamıyor.
    ///
    /// İlk çağrıda okunup saklanıyor: gömülü kaynaklar hiç değişmiyor,
    /// her çağrıda yeniden ayrıştırmanın anlamı yok.
    public static Result<PromptRegistry> Embedded => EmbeddedLazy.Value;

    private static readonly Lazy<Result<PromptRegistry>> EmbeddedLazy = new(() =>
    {
        var assembly = typeof(PromptRegistry).Assembly;
        var sources = new List<(string Name, string Content)>();

        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.EndsWith(".md", StringComparison.Ordinal))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(name);

            if (stream is null)
            {
                continue;
            }

            using var reader = new StreamReader(stream);
            sources.Add((name, reader.ReadToEnd()));
        }

        return Build(sources, "gomulu kaynaklar");
    });

    public static Result<PromptRegistry> Load(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        if (!Directory.Exists(directory))
        {
            return Error.Permanent("prompts.missing", $"Istem dizini yok: {directory}");
        }

        var sources = new List<(string Name, string Content)>();

        foreach (var file in Directory.EnumerateFiles(directory, "v*.md", SearchOption.AllDirectories))
        {
            // evals/ altındaki dosyalar istem değil; yanlışlıkla oraya
            // konmuş bir .md sürüm sayılmasın.
            if (file.Contains($"{Path.DirectorySeparatorChar}evals{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                sources.Add((file, File.ReadAllText(file)));
            }
            catch (IOException ex)
            {
                return Error.Transient("prompt.unreadable", $"{file}: {ex.Message}");
            }
        }

        return Build(sources, directory);
    }

    private static Result<PromptRegistry> Build(
        IReadOnlyList<(string Name, string Content)> sources, string origin)
    {
        var byKey = new Dictionary<string, List<PromptTemplate>>(StringComparer.Ordinal);

        foreach (var (name, content) in sources)
        {
            var parsed = Parse(name, content);

            if (parsed.IsFailure)
            {
                return Result.Failure<PromptRegistry>(parsed.Error);
            }

            var template = parsed.Value;

            if (!byKey.TryGetValue(template.Key, out var versions))
            {
                versions = [];
                byKey[template.Key] = versions;
            }

            if (versions.Any(v => v.Version == template.Version))
            {
                return Error.Permanent("prompts.duplicate_version",
                    $"'{template.Key}' icin {template.Version} surumu iki kez tanimli.");
            }

            versions.Add(template);
        }

        if (byKey.Count == 0)
        {
            return Error.Permanent("prompts.empty", $"'{origin}' altinda hic istem bulunamadi.");
        }

        foreach (var versions in byKey.Values)
        {
            versions.Sort((a, b) => b.Version.CompareTo(a.Version));
        }

        return Result.Success(new PromptRegistry(byKey));
    }

    /// Belirli bir sürüm; verilmezse EN YÜKSEK sürüm.
    ///
    /// Varsayılanın en yüksek olması, bir sürümü yayına almanın tek adımı
    /// olmasını sağlıyor: dosyayı ekle, yeter. Ayrı bir "aktif sürüm"
    /// ayarı olsaydı dosya eklendiği hâlde devreye girmeyen istemler
    /// olurdu ve bunun teşhisi zor.
    public Result<PromptTemplate> Get(string key, int? version = null)
    {
        if (!_byKey.TryGetValue(key, out var versions))
        {
            var known = string.Join(", ", _byKey.Keys.Order(StringComparer.Ordinal));

            return Error.Permanent("prompt.unknown", $"'{key}' istemi yok. Tanimlilar: {known}");
        }

        if (version is null)
        {
            return Result.Success(versions[0]);
        }

        var match = versions.Find(v => v.Version == version.Value);

        return match is null
            ? Error.Permanent("prompt.unknown_version",
                string.Create(CultureInfo.InvariantCulture, $"'{key}' icin {version} surumu yok."))
            : Result.Success(match);
    }

    public IReadOnlyList<PromptTemplate> Versions(string key)
        => _byKey.TryGetValue(key, out var versions) ? versions : [];

    /// Bir istem dosyasını ayrıştırır.
    ///
    /// Biçim bilerek ilkel: `---` arasında `ad: deger` satırları, sonra
    /// `# system` ve `# user` bölümleri. YAML ayrıştırıcısı getirmedik —
    /// YAML'ın kenar durumları (çok satırlı dizgeler, tip çıkarımı,
    /// "norway problemi") istem dosyalarında hiç işimize yaramayacak bir
    /// karmaşıklık getirirdi.
    private static Result<PromptTemplate> Parse(string path, string content)
    {
        var hash = PromptTemplate.ComputeHash(content);
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            return Error.Permanent("prompt.no_frontmatter",
                $"{path}: dosya '---' ile baslamali.");
        }

        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        var index = 1;

        for (; index < lines.Length && lines[index].Trim() != "---"; index++)
        {
            var separator = lines[index].IndexOf(':', StringComparison.Ordinal);

            if (separator > 0)
            {
                meta[lines[index][..separator].Trim()] = lines[index][(separator + 1)..].Trim();
            }
        }

        if (index >= lines.Length)
        {
            return Error.Permanent("prompt.unterminated_frontmatter",
                $"{path}: '---' blogu kapatilmamis.");
        }

        if (!meta.TryGetValue("key", out var key) || key.Length == 0)
        {
            return Error.Permanent("prompt.no_key", $"{path}: 'key' alani zorunlu.");
        }

        if (!meta.TryGetValue("version", out var versionText)
            || !int.TryParse(versionText, CultureInfo.InvariantCulture, out var version))
        {
            return Error.Permanent("prompt.no_version", $"{path}: 'version' sayi olmali.");
        }

        // Dosya adı ile içerideki sürüm birbirini tutmalı. Tutmazsa
        // dizine bakan insan yanlış dosyayı düzenler.
        //
        // Sona bakılıyor, `Path.GetFileName`e değil: gömülü kaynakların
        // adı `...Embedded.script.generate.v1.md` biçiminde ve dizin
        // ayracı içermiyor.
        var expected = $"v{version.ToString(CultureInfo.InvariantCulture)}.md";

        if (!EndsWithSegment(path, expected))
        {
            return Error.Permanent("prompt.name_mismatch",
                $"{path}: dosya adi '{expected}' olmali (icerideki version: {version}).");
        }

        var (system, user) = SplitSections(lines, index + 1);

        if (string.IsNullOrWhiteSpace(user))
        {
            return Error.Permanent("prompt.no_user", $"{path}: '# user' bolumu bos olamaz.");
        }

        return Result.Success(new PromptTemplate
        {
            Key = key,
            Version = version,
            Hash = hash,
            Description = meta.GetValueOrDefault("description"),
            System = string.IsNullOrWhiteSpace(system) ? null : system.Trim(),
            User = user.Trim(),
        });
    }

    /// `path` gerçekten `segment` ile mi bitiyor — `xv1.md` gibi bir adın
    /// `v1.md` sayılmaması için önündeki karakter de denetleniyor.
    private static bool EndsWithSegment(string path, string segment)
    {
        if (!path.EndsWith(segment, StringComparison.Ordinal))
        {
            return false;
        }

        if (path.Length == segment.Length)
        {
            return true;
        }

        var before = path[path.Length - segment.Length - 1];

        return before is '/' or '\\' or '.';
    }

    private static (string? System, string User) SplitSections(string[] lines, int start)
    {
        var system = new List<string>();
        var user = new List<string>();
        List<string>? current = null;

        for (var i = start; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();

            if (trimmed.Equals("# system", StringComparison.OrdinalIgnoreCase))
            {
                current = system;
                continue;
            }

            if (trimmed.Equals("# user", StringComparison.OrdinalIgnoreCase))
            {
                current = user;
                continue;
            }

            // Bölüm başlığı gelmeden yazılan metin kullanıcı bölümü
            // sayılıyor: tek bölümlü basit istemler için gereksiz tören
            // istemiyoruz.
            (current ?? user).Add(lines[i]);
        }

        return (string.Join('\n', system), string.Join('\n', user));
    }
}
