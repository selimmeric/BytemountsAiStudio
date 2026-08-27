using System.Globalization;
using System.Collections.Concurrent;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Core.Time;

namespace BytemountsAiStudio.Providers.Fake;

/// Sahte yayıncı. YouTube'un iki sert davranışını taklit eder:
///   1. Kota — günlük birim havuzu dolunca <see cref="ErrorKind.Resource"/>
///      döner. Hata değil, erteleme.
///   2. Idempotency — aynı anahtarla ikinci yükleme YENİ video üretmez.
///
/// İkisi de gerçek sistemde ancak üretimde ortaya çıkan sorunlar. Sahtede
/// taklit edilmezlerse çift yükleme koruması hiç sınanmamış olur (§2.4/16).
public sealed class FakePublisher : IPublisher
{
    private readonly ConcurrentDictionary<string, PublishResult> _byIdempotencyKey =
        new(StringComparer.Ordinal);

    private int _quotaRemaining;

    public FakePublisher(int dailyQuota = 10_000)
    {
        _quotaRemaining = dailyQuota;
    }

    public string Key => "fake-publisher";

    public string Platform => "fake";

    public PublishCapabilities Capabilities { get; } = new()
    {
        MaxTitleLength = 100,
        MaxDescriptionLength = 5_000,
        MaxTagsTotalLength = 500,
        MaxDuration = new Ms(12 * 60 * 60 * 1000),
        SupportsScheduling = true,
        SupportsCustomThumbnail = true,
        QuotaCostPerPublish = 1_600,
    };

    /// Gerçekte kaç yeni video oluştu. Idempotency testinin ölçtüğü sayı bu:
    /// iki çağrı, tek kayıt.
    public int PublishedCount => _byIdempotencyKey.Count;

    public int QuotaRemaining => _quotaRemaining;

    public Task<Result<ProviderResponse<PublishResult>>> PublishAsync(
        PublishRequest request,
        ProviderContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (_byIdempotencyKey.TryGetValue(request.IdempotencyKey, out var existing))
        {
            // Kota da harcanmaz: ikinci çağrı gerçek bir yükleme değil.
            return Task.FromResult(Result.Success(
                new ProviderResponse<PublishResult>(existing, UsageUnits.None)));
        }

        var titleError = ValidateLimits(request.Metadata);
        if (titleError is not null)
        {
            return Task.FromResult(Result.Failure<ProviderResponse<PublishResult>>(titleError));
        }

        var cost = Capabilities.QuotaCostPerPublish;
        if (Interlocked.Add(ref _quotaRemaining, -cost) < 0)
        {
            Interlocked.Add(ref _quotaRemaining, cost);

            return Task.FromResult(Result.Failure<ProviderResponse<PublishResult>>(
                Error.Resource(
                    "quota.exhausted",
                    "Günlük yayın kotası doldu.",
                    TimeSpan.FromHours(8))));
        }

        var hash = Determinism.Hash(request.IdempotencyKey, request.Metadata.Title);
        var externalId = Determinism.Token(hash, 11);

        var result = new PublishResult
        {
            ExternalId = externalId,
            Url = new Uri(Determinism.Format($"https://fake.invalid/watch?v={externalId}")),
            Visibility = request.PublishAt is null ? request.Visibility : Visibility.Private,
            ScheduledFor = request.PublishAt,
            QuotaSpent = cost,
        };

        _byIdempotencyKey[request.IdempotencyKey] = result;

        return Task.FromResult(Result.Success(
            new ProviderResponse<PublishResult>(result, UsageUnits.OfRequests())));
    }

    public Task<Result<PublishResult?>> FindExistingAsync(
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _byIdempotencyKey.TryGetValue(idempotencyKey, out var existing);
        return Task.FromResult(Result.Success<PublishResult?>(existing));
    }

    /// Sınırları sağlayıcı reddediyor: gerçek platform da öyle yapıyor.
    /// Kırpma çağıranın işi — sahte sağlayıcı gevşek davransaydı, sınır
    /// aşımını ancak gerçek yüklemede görürdük.
    private Error? ValidateLimits(PublishMetadata metadata)
    {
        if (metadata.Title.Length > Capabilities.MaxTitleLength)
        {
            return Error.Permanent(
                "publish.title_too_long",
                $"Başlık {Capabilities.MaxTitleLength} karakteri aşıyor: {metadata.Title.Length}");
        }

        if (metadata.Description.Length > Capabilities.MaxDescriptionLength)
        {
            return Error.Permanent(
                "publish.description_too_long",
                $"Açıklama {Capabilities.MaxDescriptionLength} karakteri aşıyor.");
        }

        var tagsLength = metadata.Tags.Sum(t => t.Length + 1);
        if (tagsLength > Capabilities.MaxTagsTotalLength)
        {
            return Error.Permanent(
                "publish.tags_too_long",
                $"Etiketler toplamı {Capabilities.MaxTagsTotalLength} karakteri aşıyor: {tagsLength}");
        }

        return null;
    }
}

/// Deterministik sahte analitik. Metrikler dış kimlikten türetilir, böylece
/// aynı video her sorgulandığında aynı sayıları verir.
public sealed class FakeAnalyticsProvider : IAnalyticsProvider
{
    public string Key => "fake-analytics";

    public Task<Result<ProviderResponse<IReadOnlyList<MetricSnapshot>>>> FetchAsync(
        string externalId,
        DateOnly from,
        DateOnly to,
        ProviderContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var snapshots = new List<MetricSnapshot>();

        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var hash = Determinism.Hash(externalId, date.ToString("O", CultureInfo.InvariantCulture));
            var views = Determinism.Range(hash, 50, 5_000);

            snapshots.Add(new MetricSnapshot
            {
                Date = date,
                Views = views,
                Impressions = views * Determinism.Range(hash >> 8, 8, 30),
                ClickThroughRate = Determinism.Range(hash >> 16, 20, 120) / 1000.0,
                AverageViewDurationSeconds = Determinism.Range(hash >> 24, 8, 45),
                Likes = views / Math.Max(1, Determinism.Range(hash >> 32, 20, 60)),
                Comments = views / Math.Max(1, Determinism.Range(hash >> 40, 200, 800)),
                SubscribersGained = views / Math.Max(1, Determinism.Range(hash >> 48, 300, 1200)),
            });
        }

        return Task.FromResult(Result.Success(
            new ProviderResponse<IReadOnlyList<MetricSnapshot>>(snapshots, UsageUnits.OfRequests())));
    }
}
