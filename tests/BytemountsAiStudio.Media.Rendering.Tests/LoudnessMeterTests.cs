using BytemountsAiStudio.Media.Rendering;

namespace BytemountsAiStudio.Media.Rendering.Tests;

/// Ses ölçümü (ADR-006, P1-21).
///
/// Bu adım uzun süre eksikti ve bedeli somuttu: ölçüm olmadığı için
/// QC'nin ses kontrolleri "ölçülmedi" diye düşüyor, her video insana
/// gidiyordu — ve retry bunu kalite sorunu sanıp aynı videoyu üç kez
/// render ediyordu.
///
/// Aşağıdaki metinler UYDURMA DEĞİL: gerçek bir ffmpeg 8.0 çıktısından
/// alındı. Uydurulmuş bir biçimi ayrıştırmayı sınamak, ayrıştırıcının
/// gerçek çıktıyla çalıştığına dair hiçbir şey söylemezdi.
public sealed class LoudnessMeterTests
{
    private const string RealSilentOutput = """
        [Parsed_ebur128_0 @ 000001498ccff4c0] t: 4.19995     TARGET:-23 LUFS    M: -70.0 S: -70.0     I: -70.0 LUFS       LRA:   0.0 LU
        [Parsed_ebur128_0 @ 000001498ccff4c0] Summary:

          Integrated loudness:
            I:         -70.0 LUFS
            Threshold:   0.0 LUFS

          Loudness range:
            LRA:         0.0 LU
            Threshold:   0.0 LUFS
            LRA low:     0.0 LUFS
            LRA high:    0.0 LUFS

          True peak:
            Peak:      -inf dBFS
        """;

    private const string RealSpokenOutput = """
        [Parsed_ebur128_0 @ 0000020a] Summary:

          Integrated loudness:
            I:         -16.4 LUFS
            Threshold: -26.7 LUFS

          Loudness range:
            LRA:         6.2 LU
            Threshold: -36.6 LUFS
            LRA low:   -20.1 LUFS
            LRA high:  -13.9 LUFS

          True peak:
            Peak:       -1.3 dBFS
        [silencedetect @ 0000020b] silence_start: 2.5
        [silencedetect @ 0000020b] silence_end: 4.1 | silence_duration: 1.6
        """;

    [Fact]
    public void GercekCikti_SeviyeOkunuyor()
    {
        var result = LoudnessMeter.Parse(RealSpokenOutput, durationSeconds: 10);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);
        Assert.Equal(-16.4, result.Value.IntegratedLufs);
        Assert.Equal(-1.3, result.Value.TruePeakDb);
        Assert.Equal(6.2, result.Value.LoudnessRange);
    }

    /// SESSİZ VİDEO GERÇEKTEN SESSİZ ÇIKIYOR.
    ///
    /// Sahte hattın seslendirmesi sessiz WAV üretiyor ve render de
    /// doğal olarak sessiz. −70 LUFS bunu söylüyor; QC'nin
    /// "ses var ve sessiz değil" kontrolü artık ÖLÇÜLMÜŞ bir sayıyla
    /// düşüyor, "ölçülmedi" diye değil. Fark önemli: biri gerçek bir
    /// kusur, diğeri eksik bir adım.
    [Fact]
    public void SessizVideo_YetmisLufs()
    {
        var result = LoudnessMeter.Parse(RealSilentOutput, durationSeconds: 5);

        Assert.True(result.IsSuccess);
        Assert.Equal(-70.0, result.Value.IntegratedLufs);
        Assert.True(result.Value.IntegratedLufs < -60, "sessiz sayilmali");
    }

    /// TEPE OKUNAMAZSA 0 DEĞİL.
    ///
    /// Sıfır "tam kırpılmış" demek. Ölçülmemiş bir değeri en kötü
    /// değerle doldurmak, sağlam bir videoyu düşürürdü. `-inf dBFS`
    /// ayrıştırılamıyor ve varsayılan −99'a düşüyor.
    [Fact]
    public void OkunamayanTepe_EnKotuDegereDusmuyor()
    {
        var result = LoudnessMeter.Parse(RealSilentOutput, durationSeconds: 5);

        Assert.True(result.Value.TruePeakDb < -50, $"tepe {result.Value.TruePeakDb}");
    }

    /// ANLIK DEĞERLER DEĞİL, ÖZET OKUNUYOR.
    ///
    /// `ebur128` akış boyunca anlık `I:` satırları da yazıyor. Onları
    /// okumak, videonun rastgele bir anındaki seviyeyi bütünleşik
    /// seviye sanmak olurdu. İlk satırdaki −70 değil, özetteki −16.4
    /// okunmalı.
    [Fact]
    public void AnlikDegerler_OzetiEzmiyor()
    {
        var karisik = "[ebur128] t: 1.0  I: -70.0 LUFS  LRA: 0.0 LU\n" + RealSpokenOutput;

        var result = LoudnessMeter.Parse(karisik, durationSeconds: 10);

        Assert.Equal(-16.4, result.Value.IntegratedLufs);
    }

    [Fact]
    public void OzetYok_GeciciHata()
    {
        var result = LoudnessMeter.Parse("hicbir sey", durationSeconds: 5);

        Assert.True(result.IsFailure);
        Assert.Equal("loudness.no_summary", result.Error.Code);
    }

    /// KONUŞMA ORANI sessizlikten türüyor: 10 saniyenin 1,6'sı sessizse
    /// oran 0,84.
    [Fact]
    public void KonusmaOrani_SessizliktenTuruyor()
    {
        var ratio = LoudnessMeter.SpeechRatioOf(RealSpokenOutput, durationSeconds: 10);

        Assert.NotNull(ratio);
        Assert.Equal(0.84, ratio.Value, 2);
    }

    /// Süre bilinmiyorsa oran `null` — sıfır dönmek "hiç konuşma yok"
    /// demekti ve ölçülememiş bir değeri en kötü değerle doldurmak,
    /// sağlam bir videoyu düşürürdü.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void SureBilinmiyor_OranBos(double duration)
        => Assert.Null(LoudnessMeter.SpeechRatioOf(RealSpokenOutput, duration));

    /// Hiç sessizlik yoksa oran 1: baştan sona konuşma.
    [Fact]
    public void SessizlikYok_OranBir()
        => Assert.Equal(1.0, LoudnessMeter.SpeechRatioOf(RealSilentOutput, durationSeconds: 5));

    /// Sessizlik süreden uzunsa oran sıfıra kırpılıyor: negatif bir
    /// oran aşağıdaki hiçbir kontrolde anlamlı değil.
    [Fact]
    public void AsiriSessizlik_SifiraKirpiliyor()
    {
        var uzun = "silence_duration: 99.0";

        Assert.Equal(0.0, LoudnessMeter.SpeechRatioOf(uzun, durationSeconds: 10));
    }

    /// GERÇEK DOSYA ÜZERİNDE: ölçüm ffmpeg'i gerçekten çağırıyor ve
    /// ürettiği sayı beklenen aralıkta.
    [Fact]
    public async Task GercekDosya_Olculuyor()
    {
        Assert.True(FfmpegProbe.Available, "ffmpeg yok — bu test gerçek ölçüm yapıyor.");

        var path = Path.Combine(Path.GetTempPath(), $"bmai-lufs-{Guid.CreateVersion7():N}.mp4");

        try
        {
            // 3 saniyelik, 1 kHz sinüs: bilinen bir seviye üretiyor.
            Assert.True(FfmpegProbe.Run(
                [
                    "-y", "-v", "error",
                    "-f", "lavfi", "-i", "color=c=black:s=320x240:d=3",
                    "-f", "lavfi", "-i", "sine=frequency=1000:duration=3",
                    "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac",
                    "-shortest", path,
                ]),
                "test videosu uretilemedi");

            var result = await new LoudnessMeter().MeasureAsync(path, 3.0, CancellationToken.None);

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);

            // Sinüs sinyali sessiz DEĞİL ve kırpılmıyor.
            Assert.True(result.Value.IntegratedLufs > -60,
                $"seviye {result.Value.IntegratedLufs} — sessiz gorunuyor");
            Assert.True(result.Value.TruePeakDb < 1.0, $"tepe {result.Value.TruePeakDb}");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task OlmayanDosya_KaliciHata()
    {
        var result = await new LoudnessMeter().MeasureAsync(
            Path.Combine(Path.GetTempPath(), "yok-boyle-bir-dosya.mp4"), 1.0, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("loudness.missing_file", result.Error.Code);
    }
}
