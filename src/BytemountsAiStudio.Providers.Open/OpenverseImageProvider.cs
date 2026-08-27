using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;

namespace BytemountsAiStudio.Providers.Open;

/// Openverse stok görsel arama — anahtarsız, Creative Commons.
///
/// LİSANS FİLTRESİ ZORUNLU VE KAPATILAMAZ.
///
/// Openverse'ün varsayılan sonuçları çoğunlukla `by-nc-nd`: ticari kullanım
/// yasak VE türev üretmek yasak. İkisi de bu proje için ölümcül —
/// gelirlendirilmiş bir kanalda kullanılamaz, üstelik NoDerivatives kuralı
/// Ken Burns hareketini bile ihlal eder (kırpma bir türevdir).
///
/// Bu yüzden `license_type=commercial,modification` filtresi kodun içinde
/// sabit; konfigürasyondan gevşetilemiyor. §2.3/14: lisans bir metadata
/// değil uyum kaydı — ve uyumu isteğe bağlı yapmak, bir gün birinin
/// kapatması demektir.
public sealed class OpenverseImageProvider(HttpClient http) : IImageProvider
{
    private const string SearchAddress = "https://api.openverse.org/v1/images/";

    /// Wikimedia gibi Openverse de tanımlayıcı User-Agent bekliyor.
    private const string UserAgent = "BytemountsAiStudio/0.1 (icerik uretim arastirmasi)";

    /// Ticari kullanıma ve değiştirmeye izin veren lisanslar.
    /// Filtre sunucu tarafında uygulanıyor ama gelen sonuç yine de
    /// doğrulanıyor: API davranışı değişirse sessizce ihlal etmeyelim.
    private static readonly HashSet<string> AllowedLicenses =
        new(StringComparer.OrdinalIgnoreCase) { "by", "by-sa", "cc0", "pdm" };

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public string Key => "openverse";

    public ImageProviderKind Kind => ImageProviderKind.Stock;

    public async Task<Result<ProviderResponse<IReadOnlyList<ImageCandidate>>>> FindAsync(
        ImageQuery query, ProviderContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var url = new Uri(
            SearchAddress
            + $"?q={Uri.EscapeDataString(query.Terms)}"
            + $"&page_size={Math.Clamp(query.MaxResults, 1, 20).ToString(CultureInfo.InvariantCulture)}"
            + "&license_type=commercial,modification"
            + "&mature=false");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(UserAgent);

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return (int)response.StatusCode is >= 500 or 429
                    ? Error.Transient("openverse.unavailable", $"HTTP {(int)response.StatusCode}")
                    : Error.Permanent("openverse.rejected", $"HTTP {(int)response.StatusCode}");
            }

            var parsed = await response.Content
                .ReadFromJsonAsync<OpenverseResponse>(Json, cancellationToken)
                .ConfigureAwait(false);

            var candidates = new List<ImageCandidate>();
            var rejected = 0;

            foreach (var item in parsed?.Results ?? [])
            {
                if (item.Url is null || !Uri.TryCreate(item.Url, UriKind.Absolute, out var imageUrl))
                {
                    continue;
                }

                // İkinci savunma hattı: sunucu filtresine rağmen izin
                // verilmeyen bir lisans gelirse burada eleniyor.
                if (item.License is null || !AllowedLicenses.Contains(item.License))
                {
                    rejected++;
                    continue;
                }

                candidates.Add(new ImageCandidate
                {
                    Url = imageUrl,
                    Width = item.Width ?? 0,
                    Height = item.Height ?? 0,
                    Description = item.Title,
                    License = new LicenseInfo
                    {
                        Name = $"CC {item.License.ToUpperInvariant()} {item.LicenseVersion}".Trim(),
                        Url = item.LicenseUrl is null ? null : new Uri(item.LicenseUrl),
                        Author = item.Creator,
                        // CC0 ve PDM dışında atıf zorunlu; video açıklamasına
                        // eklenmesi gerekiyor.
                        RequiresAttribution = !string.Equals(item.License, "cc0", StringComparison.OrdinalIgnoreCase)
                                              && !string.Equals(item.License, "pdm", StringComparison.OrdinalIgnoreCase),
                        CapturedAt = DateTimeOffset.UtcNow,
                    },
                });
            }

            if (candidates.Count == 0)
            {
                // Sonuç bulunamaması hata değil: yönlendirme politikası
                // bir sonraki sağlayıcıya (AI üretim) düşecek.
                return Result.Success(new ProviderResponse<IReadOnlyList<ImageCandidate>>(
                    [], UsageUnits.OfRequests()));
            }

            return Result.Success(new ProviderResponse<IReadOnlyList<ImageCandidate>>(
                candidates, UsageUnits.OfRequests()));
        }
        catch (HttpRequestException ex)
        {
            return Error.Transient("openverse.unreachable", ex.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Error.Transient("openverse.timeout", "Openverse zaman aşımına uğradı.");
        }
    }

    public Task<Result<ProviderResponse<GeneratedImage>>> GenerateAsync(
        ImagePrompt prompt, ProviderContext context, CancellationToken cancellationToken)
        => Task.FromResult(Result.Failure<ProviderResponse<GeneratedImage>>(
            Error.Permanent("openverse.not_generative", "Bu sağlayıcı arar, üretmez.")));

    /// Test edilebilir olsun diye ayrı: lisans kodunun kabul edilip
    /// edilmediği kuralın kendisi.
    internal static bool IsUsable(string? license) =>
        license is not null && AllowedLicenses.Contains(license);

    private sealed record OpenverseResponse(
        [property: JsonPropertyName("result_count")] int ResultCount,
        List<OpenverseImage>? Results);

    private sealed record OpenverseImage(
        string? Title,
        string? Url,
        string? Creator,
        string? License,
        [property: JsonPropertyName("license_version")] string? LicenseVersion,
        [property: JsonPropertyName("license_url")] string? LicenseUrl,
        int? Width,
        int? Height,
        string? Source);
}
