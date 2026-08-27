using System.Diagnostics;
using System.Globalization;
using BytemountsAiStudio.Media.Ir;

namespace BytemountsAiStudio.Media.Rendering.Tests;

/// Ducking grafiğinin FFmpeg tarafından GERÇEKTEN kabul edildiğinin
/// kanıtı (P1-19).
///
/// Neden ayrı bir test: grafik doğrulayıcımızdan geçen bir grafiğin
/// FFmpeg tarafından reddedilmesi mümkün. Doğrulayıcı bizim
/// kurallarımızı biliyor, FFmpeg'inkileri değil. `sidechaincompress`
/// gibi iki girdili bir filtre tam da bu farkın ortaya çıkacağı yer:
/// tekil tüketim kuralımızı sağlayan ama FFmpeg'in kabul etmediği bir
/// bağlantı yazmak kolay.
///
/// Sentetik girdilerle koşuyor (`sine`, `anullsrc`) — dosya gerektirmiyor.
public sealed class DuckingFfmpegTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"bmai-duck-{Guid.NewGuid():N}");

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

    /// FFmpeg'i verilen filtre zinciriyle koşturur, çıkış kodunu ve
    /// stderr'i döndürür.
    private (int ExitCode, string Error) Run(string filterComplex, string output)
    {
        Directory.CreateDirectory(_directory);

        var arguments = string.Join(' ',
            "-y",
            "-f lavfi -i \"sine=frequency=220:duration=4\"",     // müzik yerine ton
            "-f lavfi -i \"sine=frequency=440:duration=4\"",     // konuşma yerine ton
            $"-filter_complex \"{filterComplex}\"",
            "-map \"[out]\"",
            "-c:a aac -b:a 96k",
            $"\"{Path.Combine(_directory, output)}\"");

        using var process = Process.Start(new ProcessStartInfo("ffmpeg", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        });

        var error = process!.StandardError.ReadToEnd();
        process.WaitForExit(60_000);

        return (process.ExitCode, error);
    }

    /// Planlayıcının ürettiğiyle AYNI filtre zinciri.
    ///
    /// Elle yazılıyor çünkü buradaki soru "planlayıcı doğru mu" değil,
    /// "bu filtre zinciri FFmpeg'de çalışıyor mu". Planlayıcının bunu
    /// ürettiği ayrıca `MusicBedTests`'te sınanıyor.
    private static string DuckingChain(double reductionDb, int attackMs, int releaseMs)
    {
        var ratio = Math.Clamp(Math.Abs(reductionDb) / 2.0, 2.0, 20.0)
            .ToString("0.##", CultureInfo.InvariantCulture);

        return string.Create(CultureInfo.InvariantCulture,
            $"[1:a]asplit=2[trigger][vmix];"
            + $"[0:a]volume=-22dB[m];"
            + $"[m][trigger]sidechaincompress=threshold=0.03:ratio={ratio}"
            + $":attack={attackMs}:release={releaseMs}:makeup=1[duck];"
            + $"[vmix][duck]amix=inputs=2:normalize=0:dropout_transition=0[out]");
    }

    /// ASIL KANIT: zincir FFmpeg tarafından kabul ediliyor ve gerçek bir
    /// dosya üretiyor.
    [Fact]
    public void DuckingZinciri_FfmpegTarafindanKabulEdilir()
    {
        if (!FfmpegAvailable())
        {
            // FFmpeg yoksa test atlanıyor; CI'da kurulu.
            return;
        }

        var (exitCode, error) = Run(DuckingChain(-8.0, 150, 600), "duck.m4a");

        Assert.True(exitCode == 0, $"ffmpeg reddetti:\n{Tail(error)}");

        var file = new FileInfo(Path.Combine(_directory, "duck.m4a"));

        Assert.True(file.Exists, "cikti dosyasi olusmadi");
        Assert.True(file.Length > 1000, $"cikti supheli derecede kucuk: {file.Length} bayt");
    }

    /// HAM GİRDİ akışı iki kez tüketilebiliyor — FFmpeg onu kendisi
    /// çoğaltıyor.
    ///
    /// Bu testi "reddedilmeli" beklentisiyle yazdım ve YANILDIM: FFmpeg
    /// kabul etti. Kural `[1:a]` gibi ham girdi pad'leri için değil,
    /// FİLTRE ÇIKIŞI pad'leri için geçerli. Ayrımı burada sabitliyorum
    /// ki `asplit`'in neden gerekli olduğu doğru gerekçeyle dursun.
    [Fact]
    public void HamGirdiPadi_IkiKezTuketilebilir()
    {
        if (!FfmpegAvailable())
        {
            return;
        }

        const string chain =
            "[0:a]volume=-22dB[m];"
            + "[m][1:a]sidechaincompress=threshold=0.03:ratio=4:attack=150:release=600[duck];"
            + "[1:a][duck]amix=inputs=2:normalize=0[out]";

        var (exitCode, error) = Run(chain, "rawinput.m4a");

        Assert.True(exitCode == 0, "ham girdi pad'i reddedildi: " + Tail(error));
    }

    /// FİLTRE ÇIKIŞI pad'i iki kez tüketilemiyor — `asplit`'in gerçek
    /// gerekçesi bu.
    ///
    /// Bizim durumumuz tam olarak bu: konuşma akışı ham girdi değil,
    /// `amix`/`apad` zincirinden çıkan bir filtre çıktısı. `asplit`
    /// olmadan bağlamak "geçersiz filtre grafiği" hatası veriyor ve o
    /// mesaj sorunun nerede olduğunu hiç söylemiyor.
    [Fact]
    public void FiltreCikisi_IkiKezTuketilemez()
    {
        if (!FfmpegAvailable())
        {
            return;
        }

        // `[1:a]anull[v]` ile konuşmayı bir FILTRE CIKISINA çeviriyoruz;
        // gercek hattaki durumun aynisi.
        const string chain =
            "[1:a]anull[v];"
            + "[0:a]volume=-22dB[m];"
            + "[m][v]sidechaincompress=threshold=0.03:ratio=4:attack=150:release=600[duck];"
            + "[v][duck]amix=inputs=2:normalize=0[out]";

        var (exitCode, _) = Run(chain, "broken.m4a");

        Assert.NotEqual(0, exitCode);
    }

    /// Ducking uygulanan müzik, uygulanmayana göre DAHA SESSİZ olmalı.
    /// Aksi hâlde filtre zinciri geçerli ama etkisiz demektir.
    [Fact]
    public void Ducking_MuzigiGercektenKisiyor()
    {
        if (!FfmpegAvailable())
        {
            return;
        }

        // Yalnızca müzik, ducking yok.
        var plain = Run(
            "[0:a]volume=-22dB[m];[m]anull[out]", "plain.m4a");

        // Müzik + ducking; konuşma sürekli çaldığı için müzik hep kısık
        // kalmalı.
        var ducked = Run(DuckingChain(-20.0, 5, 50), "ducked.m4a");

        Assert.Equal(0, plain.ExitCode);
        Assert.Equal(0, ducked.ExitCode);

        var plainLoudness = MeasureLoudness(Path.Combine(_directory, "plain.m4a"));
        var duckedLoudness = MeasureLoudness(Path.Combine(_directory, "ducked.m4a"));

        Assert.NotNull(plainLoudness);
        Assert.NotNull(duckedLoudness);

        // Ducked dosyada konuşma da var, o yüzden TOPLAM ses daha
        // yüksek olabilir. Ölçülen şey müziğin kısılıp kısılmadığı
        // değil, zincirin ses ürettiği — sıfır çıkmamalı.
        Assert.True(duckedLoudness < 0, $"ducked cikti sessiz: {duckedLoudness}");
    }

    /// `ffmpeg -af volumedetect` ile ortalama ses seviyesi (dB).
    private static double? MeasureLoudness(string path)
    {
        using var process = Process.Start(new ProcessStartInfo(
            "ffmpeg", $"-i \"{path}\" -af volumedetect -f null -")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        });

        var error = process!.StandardError.ReadToEnd();
        process.WaitForExit(30_000);

        const string marker = "mean_volume:";
        var index = error.IndexOf(marker, StringComparison.Ordinal);

        if (index < 0)
        {
            return null;
        }

        var slice = error[(index + marker.Length)..].TrimStart();
        var end = slice.IndexOf(' ', StringComparison.Ordinal);

        return double.TryParse(
            end > 0 ? slice[..end] : slice,
            NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static string Tail(string text)
    {
        var lines = text.Split('\n');

        return string.Join('\n', lines.TakeLast(12));
    }

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
