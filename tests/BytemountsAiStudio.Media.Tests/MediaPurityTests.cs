using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using BytemountsAiStudio.Media;

namespace BytemountsAiStudio.Media.Tests;

/// ADR-005r ve ADR-007'nin bekcisi.
///
/// Render motorunun saf katmani (Planner, IR, Validator, Emitter) dosya sistemine,
/// surece veya aga dokunamaz. Bu kural sayesinde:
///   - testler FFmpeg olmadan, milisaniyede kosar
///   - render tekrarlanabilir ve onbelleklenebilir
///   - timeline'in "tamamen cozumlenmis belge" olma garantisi korunur
///
/// Kural derleme zamaninda degil, IL metadata'sinda dogrulanir: kod icinde bir
/// yan etkili tipe deginmek bile TypeReference tablosuna dusuyor. Boylece
/// "sadece su bir yerde File.ReadAllText kullaniverelim" sessizce gecemiyor.
public sealed class MediaPurityTests
{
    /// Saf katmanda adi bile gecmemesi gereken tipler.
    /// System.IO.Path ve System.IO.Stream bilerek listede yok: Path saf hesaplama,
    /// Stream ise ileride bellek ici tampon olarak gecebilir.
    private static readonly HashSet<string> Forbidden = new(StringComparer.Ordinal)
    {
        "System.IO.File",
        "System.IO.Directory",
        "System.IO.FileStream",
        "System.IO.FileInfo",
        "System.IO.DirectoryInfo",
        "System.Diagnostics.Process",
        "System.Diagnostics.ProcessStartInfo",
        "System.Net.Http.HttpClient",
        "System.Net.WebClient",
        "System.Net.Sockets.Socket",
        "System.Environment",
    };

    [Fact]
    public void SafMediaKatmani_YanEtkiliTiplereDokunmaz()
    {
        var offenders = ReferencedTypeNames(typeof(AssemblyMarker).Assembly.Location)
            .Where(Forbidden.Contains)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"BytemountsAiStudio.Media saf kalmali (ADR-005r). Yasakli tip kullanimi: " +
            $"{string.Join(", ", offenders)}. Yan etkili is Media.Rendering'e ait.");
    }

    private static IEnumerable<string> ReferencedTypeNames(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();

        foreach (var handle in reader.TypeReferences)
        {
            var typeRef = reader.GetTypeReference(handle);
            var ns = reader.GetString(typeRef.Namespace);
            var name = reader.GetString(typeRef.Name);
            yield return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
        }
    }
}
