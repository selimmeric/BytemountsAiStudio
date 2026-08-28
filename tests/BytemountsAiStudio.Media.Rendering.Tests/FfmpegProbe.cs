using System.Diagnostics;
using System.Globalization;

namespace BytemountsAiStudio.Media.Rendering.Tests;

/// Testlerin paylaştığı ffmpeg yardımcıları.
///
/// PAYLAŞILAN: her test dosyasında ayrı bir "ffmpeg var mı" kopyası,
/// birinde düzeltilen bir davranışın diğerinde eski kalması demekti.
///
/// ÇIKTI ÖLÇÜLÜYOR, VARSAYILMIYOR (ADR-006). "Render başarılı" demek
/// ile "video 6 saniye, sesli ve 540 piksel geniş" demek farklı
/// şeyler; ikincisi olmadan sessiz bir dosya da başarı sayılırdı.
internal static class FfmpegProbe
{
    public const string FfmpegPath = "ffmpeg";
    public const string FfprobePath = "ffprobe";

    public static bool Available { get; } = Detect();

    public sealed record ProbeResult(double Duration, int Width, int Height, bool HasAudio);

    private static bool Detect()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(FfmpegPath, "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return false;
            }

            process.WaitForExit(10_000);

            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public static bool Run(IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(FfmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info);

        if (process is null)
        {
            return false;
        }

        process.WaitForExit(120_000);

        return process.ExitCode == 0;
    }

    public static ProbeResult? Probe(string path)
    {
        var info = new ProcessStartInfo(FfprobePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in new[]
                 {
                     "-v", "error",
                     "-show_entries", "format=duration:stream=width,height,codec_type",
                     "-of", "default=nw=1",
                     path,
                 })
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info);

        if (process is null)
        {
            return null;
        }

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(60_000);

        if (process.ExitCode != 0)
        {
            return null;
        }

        double duration = 0;
        int width = 0, height = 0;
        var hasAudio = false;

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split('=', 2);

            if (parts.Length != 2)
            {
                continue;
            }

            switch (parts[0])
            {
                case "duration":
                    double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
                    break;
                case "width" when width == 0:
                    int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out width);
                    break;
                case "height" when height == 0:
                    int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out height);
                    break;
                case "codec_type" when parts[1].Trim() == "audio":
                    hasAudio = true;
                    break;
            }
        }

        return new ProbeResult(duration, width, height, hasAudio);
    }
}
