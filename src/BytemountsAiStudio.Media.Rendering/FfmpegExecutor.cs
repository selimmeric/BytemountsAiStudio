using System.Diagnostics;
using System.Globalization;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Media.Ir;

namespace BytemountsAiStudio.Media.Rendering;

public sealed record RenderProgress(double Percent, TimeSpan Elapsed, string? Speed);

public sealed record RenderOutcome
{
    public required string OutputPath { get; init; }

    public required TimeSpan RenderDuration { get; init; }

    public required MediaProbe Probe { get; init; }

    public required string FilterComplex { get; init; }
}

/// FFmpeg'i çalıştıran YAN ETKİLİ katman.
///
/// Saf katmanla (Planner/IR/Emitter) arasındaki sınır kasıtlı: burada süreç,
/// dosya sistemi ve zaman var; orada hiçbiri yok. `MediaPurityTests` bu
/// ayrımı IL seviyesinde koruyor.
///
/// Studio'dan korunan iki desen burada:
///   1. Filtre grafiği dosyadan geçirilir (`-filter_complex_script`)
///   2. Çıktı önce `.partial` yazılır, ancak başarıdan sonra taşınır
public sealed class FfmpegExecutor(string ffmpegPath = "ffmpeg", string ffprobePath = "ffprobe")
{
    public async Task<Result<RenderOutcome>> RenderAsync(
        FilterGraph graph,
        OutputOptions options,
        string outputPath,
        IProgress<RenderProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(options);

        var issues = GraphValidator.Validate(graph);
        if (issues.Count > 0)
        {
            // FFmpeg'e hiç gitmiyoruz: hatayı burada söylemek, dakikalar sonra
            // "Invalid argument" görmekten çok daha faydalı.
            return Error.Permanent("render.invalid_graph",
                "Filtre grafiği geçersiz: " + string.Join(" | ", issues));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

        // Yarım dosya asla "başarılı çıktı" sanılmamalı (Studio dersi).
        //
        // Uzantı SONDA kalmalı — `.mp4.partial` yazarsak FFmpeg konteyner
        // biçimini dosya adından çıkaramaz ve "Unable to choose an output
        // format" der. Bu yüzden ek, uzantıdan ÖNCE geliyor.
        var partialPath = Path.ChangeExtension(outputPath, null)
            + ".partial"
            + Path.GetExtension(outputPath);
        var scriptPath = Path.Combine(
            Path.GetTempPath(),
            $"bmai-filter-{Guid.CreateVersion7():N}.txt");

        var command = FilterGraphEmitter.Emit(graph, scriptPath, partialPath, options);
        await File.WriteAllTextAsync(scriptPath, command.FilterComplex, cancellationToken).ConfigureAwait(false);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var run = await RunFfmpegAsync(command.Arguments, options, progress, cancellationToken)
                .ConfigureAwait(false);

            if (run.IsFailure)
            {
                SafeDelete(partialPath);
                return Result.Failure<RenderOutcome>(run.Error);
            }

            var probe = await MediaProbe.ProbeAsync(ffprobePath, partialPath, cancellationToken)
                .ConfigureAwait(false);

            if (probe.IsFailure)
            {
                SafeDelete(partialPath);
                return Result.Failure<RenderOutcome>(probe.Error);
            }

            var verification = Verify(probe.Value, options, expectAudio: graph.AudioOut is not null);
            if (verification is not null)
            {
                SafeDelete(partialPath);
                return Result.Failure<RenderOutcome>(verification);
            }

            File.Move(partialPath, outputPath, overwrite: true);

            return new RenderOutcome
            {
                OutputPath = outputPath,
                RenderDuration = stopwatch.Elapsed,
                Probe = probe.Value,
                FilterComplex = command.FilterComplex,
            };
        }
        catch (OperationCanceledException)
        {
            // İptal edilen render yarım dosya bırakmaz.
            SafeDelete(partialPath);
            return Error.Cancelled("Render iptal edildi.");
        }
        finally
        {
            SafeDelete(scriptPath);
        }
    }

    /// Ham FFmpeg çağrısı (P2-11 birleştirme).
    ///
    /// Filtre grafiği kurmayan işler için: `concat` demuxer'ı bir
    /// filtre değil, bir girdi biçimi ve grafiğin içinden ifade
    /// edilemiyor. Ayrı bir yol açmak yerine planlayıcıyı bunu
    /// destekleyecek şekilde bükmek, `concat`'in kopyalama davranışını
    /// (yeniden kodlamama) kaybetmek olurdu — ki bölüm bazlı render'ın
    /// bütün kazancı orada.
    ///
    /// `-y` ve `-nostdin` ÖNDE: üzerine yazma sorusu ya da stdin
    /// beklemesi, arka planda koşan bir worker'ı sonsuza kadar
    /// bekletirdi.
    public Task<Result> RunRawAsync(
        IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        return RunFfmpegAsync(
            ["-y", "-nostdin", "-hide_banner", .. arguments],
            new OutputOptions
            {
                VideoCodec = "copy",
                PixelFormat = "yuv420p",
                AudioCodec = "copy",
                AudioBitrate = "192k",
                FrameRate = 30,
                DurationSeconds = 0,
            },
            null,
            cancellationToken);
    }

    private async Task<Result> RunFfmpegAsync(
        IReadOnlyList<string> arguments,
        OutputOptions options,
        IProgress<RenderProgress>? progress,
        CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
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
            return Error.Permanent("render.ffmpeg_missing",
                $"FFmpeg çalıştırılamadı ('{ffmpegPath}'). PATH üzerinde mi? {ex.Message}");
        }

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var progressTask = ReadProgressAsync(process, options, progress, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        await progressTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            // FFmpeg'in stderr'i uzun olabiliyor; son satırlar hatayı taşır.
            var tail = string.Join('\n', stderr
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .TakeLast(8));

            return Error.Permanent("render.ffmpeg_failed",
                $"FFmpeg {process.ExitCode} kodu ile çıktı.", tail);
        }

        return Result.Success();
    }

    /// `-progress pipe:1` çıktısını okur.
    ///
    /// FFmpeg burada `anahtar=değer` satırları yazar ve her blok `progress=`
    /// ile biter. `out_time_ms` mikrosaniyedir — adı yanıltıcı, bu FFmpeg'in
    /// bilinen bir tuhaflığı.
    private static async Task ReadProgressAsync(
        Process process,
        OutputOptions options,
        IProgress<RenderProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (progress is null)
        {
            _ = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        string? speed = null;
        var totalMs = options.DurationSeconds * 1000.0;

        while (await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator];
            var value = line[(separator + 1)..];

            if (key == "speed")
            {
                speed = value;
            }
            else if (key == "out_time_ms"
                     && long.TryParse(value, CultureInfo.InvariantCulture, out var microseconds))
            {
                var elapsedMs = microseconds / 1000.0;
                var percent = totalMs > 0 ? Math.Clamp(elapsedMs / totalMs * 100.0, 0, 100) : 0;
                progress.Report(new RenderProgress(percent, TimeSpan.FromMilliseconds(elapsedMs), speed));
            }
        }
    }

    /// Çıktının gerçekten istenen şey olduğunu doğrular.
    ///
    /// FFmpeg sıfır kodla çıkıp yine de bozuk dosya üretebilir: ses akışı
    /// olmayan, süresi sapan ya da çözünürlüğü yanlış bir mp4 "başarılı"
    /// görünür. QC'nin mekanik kontrolleri (§14.1) buradan besleniyor.
    ///
    /// `expectAudio` GRAFİKTEN GELİYOR, bir bayrak olarak değil: sessiz
    /// bir grafiğin (P2-11 segmentleri) sessiz çıktı vermesi doğru,
    /// sesli bir grafiğin sessiz çıktı vermesi ise tam olarak yakalamak
    /// istediğimiz sessiz başarısızlık. İkisini ayıran şey çağıranın
    /// niyeti değil, grafiğin kendisi.
    private static Error? Verify(MediaProbe probe, OutputOptions options, bool expectAudio)
    {
        if (!probe.HasVideo)
        {
            return Error.Permanent("render.no_video", "Çıktıda video akışı yok.");
        }

        if (expectAudio && !probe.HasAudio)
        {
            return Error.Permanent("render.no_audio", "Çıktıda ses akışı yok.");
        }

        if (!expectAudio && probe.HasAudio)
        {
            // Sessiz olması gereken bir segmentte ses çıkması, ses
            // zincirinin sızdığını gösteriyor: birleştirmeden sonra
            // ses İKİ KEZ binerdi ve sonuç yankılı bir anlatım olurdu.
            return Error.Permanent("render.unexpected_audio",
                "Sessiz olması gereken çıktıda ses akışı var.");
        }

        var expected = options.DurationSeconds;
        var drift = Math.Abs(probe.DurationSeconds - expected);

        // %1 tolerans: kare sınırına yuvarlama ve konteyner başlığı küçük
        // sapmalar üretir; bunun ötesi gerçek bir hatadır.
        if (expected > 0 && drift / expected > 0.01)
        {
            return Error.Permanent("render.duration_drift",
                $"Süre sapması: beklenen {expected:0.###} sn, üretilen {probe.DurationSeconds:0.###} sn.");
        }

        return null;
    }

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
    }

    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Geçici dosya silinemezse render başarısız sayılmamalı.
        }
    }
}
