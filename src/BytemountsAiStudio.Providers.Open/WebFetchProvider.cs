using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;

namespace BytemountsAiStudio.Providers.Open;

/// Sayfa içeriği çekme (P1-06).
///
/// Dört kapı, bu sırayla:
///   1. Şema ve alan adı  — yalnızca http/https, engelli liste dışı
///   2. robots.txt        — her istekten önce, atlanamaz
///   3. Boyut sınırı      — AKIŞ sırasında, indirdikten sonra değil
///   4. Süre sınırı       — bağlantı asılı kalırsa iş kuyruğu tıkanmasın
///
/// Sıra önemli: robots.txt kontrolü boyut sınırından önce, çünkü yasak
/// bir sayfayı "sadece bakıp bırakmak" da çekmek sayılıyor.
public sealed class WebFetchProvider(HttpClient http) : IWebFetchProvider
{
    /// Kimliğimizi açıkça söyleyen bir User-Agent.
    ///
    /// Tarayıcı taklidi yapmak bilinçli olarak REDDEDİLDİ: kendini
    /// gizleyen bir botun robots.txt'ye uyması da bir şey ifade etmez.
    /// Adımızı verirsek bir site bizi ayrıca engelleyebilir — bu bir
    /// eksiklik değil, doğru davranış.
    private const string UserAgent =
        "BytemountsAiStudio/0.1 (icerik arastirma; +https://github.com/selimmeric/BytemountsAiStudio)";

    /// 2 MB. Bir haber ya da ansiklopedi sayfası bunun çok altında.
    /// Üstüne çıkan şey büyük ihtimalle metin değil.
    public int MaxBytes { get; init; } = 2 * 1024 * 1024;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(20);

    /// Bu alan adları hiç denenmiyor.
    ///
    /// Sosyal ağlar ve video siteleri: içerikleri JS ile geliyor,
    /// robots.txt'leri zaten yasaklıyor ve kullanım şartları otomatik
    /// erişimi açıkça men ediyor. Denemek boşa istek ve gereksiz risk.
    public IReadOnlySet<string> BlockedHosts { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "facebook.com", "instagram.com", "x.com", "twitter.com", "linkedin.com",
        "tiktok.com", "youtube.com", "pinterest.com", "reddit.com",
    };

    /// Boşsa sınır yok. Doluysa YALNIZCA bu alan adları — kanal bazında
    /// "sadece şu kaynaklardan araştır" demek mümkün olsun (§14).
    public IReadOnlySet<string>? AllowedHosts { get; init; }

    /// robots.txt önbelleği. Her sayfa için yeniden çekmek, çektiğimiz
    /// sayfa sayısını ikiye katlardı.
    private readonly ConcurrentDictionary<string, CachedRobots> _robots = new(StringComparer.OrdinalIgnoreCase);

    public TimeSpan RobotsCacheDuration { get; init; } = TimeSpan.FromHours(1);

    public string Key => "webfetch";

    public async Task<Result<ProviderResponse<FetchedDocument>>> FetchAsync(
        Uri url, ProviderContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
        {
            return Error.Permanent("fetch.scheme", $"Yalnizca http/https destekleniyor: {url.Scheme}");
        }

        if (IsBlocked(url.Host))
        {
            return Error.Permanent("fetch.blocked_host",
                $"'{url.Host}' engelli listede: icerigi JS ile geliyor ve otomatik erisimi kullanim sartlarina aykiri.");
        }

        if (AllowedHosts is { Count: > 0 } allowed && !MatchesHost(allowed, url.Host))
        {
            return Error.Permanent("fetch.not_allowed",
                $"'{url.Host}' izinli alan listesinde yok.");
        }

        var robots = await RobotsForAsync(url, cancellationToken).ConfigureAwait(false);

        if (robots.IsFailure)
        {
            return Result.Failure<ProviderResponse<FetchedDocument>>(robots.Error);
        }

        if (!robots.Value.IsAllowed(url.PathAndQuery))
        {
            // KALICI hata: yeniden denemek robots.txt'yi değiştirmez.
            return Error.Permanent("fetch.robots_disallow",
                $"robots.txt bu yolu yasakliyor: {url.PathAndQuery}");
        }

        return await DownloadAsync(url, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<ProviderResponse<FetchedDocument>>> DownloadAsync(
        Uri url, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(Timeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            request.Headers.AcceptLanguage.ParseAdd("tr,en;q=0.8");

            using var response = await http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;

                if (status == 429)
                {
                    return Error.Resource("fetch.rate_limited", $"{url.Host} istek siniri uyguladi.",
                        response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1));
                }

                return status >= 500
                    ? Error.Transient("fetch.server_error", $"HTTP {status}")
                    : Error.Permanent("fetch.rejected", $"HTTP {status}");
            }

            var mime = response.Content.Headers.ContentType?.MediaType ?? string.Empty;

            if (mime.Length > 0 && !mime.StartsWith("text/", StringComparison.Ordinal)
                && !mime.Contains("html", StringComparison.Ordinal)
                && !mime.Contains("xml", StringComparison.Ordinal))
            {
                return Error.Permanent("fetch.not_text", $"Metin olmayan icerik: {mime}");
            }

            // Content-Length'e GÜVENİLMİYOR: sunucu yanlış ya da hiç
            // söylemeyebilir. Sınır akış sırasında uygulanıyor, yoksa
            // 500 MB'lık bir cevabı belleğe alıp sonra reddederdik.
            if (response.Content.Headers.ContentLength is { } declared && declared > MaxBytes)
            {
                return Error.Permanent("fetch.too_large",
                    string.Create(CultureInfo.InvariantCulture, $"{declared} bayt, sinir {MaxBytes}"));
            }

            var read = await ReadCappedAsync(response, cts.Token).ConfigureAwait(false);

            if (read.IsFailure)
            {
                return Result.Failure<ProviderResponse<FetchedDocument>>(read.Error);
            }

            var html = read.Value;
            var text = HtmlTextExtractor.ExtractMainText(html);

            if (text.Length < 100)
            {
                return Error.Permanent("fetch.too_little_text",
                    string.Create(CultureInfo.InvariantCulture,
                        $"Ana metin {text.Length} karakter; sayfa muhtemelen JS ile yukleniyor (sidecar gerekiyor)."));
            }

            var title = HtmlTextExtractor.ExtractTitle(html);

            return Result.Success(new ProviderResponse<FetchedDocument>(
                new FetchedDocument
                {
                    // Yönlendirme sonrası ADRES kaydediliyor: kaynak
                    // gösterirken kullanıcıyı bir yönlendirmeye değil,
                    // gerçek sayfaya göndermeliyiz.
                    Url = response.RequestMessage?.RequestUri ?? url,
                    Title = title.Length > 0 ? title : url.Host,
                    MainText = text,
                    ContentHash = Sha256(text),
                    FetchedAt = DateTimeOffset.UtcNow,
                    IsPaywalled = HtmlTextExtractor.LooksPaywalled(html, text),
                },
                new UsageUnits()));
        }
        catch (HttpRequestException ex)
        {
            return Error.Transient("fetch.unreachable", ex.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Error.Transient("fetch.timeout",
                string.Create(CultureInfo.InvariantCulture, $"{Timeout.TotalSeconds:0} saniyede cevap gelmedi."));
        }
    }

    /// Akışı sınıra kadar okur; sınır aşılırsa indirmeyi keser.
    private async Task<Result<string>> ReadCappedAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        var buffer = new byte[81920];
        using var memory = new MemoryStream();

        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (count == 0)
            {
                break;
            }

            if (memory.Length + count > MaxBytes)
            {
                return Error.Permanent("fetch.too_large",
                    string.Create(CultureInfo.InvariantCulture, $"Icerik {MaxBytes} bayt sinirini asti."));
            }

            memory.Write(buffer, 0, count);
        }

        // Sunucunun bildirdiği kodlama varsa o, yoksa UTF-8. Latin-1'e
        // düşmek Türkçe sayfalarda karakterleri bozar.
        var encoding = ResolveEncoding(response.Content.Headers.ContentType?.CharSet);

        return Result.Success(encoding.GetString(memory.ToArray()));
    }

    private static Encoding ResolveEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset))
        {
            return Encoding.UTF8;
        }

        try
        {
            return Encoding.GetEncoding(charset.Trim('"'));
        }
        catch (ArgumentException)
        {
            // Bilinmeyen kodlama adı çekmeyi düşürmemeli.
            return Encoding.UTF8;
        }
    }

    private async Task<Result<RobotsTxt>> RobotsForAsync(Uri url, CancellationToken cancellationToken)
    {
        var origin = $"{url.Scheme}://{url.Authority}";

        if (_robots.TryGetValue(origin, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return Result.Success(cached.Robots);
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"{origin}/robots.txt"));
            request.Headers.UserAgent.ParseAdd(UserAgent);

            using var response = await http.SendAsync(request, cts.Token).ConfigureAwait(false);

            RobotsTxt robots;

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound
                || (int)response.StatusCode == 410)
            {
                // Dosya yoksa kısıt yok (RFC 9309).
                robots = RobotsTxt.AllowAll;
            }
            else if ((int)response.StatusCode >= 500)
            {
                // 5xx'te ÇEKMİYORUZ. Standart bunu geçici bir
                // "her şey yasak" olarak tanımlıyor; "okuyamadım, o
                // hâlde serbesttir" demek tam tersi yönde bir hata
                // olurdu ve tam da sunucu zorlanırken yük bindirirdi.
                return Error.Transient("robots.unavailable",
                    $"robots.txt okunamadi (HTTP {(int)response.StatusCode}); cekim ertelendi.");
            }
            else if (!response.IsSuccessStatusCode)
            {
                robots = RobotsTxt.AllowAll;
            }
            else
            {
                var content = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                robots = RobotsTxt.Parse(content, "BytemountsAiStudio");
            }

            _robots[origin] = new CachedRobots(robots, DateTimeOffset.UtcNow.Add(RobotsCacheDuration));

            return Result.Success(robots);
        }
        catch (HttpRequestException ex)
        {
            return Error.Transient("robots.unreachable", $"robots.txt alinamadi: {ex.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Error.Transient("robots.timeout", "robots.txt zaman asimina ugradi.");
        }
    }

    private bool IsBlocked(string host) => MatchesHost(BlockedHosts, host);

    /// Alt alan adları da sayılıyor: `www.facebook.com` engelliyse
    /// `m.facebook.com` da engelli olmalı.
    private static bool MatchesHost(IReadOnlySet<string> hosts, string host)
        => hosts.Contains(host)
           || hosts.Any(h => host.EndsWith($".{h}", StringComparison.OrdinalIgnoreCase));

    private static string Sha256(string text)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private sealed record CachedRobots(RobotsTxt Robots, DateTimeOffset ExpiresAt);
}
