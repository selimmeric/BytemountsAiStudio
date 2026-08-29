using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Core.Time;

namespace BytemountsAiStudio.Providers.Open;

/// Python araçlar yan-servisinin yapılandırması (P1-04).
///
/// Adres UZAK OLABİLİR ve genelde uzak olacak — sebebi Ollama ile aynı
/// (`docs/DONANIM-VE-MODEL.md`): hizalama bir ASR modeli koşturuyor ve
/// filodaki 2 GB'lık kartlara sığmıyor. Yan-servis güçlü makinede
/// koşuyor, diğerleri ağ üstünden çağırıyor.
public sealed record ToolsSidecarOptions
{
    /// Kodda sabit DEĞİL, VARSAYILAN: yan-servis başka bir makinede
    /// koşuyorsa `BMAI_TOOLS_URL` yeterli.
    public Uri BaseAddress { get; init; } =
        Endpoints.Resolve("BMAI_TOOLS_URL", "http://localhost:8099");

    /// Hizalama bir modeli belleğe yüklüyor; ilk çağrı dakikalar sürebilir.
    public TimeSpan AlignTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// Tarayıcıyla render, düz HTTP çekmeden kat kat yavaş.
    public TimeSpan FetchTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// Seslendirme hızlı ama İLK çağrı modeli belleğe yüklüyor.
    public TimeSpan SpeakTimeout { get; init; } = TimeSpan.FromMinutes(3);

    /// Varsayılan adres — `config/providers.json` ile AYNI olmak
    /// zorunda; `ProviderEndpointTests` ikisini karşılaştırıyor.
    public static Uri DefaultEndpoint { get; } = new("http://localhost:8099");

    public const string EndpointVariable = "BMAI_TOOLS_URL";

    public static ToolsSidecarOptions FromEnvironment() => From(Environment.GetEnvironmentVariable);

    /// Okuma işlevi dışarıdan veriliyor: yapılandırma mantığı süreç
    /// geneli ortam değişkenlerine DOKUNMADAN sınanabilsin.
    internal static ToolsSidecarOptions From(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        var options = new ToolsSidecarOptions();

        if (read("BMAI_TOOLS_URL") is { Length: > 0 } url
            && Uri.TryCreate(url, UriKind.Absolute, out var address))
        {
            options = options with { BaseAddress = address };
        }

        return options;
    }
}

/// Python araçlar yan-servisinin istemcisi (P1-04).
///
/// Üç yeteneği tek sınıfta topluyor çünkü üçü de AYNI sürece gidiyor:
/// adres, sağlık durumu ve hata sınıflandırması ortak. Üç ayrı sınıf
/// olsaydı "yan-servis ayakta mı" sorusu üç yerde ayrı ayrı
/// cevaplanırdı.
///
/// Sınıf üç arayüzü birden gerçekliyor; çağıran taraf hangisine
/// ihtiyacı varsa onu görüyor ve arkasında tek bir servis olduğunu
/// bilmiyor.
public sealed class ToolsSidecar(HttpClient http, ToolsSidecarOptions? options = null)
    : IWebFetchProvider, IAsrProvider
{
    private readonly ToolsSidecarOptions _options = options ?? new ToolsSidecarOptions();

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Key => "tools-sidecar";

    /// Yan-servisin durumu ve HANGİ YETENEKLERİNİN açık olduğu.
    ///
    /// "Ayakta mı" tek başına yetmiyor: playwright kurulu olmayan bir
    /// servis de ayakta görünür ve `/fetch` çağrısı ancak çalışma
    /// anında patlar. Yönlendirme kararı yeteneğe bakmalı.
    public async Task<Result<SidecarHealth>> HealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http
                .GetAsync(new Uri(_options.BaseAddress, "/health"), cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return Error.Transient("tools.unhealthy", $"Yan-servis HTTP {(int)response.StatusCode} döndü.");
            }

            var payload = await response.Content
                .ReadFromJsonAsync<HealthPayload>(Json, cancellationToken)
                .ConfigureAwait(false);

            if (payload is null)
            {
                return Error.Transient("tools.bad_health", "Yan-servis sağlık yanıtı boş.");
            }

            return Result.Success(new SidecarHealth
            {
                Version = payload.Version ?? "?",
                Capabilities = (payload.Capabilities ?? [])
                    .Where(c => c.Name is not null)
                    .ToDictionary(c => c.Name!, c => new SidecarCapability(c.Available, c.Detail ?? string.Empty),
                        StringComparer.Ordinal),
            });
        }
        catch (HttpRequestException ex)
        {
            return Unreachable<SidecarHealth>(ex);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Error.Transient("tools.timeout", "Yan-servis sağlık kontrolü zaman aşımına uğradı.");
        }
    }

    /// Genel web araması (SearXNG üzerinden, yan-servis aracılığıyla).
    public async Task<Result<ProviderResponse<IReadOnlyList<SearchHit>>>> SearchAsync(
        SearchQuery query, ProviderContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var body = new
        {
            query = query.Text,
            language = (query.Language ?? context?.Language)?.Value ?? "tr-TR",
            max_results = Math.Clamp(query.MaxResults, 1, 25),
        };

        var result = await PostAsync<SearchPayload>("/search", body, TimeSpan.FromSeconds(30), cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            return Result.Failure<ProviderResponse<IReadOnlyList<SearchHit>>>(result.Error);
        }

        var hits = new List<SearchHit>();
        var rank = 1;

        foreach (var hit in result.Value.Hits ?? [])
        {
            if (hit.Url is null || !Uri.TryCreate(hit.Url, UriKind.Absolute, out var url))
            {
                continue;
            }

            hits.Add(new SearchHit
            {
                Url = url,
                Title = hit.Title ?? url.Host,
                Snippet = hit.Snippet,
                SourceType = ParseSourceType(hit.SourceType),
                Rank = rank++,
            });
        }

        return Result.Success(new ProviderResponse<IReadOnlyList<SearchHit>>(hits, UsageUnits.OfRequests()));
    }

    /// Tarayıcıyla render edilmiş sayfa çekme.
    ///
    /// robots.txt ve şema kontrolleri YAN-SERVİSTE yapılıyor, burada
    /// tekrarlanmıyor. Kontrolü çağırana taşımak, iki yerde ayrı ayrı
    /// bakılan ve bir gün birinde atlanan bir kural üretirdi (P1-06).
    public async Task<Result<ProviderResponse<FetchedDocument>>> FetchAsync(
        Uri url, ProviderContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);

        var result = await PostAsync<FetchPayload>(
            "/fetch",
            new { url = url.ToString(), render = true },
            _options.FetchTimeout,
            cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            return Result.Failure<ProviderResponse<FetchedDocument>>(result.Error);
        }

        var text = result.Value.Text ?? string.Empty;

        var finalUrl = Uri.TryCreate(result.Value.FinalUrl, UriKind.Absolute, out var parsed) ? parsed : url;

        return Result.Success(ProviderResponse<FetchedDocument>.Free(new FetchedDocument
        {
            Url = finalUrl,
            Title = result.Value.Title ?? finalUrl.Host,
            MainText = text,
            ContentHash = HtmlTextExtractor.Sha256(text),
            FetchedAt = DateTimeOffset.UtcNow,
        }));
    }

    /// Kelime zamanlarını sesten ÖLÇER (P1-15).
    ///
    /// Bu, P1-15a'daki karakter bazlı dağıtımın yerine geçiyor. Dağıtım
    /// işliyor ama bir hizalama değil: uzun bir duraklama ya da
    /// hızlanma olduğunda altyazı sesten kayıyor.
    public async Task<Result<ProviderResponse<AlignmentResult>>> AlignAsync(
        AlignRequest request, ProviderContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await PostAsync<AlignPayload>(
            "/align",
            new
            {
                audio_path = request.AudioPath,
                text = request.Transcript,
                language = request.Language.Value,
            },
            _options.AlignTimeout,
            cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            return Result.Failure<ProviderResponse<AlignmentResult>>(result.Error);
        }

        var words = ToWordTimings(result.Value.Words);

        // Sıfır kelime bir hizalama DEĞİL. Başarı olarak dönerse
        // çağıran taraf onu "ölçüldü" sayar ve tahmine düşmez —
        // sonuç altyazısız bir video olur ve hiçbir şey kırılmaz.
        if (words.Count == 0)
        {
            return Error.Transient("tools.align_empty",
                $"Hizalama hiç kelime döndürmedi ({request.AudioPath}).");
        }

        return Result.Success(ProviderResponse<AlignmentResult>.Free(
            new AlignmentResult(words, new Ms(result.Value.DurationMs))));
    }

    /// Piper ile seslendirme (P1-26).
    ///
    /// Ses BASE64 ile geliyor, yol ile değil: hizalamanın aksine dosya
    /// henüz bir yere ait değil, depoya yazmak çağıranın işi — ve
    /// yan-servisin ortak bir diske erişimi olmayabilir, başka
    /// makinede koşuyor olabilir.
    internal async Task<Result<SpokenAudio>> SpeakAsync(
        TtsRequest request, CancellationToken cancellationToken)
    {
        var result = await PostAsync<TtsPayload>(
            "/tts",
            new
            {
                text = request.SpeechText,
                language = request.Language.Value,
                voice = string.IsNullOrWhiteSpace(request.VoiceId) ? null : request.VoiceId,
                speed = request.Speed,
            },
            _options.SpeakTimeout,
            cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            return Result.Failure<SpokenAudio>(result.Error);
        }

        if (string.IsNullOrEmpty(result.Value.AudioBase64))
        {
            return Error.Transient("tools.tts_empty", "Yan-servis boş ses döndürdü.");
        }

        try
        {
            return Result.Success(new SpokenAudio(
                Convert.FromBase64String(result.Value.AudioBase64),
                result.Value.DurationMs,
                result.Value.Voice ?? "?"));
        }
        catch (FormatException ex)
        {
            return Error.Transient("tools.tts_bad_audio", $"Ses çözülemedi: {ex.Message}");
        }
    }

    /// Yan-servis çıktısını sözleşmeye çevirir. Ayrı ve `internal`:
    /// ağa çıkmadan sınanabilsin.
    internal static List<WordTiming> ToWordTimings(List<WordPayload>? words)
    {
        var timings = new List<WordTiming>();

        foreach (var word in words ?? [])
        {
            if (string.IsNullOrWhiteSpace(word.Word))
            {
                continue;
            }

            // Yan-servis aralıkları zaten düzeltiyor; burada bir kez
            // daha bakılıyor çünkü bozuk bir aralık bu tarafta sessiz
            // değil, GÖRÜNÜR bir kusura dönüşüyor: negatif süreli bir
            // altyazı ipucu.
            var start = Math.Max(word.StartMs, 0);
            var end = Math.Max(word.EndMs, start + 1);

            timings.Add(new WordTiming(word.Word.Trim(), new Ms(start), new Ms(end)));
        }

        return timings;
    }

    private async Task<Result<T>> PostAsync<T>(
        string path, object body, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            using var response = await http
                .PostAsJsonAsync(new Uri(_options.BaseAddress, path), body, Json, timeoutSource.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return await ClassifyAsync<T>(response, path, timeoutSource.Token).ConfigureAwait(false);
            }

            var payload = await response.Content
                .ReadFromJsonAsync<T>(Json, timeoutSource.Token)
                .ConfigureAwait(false);

            return payload is null
                ? Error.Transient("tools.empty_response", $"Yan-servis {path} için boş yanıt döndü.")
                : Result.Success(payload);
        }
        catch (HttpRequestException ex)
        {
            return Unreachable<T>(ex);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Error.Transient("tools.timeout",
                string.Create(CultureInfo.InvariantCulture,
                    $"Yan-servis {path} çağrısı {timeout.TotalSeconds:0} saniyede yanıt vermedi."));
        }
    }

    /// HTTP durumunu hata sınıfına çevirir (ADR-011).
    ///
    /// Sınıf ayrımı önemli çünkü kuyruğun kararını o belirliyor:
    /// - 503 KAYNAK: yetenek eksik (playwright kurulu değil). İş
    ///   başarısız değil, ERTELENMELİ — yan-servis düzeltilince
    ///   çalışacak.
    /// - 403 KALICI: robots.txt yasaklıyor. Yeniden denemek dosyayı
    ///   değiştirmez.
    /// - 400 KALICI: istek hatalı. İkinci kez göndermek aynı cevabı
    ///   verir.
    /// - 502/5xx GEÇİCİ: yan-servisin arkasındaki bir şey bozuk.
    private static async Task<Result<T>> ClassifyAsync<T>(
        HttpResponseMessage response, string path, CancellationToken cancellationToken)
    {
        var detail = await ReadDetailAsync(response, cancellationToken).ConfigureAwait(false);
        var status = (int)response.StatusCode;

        return status switch
        {
            // Yetenek eksikligi DAKIKALAR icinde duzelmiyor: birinin
            // playwright kurmasi ya da modeli indirmesi gerekiyor. Bir
            // dakika sonra tekrar denemek yalnizca kuyrugu mesgul
            // ederdi.
            503 => Error.Resource("tools.capability_missing",
                $"Yan-servis bu yeteneği sunmuyor ({path}): {detail}",
                TimeSpan.FromMinutes(15)),
            403 => Error.Permanent("tools.forbidden", detail),
            400 or 422 => Error.Permanent("tools.bad_request", $"{path}: {detail}"),
            429 => Error.Transient("tools.throttled", detail),
            _ => status >= 500
                ? Error.Transient("tools.upstream", $"Yan-servis HTTP {status}: {detail}")
                : Error.Permanent("tools.rejected", $"Yan-servis HTTP {status}: {detail}"),
        };
    }

    /// FastAPI hatayı `{"detail": "..."}` içinde veriyor. Gövdeyi
    /// atmak, teşhis için tek kullanışlı bilgiyi atmak olurdu.
    private static async Task<string> ReadDetailAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(body))
            {
                return response.ReasonPhrase ?? "gövde yok";
            }

            using var document = JsonDocument.Parse(body);

            return document.RootElement.TryGetProperty("detail", out var detail)
                   && detail.GetString() is { Length: > 0 } text
                ? text
                : body;
        }
        catch (JsonException)
        {
            return response.ReasonPhrase ?? "ayrıştırılamayan gövde";
        }
        catch (HttpRequestException)
        {
            return response.ReasonPhrase ?? "gövde okunamadı";
        }
    }

    private static Result<T> Unreachable<T>(HttpRequestException ex)
        => Error.Transient("tools.unreachable",
            $"Araçlar yan-servisine ulaşılamadı. `python -m uvicorn bmai_tools.main:app --port 8099`. {ex.Message}");

    internal static SourceType ParseSourceType(string? value) => value switch
    {
        "encyclopedia" => SourceType.Encyclopedia,
        "official" => SourceType.Official,
        "academic" => SourceType.Academic,
        "news" => SourceType.News,
        "community" => SourceType.Community,
        "blog" => SourceType.Blog,
        _ => SourceType.Unknown,
    };

    internal sealed record HealthPayload(string? Status, string? Version, List<CapabilityPayload>? Capabilities);

    internal sealed record CapabilityPayload(string? Name, bool Available, string? Detail);

    internal sealed record SearchPayload(List<SearchHitPayload>? Hits, int TotalAvailable);

    internal sealed record SearchHitPayload(string? Url, string? Title, string? Snippet, string? SourceType);

    internal sealed record FetchPayload(
        string? FinalUrl, string? Title, string? Text, int HtmlLength, bool Rendered, bool Truncated);

    internal sealed record AlignPayload(List<WordPayload>? Words, string? Language, int DurationMs, string? Model);

    internal sealed record WordPayload(string? Word, int StartMs, int EndMs, double Confidence);

    internal sealed record TtsPayload(
        string? AudioBase64, string? MimeType, int SampleRate, int DurationMs, string? Voice);
}

/// Yan-servisten gelen ses.
internal sealed record SpokenAudio(ReadOnlyMemory<byte> Audio, int DurationMs, string Voice);

/// Yan-servisin bir yeteneğinin durumu.
public sealed record SidecarCapability(bool Available, string Detail);

/// Yan-servisin sağlık raporu.
public sealed record SidecarHealth
{
    public required string Version { get; init; }

    public required IReadOnlyDictionary<string, SidecarCapability> Capabilities { get; init; }

    /// Bir yetenek gerçekten kullanılabilir mi.
    ///
    /// Bilinmeyen bir yetenek KULLANILAMAZ sayılıyor: yan-servisin
    /// eski bir sürümü onu hiç sunmuyor olabilir ve varsayılan olarak
    /// "var" demek, çağrıyı çalışma anına ertelenmiş bir hataya
    /// çevirirdi.
    public bool Can(string capability)
        => Capabilities.TryGetValue(capability, out var value) && value.Available;

    /// Bir yetenek neden kapalı.
    public string Why(string capability)
        => Capabilities.TryGetValue(capability, out var value)
            ? value.Detail
            : "yan-servis bu yeteneği hiç bildirmiyor";
}
