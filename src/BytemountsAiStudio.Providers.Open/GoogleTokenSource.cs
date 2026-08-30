using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;

namespace BytemountsAiStudio.Providers.Open;

/// Google erişim jetonu — yenilemeli ve önbellekli.
///
/// ***TEK GERÇEKLEME, İKİ TÜKETİCİ.*** Yenileme akışı önce yalnızca
/// `YouTubePublisher` içindeydi ve `YouTubeAnalyticsProvider` **statik
/// bir jeton** okuyordu. Jeton bir saat ömürlü: gece koşan bir ölçüm
/// çekimi ilk saatten sonra `analytics.no_token` ile düşerdi ve
/// öğrenme döngüsünün verisi hiç gelmezdi.
///
/// Yenilemeyi ikinci kez yazmak yerine ortak sınıf: iki kopya er geç
/// ayrışır ve ayrışan kopya, yalnızca birinin bozulduğu bir hataya
/// dönüşür — bu depoda defalarca ödenmiş bir ders.
///
/// ***JETON ÖNBELLEKLENİYOR VE BU BİR DÜZELTME.*** Yayıncı her
/// çağrıda yeniden yeniliyordu. Elli videoluk bir günlük ölçüm
/// çekimi elli jeton yenilemesi demekti; Google'ın jeton ucunun da
/// kendi hız sınırı var ve ona takılmak, asıl işin kotasıyla hiç
/// ilgisi olmayan bir yerden çökmek olurdu.
///
/// ÖMÜR `expires_in`'DEN, TAHMİNDEN DEĞİL. Google süreyi cevapta
/// söylüyor; "bir saat" diye varsaymak, Google süreyi kısalttığında
/// süresi dolmuş bir jetonu önbellekten servis etmek demekti.
public sealed class GoogleTokenSource(
    HttpClient http,
    Uri tokenAddress,
    GoogleTokenVariables variables,
    ICredentialSource? credentials = null,
    TimeProvider? timeProvider = null)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// Hesap adı → önbellekteki jeton.
    ///
    /// ***HESAP BAŞINA AYRI (P4-04):*** kota havuzunda her projenin
    /// kendi kimliği var ve tek bir gözde saklamak, bir hesabın
    /// jetonunu diğerine göndermek olurdu — yükleme başka projenin
    /// kotasından düşerdi.
    private readonly ConcurrentDictionary<string, CachedToken> _cache = new(StringComparer.Ordinal);

    private sealed record CachedToken(string Value, DateTimeOffset ExpiresAt);

    /// Süresi dolmadan ne kadar önce yenileniyor.
    ///
    /// BİR DAKİKA: çağrının kendisi de zaman alıyor. Tam süre sonunda
    /// yenilemek, yolda süresi dolan bir jetonla istek atmak demekti
    /// ve o hata ("invalid credentials") sebebini söylemiyor.
    public static TimeSpan Margin { get; } = TimeSpan.FromMinutes(1);

    /// Jeton ömrü söylenmediğinde varsayılan.
    ///
    /// Google `expires_in` gönderiyor; göndermezse kısa bir süre
    /// varsaymak, uzun varsaymaktan güvenli — en kötü ihtimal fazladan
    /// bir yenileme.
    public static TimeSpan DefaultLifetime { get; } = TimeSpan.FromMinutes(30);

    public async Task<Result<string>> GetAsync(string? account, CancellationToken cancellationToken)
    {
        var key = account ?? Credentials.DefaultAccount;

        // ***DOĞRUDAN VERİLEN JETON ÖNBELLEĞE GİRMİYOR.***
        //
        // `YOUTUBE_ACCESS_TOKEN` elle verilen bir jeton: ne zaman
        // değiştiğini bilmiyoruz ve önbelleğe alırsak kullanıcı
        // değişkeni güncellediğinde eskisini servis ederdik.
        if (Read(variables.AccessToken, account) is { Length: > 0 } direct)
        {
            return Result.Success(direct);
        }

        if (_cache.TryGetValue(key, out var cached) && cached.ExpiresAt > _time.GetUtcNow())
        {
            return Result.Success(cached.Value);
        }

        var refresh = Read(variables.RefreshToken, account);
        var clientId = Read(variables.ClientId, account);
        var secret = Read(variables.ClientSecret, account);

        if (refresh is null || clientId is null || secret is null)
        {
            // KALICI: yeniden denemek eksik bir kimliği tamamlamıyor.
            // Mesaj hangi değişkenlerin gerektiğini SAYIYOR — "kimlik
            // eksik" tek başına hangisinin eksik olduğunu söylemiyor.
            return Error.Permanent("google.no_credentials",
                $"Google kimliği eksik ({variables.AccessToken} ya da "
                + $"{variables.RefreshToken}+{variables.ClientId}+{variables.ClientSecret}).");
        }

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client_id"] = clientId,
            ["client_secret"] = secret,
            ["refresh_token"] = refresh,
            ["grant_type"] = "refresh_token",
        });

        try
        {
            using var response = await http.PostAsync(tokenAddress, content, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);

                var detail = body.Length > 300 ? body[..300] : body;

                // 400/401 KALICI: yenileme jetonu iptal edilmiş ya da
                // istemci sırrı değişmiş. Yeniden denemek düzeltmiyor;
                // insanın yeniden yetki vermesi gerekiyor.
                return (int)response.StatusCode is >= 400 and < 500
                    ? Error.Permanent("google.token_rejected",
                        $"Jeton yenilenemedi (HTTP {(int)response.StatusCode}): {detail}")
                    : Error.Transient("google.token_failed",
                        $"Jeton ucu düştü (HTTP {(int)response.StatusCode}): {detail}");
            }

            var token = await response.Content
                .ReadFromJsonAsync<TokenResponse>(Json, cancellationToken)
                .ConfigureAwait(false);

            if (token?.AccessToken is not { Length: > 0 } value)
            {
                return Error.Transient("google.no_token", "Erişim jetonu dönmedi.");
            }

            var lifetime = token.ExpiresIn is > 0
                ? TimeSpan.FromSeconds(token.ExpiresIn.Value)
                : DefaultLifetime;

            // ÖMÜR MARJIN KADAR KISALTILIYOR ve asla negatif olmuyor:
            // çok kısa bir `expires_in` gelirse önbellek anında
            // geçersiz olur, o da doğru davranış.
            var expires = _time.GetUtcNow() + lifetime - Margin;

            if (expires > _time.GetUtcNow())
            {
                _cache[key] = new CachedToken(value, expires);
            }

            return Result.Success(value);
        }
        catch (HttpRequestException ex)
        {
            return Error.Transient("google.network", ex.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Error.Transient("google.timeout", "Jeton ucu cevap vermedi.");
        }
    }

    /// Önbelleği boşaltır — jeton reddedildiğinde.
    ///
    /// Sunucu "bu jeton geçersiz" derse önbellekteki kopyanın da
    /// geçersiz olduğu kesin; tutmak, süre dolana kadar aynı hatayı
    /// tekrarlamak demekti.
    public void Invalidate(string? account = null)
        => _cache.TryRemove(account ?? Credentials.DefaultAccount, out _);

    private string? Read(string name, string? account)
    {
        var scoped = Credentials.VariableFor(name, account);

        return Lookup(scoped) ?? Lookup(name);
    }

    private string? Lookup(string name)
        => credentials?.Get(name) ?? Environment.GetEnvironmentVariable(name);

    private sealed record TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; init; }

        /// ***ÖMÜR CEVAPTAN OKUNUYOR.***
        ///
        /// Yayıncının eski `TokenResponse`'u bu alanı hiç okumuyordu
        /// çünkü her çağrıda yeniliyordu. Önbellek varken ömür şart:
        /// "bir saat" diye varsaymak, Google süreyi kısalttığında
        /// süresi dolmuş bir jetonu servis etmek demekti.
        [JsonPropertyName("expires_in")]
        public int? ExpiresIn { get; init; }
    }
}

/// Jetonun okunacağı ortam değişkenlerinin adları.
///
/// AYRI BİR KAYIT: yayıncı ve analitik AYNI Google projesini
/// kullanıyor ama farklı kapsamlar isteyebiliyor
/// (`youtube.upload` ile `yt-analytics.readonly`). Adları
/// parametreleştirmek, ikisini ayrı kimliklerle koşturmayı bir
/// yapılandırma değişikliğine indiriyor.
public sealed record GoogleTokenVariables
{
    public string AccessToken { get; init; } = "YOUTUBE_ACCESS_TOKEN";

    public string RefreshToken { get; init; } = "YOUTUBE_REFRESH_TOKEN";

    public string ClientId { get; init; } = "YOUTUBE_CLIENT_ID";

    public string ClientSecret { get; init; } = "YOUTUBE_CLIENT_SECRET";
}
