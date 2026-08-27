using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;

namespace BytemountsAiStudio.Contracts.Prompts;

/// İstem fixture koşucusu (P1-07).
///
/// NE DOĞRULANIYOR: doldurulmuş istemin kendisi — model çıktısı değil.
/// Bu ayrım bilinçli. Model çıktısını doğrulamak bir model gerektirir;
/// o zaman fixture'lar CI'da koşamaz, yavaşlar, ve modelin o günkü
/// keyfine göre kırmızı yanar. Oysa istem değişikliklerinin gerçekte
/// ürettiği hataların çoğu istemin KENDİSİNDE görünür:
///
///   - bir yer tutucu düşürülmüş  → konu isteme hiç girmiyor
///   - bir kural silinmiş         → "kaynak dışına çıkma" kaybolmuş
///   - metin şişmiş               → bağlam sınırını taşırıyor
///
/// Üçü de modelsiz yakalanıyor ve CI'da milisaniyeler sürüyor. Model
/// çıktısına bakan değerlendirme ayrı bir iş (P2), ayrı bir komut.
public static class PromptEvaluator
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// Bir dizindeki bütün fixture'ları koşar.
    ///
    /// `prompts/&lt;anahtar&gt;/evals/*.json` deseni taranıyor. Fixture'ı
    /// istemin yanında tutmak bilinçli: istemi değiştiren kişi
    /// fixture'ı da aynı dizinde görüyor.
    public static Result<EvalReport> RunAll(PromptRegistry registry, string directory)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        if (!Directory.Exists(directory))
        {
            return Error.Permanent("evals.missing", $"Istem dizini yok: {directory}");
        }

        var results = new List<EvalResult>();

        foreach (var file in Directory
            .EnumerateFiles(directory, "*.json", SearchOption.AllDirectories)
            .Where(f => f.Contains($"{Path.DirectorySeparatorChar}evals{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal))
        {
            var loaded = Load(file);

            if (loaded.IsFailure)
            {
                results.Add(new EvalResult
                {
                    Name = Path.GetFileNameWithoutExtension(file),
                    PromptKey = "?",
                    Passed = false,
                    Failures = [loaded.Error.Message],
                });

                continue;
            }

            results.Add(Run(registry, loaded.Value));
        }

        return Result.Success(new EvalReport { Results = results });
    }

    public static EvalResult Run(PromptRegistry registry, PromptFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(fixture);

        var template = registry.Get(fixture.PromptKey, fixture.Version);

        if (template.IsFailure)
        {
            return new EvalResult
            {
                Name = fixture.Name,
                PromptKey = fixture.PromptKey,
                Passed = false,
                Failures = [template.Error.Message],
            };
        }

        var rendered = template.Value.Render(fixture.Values ?? new Dictionary<string, string>(StringComparer.Ordinal));

        if (rendered.IsFailure)
        {
            return new EvalResult
            {
                Name = fixture.Name,
                PromptKey = fixture.PromptKey,
                Stamp = template.Value.Stamp,
                Passed = false,
                Failures = [rendered.Error.Message],
            };
        }

        var text = string.Join('\n', rendered.Value.System, rendered.Value.User);
        var failures = new List<string>();
        var expect = fixture.Expect;

        if (expect is not null)
        {
            foreach (var needle in expect.Contains ?? [])
            {
                if (!text.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"beklenen metin yok: \"{needle}\"");
                }
            }

            foreach (var needle in expect.NotContains ?? [])
            {
                if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"olmamasi gereken metin var: \"{needle}\"");
                }
            }

            // Doldurulmamış yer tutucu her zaman hata. Fixture yazmayı
            // unutsanız bile bu kontrol çalışıyor, çünkü `{{` kalıntısı
            // hiçbir durumda doğru olamaz.
            if (text.Contains("{{", StringComparison.Ordinal))
            {
                failures.Add("doldurulmamis yer tutucu kalmis ({{...}})");
            }

            if (expect.MaxChars is { } max && text.Length > max)
            {
                failures.Add(string.Create(CultureInfo.InvariantCulture,
                    $"istem {text.Length} karakter, sinir {max}"));
            }

            if (expect.MinChars is { } min && text.Length < min)
            {
                failures.Add(string.Create(CultureInfo.InvariantCulture,
                    $"istem {text.Length} karakter, en az {min} bekleniyordu"));
            }
        }

        return new EvalResult
        {
            Name = fixture.Name,
            PromptKey = fixture.PromptKey,
            Stamp = template.Value.Stamp,
            Passed = failures.Count == 0,
            Failures = failures,
            RenderedChars = text.Length,
        };
    }

    private static Result<PromptFixture> Load(string path)
    {
        try
        {
            var fixture = JsonSerializer.Deserialize<PromptFixture>(File.ReadAllText(path), ReadOptions);

            if (fixture is null)
            {
                return Error.Permanent("eval.empty", $"{path}: bos fixture.");
            }

            // Adı dosyadan türetmek, fixture yazarken bir alanı daha
            // doldurma zorunluluğunu kaldırıyor.
            return Result.Success(fixture with
            {
                Name = string.IsNullOrWhiteSpace(fixture.Name)
                    ? Path.GetFileNameWithoutExtension(path)
                    : fixture.Name,
            });
        }
        catch (JsonException ex)
        {
            return Error.Permanent("eval.invalid_json", $"{path}: {ex.Message}");
        }
        catch (IOException ex)
        {
            return Error.Transient("eval.unreadable", $"{path}: {ex.Message}");
        }
    }
}

public sealed record PromptFixture
{
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("prompt_key")]
    public required string PromptKey { get; init; }

    /// Belirtilmezse en yüksek sürüm — yani fixture, yeni sürümü
    /// kendiliğinden denetliyor. Bir sürümü sabitlemek istendiğinde
    /// açıkça yazılıyor.
    public int? Version { get; init; }

    public Dictionary<string, string>? Values { get; init; }

    public PromptExpectation? Expect { get; init; }
}

public sealed record PromptExpectation
{
    public List<string>? Contains { get; init; }

    [JsonPropertyName("not_contains")]
    public List<string>? NotContains { get; init; }

    [JsonPropertyName("max_chars")]
    public int? MaxChars { get; init; }

    [JsonPropertyName("min_chars")]
    public int? MinChars { get; init; }
}

public sealed record EvalResult
{
    public required string Name { get; init; }

    public required string PromptKey { get; init; }

    public string? Stamp { get; init; }

    public required bool Passed { get; init; }

    public IReadOnlyList<string> Failures { get; init; } = [];

    public int RenderedChars { get; init; }
}

public sealed record EvalReport
{
    public required IReadOnlyList<EvalResult> Results { get; init; }

    public int Passed => Results.Count(r => r.Passed);

    public int Failed => Results.Count(r => !r.Passed);

    public bool AllPassed => Failed == 0;
}
