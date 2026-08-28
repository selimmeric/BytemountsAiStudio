using System.Reflection;

namespace BytemountsAiStudio.Media.Rendering.Tests;

/// İptal edilen ffmpeg/ffprobe süreçleri arkada kalmamalı.
///
/// GERÇEK BİR SIZINTI: `WaitForExitAsync(cancellationToken)` iptal
/// olduğunda istisna atıyor ama SÜRECİ ÖLDÜRMÜYOR. `using var
/// process` yalnızca .NET tarafındaki tanıtıcıyı serbest bırakıyor;
/// işletim sistemindeki süreç koşmaya devam ediyor.
///
/// `FfmpegExecutor` bunu baştan doğru yapıyordu; `LoudnessMeter` ve
/// `MediaProbe` yapmıyordu. Render'ın yanında küçük görünüyorlar ama
/// on dakikalık bir videoda `ebur128` taraması bütün dosyayı okuyor —
/// iptal edilen her ölçüm arkada bir ffmpeg bırakıyordu.
///
/// NEDEN KAYNAK OKUYAN BİR TEST: gerçekten bir süreç başlatıp iptal
/// etmek, testin ffmpeg'in kurulu olmasına ve zamanlamaya bağlı
/// olması demekti. Sınanan şey bir davranış değil bir SÖZLEŞME: her
/// `WaitForExitAsync` çağrısının bir öldürme yolu olmalı.
public sealed class ProcessCleanupTests
{
    private static string SourceOf(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName, "src", "BytemountsAiStudio.Media.Rendering", fileName);

            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"{fileName} bulunamadı.");
    }

    /// SÜREÇ BEKLEYEN HER DOSYADA ÖLDÜRME YOLU VAR.
    ///
    /// Yeni bir yerde `WaitForExitAsync` yazan biri, iptal yolunu
    /// unutursa bu test düşüyor — ve unutmak sessiz bir sızıntı
    /// üretiyor, gürültülü bir hata değil.
    [Theory]
    [InlineData("FfmpegExecutor.cs")]
    [InlineData("LoudnessMeter.cs")]
    [InlineData("MediaProbe.cs")]
    public void SurecBekleyenDosya_OldurmeYoluTasiyor(string fileName)
    {
        var source = SourceOf(fileName);

        Assert.Contains("WaitForExitAsync", source, StringComparison.Ordinal);

        Assert.Contains("catch (OperationCanceledException)", source, StringComparison.Ordinal);
        Assert.Contains("TryKill(process)", source, StringComparison.Ordinal);
    }

    /// AĞAÇ BOYUNCA ÖLDÜRÜLÜYOR.
    ///
    /// `Kill()` tek başına yalnızca ffmpeg'i öldürür; ffmpeg alt
    /// süreç başlatırsa (bazı kodlayıcılar başlatıyor) onlar kalırdı.
    [Theory]
    [InlineData("FfmpegExecutor.cs")]
    [InlineData("LoudnessMeter.cs")]
    [InlineData("MediaProbe.cs")]
    public void Oldurme_AgacBoyunca(string fileName)
        => Assert.Contains("entireProcessTree: true", SourceOf(fileName), StringComparison.Ordinal);

    /// ÖLDÜRME HATASI TESTİ/ÜRETİMİ DÜŞÜRMÜYOR.
    ///
    /// Süreç iptal ile öldürme arasında kendi kendine bitmiş olabilir
    /// ve o durumda `Kill` istisna atıyor. Temizlik sırasında atılan
    /// bir istisna, asıl iptal sebebini gizlerdi.
    [Theory]
    [InlineData("FfmpegExecutor.cs")]
    [InlineData("LoudnessMeter.cs")]
    [InlineData("MediaProbe.cs")]
    public void OldurmeHatasi_Yutuluyor(string fileName)
        => Assert.Contains("catch (InvalidOperationException)", SourceOf(fileName), StringComparison.Ordinal);
}
