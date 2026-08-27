using System.Globalization;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;

namespace BytemountsAiStudio.Providers.Open;

/// Anahtarsız AI görsel üretimi (Pollinations).
///
/// ADR-015'in görsel ayağı: API anahtarı gerektirmeyen, ücretsiz kullanıma
/// açık bir servis. Sahte sağlayıcıdan gerçek üretime geçişin ilk adımı.
///
/// Bir ücretli sağlayıcıya göre eksikleri var — üretim süresi öngörülemez,
/// kalite dalgalı, SLA yok. Bu yüzden yönlendirme politikasında (§9.3)
/// TEK sağlayıcı olarak değil, ücretsiz varsayılan olarak duruyor; bütçe
/// açıldığında üstüne ücretli bir sağlayıcı eklenir ve bu yedeğe düşer.
public sealed class PollinationsImageProvider(HttpClient http) : IImageProvider
{
    /// Üretim uzun sürebiliyor; kısa timeout gereksiz başarısızlık üretir.
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    private const string BaseAddress = "https://image.pollinations.ai/prompt/";

    public string Key => "pollinations";

    public ImageProviderKind Kind => ImageProviderKind.Generative;

    public Task<Result<ProviderResponse<IReadOnlyList<ImageCandidate>>>> FindAsync(
        ImageQuery query, ProviderContext context, CancellationToken cancellationToken)
        => Task.FromResult(Result.Failure<ProviderResponse<IReadOnlyList<ImageCandidate>>>(
            Error.Permanent("pollinations.not_stock", "Bu sağlayıcı arama yapmaz, üretir.")));

    public async Task<Result<ProviderResponse<GeneratedImage>>> GenerateAsync(
        ImagePrompt prompt, ProviderContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        var text = prompt.StyleHint is { } style ? $"{prompt.Text}, {style}" : prompt.Text;

        // Tohum veriliyorsa aynı prompt aynı görseli veriyor — render
        // önbelleğini ve determinizmi anlamlı kılan şey bu.
        var url = new Uri(
            BaseAddress
            + Uri.EscapeDataString(text)
            + $"?width={prompt.Width.ToString(CultureInfo.InvariantCulture)}"
            + $"&height={prompt.Height.ToString(CultureInfo.InvariantCulture)}"
            + "&nologo=true"
            + (prompt.Seed is { } seed ? $"&seed={seed.ToString(CultureInfo.InvariantCulture)}" : string.Empty));

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(Timeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("BytemountsAiStudio/0.1");

            using var response = await http.SendAsync(request, cts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // 429 GECICI DEGIL, KAYNAK hatasi: hemen yeniden denemek
                // ayni cevabi alir. Is kuyruguna erteleme sinyali gidiyor;
                // deneme sayaci artmiyor ve run dusmuyor (ADR-011).
                if ((int)response.StatusCode == 429)
                {
                    var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(20);

                    return Error.Resource("pollinations.rate_limited",
                        "Pollinations istek sinirini uyguladi.", retryAfter);
                }

                return (int)response.StatusCode >= 500
                    ? Error.Transient("pollinations.unavailable", $"HTTP {(int)response.StatusCode}")
                    : Error.Permanent("pollinations.rejected", $"HTTP {(int)response.StatusCode}");
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            var mime = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";

            // Sunucu bazen hata sayfasını 200 ile döndürüyor; boyut kontrolü
            // bunu yakalıyor. Bozuk görsel render'da anlamsız bir hataya
            // dönüşürdü.
            if (bytes.Length < 1024)
            {
                return Error.Transient("pollinations.too_small",
                    $"Görsel çok küçük ({bytes.Length} bayt); üretim başarısız olmuş olabilir.");
            }

            if (!mime.StartsWith("image/", StringComparison.Ordinal))
            {
                return Error.Transient("pollinations.not_image", $"Beklenmeyen içerik tipi: {mime}");
            }

            return Result.Success(new ProviderResponse<GeneratedImage>(
                new GeneratedImage
                {
                    Data = bytes,
                    MimeType = mime,
                    Width = prompt.Width,
                    Height = prompt.Height,
                    License = new LicenseInfo
                    {
                        Name = "Pollinations (ücretsiz kullanım)",
                        Url = new Uri("https://pollinations.ai"),
                        RequiresAttribution = false,
                        CapturedAt = DateTimeOffset.UtcNow,
                    },
                },
                new UsageUnits { Images = 1 }));
        }
        catch (HttpRequestException ex)
        {
            return Error.Transient("pollinations.unreachable", ex.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Error.Transient("pollinations.timeout",
                $"{Timeout.TotalMinutes:0} dakikada görsel üretilmedi.");
        }
    }
}
