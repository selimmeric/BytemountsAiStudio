using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Content;

namespace BytemountsAiStudio.Contracts.Providers;

/// Kaynağın türü. Güven skoru ve QC kuralları buna bakar.
public enum SourceType
{
    Unknown = 0,
    Encyclopedia = 1,
    Official = 2,
    Academic = 3,
    News = 4,
    Community = 5,
    Blog = 6,
}

public sealed record SearchQuery
{
    public required string Text { get; init; }

    /// Sorgu dili içerik dilinden FARKLI olabilir ve bu normal durumdur:
    /// Türkçe içerik çoğu konuda İngilizce kaynaktan üretilecek (§20.1).
    public LanguageTag? Language { get; init; }

    public int MaxResults { get; init; } = 10;

    /// Boşsa serbest; doluysa yalnızca bu alan adları kabul edilir.
    /// §2.3: araştırma "her siteyi kazı" değil, izinli kaynak listesidir.
    public IReadOnlyList<string> AllowedDomains { get; init; } = [];

    public IReadOnlyList<string> BlockedDomains { get; init; } = [];
}

public sealed record SearchHit
{
    public required Uri Url { get; init; }

    public required string Title { get; init; }

    /// Arama motorunun döndürdüğü özet. Kaynak metni DEĞİL — iddia çıkarmak
    /// için yetersizdir, yalnızca hangi sayfanın çekileceğine karar verdirir.
    public string? Snippet { get; init; }

    public SourceType SourceType { get; init; } = SourceType.Unknown;

    public int Rank { get; init; }
}

public interface ISearchProvider : IProvider
{
    Task<Result<ProviderResponse<IReadOnlyList<SearchHit>>>> SearchAsync(
        SearchQuery query,
        ProviderContext context,
        CancellationToken cancellationToken);
}

/// Bir sayfanın çekilmiş ve ayıklanmış hâli.
public sealed record FetchedDocument
{
    public required Uri Url { get; init; }

    public required string Title { get; init; }

    /// Menü, reklam ve altbilgiden ayıklanmış ana metin.
    public required string MainText { get; init; }

    /// İçeriğin sha256'sı: aynı sayfa iki kez çekilirse tekilleştirilir ve
    /// "kaynak değişmiş mi" sorusu cevaplanabilir olur.
    public required string ContentHash { get; init; }

    public DateTimeOffset FetchedAt { get; init; }

    public LanguageTag? DetectedLanguage { get; init; }

    /// Ödeme duvarı tespit edildiyse true. İçerik eksik olabilir; iddia
    /// çıkarmak için kullanılmamalı.
    public bool IsPaywalled { get; init; }
}

/// Sayfa içeriği çekme.
///
/// Ayrı bir arayüz, çünkü iki farklı uygulaması olacak: basit HTTP istemcisi
/// ve tarayıcı render'lı çekme (Playwright, tools-sidecar). JS ile yüklenen
/// sayfalar düz HTTP ile alınamıyor.
///
/// robots.txt kontrolü ve alan adı beyaz listesi UYGULAMANIN içinde yapılır,
/// çağıranın sorumluluğunda değil — tek bir yerde olmazsa er geç atlanır.
public interface IWebFetchProvider : IProvider
{
    Task<Result<ProviderResponse<FetchedDocument>>> FetchAsync(
        Uri url,
        ProviderContext context,
        CancellationToken cancellationToken);
}
