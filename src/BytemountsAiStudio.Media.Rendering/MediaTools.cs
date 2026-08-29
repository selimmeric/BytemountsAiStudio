namespace BytemountsAiStudio.Media.Rendering;

/// FFmpeg ikililerinin yeri (P0-11).
///
/// ***BU DOSYA UZUN SÜRE YOKTU VE YOKLUĞU YALNIZCA WINDOWS'TA
/// GÖRÜLÜYORDU.***
///
/// Yol her katmanda bir parametreydi — `FfmpegExecutor`, `SegmentRenderer`,
/// `MediaRenderHandler`, node kaydı — ve hiçbir host onu VERMİYORDU:
/// hepsi varsayılana, yani `PATH`'teki `"ffmpeg"`e düşüyordu. Sonucu:
///
///   - Windows'ta ffmpeg `PATH`'te değilse render node'u her koşuda
///     "FFmpeg çalıştırılamadı" veriyor ve tek çözüm MAKİNENİN
///     `PATH`'ini değiştirmek oluyordu.
///   - Aynı makinede iki farklı ffmpeg sürümü (örneğin NVENC destekli
///     bir yapı) kullanmak imkânsızdı.
///   - Docker'da `PATH`'te olduğu için sorun HİÇ GÖRÜNMÜYORDU; hata
///     yalnızca geliştirici makinesinde çıkıyor ve kod değişikliği
///     gerektiriyordu.
///
/// ÇAĞIRANIN AÇIK DEĞERİ ORTAM DEĞİŞKENİNİ EZİYOR. Sıra tersine
/// olsaydı, bir testin verdiği yol makinedeki ortam değişkeni yüzünden
/// sessizce yok sayılırdı.
public static class MediaTools
{
    public const string FfmpegVariable = "BMAI_FFMPEG";

    public const string FfprobeVariable = "BMAI_FFPROBE";

    /// `PATH`'e güvenen varsayılan. Konteynerde doğru olan bu.
    public const string DefaultFfmpeg = "ffmpeg";

    public const string DefaultFfprobe = "ffprobe";

    public static string Ffmpeg(string? explicitPath = null)
        => Resolve(explicitPath, FfmpegVariable, DefaultFfmpeg);

    public static string Ffprobe(string? explicitPath = null)
        => Resolve(explicitPath, FfprobeVariable, DefaultFfprobe);

    private static string Resolve(string? explicitPath, string variable, string fallback)
        => explicitPath is { Length: > 0 } given
            ? given
            : Environment.GetEnvironmentVariable(variable) is { Length: > 0 } fromEnvironment
                ? fromEnvironment
                : fallback;
}
