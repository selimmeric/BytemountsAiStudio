using System.Text.Json;
using System.Text.Json.Nodes;
using BytemountsAiStudio.Core.Errors;

namespace BytemountsAiStudio.Core.Learning;

/// Atanan kolların run bağlamına yazılması ve okunması (P5-03).
///
/// BU DOSYA DENEYİN BAĞLANDIĞI YER. Atama tablosuna yazmak deneyi
/// KAYDEDIYOR ama UYGULAMIYOR: kapak node'u atama tablosunu
/// okumuyor, run bağlamını okuyor. Bu iki adım arasındaki boşluk, bu
/// depoda defalarca "yazıldı ama bağlanmadı" hatasına yol açtı —
/// seeder'ın başındaki not tam olarak bunu anlatıyor.
public static class ExperimentContext
{
    public const string Key = "experiments";

    /// Atanan kolları run bağlamına yazar.
    public static Result<string> Merge(string? contextJson, IReadOnlyList<AssignedVariant> assigned)
    {
        ArgumentNullException.ThrowIfNull(assigned);

        JsonNode? root;

        try
        {
            root = JsonNode.Parse(string.IsNullOrWhiteSpace(contextJson) ? "{}" : contextJson);
        }
        catch (JsonException ex)
        {
            return Error.Permanent("experiment.bad_context", ex.Message);
        }

        if (root is not JsonObject obj)
        {
            return Error.Permanent("experiment.bad_context", "Run bağlamı bir nesne olmalı.");
        }

        var block = new JsonObject();

        foreach (var variant in assigned)
        {
            JsonNode? config;

            try
            {
                config = JsonNode.Parse(
                    string.IsNullOrWhiteSpace(variant.ConfigJson) ? "{}" : variant.ConfigJson);
            }
            catch (JsonException ex)
            {
                return Error.Permanent("experiment.bad_config", ex.Message);
            }

            block[variant.Dimension] = new JsonObject
            {
                ["experiment"] = variant.ExperimentId.ToString(),
                ["variant"] = variant.VariantId.ToString(),
                ["name"] = variant.VariantName,
                ["config"] = config,
            };
        }

        obj[Key] = block;

        return Result.Success(obj.ToJsonString());
    }

    /// Bir boyutun bu run'daki ayarını okur.
    ///
    /// Deney yoksa `null` — hata değil. Videoların ezici çoğunluğu
    /// hiçbir deneye girmiyor ve bu normal işleyiş.
    public static string? ConfigFor(JsonElement runContext, string dimension)
    {
        if (runContext.ValueKind != JsonValueKind.Object
            || !runContext.TryGetProperty(Key, out var experiments)
            || experiments.ValueKind != JsonValueKind.Object
            || !experiments.TryGetProperty(dimension, out var entry)
            || entry.ValueKind != JsonValueKind.Object
            || !entry.TryGetProperty("config", out var config))
        {
            return null;
        }

        return config.GetRawText();
    }

    /// Bu run'ın bir boyuttaki kol adı — çıktıya yazılıyor.
    public static string? VariantName(JsonElement runContext, string dimension)
    {
        if (runContext.ValueKind != JsonValueKind.Object
            || !runContext.TryGetProperty(Key, out var experiments)
            || experiments.ValueKind != JsonValueKind.Object
            || !experiments.TryGetProperty(dimension, out var entry)
            || entry.ValueKind != JsonValueKind.Object
            || !entry.TryGetProperty("name", out var name))
        {
            return null;
        }

        return name.GetString();
    }
}
