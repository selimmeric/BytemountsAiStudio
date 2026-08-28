using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;

namespace BytemountsAiStudio.Media.Rendering;

/// Bir videonun ÖLÇÜLEN ses değerleri (P1-21, ADR-006).
public sealed record LoudnessMeasurement
{
    /// Bütünleşik ses seviyesi (LUFS). Yayın standardı −16.
    public required double IntegratedLufs { get; init; }

    /// Gerçek tepe (dBTP). 0'a yaklaşmak kırpılma demek.
    public required double TruePeakDb { get; init; }

    /// Seviye aralığı (LU).
    public required double LoudnessRange { get; init; }

    /// Konuşmanın toplam süreye oranı.
    ///
    /// Sessizlik ölçülerek bulunuyor: tamamen sessiz olmayan aralıkların
    /// toplamı / süre. Kaba ama işe yarayan bir gösterge — sıfıra yakın
    /// bir oran, seslendirmenin videoya hiç girmediğini söylüyor.
    public double? SpeechRatio { get; init; }
}

/// ffmpeg ile ses ölçümü (ADR-006: süre ve seviye TAHMİN EDİLMEZ,
/// ÖLÇÜLÜR).
///
/// NEDEN AYRI BİR ADIM: `ffprobe` kodek ve süre veriyor ama ses
/// seviyesi vermiyor — onun için sesi baştan sona OKUMAK gerekiyor
/// (`ebur128`). Bu, probe'dan belirgin biçimde pahalı ve her yerde
/// istenmiyor.
///
/// EKSİKLİĞİ GERÇEK BİR KAYBA YOL AÇTI: ölçüm olmadığı için QC'nin ses
/// kontrolleri "ölçülmedi" diye düşüyor, video her seferinde insana
/// gidiyordu — ve daha kötüsü, retry bunu kalite sorunu sanıp aynı
/// videoyu üç kez render ediyordu.
public sealed class LoudnessMeter(string ffmpegPath = "ffmpeg")
{
    /// `ebur128` özet satırları: "    I:         -16.4 LUFS".
    private static readonly Regex SummaryPattern = new(
        @"^\s*(I|LRA|Peak):\s*(-?\d+(?:\.\d+)?)\s*(LUFS|LU|dBFS)?\s*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    /// `silencedetect` çıktısı: "silence_duration: 1.234".
    private static readonly Regex SilencePattern = new(
        @"silence_duration:\s*(\d+(?:\.\d+)?)",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    /// Sessizlik eşiği (dB).
    ///
    /// −50 dB: ortam gürültüsünü sessizlik saymayacak kadar düşük, ama
    /// gerçek duraklamaları yakalayacak kadar yüksek. Daha sıkı bir
    /// eşik (−70) kayıt gürültüsünü konuşma sayardı.
    private const string SilenceThreshold = "-50dB";

    /// En kısa sessizlik.
    ///
    /// Yarım saniye: cümle araları sessizlik sayılmalı ama hece araları
    /// sayılmamalı. Daha kısa bir eşik konuşmanın kendisini parçalardı.
    private const string MinimumSilence = "0.5";

    public async Task<Result<LoudnessMeasurement>> MeasureAsync(
        string path, double durationSeconds, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return Error.Permanent("loudness.missing_file", $"Dosya yok: {path}");
        }

        var info = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // TEK GEÇİŞTE İKİ ÖLÇÜM: `ebur128` seviye, `silencedetect`
        // konuşma oranı. İki ayrı çağrı, dosyayı iki kez okumak
        // olurdu ve render'dan sonra en yavaş adım zaten bu.
        //
        // `-f null -` : çıktı yazılmıyor, yalnızca ölçülüyor.
        foreach (var argument in new[]
                 {
                     "-nostdin", "-hide_banner", "-i", path,
                     "-filter_complex",
                     $"ebur128=peak=true,silencedetect=noise={SilenceThreshold}:d={MinimumSilence}",
                     "-f", "null", "-",
                 })
        {
            info.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = info };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return Error.Permanent("loudness.ffmpeg_missing",
                $"FFmpeg çalıştırılamadı ('{ffmpegPath}'): {ex.Message}");
        }

        // ÖLÇÜM STDERR'DEN GELİYOR: ffmpeg filtre özetlerini oraya
        // yazıyor. Stdout boş ve öyle olmalı — `-f null` çıktı
        // üretmiyor.
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            return Error.Transient("loudness.ffmpeg_failed",
                "Ses ölçümü başarısız: " + string.Join(
                    '\n', stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(5)));
        }

        return Parse(stderr, durationSeconds);
    }

    /// Ölçüm çıktısını okur. `internal`: ffmpeg olmadan sınanabilsin.
    ///
    /// ÖZET BLOĞU SONDA: `ebur128` akış boyunca anlık değerler de
    /// yazıyor ve onları okumak, videonun rastgele bir anındaki
    /// seviyeyi bütünleşik seviye sanmak olurdu. Bu yüzden ÖZET
    /// bölümünden sonrası ayrıştırılıyor.
    internal static Result<LoudnessMeasurement> Parse(string stderr, double durationSeconds)
    {
        var summaryIndex = stderr.LastIndexOf("Summary:", StringComparison.Ordinal);

        if (summaryIndex < 0)
        {
            return Error.Transient("loudness.no_summary",
                "ebur128 özeti bulunamadı; ses akışı olmayabilir.");
        }

        var summary = stderr[summaryIndex..];

        double? integrated = null, peak = null, range = null;

        foreach (Match match in SummaryPattern.Matches(summary))
        {
            if (!double.TryParse(match.Groups[2].Value, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var value))
            {
                continue;
            }

            switch (match.Groups[1].Value)
            {
                case "I":
                    integrated ??= value;
                    break;
                case "LRA":
                    range ??= value;
                    break;
                case "Peak":
                    peak ??= value;
                    break;
            }
        }

        if (integrated is null)
        {
            return Error.Transient("loudness.no_integrated",
                "Bütünleşik seviye okunamadı.");
        }

        return Result.Success(new LoudnessMeasurement
        {
            IntegratedLufs = integrated.Value,
            // TEPE OKUNAMAZSA 0 DEĞİL: sıfır "tam kırpılmış" demek ve
            // ölçülmemiş bir değeri en kötü değerle doldurmak, sağlam
            // bir videoyu düşürürdü. −99 "pratikte sessiz" tarafında
            // ve kırpılma kontrolünü tetiklemiyor.
            TruePeakDb = peak ?? -99.0,
            LoudnessRange = range ?? 0,
            SpeechRatio = SpeechRatioOf(stderr, durationSeconds),
        });
    }

    /// Konuşma oranı: sessiz OLMAYAN sürenin toplama oranı.
    ///
    /// Süre bilinmiyorsa `null` — sıfır dönmek "hiç konuşma yok"
    /// demekti ve ölçülememiş bir değeri en kötü değerle doldurmak,
    /// sağlam bir videoyu düşürürdü.
    internal static double? SpeechRatioOf(string stderr, double durationSeconds)
    {
        if (durationSeconds <= 0)
        {
            return null;
        }

        double silence = 0;

        foreach (Match match in SilencePattern.Matches(stderr))
        {
            if (double.TryParse(match.Groups[1].Value, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var value))
            {
                silence += value;
            }
        }

        return Math.Clamp((durationSeconds - silence) / durationSeconds, 0, 1);
    }

    /// İptal edildiğinde SÜRECİ ÖLDÜRÜR.
    ///
    /// GERÇEK BİR SIZINTI: `WaitForExitAsync(cancellationToken)` iptal
    /// olduğunda istisna atıyor ama SÜRECİ ÖLDÜRMÜYOR. `using var
    /// process` yalnızca .NET tarafındaki tanıtıcıyı serbest
    /// bırakıyor; işletim sistemindeki süreç koşmaya devam ediyor.
    ///
    /// `FfmpegExecutor` bunu doğru yapıyordu (`Kill(entireProcessTree:
    /// true)`); burası ve `MediaProbe` yapmıyordu. Render'ın yanında
    /// küçük görünüyorlar ama on dakikalık bir videoda `ebur128`
    /// taraması bütün dosyayı okuyor — yani iptal edilen her ölçüm
    /// arkada bir ffmpeg bırakıyordu.
    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Süreç zaten bitmişse sorun değil.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Öldürme yetkisi yoksa yapılacak bir şey yok; sessizce
            // geçmek, sızıntıyı büyütmekten iyi.
        }
    }
}
