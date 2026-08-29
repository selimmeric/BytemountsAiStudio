using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Core.Time;

namespace BytemountsAiStudio.Providers.Open;

/// Instagram yayıncısının ayarları (P6-02).
///
/// HER PARAMETRE AYARLANABİLİR — API sürümü dahil. Meta sürümü
/// yılda birkaç kez yükseltiyor ve eski sürümleri kapatıyor;
/// `v21.0` kodda sabit olsaydı her sürüm yeni bir derleme demekti.
public sealed record InstagramOptions
{
    /// Varsayılan adres — `config/providers.json` ile AYNI olmak
    /// zorunda; `ProviderEndpointTests` ikisini karşılaştırıyor.
    public static Uri DefaultEndpoint { get; } = new("https://graph.facebook.com/v21.0/");

    public const string EndpointVariable = "BMAI_INSTAGRAM_URL";

    public Uri BaseAddress { get; init; } =
        Endpoints.Resolve(EndpointVariable, "https://graph.facebook.com/v21.0/");

    public string KeyEnvironmentVariable { get; init; } = "INSTAGRAM_ACCESS_TOKEN";

    /// Yayının yapılacağı Instagram iş hesabı kimliği.
    ///
    /// ANAHTARDAN AYRI: bir jeton birden fazla hesabı yönetebiliyor ve
    /// hangisine yayınlandığı bir tercih, kimlik bilgisi değil.
    public string? UserIdEnvironmentVariable { get; init; } = "INSTAGRAM_USER_ID";

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// Konteyner hazır olana kadar en fazla kaç yoklama.
    ///
    /// Meta videoyu KENDİ indirip işliyor; uzun videolarda bu dakikalar
    /// sürüyor. Sınır dolduğunda hata KAYNAK sınıfı (ADR-011): iş
    /// düşmüyor, erteleniyor.
    public int MaxPollAttempts { get; init; } = 60;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
}

/// Instagram Reels yayıncısı (P6-02).
///
/// İKİ ADIM: önce konteyner oluşturuluyor, `FINISHED` olana kadar
/// yoklanıyor, sonra yayınlanıyor. Konteyner hazır olmadan yayınlamak
/// Meta tarafında hata veriyor ve hata metni sebebi söylemiyor.
///
/// ***VİDEO YÜKLENMİYOR, ÇEKİLİYOR — ve bu sınıfın en önemli sınırı.***
///
/// Graph API dosya kabul etmiyor: `video_url` veriliyor ve Meta o
/// adresi KENDİ sunucularından indiriyor. Yerel bir dosya yolu ya da
/// yalnızca iç ağdan erişilebilen bir MinIO adresi orada işe
/// yaramıyor; Meta "medya indirilemedi" diye anlaşılmaz bir kodla
/// düşüyor ve sebebini bulmak saatler alıyor.
///
/// Bu yüzden adresin varlığı yayından ÖNCE kontrol ediliyor ve eksikse
/// hata, ne yapılması gerektiğini söylüyor.
public sealed class InstagramPublisher(
    HttpClient http, InstagramOptions? options = null, ICredentialSource? credentials = null) : IPublisher
{
    private readonly InstagramOptions _options = options ?? new InstagramOptions();

    public string Key => "instagram";

    public string Platform => "instagram";

    public PublishCapabilities Capabilities { get; } = new()
    {
        // Instagram'da "başlık" yok, altyazı (caption) var: 2.200
        // karakter ve başlıkla açıklama aynı alana giriyor.
        MaxTitleLength = 2_200,
        MaxDescriptionLength = 2_200,
        MaxTagsTotalLength = 2_200,

        // Reels üst sınırı 15 dakika.
        MaxDuration = new Ms(15 * 60 * 1000),
        SupportsScheduling = false,

        // Kapak `cover_url` ile veriliyor ama o da ÇEKİLEN bir adres;
        // dosya yüklenemiyor.
        SupportsCustomThumbnail = true,
        QuotaCostPerPublish = 1,
    };

    public async Task<Result<ProviderResponse<PublishResult>>> PublishAsync(
        PublishRequest request, ProviderContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var token = Read(_options.KeyEnvironmentVariable);

        if (token is null)
        {
            return Error.Permanent("instagram.no_token",
                $"Instagram erişim jetonu yok ({_options.KeyEnvironmentVariable}).");
        }

        var userId = _options.UserIdEnvironmentVariable is { } variable ? Read(variable) : null;

        if (string.IsNullOrWhiteSpace(userId))
        {
            return Error.Permanent("instagram.no_user",
                $"Instagram iş hesabı kimliği yok ({_options.UserIdEnvironmentVariable}).");
        }

        if (request.VideoUrl is null)
        {
            // YEREL DOSYA YOLU İŞE YARAMIYOR ve bunu Meta tarafında
            // öğrenmek pahalı: hata "medya indirilemedi" diyor,
            // sebebini söylemiyor.
            return Error.Permanent("instagram.no_public_url",
                "Instagram videoyu ÇEKİYOR, yükleme kabul etmiyor: `VideoUrl` dolu olmalı "
                + "ve adres dışarıdan erişilebilir olmalı. "
                // ***HATA NE YAPILACAĞINI SÖYLÜYOR.*** Önce yalnızca
                // "adres olmalı" diyordu ve o adresin nereden geldiği
                // hiçbir yerde yazmıyordu: alan render çıktısında
                // ÜRETİLMİYORDU bile.
                + "Render çıktısının `public_url` alanı `BMAI_PUBLIC_BASE_URL` "
                + "ayarlandığında doluyor; bu bir dağıtım kararı, kod bunu üretemez.");
        }

        if (request.Visibility != Visibility.Public)
        {
            // GİZLİ REELS DİYE BİR ŞEY YOK. "Gizli yayınladık" demek,
            // herkese açık olan bir videoyu gizli sanmak olurdu.
            return Error.Permanent("instagram.public_only",
                "Instagram Reels yalnızca herkese açık yayınlanabiliyor; "
                + $"istenen görünürlük: {request.Visibility}.");
        }

        var container = await CreateContainerAsync(token, userId, request, cancellationToken)
            .ConfigureAwait(false);

        if (container.IsFailure)
        {
            return Result.Failure<ProviderResponse<PublishResult>>(container.Error);
        }

        var ready = await AwaitContainerAsync(token, container.Value, cancellationToken)
            .ConfigureAwait(false);

        if (ready.IsFailure)
        {
            return Result.Failure<ProviderResponse<PublishResult>>(ready.Error);
        }

        var published = await PublishContainerAsync(token, userId, container.Value, cancellationToken)
            .ConfigureAwait(false);

        if (published.IsFailure)
        {
            return Result.Failure<ProviderResponse<PublishResult>>(published.Error);
        }

        return Result.Success(ProviderResponse<PublishResult>.Free(new PublishResult
        {
            ExternalId = published.Value,
            Url = new Uri($"https://www.instagram.com/reel/{published.Value}/"),
            Visibility = Visibility.Public,
            QuotaSpent = Capabilities.QuotaCostPerPublish,
        }));
    }

    /// Yarım kalmış yayının durumu.
    ///
    /// Konteyner kimliği idempotency anahtarı olarak saklanıyor: çökme
    /// sonrası "yayınlandı mı" sorusunun tek doğru cevabı platforma
    /// sormak (§15.2). Konteyner `PUBLISHED` ise ikinci kez
    /// yayınlamak, aynı Reels'i iki kez paylaşmak olurdu.
    public async Task<Result<PublishResult?>> FindExistingAsync(
        string idempotencyKey, CancellationToken cancellationToken)
    {
        var token = Read(_options.KeyEnvironmentVariable);

        if (token is null || string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Result.Success<PublishResult?>(null);
        }

        var status = await ContainerStatusAsync(token, idempotencyKey, cancellationToken)
            .ConfigureAwait(false);

        if (status.IsFailure || status.Value.StatusCode != "PUBLISHED")
        {
            return Result.Success<PublishResult?>(null);
        }

        return Result.Success<PublishResult?>(new PublishResult
        {
            ExternalId = idempotencyKey,
            Url = new Uri($"https://www.instagram.com/reel/{idempotencyKey}/"),
            Visibility = Visibility.Public,
        });
    }

    /* ---- adımlar ---- */

    private async Task<Result<string>> CreateContainerAsync(
        string token, string userId, PublishRequest request, CancellationToken cancellationToken)
    {
        var caption = Trim(
            request.Metadata.Title + "\n\n" + request.Metadata.Description,
            Capabilities.MaxDescriptionLength);

        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["media_type"] = "REELS",
            ["video_url"] = request.VideoUrl!.ToString(),
            ["caption"] = caption,
            ["access_token"] = token,
        };

        using var content = new FormUrlEncodedContent(query);

        using var message = new HttpRequestMessage(
            HttpMethod.Post, new Uri(_options.BaseAddress, $"{userId}/media"))
        {
            Content = content,
        };

        var response = await SendAsync<IdResponse>(message, cancellationToken).ConfigureAwait(false);

        return response.IsFailure
            ? Result.Failure<string>(response.Error)
            : response.Value.Id is { Length: > 0 } id
                ? Result.Success(id)
                : Error.Transient("instagram.no_container", "Konteyner kimliği dönmedi.");
    }

    private async Task<Result> AwaitContainerAsync(
        string token, string containerId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < _options.MaxPollAttempts; attempt++)
        {
            var status = await ContainerStatusAsync(token, containerId, cancellationToken)
                .ConfigureAwait(false);

            if (status.IsFailure)
            {
                return Result.Failure(status.Error);
            }

            switch (status.Value.StatusCode)
            {
                case "FINISHED":
                    return Result.Success();

                case "ERROR":
                    return Error.Permanent("instagram.container_failed",
                        status.Value.Status ?? "Meta videoyu işleyemedi.");

                case "EXPIRED":
                    // Konteyner 24 saatte düşüyor. Yeniden denemek
                    // ANLAMLI: yeni bir konteyner açılacak.
                    return Error.Transient("instagram.container_expired",
                        "Konteyner süresi doldu; yeniden oluşturulmalı.");
            }

            await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
        }

        return Error.Resource("instagram.still_processing",
            $"Konteyner {_options.MaxPollAttempts} yoklamada hazır olmadı: {containerId}",
            _options.PollInterval * _options.MaxPollAttempts);
    }

    private async Task<Result<string>> PublishContainerAsync(
        string token, string userId, string containerId, CancellationToken cancellationToken)
    {
        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["creation_id"] = containerId,
            ["access_token"] = token,
        };

        using var content = new FormUrlEncodedContent(query);

        using var message = new HttpRequestMessage(
            HttpMethod.Post, new Uri(_options.BaseAddress, $"{userId}/media_publish"))
        {
            Content = content,
        };

        var response = await SendAsync<IdResponse>(message, cancellationToken).ConfigureAwait(false);

        return response.IsFailure
            ? Result.Failure<string>(response.Error)
            : response.Value.Id is { Length: > 0 } id
                ? Result.Success(id)
                : Error.Transient("instagram.no_media_id", "Yayın kimliği dönmedi.");
    }

    internal async Task<Result<ContainerStatus>> ContainerStatusAsync(
        string token, string containerId, CancellationToken cancellationToken)
    {
        var address = new Uri(
            _options.BaseAddress,
            $"{containerId}?fields=status_code,status&access_token={Uri.EscapeDataString(token)}");

        using var message = new HttpRequestMessage(HttpMethod.Get, address);

        var response = await SendAsync<ContainerStatus>(message, cancellationToken)
            .ConfigureAwait(false);

        return response.IsFailure
            ? Result.Failure<ContainerStatus>(response.Error)
            : Result.Success(response.Value);
    }

    /* ---- ortak ---- */

    private async Task<Result<T>> SendAsync<T>(
        HttpRequestMessage message, CancellationToken cancellationToken)
    {
        using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(_options.Timeout);

        try
        {
            using var response = await http.SendAsync(message, source.Token).ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                return Error.Resource("instagram.rate_limited", "Instagram hız sınırı.",
                    response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(5));
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(source.Token).ConfigureAwait(false);

                return (int)response.StatusCode >= 500
                    ? Error.Transient("instagram.server_error", $"{(int)response.StatusCode}: {body}")
                    : Error.Permanent("instagram.request_failed", $"{(int)response.StatusCode}: {body}");
            }

            var parsed = await response.Content
                .ReadFromJsonAsync<T>(Json, source.Token)
                .ConfigureAwait(false);

            return parsed is null
                ? Error.Transient("instagram.bad_response", "Cevap okunamadı.")
                : Result.Success(parsed);
        }
        catch (HttpRequestException ex)
        {
            return Error.Transient("instagram.network", ex.Message);
        }
        catch (JsonException ex)
        {
            return Error.Transient("instagram.bad_json", ex.Message);
        }
    }

    private string? Read(string name)
        => credentials?.Get(name) ?? Environment.GetEnvironmentVariable(name);

    private static string Trim(string text, int limit)
        => text.Length <= limit ? text : text[..limit];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed record IdResponse
    {
        public string? Id { get; init; }
    }

    internal sealed record ContainerStatus
    {
        [JsonPropertyName("status_code")]
        public string? StatusCode { get; init; }

        public string? Status { get; init; }
    }
}
