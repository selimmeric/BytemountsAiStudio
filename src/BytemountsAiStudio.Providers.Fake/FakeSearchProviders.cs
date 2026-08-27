using System.Globalization;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;

namespace BytemountsAiStudio.Providers.Fake;

/// Deterministik sahte arama.
///
/// Sonuçlar sorgudan türetilir: aynı sorgu her zaman aynı URL listesini verir.
/// Alan adı beyaz/kara listesi burada da uygulanır — gerçek sağlayıcıda
/// filtreleme varken sahtede olmasaydı, filtrenin bozulduğunu ancak üretimde
/// fark ederdik.
public sealed class FakeSearchProvider : ISearchProvider
{
    private static readonly (string Domain, SourceType Type)[] Catalog =
    [
        ("tr.wikipedia.org", SourceType.Encyclopedia),
        ("en.wikipedia.org", SourceType.Encyclopedia),
        ("nasa.gov", SourceType.Official),
        ("who.int", SourceType.Official),
        ("arxiv.org", SourceType.Academic),
        ("nature.com", SourceType.Academic),
        ("reuters.com", SourceType.News),
        ("bbc.com", SourceType.News),
        ("reddit.com", SourceType.Community),
        ("ornek-blog.net", SourceType.Blog),
    ];

    public string Key => "fake-search";

    public Task<Result<ProviderResponse<IReadOnlyList<SearchHit>>>> SearchAsync(
        SearchQuery query,
        ProviderContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var allowed = Catalog.Where(entry => IsAllowed(entry.Domain, query)).ToList();
        var hits = new List<SearchHit>();

        for (var i = 0; i < Math.Min(query.MaxResults, allowed.Count); i++)
        {
            var (domain, type) = allowed[i];
            var hash = Determinism.Hash(query.Text, domain, i.ToString(CultureInfo.InvariantCulture));
            var slug = Determinism.Token(hash, 10);

            hits.Add(new SearchHit
            {
                Url = new Uri(Determinism.Format($"https://{domain}/{slug}")),
                Title = Determinism.Format($"{query.Text} — {domain} kaydı"),
                Snippet = Determinism.Format($"'{query.Text}' konusunda sahte özet ({slug})."),
                SourceType = type,
                Rank = i + 1,
            });
        }

        return Task.FromResult(Result.Success(
            new ProviderResponse<IReadOnlyList<SearchHit>>(hits, UsageUnits.OfRequests())));
    }

    private static bool IsAllowed(string domain, SearchQuery query)
    {
        if (query.BlockedDomains.Any(b => Matches(domain, b)))
        {
            return false;
        }

        return query.AllowedDomains.Count == 0
            || query.AllowedDomains.Any(a => Matches(domain, a));
    }

    /// "*.edu" gibi basit joker desteği — gerçek sağlayıcıdaki kuralın aynısı.
    private static bool Matches(string domain, string pattern)
        => pattern.StartsWith("*.", StringComparison.Ordinal)
            ? domain.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase)
            : domain.Equals(pattern, StringComparison.OrdinalIgnoreCase);
}

/// Deterministik sahte sayfa çekme.
///
/// Üretilen metin, iddia çıkarma adımının sınanabilmesi için birkaç cümle
/// içerir — tek satırlık bir metinde "alıntı bu iddiayı destekliyor mu"
/// sorusu anlamsız olurdu.
public sealed class FakeWebFetchProvider : IWebFetchProvider
{
    /// Bu alan adları ödeme duvarlı sayılır; iddia çıkarımında atlanmaları test edilir.
    private static readonly HashSet<string> PaywalledDomains =
        new(StringComparer.OrdinalIgnoreCase) { "paywall.example.com" };

    public string Key => "fake-fetch";

    public Task<Result<ProviderResponse<FetchedDocument>>> FetchAsync(
        Uri url,
        ProviderContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        cancellationToken.ThrowIfCancellationRequested();

        var hash = Determinism.Hash(url.ToString());
        var token = Determinism.Token(hash, 8);

        var text = string.Join(' ',
            Determinism.Format($"Bu, {url.Host} adresinden alınmış sahte bir belgedir."),
            Determinism.Format($"Birinci iddia: {token} değeri {Determinism.Range(hash, 10, 99)} olarak ölçülmüştür."),
            Determinism.Format($"İkinci iddia: kayıtlar {1400 + Determinism.Range(hash, 0, 600)} yılına dayanır."),
            "Üçüncü cümle bağlam sağlar ve doğrudan bir iddia içermez.");

        var document = new FetchedDocument
        {
            Url = url,
            Title = Determinism.Format($"{url.Host} — {token}"),
            MainText = text,
            ContentHash = Determinism.Format($"{hash:x16}"),
            FetchedAt = Determinism.Epoch,
            IsPaywalled = PaywalledDomains.Contains(url.Host),
        };

        return Task.FromResult(Result.Success(
            new ProviderResponse<FetchedDocument>(document, UsageUnits.OfRequests())));
    }
}
