using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Errors;

namespace BytemountsAiStudio.Providers.Open;

/// Wikipedia arama ve içerik çekme — resmî API, anahtarsız.
///
/// §9.5: "En iyi 10'lar / tarih / gizem" gibi içeriğin büyük kısmı burada.
/// Ücretli arama sağlayıcılarına gitmeden önce sorulacak ilk yer.
///
/// İki arayüzü birden karşılıyor: <see cref="ISearchProvider"/> (hangi
/// sayfalar) ve <see cref="IWebFetchProvider"/> (sayfanın metni). Ayrı ayrı
/// olması gerekmiyor çünkü aynı API'nin iki uç noktası; ama arayüzler ayrı
/// kalıyor ki başka sağlayıcılar birini uygulayıp diğerini uygulamayabilsin.
public sealed class WikipediaProvider(HttpClient http, LanguageTag? defaultLanguage = null)
    : ISearchProvider, IWebFetchProvider
{
    private readonly LanguageTag _default = defaultLanguage ?? LanguageTag.Create("tr-TR");

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// Wikimedia kullanım şartları tanımlayıcı bir User-Agent istiyor.
    /// Vermemek engellenme sebebi.
    private const string UserAgent = "BytemountsAiStudio/0.1 (icerik uretim arastirmasi)";

    public string Key => "wikipedia";

    public async Task<Result<ProviderResponse<IReadOnlyList<SearchHit>>>> SearchAsync(
        SearchQuery query, ProviderContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var language = (query.Language ?? context?.Language ?? _default).Primary;

        var url = new Uri(
            $"https://{language}.wikipedia.org/w/api.php"
            + "?action=query&list=search&format=json&utf8=1"
            + $"&srsearch={Uri.EscapeDataString(query.Text)}"
            + $"&srlimit={Math.Clamp(query.MaxResults, 1, 50).ToString(CultureInfo.InvariantCulture)}");

        var response = await GetAsync<WikiSearchResponse>(url, cancellationToken).ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result.Failure<ProviderResponse<IReadOnlyList<SearchHit>>>(response.Error);
        }

        var results = response.Value?.Query?.Search ?? [];
        var hits = new List<SearchHit>();
        var rank = 1;

        foreach (var item in results)
        {
            var title = item.Title ?? string.Empty;

            hits.Add(new SearchHit
            {
                Url = new Uri($"https://{language}.wikipedia.org/wiki/{Uri.EscapeDataString(title.Replace(' ', '_'))}"),
                Title = title,
                // `snippet` HTML vurgu etiketleri içeriyor; temizlenmezse
                // iddia çıkarma adımına `<span class="searchmatch">` gider.
                Snippet = StripHtml(item.Snippet),
                SourceType = SourceType.Encyclopedia,
                Rank = rank++,
            });
        }

        return Result.Success(new ProviderResponse<IReadOnlyList<SearchHit>>(
            hits, UsageUnits.OfRequests()));
    }

    public async Task<Result<ProviderResponse<FetchedDocument>>> FetchAsync(
        Uri url, ProviderContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (!url.Host.EndsWith("wikipedia.org", StringComparison.OrdinalIgnoreCase))
        {
            return Error.Permanent("wikipedia.wrong_host",
                $"Bu sağlayıcı yalnızca Wikipedia sayfalarını çeker: {url.Host}");
        }

        var language = url.Host.Split('.')[0];
        var title = Uri.UnescapeDataString(url.AbsolutePath.Replace("/wiki/", string.Empty, StringComparison.Ordinal));

        // `extracts` düz metin döndürüyor; HTML ayrıştırmaya gerek kalmıyor
        // ve şablon/altbilgi gürültüsü de gelmiyor.
        var apiUrl = new Uri(
            $"https://{language}.wikipedia.org/w/api.php"
            + "?action=query&prop=extracts&explaintext=1&exsectionformat=plain&format=json&utf8=1"
            + $"&titles={Uri.EscapeDataString(title)}");

        var response = await GetAsync<WikiExtractResponse>(apiUrl, cancellationToken).ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result.Failure<ProviderResponse<FetchedDocument>>(response.Error);
        }

        var page = response.Value?.Query?.Pages?.Values.FirstOrDefault();

        if (page?.Extract is not { Length: > 0 } extract)
        {
            return Error.Permanent("wikipedia.no_content", $"Sayfada metin yok: {title}");
        }

        var hash = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(extract)));

        return Result.Success(new ProviderResponse<FetchedDocument>(
            new FetchedDocument
            {
                Url = url,
                Title = page.Title ?? title,
                MainText = extract,
                ContentHash = hash,
                FetchedAt = DateTimeOffset.UtcNow,
                DetectedLanguage = LanguageTag.TryCreate(language) is { IsSuccess: true } tag ? tag.Value : null,
                IsPaywalled = false,
            },
            UsageUnits.OfRequests()));
    }

    private async Task<Result<T?>> GetAsync<T>(Uri url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(UserAgent);

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // 429 ve 5xx geçici: Wikimedia yoğunlukta kısıtlıyor ama
                // birkaç saniye sonra yine cevap veriyor.
                return (int)response.StatusCode is >= 500 or 429
                    ? Error.Transient("wikipedia.unavailable", $"HTTP {(int)response.StatusCode}")
                    : Error.Permanent("wikipedia.rejected", $"HTTP {(int)response.StatusCode}");
            }

            return Result.Success(await response.Content
                .ReadFromJsonAsync<T>(Json, cancellationToken).ConfigureAwait(false));
        }
        catch (HttpRequestException ex)
        {
            return Error.Transient("wikipedia.unreachable", ex.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Error.Transient("wikipedia.timeout", "Wikipedia zaman aşımına uğradı.");
        }
    }

    /// Arama sonuçlarındaki vurgu etiketlerini temizler.
    internal static string StripHtml(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(html.Length);
        var inside = false;

        foreach (var c in html)
        {
            switch (c)
            {
                case '<': inside = true; break;
                case '>': inside = false; break;
                default:
                    if (!inside)
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        return builder.ToString()
            .Replace("&quot;", "\"", StringComparison.Ordinal)
            .Replace("&amp;", "&", StringComparison.Ordinal)
            .Replace("&nbsp;", " ", StringComparison.Ordinal)
            .Trim();
    }

    private sealed record WikiSearchResponse(WikiSearchQuery? Query);

    private sealed record WikiSearchQuery(List<WikiSearchItem>? Search);

    private sealed record WikiSearchItem(string? Title, string? Snippet);

    private sealed record WikiExtractResponse(WikiExtractQuery? Query);

    private sealed record WikiExtractQuery(
        [property: JsonPropertyName("pages")] Dictionary<string, WikiPage>? Pages);

    private sealed record WikiPage(string? Title, string? Extract);
}
