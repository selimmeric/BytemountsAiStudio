using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Core.Time;

namespace BytemountsAiStudio.Providers.Open;

/// TikTok yayıncısının ayarları (P6-01).
///
/// HER PARAMETRE AYARLANABİLİR. Adres, uç nokta yolları, yoklama
/// aralığı, deneme sayısı, parça boyutu — hiçbiri koda gömülü değil.
/// TikTok uç nokta sürümünü değiştirdiğinde (v2 → v3) yapılacak şey
/// bir ayar güncellemek olmalı, yeniden derleme değil.
public sealed record TikTokOptions
{
    /// Varsayılan adres — `config/providers.json` ile AYNI olmak
    /// zorunda; `ProviderEndpointTests` ikisini karşılaştırıyor.
    public static Uri DefaultEndpoint { get; } = new("https://open.tiktokapis.com/v2/");

    public const string EndpointVariable = "BMAI_TIKTOK_URL";

    public Uri BaseAddress { get; init; } =
        Endpoints.Resolve(EndpointVariable, "https://open.tiktokapis.com/v2/");

    public string KeyEnvironmentVariable { get; init; } = "TIKTOK_ACCESS_TOKEN";

    /// Uç nokta yolları — ayarda, kodda değil.
    public string CreatorInfoPath { get; init; } = "post/publish/creator_info/query/";

    public string InitPath { get; init; } = "post/publish/video/init/";

    public string StatusPath { get; init; } = "post/publish/status/fetch/";

    /// Yükleme parçası. TikTok 5 MB–64 MB arası parça istiyor.
    public int ChunkBytes { get; init; } = 10 * 1024 * 1024;

    /// Durum yoklamaları arasındaki bekleme.
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// En fazla kaç kez yoklanacağı.
    ///
    /// SONSUZ YOKLAMA YOK: platform takılırsa iş sonsuza kadar bir
    /// worker'ı tutar. Sınıra gelindiğinde hata KAYNAK sınıfı (ADR-011)
    /// — yani başarısızlık değil, ERTELEME: video muhtemelen
    /// işleniyor ve bir sonraki deneme durumu yeniden soracak.
    public int MaxPollAttempts { get; init; } = 60;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);
}

/// TikTok Content Posting API yayıncısı (P6-01).
///
/// ÜÇ ADIM: `init` yükleme adresi veriyor, video parça parça
/// yükleniyor, sonra durum `PUBLISH_COMPLETE` olana kadar yoklanıyor.
/// İkinci adımı atlayıp "yüklendi" demek, TikTok'un hiç görmediği bir
/// videoyu yayınlanmış saymak olurdu.
///
/// ***GÖRÜNÜRLÜK ÖNCEDEN SORULUYOR — ve bu sınıfın en önemli işi.***
///
/// Denetimden geçmemiş uygulamalar yalnızca `SELF_ONLY`
/// yayınlayabiliyor. "Herkese açık" istenip sandbox uygulamayla
/// gönderildiğinde TikTok hata VERMİYOR: videoyu sessizce gizli
/// yayınlıyor. Sistem "yayınlandı" der, kayıtta herkese açık yazar,
/// video hiç kimseye görünmez ve bunu ancak haftalar sonra
/// "izlenmesi neden sıfır" diye fark edersiniz.
///
/// Bu yüzden `creator_info` önce sorgulanıyor ve istenen görünürlük
/// izin verilenler arasında değilse yayın HİÇ BAŞLAMIYOR.
public sealed class TikTokPublisher(
    HttpClient http, TikTokOptions? options = null, ICredentialSource? credentials = null) : IPublisher
{
    private readonly TikTokOptions _options = options ?? new TikTokOptions();

    public string Key => "tiktok";

    public string Platform => "tiktok";

    /// Sınırlar KATALOGDAN DEĞİL BURADAN: bunlar platformun sert
    /// kuralları, yapılandırma değil. Ayarlanabilir yapmak, birinin
    /// sınırı gevşetip yüklemeyi API tarafında reddettirmesi demekti.
    public PublishCapabilities Capabilities { get; } = new()
    {
        MaxTitleLength = 2_200,
        MaxDescriptionLength = 2_200,
        MaxTagsTotalLength = 2_200,
        MaxDuration = new Ms(10 * 60 * 1000),
        SupportsScheduling = false,
        SupportsCustomThumbnail = false,

        // TikTok kota birimini YouTube gibi saymıyor; günlük yayın
        // sayısı sınırlı. Bir yayın bir birim.
        QuotaCostPerPublish = 1,
    };

    public async Task<Result<ProviderResponse<PublishResult>>> PublishAsync(
        PublishRequest request, ProviderContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var token = Token();

        if (token is null)
        {
            return Error.Permanent("tiktok.no_token",
                $"TikTok erişim jetonu yok ({_options.KeyEnvironmentVariable}).");
        }

        if (!File.Exists(request.VideoPath))
        {
            return Error.Permanent("tiktok.no_file", $"Video yok: {request.VideoPath}");
        }

        var allowed = await AllowedVisibilityAsync(token, cancellationToken).ConfigureAwait(false);

        if (allowed.IsFailure)
        {
            return Result.Failure<ProviderResponse<PublishResult>>(allowed.Error);
        }

        var privacy = PrivacyLevel(request.Visibility);

        if (!allowed.Value.Contains(privacy, StringComparer.Ordinal))
        {
            // SESSİZ GİZLİ YAYIN ENGELLENİYOR. TikTok bu durumda hata
            // vermiyor, videoyu SELF_ONLY yayınlıyor ve sistem
            // "herkese açık yayınlandı" diye kaydediyor.
            return Error.Permanent("tiktok.visibility_not_allowed",
                $"Hesap '{privacy}' yayınlayamıyor. İzin verilenler: "
                + string.Join(", ", allowed.Value)
                + ". Uygulama denetimden geçmemişse yalnızca SELF_ONLY mümkün.");
        }

        var init = await InitAsync(token, request, privacy, cancellationToken).ConfigureAwait(false);

        if (init.IsFailure)
        {
            return Result.Failure<ProviderResponse<PublishResult>>(init.Error);
        }

        var uploaded = await UploadAsync(init.Value, request.VideoPath, cancellationToken)
            .ConfigureAwait(false);

        if (uploaded.IsFailure)
        {
            return Result.Failure<ProviderResponse<PublishResult>>(uploaded.Error);
        }

        var published = await AwaitPublishAsync(token, init.Value.PublishId, cancellationToken)
            .ConfigureAwait(false);

        if (published.IsFailure)
        {
            return Result.Failure<ProviderResponse<PublishResult>>(published.Error);
        }

        return Result.Success(ProviderResponse<PublishResult>.Free(new PublishResult
        {
            ExternalId = published.Value,
            Url = new Uri($"https://www.tiktok.com/video/{published.Value}"),

            // GERÇEKLEŞEN GÖRÜNÜRLÜK YAZILIYOR, İSTENEN DEĞİL.
            Visibility = VisibilityOf(privacy),
            QuotaSpent = Capabilities.QuotaCostPerPublish,
        }));
    }

    /// Yarım kalmış yüklemenin durumu.
    ///
    /// `publish_id` idempotency anahtarı olarak saklanıyor: çökme
    /// sonrası "bu video gerçekten yüklendi mi" sorusunun tek doğru
    /// cevabı platforma sormak (§15.2).
    public async Task<Result<PublishResult?>> FindExistingAsync(
        string idempotencyKey, CancellationToken cancellationToken)
    {
        var token = Token();

        if (token is null || string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Result.Success<PublishResult?>(null);
        }

        var status = await StatusAsync(token, idempotencyKey, cancellationToken).ConfigureAwait(false);

        if (status.IsFailure || status.Value.Status != "PUBLISH_COMPLETE")
        {
            return Result.Success<PublishResult?>(null);
        }

        var id = FirstId(status.Value) ?? idempotencyKey;

        return Result.Success<PublishResult?>(new PublishResult
        {
            ExternalId = id,
            Url = new Uri($"https://www.tiktok.com/video/{id}"),
            Visibility = Visibility.Private,
        });
    }

    /* ---- adımlar ---- */

    internal async Task<Result<IReadOnlyList<string>>> AllowedVisibilityAsync(
        string token, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(
            HttpMethod.Post, new Uri(_options.BaseAddress, _options.CreatorInfoPath));

        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await SendAsync<CreatorInfoResponse>(message, cancellationToken)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result.Failure<IReadOnlyList<string>>(response.Error);
        }

        var options = response.Value.Data?.PrivacyLevelOptions;

        if (options is null || options.Count == 0)
        {
            // BOŞ LİSTE "HER ŞEY SERBEST" DEĞİL: cevabın anlaşılmadığı
            // anlamına geliyor ve o hâlde yayınlamak, görünürlüğü
            // şansa bırakmak olurdu.
            return Error.Permanent("tiktok.no_privacy_options",
                "Hesabın izin verdiği görünürlükler okunamadı.");
        }

        return Result.Success<IReadOnlyList<string>>(options);
    }

    private async Task<Result<InitData>> InitAsync(
        string token, PublishRequest request, string privacy, CancellationToken cancellationToken)
    {
        var size = new FileInfo(request.VideoPath).Length;

        var payload = new
        {
            post_info = new
            {
                title = Trim(request.Metadata.Title, Capabilities.MaxTitleLength),
                privacy_level = privacy,
                disable_comment = false,
                disable_duet = false,
                disable_stitch = false,
            },
            source_info = new
            {
                source = "FILE_UPLOAD",
                video_size = size,
                chunk_size = Math.Min(_options.ChunkBytes, size),
                total_chunk_count = Math.Max(1, (int)Math.Ceiling((double)size / _options.ChunkBytes)),
            },
        };

        using var message = new HttpRequestMessage(
            HttpMethod.Post, new Uri(_options.BaseAddress, _options.InitPath))
        {
            Content = JsonContent.Create(payload),
        };

        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await SendAsync<InitResponse>(message, cancellationToken).ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result.Failure<InitData>(response.Error);
        }

        var data = response.Value.Data;

        if (data?.PublishId is not { Length: > 0 } id || data.UploadUrl is not { Length: > 0 } url)
        {
            return Error.Transient("tiktok.bad_init", "Yükleme oturumu açılamadı.");
        }

        return Result.Success(new InitData(id, new Uri(url), size));
    }

    private async Task<Result> UploadAsync(
        InitData init, string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);

        using var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");

        // TAM ARALIK BİLDİRİLİYOR: TikTok `Content-Range` olmadan
        // parçayı reddediyor ve hatası "invalid request" oluyor —
        // nedenini söylemeden.
        content.Headers.ContentRange = new ContentRangeHeaderValue(0, init.Size - 1, init.Size);

        using var message = new HttpRequestMessage(HttpMethod.Put, init.UploadUrl)
        {
            Content = content,
        };

        using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(_options.Timeout);

        try
        {
            using var response = await http.SendAsync(message, source.Token).ConfigureAwait(false);

            return response.IsSuccessStatusCode
                ? Result.Success()
                : Error.Transient("tiktok.upload_failed",
                    $"Yükleme başarısız: {(int)response.StatusCode}");
        }
        catch (HttpRequestException ex)
        {
            return Error.Transient("tiktok.upload_failed", ex.Message);
        }
    }

    private async Task<Result<string>> AwaitPublishAsync(
        string token, string publishId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < _options.MaxPollAttempts; attempt++)
        {
            var status = await StatusAsync(token, publishId, cancellationToken).ConfigureAwait(false);

            if (status.IsFailure)
            {
                return Result.Failure<string>(status.Error);
            }

            switch (status.Value.Status)
            {
                case "PUBLISH_COMPLETE":
                    return Result.Success(
                        FirstId(status.Value) ?? publishId);

                case "FAILED":
                    return Error.Permanent("tiktok.publish_failed",
                        status.Value.FailReason ?? "TikTok yayını reddetti.");
            }

            await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
        }

        // KAYNAK HATASI, BAŞARISIZLIK DEĞİL (ADR-011): video muhtemelen
        // hâlâ işleniyor. Kalıcı hata saymak, gerçekte yayınlanmış bir
        // videoyu "düştü" diye işaretlemek ve yeniden yükletmek olurdu.
        return Error.Resource("tiktok.still_processing",
            $"Yayın {_options.MaxPollAttempts} yoklamada tamamlanmadı: {publishId}",
            _options.PollInterval * _options.MaxPollAttempts);
    }

    private async Task<Result<StatusData>> StatusAsync(
        string token, string publishId, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(
            HttpMethod.Post, new Uri(_options.BaseAddress, _options.StatusPath))
        {
            Content = JsonContent.Create(new { publish_id = publishId }),
        };

        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await SendAsync<StatusResponse>(message, cancellationToken).ConfigureAwait(false);

        return response.IsFailure
            ? Result.Failure<StatusData>(response.Error)
            : Result.Success(response.Value.Data ?? new StatusData());
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
                return Error.Resource("tiktok.rate_limited", "TikTok hız sınırı.",
                    response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1));
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(source.Token).ConfigureAwait(false);

                return (int)response.StatusCode >= 500
                    ? Error.Transient("tiktok.server_error", $"{(int)response.StatusCode}: {body}")
                    : Error.Permanent("tiktok.request_failed", $"{(int)response.StatusCode}: {body}");
            }

            var parsed = await response.Content
                .ReadFromJsonAsync<T>(Json, source.Token)
                .ConfigureAwait(false);

            return parsed is null
                ? Error.Transient("tiktok.bad_response", "Cevap okunamadı.")
                : Result.Success(parsed);
        }
        catch (HttpRequestException ex)
        {
            return Error.Transient("tiktok.network", ex.Message);
        }
        catch (JsonException ex)
        {
            return Error.Transient("tiktok.bad_json", ex.Message);
        }
    }

    private string? Token()
        => credentials?.Get(_options.KeyEnvironmentVariable)
            ?? Environment.GetEnvironmentVariable(_options.KeyEnvironmentVariable);

    /// İstenen görünürlüğün TikTok karşılığı.
    internal static string PrivacyLevel(Visibility visibility) => visibility switch
    {
        Visibility.Public => "PUBLIC_TO_EVERYONE",
        Visibility.Unlisted => "MUTUAL_FOLLOW_FRIENDS",
        _ => "SELF_ONLY",
    };

    internal static Visibility VisibilityOf(string privacy) => privacy switch
    {
        "PUBLIC_TO_EVERYONE" => Visibility.Public,
        "MUTUAL_FOLLOW_FRIENDS" => Visibility.Unlisted,
        _ => Visibility.Private,
    };

    /// TikTok yayınlanan videonun kimliğini LİSTE olarak dönüyor;
    /// tek eleman bekliyoruz ama boş gelebiliyor.
    private static string? FirstId(StatusData status)
        => status.PubliclyAvailablePostId is { Count: > 0 } ids ? ids[0] : null;

    private static string Trim(string text, int limit)
        => text.Length <= limit ? text : text[..limit];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly record struct InitData(string PublishId, Uri UploadUrl, long Size);

    private sealed record CreatorInfoResponse
    {
        public CreatorInfoData? Data { get; init; }
    }

    private sealed record CreatorInfoData
    {
        [JsonPropertyName("privacy_level_options")]
        public IReadOnlyList<string>? PrivacyLevelOptions { get; init; }
    }

    private sealed record InitResponse
    {
        public InitResponseData? Data { get; init; }
    }

    private sealed record InitResponseData
    {
        [JsonPropertyName("publish_id")]
        public string? PublishId { get; init; }

        [JsonPropertyName("upload_url")]
        public string? UploadUrl { get; init; }
    }

    private sealed record StatusResponse
    {
        public StatusData? Data { get; init; }
    }

    internal sealed record StatusData
    {
        public string? Status { get; init; }

        [JsonPropertyName("fail_reason")]
        public string? FailReason { get; init; }

        [JsonPropertyName("publicaly_available_post_id")]
        public IReadOnlyList<string>? PubliclyAvailablePostId { get; init; }
    }
}
