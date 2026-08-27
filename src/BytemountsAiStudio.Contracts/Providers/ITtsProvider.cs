using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Time;

namespace BytemountsAiStudio.Contracts.Providers;

/// Tek bir kelimenin ses içindeki yeri.
///
/// §2.1/4: kelime vurgulu altyazı ve kinetic typography bunsuz yapılamaz.
public sealed record WordTiming(string Text, Ms Start, Ms End);

public sealed record VoiceInfo
{
    public required string VoiceId { get; init; }

    public required string DisplayName { get; init; }

    public required LanguageTag Language { get; init; }

    public string? Gender { get; init; }
}

public sealed record TtsRequest
{
    /// Sentezlenecek metin. Bu, ekranda görünen metin DEĞİL, okunacak metindir:
    /// "1453" burada "bin dört yüz elli üç" olarak gelir (§2.2/10, §20.3).
    public required string SpeechText { get; init; }

    public required string VoiceId { get; init; }

    public required LanguageTag Language { get; init; }

    /// 0.8 – 1.3 arası konuşma hızı.
    public double Speed { get; init; } = 1.0;

    /// Sağlayıcı destekliyorsa duygu/stil ipucu.
    public string? Style { get; init; }
}

public sealed record TtsResponse
{
    /// Üretilen ses. Depoya yazma çağıranın işi — sağlayıcı dosya sistemine dokunmaz.
    public required ReadOnlyMemory<byte> Audio { get; init; }

    public required string MimeType { get; init; }

    /// Sağlayıcının bildirdiği süre. Otorite DEĞİL: timeline'a giren süre
    /// her zaman ffprobe ile ölçülenidir (ADR-006).
    public required Ms ReportedDuration { get; init; }

    /// Sağlayıcı kelime zamanlaması veriyorsa dolu gelir; bu durumda ASR
    /// yan servisi hiç çalıştırılmaz.
    public IReadOnlyList<WordTiming> WordTimings { get; init; } = [];

    /// GERÇEKTEN kullanılan ses.
    ///
    /// İstenen ses ile kullanılan ses aynı olmayabiliyor: istenen ses
    /// yoksa sağlayıcı dile göre bir tane seçiyor. Bu alan olmasaydı
    /// fark hiçbir yerde görünmezdi — ve yanlış seslendirilmiş bir
    /// videoda hiçbir şey "kırılmadığı" için kimse fark etmezdi.
    public string? VoiceUsed { get; init; }
}

public interface ITtsProvider : IProvider
{
    /// ADR-002r: bu bayrak bir SEÇİM KRİTERİDİR. Kelime zamanlaması veren
    /// sağlayıcı tercih edilir; vermeyende WhisperX yan servisine düşülür,
    /// bu da ek gecikme ve ek karmaşıklık demektir.
    bool SupportsWordTimings { get; }

    Task<Result<ProviderResponse<TtsResponse>>> SynthesizeAsync(
        TtsRequest request,
        ProviderContext context,
        CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<VoiceInfo>>> ListVoicesAsync(
        LanguageTag language,
        CancellationToken cancellationToken);
}

public sealed record AlignRequest
{
    /// Hizalanacak sesin yerel yolu. ASR yan servisi dosyayı okur.
    public required string AudioPath { get; init; }

    /// Sesin metni. Serbest tanıma değil, ZORLANMIŞ hizalama yapılır:
    /// metin zaten bilindiği için doğruluk çok daha yüksek.
    public required string Transcript { get; init; }

    public required LanguageTag Language { get; init; }
}

public sealed record AlignmentResult(IReadOnlyList<WordTiming> Words, Ms Duration);

/// Kelime seviyesi zorlanmış hizalama.
///
/// ADR-002r'nin tek istisnası burada: bu arayüzün gerçek uygulaması küçük bir
/// Python yan servisidir (WhisperX). Sebep teknik — whisper.cpp'nin dikkat
/// tabanlı kelime zamanlaması kırılgan, doğru sonuç wav2vec2 hizalaması istiyor.
public interface IAsrProvider : IProvider
{
    Task<Result<ProviderResponse<AlignmentResult>>> AlignAsync(
        AlignRequest request,
        ProviderContext context,
        CancellationToken cancellationToken);
}
