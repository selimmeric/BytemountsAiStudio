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

public sealed record ElevenLabsOptions
{
    public Uri BaseAddress { get; init; } = new("https://api.elevenlabs.io/v1/");

    public string KeyEnvironmentVariable { get; init; } = "ELEVENLABS_API_KEY";

    /// Çok dilli model: aynı ses kimliği hem Türkçe hem İngilizce
    /// konuşuyor. Dile özel model kullanılsaydı her dil için ayrı ses
    /// seçmek ve kanallar arası ton farkını kabul etmek gerekirdi.
    public string ModelId { get; init; } = "eleven_multilingual_v2";

    /// Varsayılan ses. Kanal ayarından eziliyor.
    public string DefaultVoiceId { get; init; } = "21m00Tcm4TlvDq8ikWAM";

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(3);
}

/// ElevenLabs seslendirme (P1-14).
///
/// SİSTEMİN EN PAHALI KALEMİ. Video başına maliyet neredeyse tamamen
/// buradan geliyor (ADR-015) — LLM işleri yerel modele düştüğü için
/// geriye kalan tek ölçülebilir gider bu.
///
/// KELİME ZAMANLAMASI VERİYOR ve asıl değeri burada. Windows TTS ve
/// Piper vermiyor; onlarda altyazı için ASR yan-servisine gidilmesi
/// gerekiyor (P1-15) ve o adım saniyeler ekliyor. Bu sağlayıcı
/// kullanıldığında hizalama adımı HİÇ ÇALIŞMIYOR.
///
/// Bu yüzden `SupportsWordTimings` bir SEÇİM KRİTERİ (ADR-002r):
/// hat, zamanlama veren sağlayıcıyı tercih ediyor.
public sealed class ElevenLabsTtsProvider(
    HttpClient http, ElevenLabsOptions? options = null, ICredentialSource? credentials = null) : ITtsProvider
{
    private readonly ElevenLabsOptions _options = options ?? new ElevenLabsOptions();

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Key => "elevenlabs";

    public bool SupportsWordTimings => true;

    public async Task<Result<ProviderResponse<TtsResponse>>> SynthesizeAsync(
        TtsRequest request, ProviderContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.SpeechText))
        {
            return Error.Permanent("elevenlabs.empty", "Seslendirilecek metin boş.");
        }

        var apiKey = ResolveKey();

        if (apiKey.IsFailure)
        {
            return Result.Failure<ProviderResponse<TtsResponse>>(apiKey.Error);
        }

        var voice = string.IsNullOrWhiteSpace(request.VoiceId) ? _options.DefaultVoiceId : request.VoiceId;

        // `with-timestamps` UCU: normal uç yalnızca ses veriyor.
        // Zamanlamayı ayrı bir çağrıyla istemek hem ikinci kez para
        // harcamak hem de iki çağrının farklı ses üretme riski demekti
        // — model deterministik değil.
        var path = $"text-to-speech/{Uri.EscapeDataString(voice)}/with-timestamps";

        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["text"] = request.SpeechText,
            ["model_id"] = _options.ModelId,
            ["voice_settings"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                // Hız SSML'le değil `speed` alanıyla veriliyor; sınırı
                // sağlayıcının kabul ettiği aralık.
                ["speed"] = Math.Clamp(request.Speed, 0.7, 1.2),
                ["stability"] = 0.5,
                ["similarity_boost"] = 0.75,
            },
        };

        using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(_options.Timeout);

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_options.BaseAddress, path))
        {
            Content = JsonContent.Create(body, options: Json),
        };

        message.Headers.TryAddWithoutValidation("xi-api-key", apiKey.Value);

        try
        {
            using var response = await http.SendAsync(message, source.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return await ClassifyAsync<ProviderResponse<TtsResponse>>(response, source.Token)
                    .ConfigureAwait(false);
            }

            var parsed = await response.Content
                .ReadFromJsonAsync<TimestampedAudio>(Json, source.Token)
                .ConfigureAwait(false);

            if (parsed?.AudioBase64 is not { Length: > 0 } encoded)
            {
                return Error.Transient("elevenlabs.no_audio", "Yanıtta ses yok.");
            }

            byte[] audio;

            try
            {
                audio = Convert.FromBase64String(encoded);
            }
            catch (FormatException ex)
            {
                return Error.Transient("elevenlabs.bad_audio", $"Ses çözülemedi: {ex.Message}");
            }

            // Sessiz bir video hiçbir şeyi kırmadan yayına gider.
            if (audio.Length < 1024)
            {
                return Error.Transient("elevenlabs.too_small",
                    $"Üretilen ses çok küçük ({audio.Length} bayt).");
            }

            var timings = ToWordTimings(parsed.Alignment);

            return Result.Success(new ProviderResponse<TtsResponse>(
                new TtsResponse
                {
                    Audio = audio,
                    MimeType = "audio/mpeg",
                    // OTORİTE DEĞİL: hatta giren süre her zaman ffprobe
                    // ile ölçülenidir (ADR-006). Buradaki değer
                    // hizalamanın son kelimesinden türetiliyor ve
                    // yalnızca teşhis için.
                    ReportedDuration = timings.Count > 0 ? timings[^1].End : Ms.Zero,
                    WordTimings = timings,
                    VoiceUsed = voice,
                },
                new UsageUnits { Characters = request.SpeechText.Length }));
        }
        catch (HttpRequestException ex)
        {
            return Error.Transient("elevenlabs.unreachable", ex.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Error.Transient("elevenlabs.timeout", "ElevenLabs zaman aşımına uğradı.");
        }
    }

    public async Task<Result<IReadOnlyList<VoiceInfo>>> ListVoicesAsync(
        LanguageTag language, CancellationToken cancellationToken)
    {
        var apiKey = ResolveKey();

        if (apiKey.IsFailure)
        {
            return Result.Failure<IReadOnlyList<VoiceInfo>>(apiKey.Error);
        }

        using var message = new HttpRequestMessage(HttpMethod.Get, new Uri(_options.BaseAddress, "voices"));
        message.Headers.TryAddWithoutValidation("xi-api-key", apiKey.Value);

        try
        {
            using var response = await http.SendAsync(message, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return await ClassifyAsync<IReadOnlyList<VoiceInfo>>(response, cancellationToken)
                    .ConfigureAwait(false);
            }

            var parsed = await response.Content
                .ReadFromJsonAsync<VoicesResponse>(Json, cancellationToken)
                .ConfigureAwait(false);

            // DİLE GÖRE SÜZÜLMÜYOR ve bu bilinçli: çok dilli model
            // her sesi her dilde konuşturuyor. Süzmek, kullanılabilir
            // seslerin çoğunu gizlerdi.
            var voices = (parsed?.Voices ?? [])
                .Where(v => v.VoiceId is { Length: > 0 })
                .Select(v => new VoiceInfo
                {
                    VoiceId = v.VoiceId!,
                    DisplayName = v.Name ?? v.VoiceId!,
                    Language = language,
                    Gender = v.Labels is not null && v.Labels.TryGetValue("gender", out var gender)
                        ? gender
                        : "unknown",
                })
                .ToList();

            return Result.Success<IReadOnlyList<VoiceInfo>>(voices);
        }
        catch (HttpRequestException ex)
        {
            return Error.Transient("elevenlabs.unreachable", ex.Message);
        }
    }

    /// Karakter hizalamasını KELİME zamanlamasına çevirir.
    ///
    /// ElevenLabs KARAKTER başına zaman veriyor; altyazı ipucu kelime
    /// başına isteniyor. Ayrı ve `internal`: ağa çıkmadan sınanabilsin.
    ///
    /// Boşluk kelime SINIRI ama kelimenin parçası değil: sonunu bir
    /// önceki karakterin bitişi belirliyor, yoksa her kelime bir
    /// sonrakine kadar uzar ve altyazı geç sönerdi.
    internal static IReadOnlyList<WordTiming> ToWordTimings(Alignment? alignment)
    {
        var timings = new List<WordTiming>();

        if (alignment?.Characters is not { Count: > 0 } characters
            || alignment.CharacterStartTimesSeconds is not { } starts
            || alignment.CharacterEndTimesSeconds is not { } ends)
        {
            return timings;
        }

        var count = Math.Min(characters.Count, Math.Min(starts.Count, ends.Count));

        var word = new System.Text.StringBuilder();
        double wordStart = 0;
        double wordEnd = 0;

        for (var i = 0; i < count; i++)
        {
            var character = characters[i];

            if (string.IsNullOrWhiteSpace(character))
            {
                Flush();
                continue;
            }

            if (word.Length == 0)
            {
                wordStart = starts[i];
            }

            word.Append(character);
            wordEnd = ends[i];
        }

        Flush();

        return timings;

        void Flush()
        {
            if (word.Length == 0)
            {
                return;
            }

            var start = Math.Max(0, (int)Math.Round(wordStart * 1000));
            var end = (int)Math.Round(wordEnd * 1000);

            // Sıfır ya da ters süreli bir ipucu altyazı
            // oluşturucusunda çöküyor; en az 1 ms.
            timings.Add(new WordTiming(word.ToString(), new Ms(start), new Ms(Math.Max(end, start + 1))));
            word.Clear();
        }
    }

    private Result<string> ResolveKey()
    {
        var value = credentials is not null
            ? credentials.Get(_options.KeyEnvironmentVariable)
            : Environment.GetEnvironmentVariable(_options.KeyEnvironmentVariable);

        return string.IsNullOrWhiteSpace(value)
            ? Error.Permanent("elevenlabs.no_key",
                $"ElevenLabs için anahtar yok ({_options.KeyEnvironmentVariable} tanımlı değil). "
                + "Anahtarsız yol: Windows konuşma sentezi ya da Piper.")
            : Result.Success(value);
    }

    /// HTTP durumunu hata sınıfına çevirir (ADR-011).
    ///
    /// 401 burada KAYNAK olabiliyor ve bu ayrım önemli: ElevenLabs
    /// karakter kotası bittiğinde de 401 dönüyor ve gövdedeki
    /// `quota_exceeded` durumu tek ayırt edici. Kalıcı sayılsaydı,
    /// kota yenilendikten sonra bile çalışmayacak bir işe dönüşürdü.
    private static async Task<Result<T>> ClassifyAsync<T>(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = string.Empty;

        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            body = response.ReasonPhrase ?? string.Empty;
        }

        var status = (int)response.StatusCode;
        var quota = body.Contains("quota_exceeded", StringComparison.OrdinalIgnoreCase);

        return status switch
        {
            429 => Error.Resource("elevenlabs.rate_limited", Trim(body), TimeSpan.FromMinutes(5)),
            401 when quota => Error.Resource("elevenlabs.quota", Trim(body), TimeSpan.FromHours(12)),
            401 or 403 => Error.Permanent("elevenlabs.unauthorized", Trim(body)),
            >= 500 => Error.Transient("elevenlabs.server_error",
                string.Create(CultureInfo.InvariantCulture, $"HTTP {status}: {Trim(body)}")),
            _ => Error.Permanent("elevenlabs.rejected",
                string.Create(CultureInfo.InvariantCulture, $"HTTP {status}: {Trim(body)}")),
        };
    }

    private static string Trim(string body)
        => body.Length > 400 ? body[..400] : body;

    internal sealed record TimestampedAudio(
        [property: JsonPropertyName("audio_base64")] string? AudioBase64,
        Alignment? Alignment);

    internal sealed record Alignment(
        List<string>? Characters,
        List<double>? CharacterStartTimesSeconds,
        List<double>? CharacterEndTimesSeconds);

    internal sealed record VoicesResponse(List<Voice>? Voices);

    internal sealed record Voice(
        [property: JsonPropertyName("voice_id")] string? VoiceId,
        string? Name,
        Dictionary<string, string>? Labels);
}
