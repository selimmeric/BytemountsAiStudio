using System.Reflection;
using BytemountsAiStudio.Contracts.Providers;

namespace BytemountsAiStudio.Contracts.Tests;

/// P0-13'ün kabul kriterinin bekçisi: arayüzler `.Contracts`'ta ve hiçbir
/// implementasyona bağımlı değil.
///
/// Bu kural gevşerse provider soyutlaması kâğıt üstünde kalır — bir sağlayıcıyı
/// değiştirmek için Contracts'a dokunmak gerekir ve mimarinin ana vaadi (§25)
/// çöker. Sessizce olmasın diye teste bağlı.
public sealed class ProviderContractTests
{
    private static readonly Assembly ContractsAssembly = typeof(IProvider).Assembly;

    /// Mimari §9.1'de sayılan arayüzlerin tamamı. Buradan bir şey düşerse
    /// test kırmızıya döner — arayüz silmek bilinçli bir karar olmalı.
    private static readonly string[] ExpectedProviders =
    [
        nameof(ILlmProvider),
        nameof(ISearchProvider),
        nameof(IWebFetchProvider),
        nameof(ITtsProvider),
        nameof(IAsrProvider),
        nameof(IImageProvider),
        nameof(IMusicProvider),
        nameof(IStorageProvider),
        nameof(IPublisher),
        nameof(IAnalyticsProvider),
    ];

    [Fact]
    public void MimarideSayilanTumSaglayiciArayuzleri_Mevcut()
    {
        var found = ContractsAssembly.GetTypes()
            .Where(t => t.IsInterface && t.IsPublic)
            .Select(t => t.Name)
            .ToHashSet(StringComparer.Ordinal);

        var missing = ExpectedProviders.Where(name => !found.Contains(name)).ToList();

        Assert.True(missing.Count == 0, $"Eksik sağlayıcı arayüzü: {string.Join(", ", missing)}");
    }

    [Fact]
    public void HerSaglayiciArayuzu_IProviderTuretir()
    {
        // Ortak taban olmadan yönlendirme, ölçüm ve devre kesici dekoratörleri
        // her arayüz için ayrı ayrı yazılmak zorunda kalır.
        var offenders = ContractsAssembly.GetTypes()
            .Where(t => t.IsInterface && t.IsPublic && t != typeof(IProvider))
            .Where(t => ExpectedProviders.Contains(t.Name, StringComparer.Ordinal))
            .Where(t => !typeof(IProvider).IsAssignableFrom(t))
            .Select(t => t.Name)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"IProvider türetmeyen sağlayıcı arayüzü: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void Contracts_YalnizcaCoreaBagimli()
    {
        var ownReferences = ContractsAssembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(n => n.StartsWith("BytemountsAiStudio", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["BytemountsAiStudio.Core"], ownReferences);
    }

    [Fact]
    public void TumSaglayiciMetotlari_IptalTokeniAlir()
    {
        // Otonom sistemde kill-switch ve bütçe durdurması her çağrıya
        // ulaşabilmeli. İptal alamayan tek bir metot, kapanışı asan
        // yer olur ve bunu ancak üretimde fark edersiniz.
        var offenders = new List<string>();

        foreach (var type in ContractsAssembly.GetTypes()
                     .Where(t => t.IsInterface && t.IsPublic && typeof(IProvider).IsAssignableFrom(t)))
        {
            foreach (var method in type.GetMethods().Where(m => m.ReturnType.Name.StartsWith("Task", StringComparison.Ordinal)))
            {
                var hasToken = method.GetParameters()
                    .Any(p => p.ParameterType == typeof(CancellationToken));

                if (!hasToken)
                {
                    offenders.Add($"{type.Name}.{method.Name}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            $"CancellationToken almayan asenkron metot: {string.Join(", ", offenders)}");
    }
}
