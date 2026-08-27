using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;

namespace BytemountsAiStudio.Media.Rendering;

/// ffprobe ile ölçülen gerçek dosya özellikleri.
///
/// ADR-006: süre burada ÖLÇÜLÜR. Sağlayıcının ya da timeline'ın bildirdiği
/// süreye güvenmek her videoda birikimli kayma üretiyor.
public sealed record MediaProbe
{
    public required double DurationSeconds { get; init; }

    public required long SizeBytes { get; init; }

    public bool HasVideo { get; init; }

    public bool HasAudio { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public string? VideoCodec { get; init; }

    public string? AudioCodec { get; init; }

    public int? SampleRate { get; init; }

    public static async Task<Result<MediaProbe>> ProbeAsync(
        string ffprobePath, string filePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return Error.Permanent("probe.missing_file", $"Dosya yok: {filePath}");
        }

        var info = new ProcessStartInfo
        {
            FileName = ffprobePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in new[]
                 {
                     "-v", "error",
                     "-print_format", "json",
                     "-show_format",
                     "-show_streams",
                     filePath,
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
            return Error.Permanent("probe.ffprobe_missing",
                $"ffprobe çalıştırılamadı ('{ffprobePath}'). PATH üzerinde mi? {ex.Message}");
        }

        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            return Error.Permanent("probe.failed", $"ffprobe {process.ExitCode} kodu ile çıktı.", stderr);
        }

        try
        {
            return Parse(stdout, new FileInfo(filePath).Length);
        }
        catch (JsonException ex)
        {
            return Error.Permanent("probe.parse_failed", $"ffprobe çıktısı okunamadı: {ex.Message}");
        }
    }

    internal static MediaProbe Parse(string json, long sizeBytes)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var duration = 0.0;
        if (root.TryGetProperty("format", out var format)
            && format.TryGetProperty("duration", out var durationValue)
            && double.TryParse(durationValue.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var parsed))
        {
            duration = parsed;
        }

        var probe = new MediaProbe { DurationSeconds = duration, SizeBytes = sizeBytes };

        if (!root.TryGetProperty("streams", out var streams))
        {
            return probe;
        }

        foreach (var stream in streams.EnumerateArray())
        {
            var type = stream.TryGetProperty("codec_type", out var codecType)
                ? codecType.GetString()
                : null;

            switch (type)
            {
                case "video":
                    probe = probe with
                    {
                        HasVideo = true,
                        Width = stream.TryGetProperty("width", out var w) ? w.GetInt32() : 0,
                        Height = stream.TryGetProperty("height", out var h) ? h.GetInt32() : 0,
                        VideoCodec = stream.TryGetProperty("codec_name", out var vc) ? vc.GetString() : null,
                    };
                    break;

                case "audio":
                    probe = probe with
                    {
                        HasAudio = true,
                        AudioCodec = stream.TryGetProperty("codec_name", out var ac) ? ac.GetString() : null,
                        SampleRate = stream.TryGetProperty("sample_rate", out var sr)
                            && int.TryParse(sr.GetString(), CultureInfo.InvariantCulture, out var rate)
                                ? rate
                                : null,
                    };
                    break;

                default:
                    break;
            }
        }

        return probe;
    }
}
