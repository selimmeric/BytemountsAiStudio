using System.Text.Json;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;

namespace BytemountsAiStudio.Providers.Open;

/// DuckDuckGo Instant Answer (P1-05).
///
/// NE OLMADIĞI önce söylenmeli: bu bir web araması DEĞİL. DuckDuckGo'nun
/// sonuç sayfasını kazımak kullanım şartlarına aykırı ve bot tespitine
/// takılıyor; burada kullanılan, DuckDuckGo'nun bu iş için AÇTIĞI
/// belgelenmiş uç nokta. Karşılığında yalnızca "anında cevap" veriyor:
/// ansiklopedik özet ve birkaç ilgili başlık.
///
/// Bu yüzden katalogda `enabled: false` ve yönlendirmede ÜÇÜNCÜ sırada.
/// Wikipedia ve SearXNG'nin bulamadığı bir konuda hâlâ bir cevap
/// dönebiliyor, ve anahtar gerektirmiyor; yedek olarak değeri bu kadar.
///
/// Gerçek web araması gerektiğinde doğru cevap SearXNG (kendi
/// sunucunuzda) ya da Brave API (anahtar ücretsiz).
public sealed class DuckDuckGoProvider(HttpClient http) : ISearchProvider
{
    private const string Endpoint = "https://api.duckduckgo.com/";

    private const string UserAgent =
        "BytemountsAiStudio/0.1 (icerik arastirma; +https://github.com/selimmeric/BytemountsAiStudio)";

    public string Key => "duckduckgo-ia";

    public async Task<Result<ProviderResponse<IReadOnlyList<SearchHit>>>> SearchAsync(
        SearchQuery query, ProviderContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        // `no_html=1` şart: özet metni HTML etiketleriyle geliyor ve
        // iddia çıkarımına giren metinde etiket olmamalı.
        var url = new Uri(
            $"{Endpoint}?q={Uri.EscapeDataString(query.Text)}"
            + "&format=json&no_html=1&skip_disambig=1&no_redirect=1");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(UserAgent);

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;

                return status is 429 or >= 500
                    ? Error.Transient("ddg.unavailable", $"HTTP {status}")
                    : Error.Permanent("ddg.rejected", $"HTTP {status}");
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            using var json = await JsonDocument
                .ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return Result.Success(new ProviderResponse<IReadOnlyList<SearchHit>>(
                ReadHits(json.RootElement, query), new UsageUnits()));
        }
        catch (HttpRequestException ex)
        {
            return Error.Transient("ddg.unreachable", ex.Message);
        }
        catch (JsonException ex)
        {
            // Uç nokta bazen sorgu anlaşılmadığında HTML dönüyor.
            return Error.Transient("ddg.bad_json", ex.Message);
        }
    }

    private static List<SearchHit> ReadHits(JsonElement root, SearchQuery query)
    {
        var hits = new List<SearchHit>();
        var rank = 0;

        // Ana özet: varsa en değerli sonuç bu.
        var abstractText = Text(root, "AbstractText") ?? Text(root, "Abstract");
        var abstractUrl = Text(root, "AbstractURL");

        if (!string.IsNullOrWhiteSpace(abstractText)
            && Uri.TryCreate(abstractUrl, UriKind.Absolute, out var source))
        {
            hits.Add(new SearchHit
            {
                Url = source,
                Title = Text(root, "Heading") ?? query.Text,
                Snippet = abstractText,
                SourceType = Classify(Text(root, "AbstractSource")),
                Rank = rank++,
            });
        }

        if (!root.TryGetProperty("RelatedTopics", out var related)
            || related.ValueKind != JsonValueKind.Array)
        {
            return hits;
        }

        foreach (var topic in related.EnumerateArray())
        {
            if (hits.Count >= query.MaxResults)
            {
                break;
            }

            // İç içe gruplar var ("Topics" alanı); tek düzeye
            // indirmiyoruz çünkü grup başlıkları içerik taşımıyor.
            if (!topic.TryGetProperty("FirstURL", out var urlJson)
                || !Uri.TryCreate(urlJson.GetString(), UriKind.Absolute, out var topicUrl))
            {
                continue;
            }

            var text = Text(topic, "Text");

            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            hits.Add(new SearchHit
            {
                Url = topicUrl,
                // İlgili başlıklarda ayrı bir başlık alanı yok; metnin
                // ilk cümlesi başlık yerine geçiyor.
                Title = FirstClause(text),
                Snippet = text,
                SourceType = SourceType.Encyclopedia,
                Rank = rank++,
            });
        }

        return hits;
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string FirstClause(string text)
    {
        var stop = text.IndexOf(" - ", StringComparison.Ordinal);

        return stop > 0 ? text[..stop] : text.Length > 80 ? text[..80] : text;
    }

    /// Kaynak adına göre tür. Instant Answer'ın büyük kısmı Wikipedia'dan
    /// geliyor ve bunu ansiklopedi saymak güven skorunu doğru kuruyor.
    private static SourceType Classify(string? source)
        => source?.Contains("Wikipedia", StringComparison.OrdinalIgnoreCase) == true
            ? SourceType.Encyclopedia
            : SourceType.Unknown;
}
