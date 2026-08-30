using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Core.Time;

namespace BytemountsAiStudio.Providers.Open;

/// YouTube yayıncısının ayarları (P1-24, P1-25).
///
/// HER PARAMETRE AYARLANABİLİR: yükleme adresi, veri API adresi, jeton
/// adresi, parça boyutu, zaman aşımları. Google uç noktalarını
/// taşıdığında ya da bir vekil sunucu gerektiğinde yapılacak şey ayar
/// değiştirmek olmalı.
public sealed record YouTubeOptions
{
    /// Varsayılan adres — `config/providers.json` ile AYNI olmak
    /// zorunda; `ProviderEndpointTests` ikisini karşılaştırıyor.
    public static Uri DefaultEndpoint { get; } =
        new("https://www.googleapis.com/upload/youtube/v3/videos");

    public const string EndpointVariable = "BMAI_YOUTUBE_URL";

    /// Sürdürülebilir yükleme adresi.
    public Uri UploadAddress { get; init; } =
        Endpoints.Resolve(EndpointVariable, "https://www.googleapis.com/upload/youtube/v3/videos");

    /// Veri API'si — kapak ve liste çağrıları.
    public Uri ApiAddress { get; init; } =
        Endpoints.Resolve("BMAI_YOUTUBE_API_URL", "https://www.googleapis.com/youtube/v3/");

    /// OAuth jeton adresi.
    public Uri TokenAddress { get; init; } =
        Endpoints.Resolve("BMAI_GOOGLE_TOKEN_URL", "https://oauth2.googleapis.com/token");

    public string AccessTokenVariable { get; init; } = "YOUTUBE_ACCESS_TOKEN";

    public string RefreshTokenVariable { get; init; } = "YOUTUBE_REFRESH_TOKEN";

    public string ClientIdVariable { get; init; } = "YOUTUBE_CLIENT_ID";

    public string ClientSecretVariable { get; init; } = "YOUTUBE_CLIENT_SECRET";

    /// Yükleme parçası. 256 KiB'in katına HİZALANIYOR (Google şartı).
    public int ChunkBytes { get; init; } = 8 * 1024 * 1024;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);
}

/// YouTube yayıncısı (P1-24, P1-25).
///
/// SÜRDÜRÜLEBİLİR YÜKLEME, TEK ATIŞ DEĞİL. 60 MB'lık bir video tek
/// istekte gönderilirse ağdaki bir kesinti bütün bant genişliğini ve
/// kotayı çöpe atıyor. Parça sınırlarının hesabı `ResumableUpload`'da
/// ve SAF: hangi bayttan devam edileceği, gerçek bir dosya
/// yüklenerek öğrenilecek bir şey olmamalı.
///
/// ***ONAYLANAN BAYT BİZİM GÖNDERDİĞİMİZ DEĞİL.*** Gönderdiğimiz bir
/// parça karşı tarafa hiç ulaşmamış olabilir; oradan devam etmek
/// dosyada delik bırakır ve YouTube bozuk bir video kabul eder.
/// Devam noktası her zaman sunucunun `Range` başlığından okunuyor.
public sealed class YouTubePublisher(
    HttpClient http, YouTubeOptions? options = null, ICredentialSource? credentials = null) : IPublisher
{
    private readonly YouTubeOptions _options = options ?? new YouTubeOptions();

    /// ***JETON KAYNAĞI ORTAK (`GoogleTokenSource`).***
    ///
    /// Yenileme akışı burada yazılmıştı ve `YouTubeAnalyticsProvider`
    /// aynı işi yapmak yerine **statik bir jeton** okuyordu. İkinci
    /// kopyayı yazmak yerine bu taraf ortak sınıfa devredildi: iki
    /// kopya er geç ayrışır ve ayrışan kopya, yalnızca birinin
    /// bozulduğu bir hataya dönüşür.
    ///
    /// Yan kazanç: jeton artık ÖNBELLEKLİ. Önceden her çağrıda
    /// yeniden yenileniyordu — havuzdaki her hesap için, her yükleme
    /// ve her kurtarma sorgusunda.
    private readonly GoogleTokenSource _tokens = Tokens(http, options, credentials);

    private static GoogleTokenSource Tokens(
        HttpClient http, YouTubeOptions? options, ICredentialSource? credentials)
    {
        var resolved = options ?? new YouTubeOptions();

        return new GoogleTokenSource(
            http,
            resolved.TokenAddress,
            new GoogleTokenVariables
            {
                AccessToken = resolved.AccessTokenVariable,
                RefreshToken = resolved.RefreshTokenVariable,
                ClientId = resolved.ClientIdVariable,
                ClientSecret = resolved.ClientSecretVariable,
            },
            credentials);
    }

    public string Key => "youtube";

    public string Platform => "youtube";

    public PublishCapabilities Capabilities { get; } = new()
    {
        MaxTitleLength = 100,
        MaxDescriptionLength = 5_000,
        MaxTagsTotalLength = 500,

        // Shorts sınırı 3 dakika; uzun video 12 saate kadar. Sınır
        // uzun videoya göre, çünkü ikisi de bu yayıncıdan geçiyor.
        MaxDuration = new Ms(12 * 60 * 60 * 1000),
        SupportsScheduling = true,
        SupportsCustomThumbnail = true,
        QuotaCostPerPublish = QuotaLedger.UploadCost,
    };

    public async Task<Result<ProviderResponse<PublishResult>>> PublishAsync(
        PublishRequest request, ProviderContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        // HESAP BAĞLAMDAN: kota havuzunun seçtiği proje burada
        // gerçekleşiyor (P4-04).
        var token = await TokenAsync(cancellationToken, context.Account).ConfigureAwait(false);

        if (token.IsFailure)
        {
            return Result.Failure<ProviderResponse<PublishResult>>(token.Error);
        }

        if (!File.Exists(request.VideoPath))
        {
            return Error.Permanent("youtube.no_file", $"Video yok: {request.VideoPath}");
        }

        var total = new FileInfo(request.VideoPath).Length;

        // YARIM KALMIŞ OTURUM SÜRDÜRÜLÜYOR.
        //
        // Baştan başlamak, harcanan bant genişliğini ve — daha
        // pahalısı — bir günlük kotanın 1.600 birimini çöpe atmak
        // olurdu.
        var session = request.ResumeToken is { Length: > 0 } resume
            ? new UploadSession { SessionUrl = resume, TotalBytes = total }
            : null;

        if (session is null)
        {
            var started = await StartSessionAsync(token.Value, request, total, cancellationToken)
                .ConfigureAwait(false);

            if (started.IsFailure)
            {
                return Result.Failure<ProviderResponse<PublishResult>>(started.Error);
            }

            session = started.Value;
        }
        else
        {
            // ONAYLANAN BAYT SUNUCUYA SORULUYOR: bizim en son ne
            // gönderdiğimiz değil, karşı tarafın ne aldığı önemli.
            var confirmed = await ConfirmedAsync(token.Value, session, cancellationToken)
                .ConfigureAwait(false);

            if (confirmed.IsFailure)
            {
                return Result.Failure<ProviderResponse<PublishResult>>(confirmed.Error);
            }

            if (confirmed.Value.Video is { } already)
            {
                // OTURUM ZATEN TAMAMLANMIŞ: çökme veritabanı yazımı ile
                // yükleme arasında olmuş. İkinci kez yüklemek, aynı
                // videoyu iki kez yayınlamak olurdu (§2.4/16).
                return Result.Success(ProviderResponse<PublishResult>.Free(
                    Publish(already, request.Visibility, request.PublishAt)));
            }

            session = session with { ConfirmedBytes = confirmed.Value.Confirmed };
        }

        return await UploadAsync(token.Value, session, request, cancellationToken).ConfigureAwait(false);
    }

    /// Yarım kalmış yüklemenin sonucu.
    ///
    /// ANAHTAR OLARAK OTURUM ADRESİ KULLANILIYOR ve bunun sebebi var:
    /// YouTube'da keyfi bir idempotency anahtarıyla "bunu yükledim mi"
    /// diye sorulacak bir uç nokta YOK. Sorulabilecek tek şey, bizim
    /// sakladığımız sürdürme oturumu — protokol tamamlanmış bir
    /// oturumu sorgulayınca video kaynağını döndürüyor.
    ///
    /// Anahtar bir oturum adresi değilse `null` dönüyor: uydurma bir
    /// arama (açıklamaya gömülmüş işaret, `search` uç noktası) hem 100
    /// birim kota harcar hem de eşleşmeyi şansa bırakırdı.
    public async Task<Result<PublishResult?>> FindExistingAsync(
        string idempotencyKey, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(idempotencyKey, UriKind.Absolute, out _))
        {
            return Result.Success<PublishResult?>(null);
        }

        var token = await TokenAsync(cancellationToken).ConfigureAwait(false);

        if (token.IsFailure)
        {
            return Result.Failure<PublishResult?>(token.Error);
        }

        var session = new UploadSession { SessionUrl = idempotencyKey, TotalBytes = 1 };
        var confirmed = await ConfirmedAsync(token.Value, session, cancellationToken).ConfigureAwait(false);

        if (confirmed.IsFailure)
        {
            return Result.Failure<PublishResult?>(confirmed.Error);
        }

        return Result.Success(confirmed.Value.Video is { } video
            ? Publish(video, Visibility.Private, null)
            : null);
    }

    /* ---- adımlar ---- */

    private async Task<Result<UploadSession>> StartSessionAsync(
        string token, PublishRequest request, long total, CancellationToken cancellationToken)
    {
        var metadata = new
        {
            snippet = new
            {
                title = Trim(request.Metadata.Title, Capabilities.MaxTitleLength),
                description = Trim(request.Metadata.Description, Capabilities.MaxDescriptionLength),
                tags = request.Metadata.Tags,
                categoryId = request.Metadata.CategoryId ?? "27",
                defaultLanguage = request.Metadata.Language.Value,
            },
            status = new
            {
                privacyStatus = PrivacyOf(request.Visibility),

                // ZAMANLAMA GİZLİ YÜKLEMEYLE ÇALIŞIYOR (§15.3): kota
                // gündüz harcanıyor, yayın istenen saatte oluyor.
                publishAt = request.PublishAt?.ToString("o", CultureInfo.InvariantCulture),
                selfDeclaredMadeForKids = false,
            },
        };

        var address = new Uri(
            _options.UploadAddress + "?uploadType=resumable&part=snippet,status");

        using var message = new HttpRequestMessage(HttpMethod.Post, address)
        {
            Content = JsonContent.Create(metadata),
        };

        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        message.Headers.TryAddWithoutValidation("X-Upload-Content-Length",
            total.ToString(CultureInfo.InvariantCulture));
        message.Headers.TryAddWithoutValidation("X-Upload-Content-Type", "video/mp4");

        using var source = Linked(cancellationToken);

        try
        {
            using var response = await http.SendAsync(message, source.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return await FailureAsync<UploadSession>(response, source.Token).ConfigureAwait(false);
            }

            var location = response.Headers.Location?.ToString();

            if (string.IsNullOrWhiteSpace(location))
            {
                return Error.Transient("youtube.no_session",
                    "Sürdürme adresi dönmedi; yükleme başlatılamadı.");
            }

            return Result.Success(new UploadSession
            {
                SessionUrl = location,
                TotalBytes = total,
                StartedAt = DateTimeOffset.UtcNow,
            });
        }
        catch (HttpRequestException ex)
        {
            return Error.Transient("youtube.network", ex.Message);
        }
    }

    private async Task<Result<(long Confirmed, VideoResource? Video)>> ConfirmedAsync(
        string token, UploadSession session, CancellationToken cancellationToken)
    {
        using var content = new ByteArrayContent([]);

        // BOYUT SORGUSU: "ne kadarını aldın" diye sormanın biçimi
        // `bytes */toplam`. Çökme sonrası ilk adım bu.
        content.Headers.TryAddWithoutValidation(
            "Content-Range", ResumableUpload.ContentRange(0, 0, session.TotalBytes));

        using var message = new HttpRequestMessage(HttpMethod.Put, session.SessionUrl)
        {
            Content = content,
        };

        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var source = Linked(cancellationToken);

        try
        {
            using var response = await http.SendAsync(message, source.Token).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                var video = await response.Content
                    .ReadFromJsonAsync<VideoResource>(Json, source.Token)
                    .ConfigureAwait(false);

                return Result.Success((session.TotalBytes, video));
            }

            // 308: "devam et" — kaç bayt alındığı `Range` başlığında.
            if ((int)response.StatusCode == 308)
            {
                var range = response.Headers.TryGetValues("Range", out var values)
                    ? values.FirstOrDefault()
                    : null;

                return Result.Success((ResumableUpload.ConfirmedFrom(range), (VideoResource?)null));
            }

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                // OTURUM DÜŞMÜŞ (bir hafta ömürlü). Yeniden denemek
                // ANLAMLI: yeni bir oturum açılacak.
                return Error.Transient("youtube.session_expired",
                    "Sürdürme oturumu geçersiz; baştan başlanmalı.");
            }

            return await FailureAsync<(long, VideoResource?)>(response, source.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return Error.Transient("youtube.network", ex.Message);
        }
    }

    private async Task<Result<ProviderResponse<PublishResult>>> UploadAsync(
        string token, UploadSession session, PublishRequest request, CancellationToken cancellationToken)
    {
        var chunkSize = ResumableUpload.AlignChunk(_options.ChunkBytes);
        var current = session;

        await using var stream = File.OpenRead(request.VideoPath);

        while (!current.Complete)
        {
            var (start, length) = ResumableUpload.NextChunk(current, chunkSize);

            if (length <= 0)
            {
                break;
            }

            var buffer = new byte[length];
            stream.Seek(start, SeekOrigin.Begin);

            var read = await stream.ReadAtLeastAsync(buffer, length, false, cancellationToken)
                .ConfigureAwait(false);

            using var content = new ByteArrayContent(buffer, 0, read);
            content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
            content.Headers.TryAddWithoutValidation(
                "Content-Range", ResumableUpload.ContentRange(start, read, current.TotalBytes));

            using var message = new HttpRequestMessage(HttpMethod.Put, current.SessionUrl)
            {
                Content = content,
            };

            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var source = Linked(cancellationToken);

            HttpResponseMessage response;

            try
            {
                response = await http.SendAsync(message, source.Token).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                // YARIM KALAN YÜKLEME KAYBOLMUYOR: oturum adresi
                // sürdürme jetonu olarak dönüyor ve bir sonraki deneme
                // kaldığı yerden devam ediyor.
                return Error.Transient("youtube.network", ex.Message);
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    var video = await response.Content
                        .ReadFromJsonAsync<VideoResource>(Json, source.Token)
                        .ConfigureAwait(false);

                    if (video?.Id is not { Length: > 0 })
                    {
                        return Error.Transient("youtube.no_video_id", "Video kimliği dönmedi.");
                    }

                    await ThumbnailAsync(token, video.Id, request, cancellationToken)
                        .ConfigureAwait(false);

                    return Result.Success(ProviderResponse<PublishResult>.Free(
                        Publish(video, request.Visibility, request.PublishAt)));
                }

                if ((int)response.StatusCode != 308)
                {
                    return await FailureAsync<ProviderResponse<PublishResult>>(response, source.Token)
                        .ConfigureAwait(false);
                }

                var range = response.Headers.TryGetValues("Range", out var values)
                    ? values.FirstOrDefault()
                    : null;

                var confirmed = ResumableUpload.ConfirmedFrom(range);

                if (confirmed <= current.ConfirmedBytes)
                {
                    // İLERLEME YOK: aynı parçayı sonsuza kadar
                    // göndermek yerine erteleniyor (ADR-011).
                    return Error.Resource("youtube.no_progress",
                        $"Yükleme ilerlemiyor: {current}", TimeSpan.FromMinutes(5));
                }

                current = current with
                {
                    ConfirmedBytes = confirmed,
                    Attempts = current.Attempts + 1,
                };
            }
        }

        return Error.Transient("youtube.incomplete", $"Yükleme tamamlanmadı: {current}");
    }

    /// Kapak yükleme — BAŞARISIZLIĞI VİDEOYU DÜŞÜRMÜYOR.
    ///
    /// Video yayınlandı; kapak ayrı bir çağrı ve ayrı 50 birim.
    /// Kapak yüzünden yayını başarısız saymak, yüklenmiş bir videoyu
    /// yeniden yükletmek olurdu.
    private async Task ThumbnailAsync(
        string token, string videoId, PublishRequest request, CancellationToken cancellationToken)
    {
        if (request.Thumbnail is null)
        {
            return;
        }

        try
        {
            var address = new Uri(_options.ApiAddress, $"thumbnails/set?videoId={videoId}");

            using var message = new HttpRequestMessage(HttpMethod.Post, address);
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var source = Linked(cancellationToken);
            using var response = await http.SendAsync(message, source.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // Yutuluyor ve bu bilinçli: kapak eksikliği QC'de zaten
            // görülüyor.
        }
    }

    /* ---- kimlik ---- */

    /// Erişim jetonu: doğrudan verilmişse o, yoksa yenileme jetonuyla
    /// alınıyor.
    ///
    /// YENİLEME BURADA ÇÜNKÜ JETON BİR SAAT ÖMÜRLÜ. Gece koşan bir
    /// fabrikada "jeton süresi doldu" hatası, sabaha kadar hiçbir
    /// videonun yayınlanmaması demek.
    internal async Task<Result<string>> TokenAsync(
        CancellationToken cancellationToken, string? account = null)
    {
        var token = await _tokens.GetAsync(account, cancellationToken).ConfigureAwait(false);

        // ***HATA KODU KORUNUYOR.***
        //
        // Ortak kaynak `google.no_credentials` diyor; bu sağlayıcının
        // sözleşmesi `youtube.no_credentials`. Kodu değiştirmek,
        // ona bakan testleri ve — daha önemlisi — bir operatörün
        // aradığı dizgiyi sessizce kaydırmak olurdu. Hata kodları
        // bir arayüz.
        if (token.IsFailure && token.Error.Code.StartsWith("google.", StringComparison.Ordinal))
        {
            return new Error(
                "youtube." + token.Error.Code["google.".Length..],
                token.Error.Message,
                token.Error.Kind,
                token.Error.Detail);
        }

        return token;
    }

    /* ---- ortak ---- */

    private CancellationTokenSource Linked(CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(_options.Timeout);

        return source;
    }

    private static async Task<Result<T>> FailureAsync<T>(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests
            || (int)response.StatusCode == 403 && body.Contains("quota", StringComparison.OrdinalIgnoreCase))
        {
            // KOTA HATASI KAYNAK SINIFI (ADR-011): yarın kota
            // sıfırlanıyor ve iş o zaman koşabilir. Kalıcı saymak,
            // üretilmiş bir videoyu çöpe atmak olurdu.
            return Error.Resource("youtube.quota", body,
                QuotaLedger.NextReset(DateTimeOffset.UtcNow) - DateTimeOffset.UtcNow);
        }

        return (int)response.StatusCode >= 500
            ? Error.Transient("youtube.server_error", $"{(int)response.StatusCode}: {body}")
            : Error.Permanent("youtube.request_failed", $"{(int)response.StatusCode}: {body}");
    }

    private static PublishResult Publish(
        VideoResource video, Visibility requested, DateTimeOffset? publishAt)
        => new()
        {
            ExternalId = video.Id ?? string.Empty,
            Url = new Uri($"https://www.youtube.com/watch?v={video.Id}"),

            // GERÇEKLEŞEN GÖRÜNÜRLÜK PLATFORMDAN OKUNUYOR: zamanlanmış
            // bir yükleme gizli başlıyor ve "herkese açık yayınlandı"
            // demek yanlış olurdu.
            Visibility = video.Status?.PrivacyStatus is { Length: > 0 } status
                ? VisibilityOf(status)
                : requested,
            ScheduledFor = publishAt,
            QuotaSpent = QuotaLedger.UploadCost,
        };

    internal static string PrivacyOf(Visibility visibility) => visibility switch
    {
        Visibility.Public => "public",
        Visibility.Unlisted => "unlisted",
        _ => "private",
    };

    internal static Visibility VisibilityOf(string privacy) => privacy switch
    {
        "public" => Visibility.Public,
        "unlisted" => Visibility.Unlisted,
        _ => Visibility.Private,
    };

    private string? Read(string name)
        => credentials?.Get(name) ?? Environment.GetEnvironmentVariable(name);

    /// Hesaba göre değişken adı — kural `Credentials.VariableFor`'da.
    ///
    /// ***KURAL BURADAN TAŞINDI:*** şifreli kimlik deposu köprüsü de
    /// aynı adı üretmek zorunda (depo hesaba göre saklıyor, yayıncı
    /// hesaba göre okuyor). İki yerde ayrı yazılsalardı, birinin
    /// `PROJE_02` diğerinin `PROJE-02` üretmesi yeterdi — ve sonuç
    /// "anahtar kayıtlı ama bulunamıyor" olurdu.
    public static string VariableFor(string name, string? account)
        => Credentials.VariableFor(name, account);

    private string? ReadFor(string name, string? account)
        => Read(VariableFor(name, account)) ?? Read(name);

    private static string Trim(string text, int limit)
        => text.Length <= limit ? text : text[..limit];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    internal sealed record VideoResource
    {
        public string? Id { get; init; }

        public VideoStatus? Status { get; init; }
    }

    internal sealed record VideoStatus
    {
        public string? PrivacyStatus { get; init; }
    }

    private sealed record TokenResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("access_token")]
        public string? AccessToken { get; init; }
    }
}
