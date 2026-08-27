using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;

namespace BytemountsAiStudio.Providers.Open;

/// Pexels stok görsel arama (P1-17).
///
/// Openverse'ten farkı KALİTE: Pexels'te sonuçlar elle küratörlenmiş ve
/// bir belgesel anlatıya yakışan kareler bulmak çok daha kolay.
/// Karşılığında bir anahtar istiyor — ücretsiz ama kayıt gerektiriyor.
///
/// LİSANS KAYDI BURADA DA ZORUNLU (§2.3/14).
///
/// Pexels lisansı atıf ZORUNLU KILMIYOR ama "takdir edilir" diyor ve
/// kuralları zamanla değişebiliyor. Bu yüzden atıf bilgisi her sonuçla
/// birlikte saklanıyor: "o gün ne yazıyordu" sorusunun cevabı ancak
/// alındığı anda saklanmışsa var. Sonradan toplamak imkânsız — fotoğraf
/// silinmiş olabiliyor.
public sealed class PexelsImageProvider(HttpClient http, ICredentialSource? credentials = null) : IImageProvider
{
    private const string SearchAddress = "https://api.pexels.com/v1/search";

    public const string KeyEnvironmentVariable = "PEXELS_API_KEY";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public string Key => "pexels";

    public ImageProviderKind Kind => ImageProviderKind.Stock;

    public async Task<Result<ProviderResponse<IReadOnlyList<ImageCandidate>>>> FindAsync(
        ImageQuery query, ProviderContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var apiKey = ResolveKey();

        if (apiKey.IsFailure)
        {
            return Result.Failure<ProviderResponse<IReadOnlyList<ImageCandidate>>>(apiKey.Error);
        }

        var address = SearchAddress
            + $"?query={Uri.EscapeDataString(query.Terms)}"
            + $"&per_page={Math.Clamp(query.MaxResults, 1, 80).ToString(CultureInfo.InvariantCulture)}"
            + Orientation(query.PreferredAspectRatio);

        using var message = new HttpRequestMessage(HttpMethod.Get, address);

        // Pexels `Authorization` başlığını şemasız istiyor: "Bearer"
        // öneki eklemek 401 veriyor.
        message.Headers.TryAddWithoutValidation("Authorization", apiKey.Value);

        try
        {
            using var response = await http.SendAsync(message, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return Classify<ProviderResponse<IReadOnlyList<ImageCandidate>>>(response);
            }

            var parsed = await response.Content
                .ReadFromJsonAsync<SearchResponse>(Json, cancellationToken)
                .ConfigureAwait(false);

            var captured = DateTimeOffset.UtcNow;
            var candidates = new List<ImageCandidate>();

            foreach (var photo in parsed?.Photos ?? [])
            {
                // ÖLÇÜLERİ OLMAYAN sonuç atlanıyor: kadraja uydurma
                // kararı en–boy oranına bakıyor ve bilinmeyen bir oran
                // o kararı kör bırakırdı.
                if (photo.Width <= 0 || photo.Height <= 0)
                {
                    continue;
                }

                // BÜYÜK boy tercih ediliyor: dikey videoda 1080 genişlik
                // gerekiyor ve küçük bir kare büyütüldüğünde bulanık
                // çıkıyor. `original` seçilmiyor çünkü bazen 8000 piksel
                // ve indirmesi boşuna uzun.
                var url = photo.Src?.Large2x ?? photo.Src?.Large ?? photo.Src?.Original;

                if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var parsedUrl))
                {
                    continue;
                }

                candidates.Add(new ImageCandidate
                {
                    Url = parsedUrl,
                    Width = photo.Width,
                    Height = photo.Height,
                    Description = string.IsNullOrWhiteSpace(photo.Alt) ? null : photo.Alt,
                    License = new LicenseInfo
                    {
                        Name = "Pexels License",
                        Url = new Uri("https://www.pexels.com/license/"),
                        Author = photo.Photographer,
                        // Pexels atıf ZORUNLU KILMIYOR. Yine de false
                        // yazmak yerine bu alanı bilinçli işaretliyoruz:
                        // kural değişirse buradaki tek satır değişecek
                        // ve eski kayıtlar o günkü hâli taşımaya devam
                        // edecek.
                        RequiresAttribution = false,
                        CapturedAt = captured,
                    },
                });
            }

            return Result.Success(new ProviderResponse<IReadOnlyList<ImageCandidate>>(
                candidates, UsageUnits.OfRequests()));
        }
        catch (HttpRequestException ex)
        {
            return Error.Transient("pexels.unreachable", ex.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Error.Transient("pexels.timeout", "Pexels zaman aşımına uğradı.");
        }
    }

    /// Pexels görsel ÜRETMİYOR.
    ///
    /// Kalıcı hata, çünkü yeniden denemek bu sağlayıcıya üretim
    /// yeteneği kazandırmıyor. Sessizce boş dönmek, çağıran tarafta
    /// "görsel bulunamadı" gibi görünür ve asıl sebep gizlenirdi.
    public Task<Result<ProviderResponse<GeneratedImage>>> GenerateAsync(
        ImagePrompt prompt, ProviderContext context, CancellationToken cancellationToken)
        => Task.FromResult(Result.Failure<ProviderResponse<GeneratedImage>>(
            Error.Permanent("pexels.not_generative", "Pexels stok sağlayıcı; görsel üretmiyor.")));

    /// Dikey video için DİKEY sonuç isteniyor.
    ///
    /// Yatay bir kareyi 9:16'ya kırpmak, karenin çoğunu atmak demek —
    /// ve atılan kısım genellikle konunun kendisi oluyor.
    internal static string Orientation(double? aspectRatio) => aspectRatio switch
    {
        null => string.Empty,
        < 0.9 => "&orientation=portrait",
        > 1.1 => "&orientation=landscape",
        _ => "&orientation=square",
    };

    private Result<string> ResolveKey()
    {
        var value = credentials is not null
            ? credentials.Get(KeyEnvironmentVariable)
            : Environment.GetEnvironmentVariable(KeyEnvironmentVariable);

        return string.IsNullOrWhiteSpace(value)
            ? Error.Permanent("pexels.no_key",
                $"Pexels için anahtar yok ({KeyEnvironmentVariable} tanımlı değil). "
                + "Anahtarsız yol: Openverse (P1-17a).")
            : Result.Success(value);
    }

    /// Pexels kota aşımında 429 döndürüyor ve `X-Ratelimit-Reset`
    /// başlığında bir Unix zamanı veriyor. Kotanın SAATLİK olduğu
    /// düşünülürse bu bir erteleme — kaynak hatası.
    private static Result<T> Classify<T>(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;

        return status switch
        {
            429 => Error.Resource("pexels.quota", "Pexels kotası doldu.", ResetAfter(response)),
            401 or 403 => Error.Permanent("pexels.unauthorized", "Pexels anahtarı reddedildi."),
            >= 500 => Error.Transient("pexels.server_error", $"HTTP {status}"),
            _ => Error.Permanent("pexels.rejected", $"HTTP {status}"),
        };
    }

    /// Sunucunun söylediği sıfırlama anı, tahmin edilene tercih
    /// ediliyor: kendi kestirimimiz ya çok erken denerdi ya da
    /// gereğinden uzun beklerdi.
    internal static TimeSpan ResetAfter(HttpResponseMessage response)
    {
        const int fallbackHours = 1;

        if (!response.Headers.TryGetValues("X-Ratelimit-Reset", out var values)
            || !long.TryParse(values.FirstOrDefault(), CultureInfo.InvariantCulture, out var epoch))
        {
            return TimeSpan.FromHours(fallbackHours);
        }

        var remaining = DateTimeOffset.FromUnixTimeSeconds(epoch) - DateTimeOffset.UtcNow;

        // GEÇMİŞTE kalmış bir sıfırlama anı ya saat kayması ya da
        // bozuk bir başlık; ikisinde de hemen denemek yerine
        // varsayılana düşülüyor.
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.FromHours(fallbackHours);
    }

    internal sealed record SearchResponse(List<Photo>? Photos);

    internal sealed record Photo(int Width, int Height, string? Photographer, string? Alt, Source? Src);

    internal sealed record Source(
        string? Original,
        string? Large,
        [property: JsonPropertyName("large2x")] string? Large2x);
}
