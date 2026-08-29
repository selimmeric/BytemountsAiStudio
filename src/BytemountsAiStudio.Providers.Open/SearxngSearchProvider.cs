using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;

namespace BytemountsAiStudio.Providers.Open;

/// SearXNG üzerinden genel web araması — kendi sunucunuzda, anahtarsız.
///
/// §9.5: anahtarsız gerçek web aramasının en sağlam yolu. Wikipedia
/// ansiklopedik konularda yeterli ama güncel olaylar, haber ve niş konular
/// için genel arama gerekiyor.
///
/// Kendi sunucumuzda koştuğu için üçüncü bir tarafın kullanım şartlarına
/// tabi değiliz ve kota da yok — sınır yalnızca SearXNG'nin arkasındaki
/// motorların bizi kesip kesmediği.
public sealed class SearxngSearchProvider(HttpClient http, Uri? baseAddress = null) : ISearchProvider
{
    /// Varsayılan adres — `config/providers.json` ile AYNI olmak
    /// zorunda; `ProviderEndpointTests` ikisini karşılaştırıyor.
    public static Uri DefaultEndpoint { get; } = new("http://localhost:8888");

    public const string EndpointVariable = "BMAI_SEARXNG_URL";

    private readonly Uri _base = baseAddress
        ?? Endpoints.Resolve(EndpointVariable, "http://localhost:8888");

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// Alan adından kaynak türü tahmini.
    ///
    /// Kaba ama işe yarıyor: güven skorlaması ve QC kuralları kaynak türüne
    /// bakıyor, "bilinmiyor" olarak bırakmak o kararları kör bırakırdı.
    private static readonly (string Suffix, SourceType Type)[] DomainHints =
    [
        (".gov", SourceType.Official),
        (".gov.tr", SourceType.Official),
        (".edu", SourceType.Academic),
        (".edu.tr", SourceType.Academic),
        (".ac.uk", SourceType.Academic),
        ("arxiv.org", SourceType.Academic),
        ("nature.com", SourceType.Academic),
        ("wikipedia.org", SourceType.Encyclopedia),
        ("britannica.com", SourceType.Encyclopedia),
        ("reddit.com", SourceType.Community),
        ("quora.com", SourceType.Community),
    ];

    public string Key => "searxng";

    /// Sunucu ayakta mı. Yönlendirme politikası kapalı bir SearXNG'ye
    /// takılıp kalmasın diye.
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http
                .GetAsync(new Uri(_base, "/healthz"), cancellationToken)
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    public async Task<Result<ProviderResponse<IReadOnlyList<SearchHit>>>> SearchAsync(
        SearchQuery query, ProviderContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var language = (query.Language ?? context?.Language)?.Value ?? "all";

        var url = new Uri(_base,
            "/search?format=json"
            + $"&q={Uri.EscapeDataString(query.Text)}"
            + $"&language={Uri.EscapeDataString(language)}"
            + "&safesearch=1");

        try
        {
            using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // 403 neredeyse her zaman aynı sebep: `formats` listesinde
                // json yok. Mesajın bunu söylemesi teşhis süresini
                // dakikalardan saniyeye indiriyor.
                if ((int)response.StatusCode == 403)
                {
                    return Error.Permanent("searxng.json_disabled",
                        "SearXNG JSON biçimini reddetti. `docker/searxng/settings.yml` "
                        + "içinde search.formats listesine 'json' eklenmiş olmalı.");
                }

                return (int)response.StatusCode is >= 500 or 429
                    ? Error.Transient("searxng.unavailable", $"HTTP {(int)response.StatusCode}")
                    : Error.Permanent("searxng.rejected", $"HTTP {(int)response.StatusCode}");
            }

            var parsed = await response.Content
                .ReadFromJsonAsync<SearxngResponse>(Json, cancellationToken)
                .ConfigureAwait(false);

            var hits = new List<SearchHit>();
            var rank = 1;

            foreach (var item in parsed?.Results ?? [])
            {
                if (item.Url is null || !Uri.TryCreate(item.Url, UriKind.Absolute, out var hitUrl))
                {
                    continue;
                }

                if (!IsAllowed(hitUrl.Host, query))
                {
                    continue;
                }

                hits.Add(new SearchHit
                {
                    Url = hitUrl,
                    Title = item.Title ?? hitUrl.Host,
                    Snippet = item.Content,
                    SourceType = ClassifyDomain(hitUrl.Host),
                    Rank = rank++,
                });

                if (hits.Count >= query.MaxResults)
                {
                    break;
                }
            }

            return Result.Success(new ProviderResponse<IReadOnlyList<SearchHit>>(
                hits, UsageUnits.OfRequests()));
        }
        catch (HttpRequestException ex)
        {
            return Error.Transient("searxng.unreachable",
                $"SearXNG'ye ulaşılamadı ({_base}). `docker compose --profile tools up -d searxng`. {ex.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Error.Transient("searxng.timeout", "SearXNG zaman aşımına uğradı.");
        }
    }

    internal static SourceType ClassifyDomain(string host)
    {
        foreach (var (suffix, type) in DomainHints)
        {
            if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                || host.Contains(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return type;
            }
        }

        return SourceType.Unknown;
    }

    /// §2.3/15: araştırma "her siteyi kazı" değil, izinli kaynak listesidir.
    /// Filtre burada da uygulanıyor — çağırana bırakılsaydı bir yerde atlanırdı.
    private static bool IsAllowed(string host, SearchQuery query)
    {
        if (query.BlockedDomains.Any(b => Matches(host, b)))
        {
            return false;
        }

        return query.AllowedDomains.Count == 0
            || query.AllowedDomains.Any(a => Matches(host, a));
    }

    private static bool Matches(string host, string pattern)
        => pattern.StartsWith("*.", StringComparison.Ordinal)
            ? host.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase)
            : host.Equals(pattern, StringComparison.OrdinalIgnoreCase);

    private sealed record SearxngResponse(string? Query, List<SearxngResult>? Results);

    private sealed record SearxngResult(string? Title, string? Url, string? Content, string? Engine);
}
