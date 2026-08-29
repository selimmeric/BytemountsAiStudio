using System.Text.RegularExpressions;

namespace BytemountsAiStudio.Worker.Tests;

/// Worker'ın hangi arka plan servislerini KAYDETTİĞİNİN sınanması.
///
/// ***BU DOSYANIN VAR OLMA SEBEBİ:*** `MetricsCollector` yazılmış,
/// testlenmiş ve **yalnızca CLI'den** çağrılabiliyordu. Worker'ın altı
/// arka plan servisinin hiçbiri ölçüm toplamıyordu — oysa P5-01'in adı
/// "YouTube Analytics **günlük çekim**". Günlük çekimin tetiği hiç
/// yoktu: biri her gün elle komut çalıştırmadıkça deney sonuçları asla
/// gelmiyordu ve **öğrenme döngüsü kapanmıyordu**.
///
/// Sınıfın kendi testleri bunu yakalayamazdı: `MetricsCollector`
/// doğru çalışıyordu, onu çağıran yoktu. Bu dosya KAYDA bakıyor.
///
/// KAYNAK METNİ OKUNUYOR, DI GRAFI KURULMUYOR: `Program.cs` üst düzey
/// ifadelerle yazılmış ve host'u ayağa kaldırmak veritabanı bağlantısı
/// istiyor. Metin okumak kırılgan görünüyor ama sınadığı şey tam
/// olarak doğru: "bu satır kodda var mı".
public sealed class HostedServiceRegistrationTests
{
    private static string Program()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName, "src", "BytemountsAiStudio.Worker", "Program.cs");

            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Worker/Program.cs bulunamadı.");
    }

    /// ***ÖLÇÜM SERVİSİ KAYITLI.***
    ///
    /// BU DOSYANIN EN ÖNEMLİ TESTİ. Kayıtlı olmadığında öğrenme
    /// döngüsünün veri girişi elle çevrilen bir kol oluyor.
    [Fact]
    public void OlcumServisi_Kayitli()
        => Assert.Contains("AddHostedService<MetricsService>()", Program(), StringComparison.Ordinal);

    /// BÜTÜN ARKA PLAN SERVİSLERİ KAYITLI.
    ///
    /// Yeni bir servis yazılıp kaydedilmemesi bu depoda tekrar eden
    /// hata sınıfı; liste burada duruyor ki eklemeyi unutmak bir test
    /// düşürsün.
    [Theory]
    [InlineData("HeartbeatWriter")]
    [InlineData("SelfRestartService")]
    [InlineData("StorageReadyService")]
    [InlineData("PartitionService")]
    [InlineData("MetricsService")]
    [InlineData("QueueWorker")]
    [InlineData("OrchestratorService")]
    public void ArkaPlanServisleri_Kayitli(string service)
        => Assert.Contains(
            $"AddHostedService<{service}>()", Program(), StringComparison.Ordinal);

    /// ***KAYITLI SERVİS SAYISI İLE DOSYA SAYISI TUTUYOR.***
    ///
    /// Yukarıdaki liste elle yazılı ve elle yazılan listeler eskiyor.
    /// Bu test, `BackgroundService` türeten her sınıfın kayıtta
    /// olduğunu sayıyla doğruluyor — yeni bir servis eklenip
    /// kaydedilmediğinde düşüyor.
    [Fact]
    public void HerArkaPlanSinifi_Kayitta()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        DirectoryInfo? root = null;

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "BytemountsAiStudio.Worker")))
            {
                root = new DirectoryInfo(
                    Path.Combine(directory.FullName, "src", "BytemountsAiStudio.Worker"));

                break;
            }

            directory = directory.Parent;
        }

        Assert.NotNull(root);

        var program = Program();

        foreach (var file in root.GetFiles("*.cs", SearchOption.TopDirectoryOnly))
        {
            var text = File.ReadAllText(file.FullName);

            // `BackgroundService` türeten sınıfın adını yakala.
            var match = Regex.Match(
                text,
                @"class\s+(?<ad>\w+)\s*\([^)]*\)?\s*:\s*BackgroundService",
                RegexOptions.Singleline,
                TimeSpan.FromSeconds(5));

            if (!match.Success)
            {
                continue;
            }

            var name = match.Groups["ad"].Value;

            Assert.True(
                program.Contains($"AddHostedService<{name}>()", StringComparison.Ordinal),
                $"'{name}' bir arka plan servisi ama Program.cs'te kaydı yok.");
        }
    }
}
