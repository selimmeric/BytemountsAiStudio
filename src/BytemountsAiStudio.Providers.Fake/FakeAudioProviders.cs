using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Time;
using BytemountsAiStudio.Providers.Fake.Media;

namespace BytemountsAiStudio.Providers.Fake;

/// Deterministik sahte TTS. Gerçek, çalınabilir bir sessiz WAV üretir.
///
/// Süre metin uzunluğuyla orantılı: konuşma hızını kabaca 14 karakter/saniye
/// varsayıyoruz. Sabit süre döndürseydik timeline derleyicisinin "sahne
/// süresini sesten al" davranışı sınanamazdı — her sahne aynı uzunlukta çıkar,
/// hata görünmezdi.
public sealed class FakeTtsProvider : ITtsProvider
{
    private const int MillisecondsPerCharacter = 71;
    private const int MinimumDurationMs = 400;

    public string Key => "fake-tts";

    /// Sahte sağlayıcı kelime zamanlaması verir. Böylece varsayılan boru hattı
    /// ASR yan servisine hiç gitmez; ASR yolu ayrıca <see cref="FakeAsrProvider"/>
    /// ile sınanır.
    public bool SupportsWordTimings => true;

    public int SynthesisCount => _synthesisCount;

    private int _synthesisCount;

    public Task<Result<ProviderResponse<TtsResponse>>> SynthesizeAsync(
        TtsRequest request,
        ProviderContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _synthesisCount);

        if (string.IsNullOrWhiteSpace(request.SpeechText))
        {
            return Task.FromResult(Result.Failure<ProviderResponse<TtsResponse>>(
                Error.Permanent("fake.tts.empty", "Seslendirilecek metin boş.")));
        }

        var duration = DurationFor(request.SpeechText, request.Speed);
        // SESSİZLİK DEĞİL, KONUŞMA BENZERİ SES.
        //
        // Sessizlik ürettiğinde sahte hat kabul kriterini hiçbir zaman
        // sağlayamıyordu: render −70 LUFS çıkıyor ve QC haklı olarak
        // düşürüyordu. Sahte bir sağlayıcı temsil ettiği şeyi temsil
        // etmeli — gerçek bir TTS konuşma döndürüyor.
        var audio = WavWriter.Speech(duration);
        var timings = DistributeWords(request.SpeechText, duration);

        var response = new TtsResponse
        {
            Audio = audio,
            MimeType = "audio/wav",
            ReportedDuration = duration,
            WordTimings = timings,
        };

        return Task.FromResult(Result.Success(new ProviderResponse<TtsResponse>(
            response, UsageUnits.OfCharacters(request.SpeechText.Length))));
    }

    public Task<Result<IReadOnlyList<VoiceInfo>>> ListVoicesAsync(
        LanguageTag language,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<VoiceInfo> voices =
        [
            new() { VoiceId = $"fake-{language.Primary}-f1", DisplayName = "Sahte Kadın 1", Language = language, Gender = "female" },
            new() { VoiceId = $"fake-{language.Primary}-m1", DisplayName = "Sahte Erkek 1", Language = language, Gender = "male" },
        ];

        return Task.FromResult(Result.Success(voices));
    }

    internal static Ms DurationFor(string text, double speed)
    {
        var raw = text.Length * MillisecondsPerCharacter / Math.Clamp(speed, 0.5, 2.0);
        return new Ms(Math.Max(MinimumDurationMs, (int)Math.Round(raw)));
    }

    /// Süreyi kelimelere uzunlukları oranında dağıtır.
    ///
    /// Son kelimenin bitişi süreye TAM oturtulur: oransal dağıtımda biriken
    /// yuvarlama farkı bırakılırsa altyazı sesin bir tık ilerisinde ya da
    /// gerisinde kalır ve bu, 50 sahnede gözle görülür hâle gelir.
    internal static IReadOnlyList<WordTiming> DistributeWords(string text, Ms duration)
    {
        var words = text.Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return [];
        }

        var totalWeight = words.Sum(w => w.Length);
        var timings = new List<WordTiming>(words.Length);
        var cursor = 0;

        for (var i = 0; i < words.Length; i++)
        {
            var isLast = i == words.Length - 1;
            var end = isLast
                ? duration.Value
                : cursor + Math.Max(1, (int)((long)duration.Value * words[i].Length / totalWeight));

            timings.Add(new WordTiming(words[i], new Ms(cursor), new Ms(Math.Min(end, duration.Value))));
            cursor = Math.Min(end, duration.Value);
        }

        return timings;
    }
}

/// Deterministik sahte hizalama.
///
/// TTS'in dağıtımıyla aynı algoritmayı kullanır: iki yol da aynı şemayı ve
/// aynı davranışı üretmeli ki "TTS timing veriyorsa ASR'ye gitme" kararı
/// çıktıyı değiştirmesin.
public sealed class FakeAsrProvider : IAsrProvider
{
    public string Key => "fake-asr";

    public int AlignCount => _alignCount;

    private int _alignCount;

    public Task<Result<ProviderResponse<AlignmentResult>>> AlignAsync(
        AlignRequest request,
        ProviderContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _alignCount);

        var duration = FakeTtsProvider.DurationFor(request.Transcript, 1.0);
        var words = FakeTtsProvider.DistributeWords(request.Transcript, duration);

        return Task.FromResult(Result.Success(new ProviderResponse<AlignmentResult>(
            new AlignmentResult(words, duration),
            new UsageUnits { Seconds = duration.TotalSeconds })));
    }
}
