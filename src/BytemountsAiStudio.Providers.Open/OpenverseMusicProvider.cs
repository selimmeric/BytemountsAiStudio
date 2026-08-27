using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Core.Time;

namespace BytemountsAiStudio.Providers.Open;

/// Openverse üzerinden arka plan müziği — anahtarsız, Creative Commons
/// (P2-09).
///
/// LİSANS KANITI OLMAYAN MÜZİK YAYINA GİREMEZ (§2.3/13).
///
/// Bu, görsellerden daha sert bir kural. Content ID sistemi müziği
/// otomatik tanıyor ve bir talep, kanalın o videodan gelen gelirinin
/// tamamını götürüyor — bazen kanalın tamamına ihtar geliyor. Bir
/// görselde atıf eksikliği düzeltilebilir bir kusur; müzikte
/// düzeltilemez bir hasar.
///
/// Bu yüzden `license_type=commercial,modification` filtresi kodun
/// içinde sabit ve dönen her sonuç ayrıca doğrulanıyor — görsel
/// sağlayıcıyla aynı gerekçe (P1-17a).
public sealed class OpenverseMusicProvider(HttpClient http) : IMusicProvider
{
    private const string SearchAddress = "https://api.openverse.org/v1/audio/";

    private const string UserAgent = "BytemountsAiStudio/0.1 (icerik uretim arastirmasi)";

    /// Ticari kullanıma ve değiştirmeye izin verenler.
    ///
    /// `by-sa` LİSTEDE DEĞİL, görsellerden farklı olarak: ShareAlike,
    /// türev eserin aynı lisansla yayılmasını istiyor ve arka plan
    /// müziği videonun tamamını türev hâline getiriyor. Bir videoyu
    /// CC BY-SA ile yayınlamak, kanalın kendi içeriğini de o lisansa
    /// bağlamak demek.
    private static readonly HashSet<string> AllowedLicenses =
        new(StringComparer.OrdinalIgnoreCase) { "by", "cc0", "pdm" };

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        // SNAKE_CASE ZORUNLU: Openverse `license_version` ve
        // `license_url` gönderiyor. Yalnızca büyük/küçük harf
        // duyarsızlığı bunları eşlemiyor ve alanlar SESSİZCE null
        // kalıyordu — lisans sürümü kayboluyor, oysa CC BY 2.0 ile
        // 4.0'ın atıf gereklilikleri farklı.
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public string Key => "openverse-audio";

    /// Bir parça seçer.
    ///
    /// SEÇİM KURALI: önce ATIF İSTEMEYENLER (cc0, pdm), sonra süresi
    /// gerekene en yakın olan.
    ///
    /// Atıf istemeyeni tercih etmek bir kolaylık değil bir risk
    /// azaltma: CC BY'de atıf videonun açıklamasına girmek zorunda ve
    /// o açıklama sonradan düzenlenirse ya da bir platforma
    /// kopyalanırken kısalırsa lisans ihlali oluyor. Atıf gerekmeyen
    /// bir parçada bu zincirin hiçbir halkası yok.
    public async Task<Result<ProviderResponse<MusicTrack>>> SelectAsync(
        MusicQuery query, ProviderContext context, CancellationToken cancellationToken)
    {
        var found = await FindAsync(query, context, cancellationToken).ConfigureAwait(false);

        if (found.IsFailure)
        {
            return Result.Failure<ProviderResponse<MusicTrack>>(found.Error);
        }

        var track = found.Value.Value
            .OrderBy(t => t.License.RequiresAttribution)
            .ThenBy(t => Math.Abs(t.Duration.Value - query.MinimumDuration.Value))
            .FirstOrDefault();

        // MÜZİK BULUNAMAMASI GEÇİCİ bir durum: arama başka bir zaman
        // başka sonuç verebiliyor. Kalıcı saymak, o kanalın bir daha
        // hiç müzik denememesi demekti.
        //
        // Çağıran taraf bunu düşürmeden geçebilir: arka plan müziği
        // olmayan bir video hâlâ yayınlanabilir bir video.
        return track is null
            ? Error.Transient("openverse_audio.no_match",
                $"'{query.Mood}' için en az {query.MinimumDuration.Value} ms süren lisanslı parça bulunamadı.")
            : Result.Success(new ProviderResponse<MusicTrack>(track, UsageUnits.OfRequests()));
    }

    internal async Task<Result<ProviderResponse<IReadOnlyList<MusicTrack>>>> FindAsync(
        MusicQuery query, ProviderContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var address = SearchAddress
            + $"?q={Uri.EscapeDataString(MoodToTerms(query.Mood))}"
            + "&license_type=commercial,modification"
            + "&page_size=20";

        using var message = new HttpRequestMessage(HttpMethod.Get, address);
        message.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

        try
        {
            using var response = await http.SendAsync(message, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;

                return status is >= 500 or 429
                    ? Error.Transient("openverse_audio.unavailable", $"HTTP {status}")
                    : Error.Permanent("openverse_audio.rejected", $"HTTP {status}");
            }

            var parsed = await response.Content
                .ReadFromJsonAsync<SearchResponse>(Json, cancellationToken)
                .ConfigureAwait(false);

            var captured = DateTimeOffset.UtcNow;
            var tracks = new List<MusicTrack>();

            foreach (var item in parsed?.Results ?? [])
            {
                // LİSANS İKİNCİ KEZ DOĞRULANIYOR: sunucu tarafındaki
                // filtre yeterli görünüyor ama API davranışı
                // değişirse sessizce ihlal etmeyelim.
                if (item.License is not { } license || !AllowedLicenses.Contains(license))
                {
                    continue;
                }

                if (item.Url is null || !Uri.TryCreate(item.Url, UriKind.Absolute, out var url))
                {
                    continue;
                }

                // SÜRESİ BİLİNMEYEN parça atlanıyor.
                //
                // Videodan kısa bir müzik ortada kesiliyor ve o kesinti
                // izleyicinin fark ettiği ilk şey oluyor. "Bilinmiyor"u
                // "yeterli" saymak, bu kusuru rastgele bir videoda
                // ortaya çıkarmaktı.
                if (item.Duration is not { } durationMs || durationMs <= 0)
                {
                    continue;
                }

                if (durationMs < query.MinimumDuration.Value)
                {
                    continue;
                }

                tracks.Add(new MusicTrack
                {
                    Url = url,
                    Duration = new Ms(durationMs),
                    Title = item.Title,
                    License = new LicenseInfo
                    {
                        Name = LicenseName(license, item.LicenseVersion),
                        Url = item.LicenseUrl is { Length: > 0 } licenseUrl
                              && Uri.TryCreate(licenseUrl, UriKind.Absolute, out var parsedLicense)
                            ? parsedLicense
                            : null,
                        Author = item.Creator,
                        // CC BY ATIF ZORUNLU KILIYOR ve bu bilgi
                        // videonun açıklamasına girmek zorunda. `cc0`
                        // ve `pdm` istemiyor.
                        RequiresAttribution = string.Equals(license, "by", StringComparison.OrdinalIgnoreCase),
                        CapturedAt = captured,
                    },
                });
            }

            return Result.Success(new ProviderResponse<IReadOnlyList<MusicTrack>>(
                tracks, UsageUnits.OfRequests()));
        }
        catch (HttpRequestException ex)
        {
            return Error.Transient("openverse_audio.unreachable", ex.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Error.Transient("openverse_audio.timeout", "Openverse ses araması zaman aşımına uğradı.");
        }
    }

    /// Ruh hâlini arama terimine çevirir.
    ///
    /// TEK KELİME, ve bu canlı sorgularla öğrenildi: Openverse
    /// terimleri VE ile birleştiriyor. "ambient documentary
    /// underscore" SIFIR sonuç veriyor, "documentary" tek başına 240.
    /// Terimleri "zenginleştirmek" aramayı iyileştirmiyor, tamamen
    /// bozuyor — ve boş sonuç sessizce "müzik yok" olarak geçiyordu.
    ///
    /// Ayrı ve `internal`: hangi kelimenin arandığı sınanabilir
    /// olmalı.
    internal static string MoodToTerms(string? mood) => mood?.Trim().ToUpperInvariant() switch
    {
        "CINEMATIC" => "cinematic",
        "DOCUMENTARY" => "documentary",
        "SUSPENSE" => "suspense",
        "EMOTIONAL" => "emotional",
        "ENERGETIC" => "upbeat",
        "AMBIENT" => "ambient",
        // Bilinmeyen bir ruh hâli için ambient: arka planda en az
        // dikkat çeken tür ve yanlış seçim en az zarar veriyor.
        _ => "ambient",
    };

    /// Lisans adı, sürümüyle birlikte.
    ///
    /// Sürüm ÖNEMLİ: CC BY 2.0 ile 4.0'ın atıf gereklilikleri farklı
    /// ve "o gün hangi sürümdü" sorusunun cevabı ancak kaydedilmişse
    /// var.
    internal static string LicenseName(string license, string? version)
    {
        var name = license.ToUpperInvariant() switch
        {
            "BY" => "CC BY",
            "CC0" => "CC0",
            "PDM" => "Public Domain Mark",
            _ => license.ToUpperInvariant(),
        };

        return string.IsNullOrWhiteSpace(version)
            ? name
            : string.Create(CultureInfo.InvariantCulture, $"{name} {version}");
    }

    internal sealed record SearchResponse(List<AudioItem>? Results);

    internal sealed record AudioItem(
        string? Title,
        string? Url,
        string? Creator,
        string? License,
        string? LicenseVersion,
        string? LicenseUrl,
        int? Duration);
}
