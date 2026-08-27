using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Core.Time;

namespace BytemountsAiStudio.Providers.Open;

/// Yan-servis üzerinden Piper ile seslendirme (P1-26).
///
/// VAR OLMA SEBEBİ SOMUT: Windows'un yerel sentezi yalnızca KURULU dil
/// paketleri için ses veriyor. Bu makinede yalnızca `Microsoft Tolga`
/// (tr-TR) var, yani ikinci dil hiç üretilemiyordu.
///
/// Piper tamamen çevrimdışı ve anahtarsız (ADR-015). Kalite ücretli
/// sağlayıcıların altında ama Windows'un SAPI seslerinin üstünde ve —
/// asıl mesele — hangi dili istersek onu konuşuyor.
public sealed class SidecarTtsProvider(HttpClient http, ToolsSidecarOptions? options = null) : ITtsProvider
{
    private readonly ToolsSidecar _sidecar = new(http, options);

    public string Key => "piper";

    /// Piper kelime zamanı vermiyor; hizalama ASR'ye düşüyor (P1-15).
    public bool SupportsWordTimings => false;

    public async Task<Result<ProviderResponse<TtsResponse>>> SynthesizeAsync(
        TtsRequest request, ProviderContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.SpeechText))
        {
            return Error.Permanent("piper.empty", "Seslendirilecek metin boş.");
        }

        var result = await _sidecar.SpeakAsync(request, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            return Result.Failure<ProviderResponse<TtsResponse>>(result.Error);
        }

        var audio = result.Value.Audio;

        // Boş ya da anlamsız küçük bir WAV başarı sayılmamalı: sessiz
        // bir video hiçbir şeyi kırmadan yayına gider.
        if (audio.Length < 1024)
        {
            return Error.Transient("piper.too_small",
                $"Üretilen ses çok küçük ({audio.Length} bayt).");
        }

        return Result.Success(new ProviderResponse<TtsResponse>(
            new TtsResponse
            {
                Audio = audio,
                MimeType = "audio/wav",
                // Otorite DEĞİL: hatta giren süre her zaman ffprobe ile
                // ölçülenidir (ADR-006).
                ReportedDuration = new Ms(result.Value.DurationMs),
                WordTimings = [],
                VoiceUsed = result.Value.Voice,
            },
            new UsageUnits { Characters = request.SpeechText.Length }));
    }

    /// Yan-servisteki kurulu sesler.
    public async Task<Result<IReadOnlyList<VoiceInfo>>> ListVoicesAsync(
        LanguageTag language, CancellationToken cancellationToken)
    {
        var health = await _sidecar.HealthAsync(cancellationToken).ConfigureAwait(false);

        if (health.IsFailure)
        {
            return Result.Failure<IReadOnlyList<VoiceInfo>>(health.Error);
        }

        var voices = new List<VoiceInfo>();

        if (!health.Value.Can("tts"))
        {
            return Result.Success<IReadOnlyList<VoiceInfo>>(voices);
        }

        // Yetenek ayrıntısı ses adlarını virgülle ayrılmış veriyor:
        // "en_US-amy-medium, tr_TR-dfki-medium".
        foreach (var name in health.Value.Why("tts").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var tag = ParseLanguage(name);

            if (tag is not { } voiceLanguage
                || !string.Equals(voiceLanguage.Primary, language.Primary, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            voices.Add(new VoiceInfo
            {
                VoiceId = name,
                DisplayName = name,
                Language = voiceLanguage,
                Gender = "unknown",
            });
        }

        return Result.Success<IReadOnlyList<VoiceInfo>>(voices);
    }

    /// Piper ses adından dil etiketi: `tr_TR-dfki-medium` → `tr-TR`.
    internal static LanguageTag? ParseLanguage(string voiceName)
    {
        var dash = voiceName.IndexOf('-', StringComparison.Ordinal);

        if (dash <= 0)
        {
            return null;
        }

        var tag = LanguageTag.TryCreate(voiceName[..dash].Replace('_', '-'));

        return tag.IsSuccess ? tag.Value : null;
    }
}

/// Sırayla denenen TTS sağlayıcıları.
///
/// Kural, katmanlı LLM sağlayıcısıyla AYNI (P1-03): KAYNAK ve GEÇİCİ
/// hatada bir sonrakine geçiliyor, KALICI hatada geçilmiyor — aynı
/// geçersiz isteği ikinci bir sağlayıcıya göndermek yalnızca ikinci
/// kez başarısız olmaktı.
///
/// Asıl kullanımı dil: Windows'un yerel sesi Türkçe için kurulu ve
/// bedava, ama İngilizce için hiç yok. O dilde `Kaynak` hatası dönüyor
/// ve sıra Piper'a geçiyor.
public sealed class FallbackTtsProvider(IReadOnlyList<ITtsProvider> providers) : ITtsProvider
{
    private readonly IReadOnlyList<ITtsProvider> _providers = providers.Count > 0
        ? providers
        : throw new ArgumentException("En az bir sağlayıcı gerekiyor.", nameof(providers));

    public string Key => "tts-fallback";

    /// Sağlayıcılardan HERHANGİ BİRİ kelime zamanı veriyorsa true.
    ///
    /// Bu bir SEÇİM KRİTERİ (ADR-002r) ve iyimser olması doğru: hangi
    /// sağlayıcının kullanılacağı ancak çağrı anında belli oluyor ve
    /// zamanlama gelirse ASR'ye hiç gidilmiyor.
    public bool SupportsWordTimings => _providers.Any(p => p.SupportsWordTimings);

    public async Task<Result<ProviderResponse<TtsResponse>>> SynthesizeAsync(
        TtsRequest request, ProviderContext context, CancellationToken cancellationToken)
    {
        var errors = new List<Error>();

        foreach (var provider in _providers)
        {
            var result = await provider.SynthesizeAsync(request, context, cancellationToken)
                .ConfigureAwait(false);

            if (result.IsSuccess)
            {
                return result;
            }

            errors.Add(result.Error);

            if (result.Error.Kind == ErrorKind.Permanent)
            {
                break;
            }
        }

        // HATALARIN TAMAMI bildiriliyor. Yalnızca sonuncuyu vermek en
        // yaygın yanlış teşhis sebebi olurdu: "Piper'da ses yok"
        // denirken asıl sorun Windows'un dil paketi eksikliği olabilir.
        //
        // Sınıf İLKİNİN sınıfı kalıyor: kuyruğun kararı birincile göre
        // verilmeli.
        var detail = string.Join(" | ", errors.Select(e => $"{e.Code}: {e.Message}"));

        return errors[0].Kind switch
        {
            ErrorKind.Resource => Error.Resource("tts.all_failed", detail, TimeSpan.FromMinutes(30)),
            ErrorKind.Permanent => Error.Permanent("tts.all_failed", detail),
            _ => Error.Transient("tts.all_failed", detail),
        };
    }

    public async Task<Result<IReadOnlyList<VoiceInfo>>> ListVoicesAsync(
        LanguageTag language, CancellationToken cancellationToken)
    {
        var voices = new List<VoiceInfo>();

        foreach (var provider in _providers)
        {
            var result = await provider.ListVoicesAsync(language, cancellationToken).ConfigureAwait(false);

            if (result.IsSuccess)
            {
                voices.AddRange(result.Value);
            }
        }

        return Result.Success<IReadOnlyList<VoiceInfo>>(voices);
    }
}
