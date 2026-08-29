using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;

namespace BytemountsAiStudio.Providers.Open;

/// YouTube Analytics ayarları (P5-01).
///
/// HER PARAMETRE AYARLANABİLİR: adres, ölçüt listesi, gecikme payı.
public sealed record YouTubeAnalyticsOptions
{
    public static Uri DefaultEndpoint { get; } =
        new("https://youtubeanalytics.googleapis.com/v2/reports");

    public const string EndpointVariable = "BMAI_YOUTUBE_ANALYTICS_URL";

    public Uri BaseAddress { get; init; } = Endpoints.Resolve(
        EndpointVariable, "https://youtubeanalytics.googleapis.com/v2/reports");

    public string AccessTokenVariable { get; init; } = "YOUTUBE_ACCESS_TOKEN";

    /// Çekilecek ölçütler.
    ///
    /// GÖSTERİM VE TIKLANMA AYRI BİR RAPORDAN geliyor
    /// (`impressions`, `impressionsClickThroughRate`) ama aynı
    /// çağrıda isteniyor: iki ayrı istek, iki ayrı kota ve iki ayrı
    /// başarısızlık noktası demekti.
    public string Metrics { get; init; } =
        "views,estimatedMinutesWatched,likes,comments,subscribersGained";

    /// ***ANALİTİK VERİSİ GECİKMELİ GELİYOR.***
    ///
    /// YouTube'un raporları iki güne kadar geriden geliyor. Yedinci
    /// günün sayılarını yedinci gün çekmek, TAMAMLANMAMIŞ bir sayıyı
    /// tam sanmak demek — ve deney o eksik sayıyla karar verirdi.
    ///
    /// Bu yüzden ölçüm, ölçülen günden bu kadar sonra çekiliyor.
    public int SettlingDays { get; init; } = 2;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);
}

/// YouTube Analytics günlük çekim (P5-01).
///
/// ÖĞRENME DÖNGÜSÜNÜN VERİ KAYNAĞI BURASI. P5-02'den P5-07'ye kadar
/// yazılan her şey — deney kararı, ağırlık kalibrasyonu, istem
/// raporu — bu tablodan besleniyor ve şimdiye kadar tablo yalnızca
/// elle dolduruluyordu.
///
/// ***VERİ GECİKMELİ GELİYOR VE BU SESSİZ BİR TUZAK.*** YouTube'un
/// raporları iki güne kadar geriden geliyor: yedinci günün sayılarını
/// yedinci gün çekmek eksik bir sayıyı tam sanmak demek. Sayı
/// makul görünüyor, kimse şüphelenmiyor, deney o sayıyla karar
/// veriyor. `SettlingDays` bunu engelliyor.
public sealed class YouTubeAnalyticsProvider(
    HttpClient http, YouTubeAnalyticsOptions? options = null, ICredentialSource? credentials = null)
    : IDailyMetricsSource
{
    public string Platform => "youtube";

    private readonly YouTubeAnalyticsOptions _options = options ?? new YouTubeAnalyticsOptions();

    public static string Key => "youtube-analytics";

    /// Bir günün ölçümü hazır mı.
    ///
    /// Ayrı ve saf: "bugün bu videoyu çekebilir miyim" sorusu, gerçek
    /// bir API çağrısı yapılarak öğrenilecek bir şey olmamalı.
    public bool IsSettled(DateOnly metricDate, DateOnly today)
        => today.DayNumber - metricDate.DayNumber >= _options.SettlingDays;

    /// Bir videonun bir günlük ölçümü.
    public async Task<Result<DailyMetric?>> DailyAsync(
        string externalId, DateOnly date, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);

        var token = credentials?.Get(_options.AccessTokenVariable)
            ?? Environment.GetEnvironmentVariable(_options.AccessTokenVariable);

        if (token is null)
        {
            return Error.Permanent("analytics.no_token",
                $"YouTube erişim jetonu yok ({_options.AccessTokenVariable}).");
        }

        var day = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var address = new Uri(
            _options.BaseAddress
            + "?ids=channel==MINE"
            + $"&startDate={day}&endDate={day}"
            + $"&metrics={Uri.EscapeDataString(_options.Metrics)}"
            + $"&filters=video=={Uri.EscapeDataString(externalId)}");

        using var message = new HttpRequestMessage(HttpMethod.Get, address);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(_options.Timeout);

        try
        {
            using var response = await http.SendAsync(message, source.Token).ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                return Error.Resource("analytics.rate_limited", "Analytics hız sınırı.",
                    response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(10));
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(source.Token).ConfigureAwait(false);

                return (int)response.StatusCode >= 500
                    ? Error.Transient("analytics.server_error", $"{(int)response.StatusCode}: {body}")
                    : Error.Permanent("analytics.request_failed", $"{(int)response.StatusCode}: {body}");
            }

            var report = await response.Content
                .ReadFromJsonAsync<ReportResponse>(Json, source.Token)
                .ConfigureAwait(false);

            return Result.Success(Parse(report, date));
        }
        catch (HttpRequestException ex)
        {
            return Error.Transient("analytics.network", ex.Message);
        }
        catch (JsonException ex)
        {
            return Error.Transient("analytics.bad_json", ex.Message);
        }
    }

    /// Rapor satırını ölçüme çevirir.
    ///
    /// SATIR YOKSA `null`, SIFIR DEĞİL. "O gün hiç izlenme yok" ile
    /// "o günün verisi henüz gelmedi" farklı iki şey; sıfır yazmak,
    /// gelmemiş bir günü ölçülmüş saymak ve ortalamayı aşağı çekmek
    /// olurdu.
    internal static DailyMetric? Parse(ReportResponse? report, DateOnly date)
    {
        if (report?.Rows is not { Count: > 0 } rows || rows[0].Count < 5)
        {
            return null;
        }

        var row = rows[0];

        return new DailyMetric(
            date,
            Long(row[0]),
            Long(row[1]),
            Long(row[2]),
            Long(row[3]),
            Long(row[4]));
    }

    private static long Long(JsonElement value)
        => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? (long)Math.Round(number)
            : 0;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    internal sealed record ReportResponse
    {
        public IReadOnlyList<IReadOnlyList<JsonElement>>? Rows { get; init; }
    }
}
