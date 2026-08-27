using System.Diagnostics;
using System.Globalization;

namespace BytemountsAiStudio.Media.Rendering.Tests;

/// Filigran zincirinin FFmpeg tarafından kabul edildiğinin ve
/// saydamlığın GERÇEKTEN uygulandığının kanıtı (P1-20).
///
/// Ducking testleriyle aynı gerekçe: grafik doğrulayıcımız bizim
/// kurallarımızı biliyor, FFmpeg'inkileri değil. Ayrıca burada
/// doğrulanan ikinci bir şey var — "alfa kanalı olmayan görselde
/// `colorchannelmixer` saydamlık üretemiyor" iddiası. Bu iddia
/// `FormatRgba` adımının varlık sebebi; doğru olup olmadığını
/// ölçmeden bilemeyiz.
public sealed class WatermarkFfmpegTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"bmai-wm-{Guid.NewGuid():N}");

    private static bool FfmpegAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("ffmpeg", "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });

            process!.WaitForExit(10_000);

            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// Tek kare PNG üretir; alfa kanalı olan ya da olmayan.
    private (int ExitCode, string Path) MakeFrame(string name, string source)
    {
        Directory.CreateDirectory(_directory);

        var path = Path.Combine(_directory, name);

        var arguments = string.Join(' ',
            "-y",
            $"-f lavfi -i \"{source}\"",
            "-frames:v 1",
            $"\"{path}\"");

        using var process = Process.Start(new ProcessStartInfo("ffmpeg", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        });

        process!.StandardError.ReadToEnd();
        process.WaitForExit(30_000);

        return (process.ExitCode, path);
    }

    private (int ExitCode, string Error, string Output) Overlay(string filterComplex, string name)
    {
        var output = Path.Combine(_directory, name);

        var arguments = string.Join(' ',
            "-y",
            "-f lavfi -i \"color=c=black:s=320x240:d=1:r=10\"",
            $"-i \"{Path.Combine(_directory, "wm.png")}\"",
            $"-filter_complex \"{filterComplex}\"",
            "-map \"[v]\" -frames:v 1",
            $"\"{output}\"");

        using var process = Process.Start(new ProcessStartInfo("ffmpeg", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        });

        var error = process!.StandardError.ReadToEnd();
        process.WaitForExit(60_000);

        return (process.ExitCode, error, output);
    }

    /// Bir karenin belirli noktasındaki parlaklık (0–255).
    private static int? Brightness(string path, int x, int y)
    {
        using var bitmap = SkiaSharp.SKBitmap.Decode(path);

        if (bitmap is null || x >= bitmap.Width || y >= bitmap.Height)
        {
            return null;
        }

        var pixel = bitmap.GetPixel(x, y);

        return (pixel.Red + pixel.Green + pixel.Blue) / 3;
    }

    /// ASIL KANIT: planlayıcının ürettiği zincir FFmpeg'de çalışıyor.
    [Fact]
    public void FiligranZinciri_FfmpegTarafindanKabulEdilir()
    {
        if (!FfmpegAvailable())
        {
            return;
        }

        Assert.Equal(0, MakeFrame("wm.png", "color=c=white:s=64x64:d=1").ExitCode);

        // Planlayıcının kurduğu sıra: rgba → saydamlık → overlay
        var (exitCode, error, output) = Overlay(
            "[1:v]format=pix_fmts=rgba[wm];"
            + "[wm]colorchannelmixer=aa=0.5[wma];"
            + "[0:v][wma]overlay=x=W-w-10:y=10[v]",
            "with.png");

        Assert.True(exitCode == 0, "ffmpeg reddetti: " + Tail(error));
        Assert.True(new FileInfo(output).Exists);
    }

    /// İDDİA SINAMASI: alfa kanalı olmayan bir görselde
    /// `colorchannelmixer` saydamlık üretemiyor.
    ///
    /// `FormatRgba` adımının varlık sebebi bu. Doğru olup olmadığını
    /// ölçmeden bilemezdik — ducking'de bir varsayımım zaten yanlış
    /// çıkmıştı.
    [Fact]
    public void RgbaOlmadan_SaydamlikUygulanmaz()
    {
        if (!FfmpegAvailable())
        {
            return;
        }

        // Alfa kanalı OLMAYAN beyaz kare (rgb24 JPEG).
        Assert.Equal(0, MakeFrame("wm.png", "color=c=white:s=64x64:d=1").ExitCode);

        // rgba'ya çevirmeden saydamlık: iddiaya göre etkisiz kalmalı.
        var without = Overlay(
            "[1:v]colorchannelmixer=aa=0.2[wma];[0:v][wma]overlay=x=10:y=10[v]",
            "without.png");

        // rgba'ya çevirerek: saydamlık uygulanmalı.
        var with = Overlay(
            "[1:v]format=pix_fmts=rgba[wm];[wm]colorchannelmixer=aa=0.2[wma];"
            + "[0:v][wma]overlay=x=10:y=10[v]",
            "with.png");

        Assert.Equal(0, without.ExitCode);
        Assert.Equal(0, with.ExitCode);

        // Filigranın ortasına bakılıyor (10,10 + 32,32).
        var withoutBrightness = Brightness(without.Output, 42, 42);
        var withBrightness = Brightness(with.Output, 42, 42);

        Assert.NotNull(withoutBrightness);
        Assert.NotNull(withBrightness);

        // Zemin siyah; %20 saydam beyaz, opak beyazdan KOYU olmalı.
        Assert.True(withBrightness < withoutBrightness,
            $"rgba'li saydamlik etkisiz kaldi: rgba'siz={withoutBrightness}, rgba'li={withBrightness}");
    }

    /// Bağlantı noktası ifadeleri FFmpeg tarafından değerlendirilebilmeli.
    /// `W-w-40` gibi bir ifade yazım hatası içerse render aşamasında
    /// anlaşılmaz bir hataya dönüşürdü.
    [Theory]
    [InlineData("40", "40")]
    [InlineData("W-w-40", "40")]
    [InlineData("40", "H-h-40")]
    [InlineData("W-w-40", "H-h-40")]
    [InlineData("(W-w)/2", "H-h-40")]
    [InlineData("(W-w)/2", "(H-h)/2")]
    public void BaglantiIfadeleri_FfmpegTarafindanDegerlendirilir(string x, string y)
    {
        if (!FfmpegAvailable())
        {
            return;
        }

        Assert.Equal(0, MakeFrame("wm.png", "color=c=white:s=32x32:d=1").ExitCode);

        var (exitCode, error, _) = Overlay(
            string.Create(CultureInfo.InvariantCulture,
                $"[1:v]format=pix_fmts=rgba[wm];[0:v][wm]overlay=x={x}:y={y}[v]"),
            "anchor.png");

        Assert.True(exitCode == 0, $"'{x}' / '{y}' reddedildi: " + Tail(error));
    }

    private static string Tail(string text)
        => string.Join('\n', text.Split('\n').TakeLast(10));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Geçici dizin silinemezse test sonucunu etkilemez.
        }
    }
}
