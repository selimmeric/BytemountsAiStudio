namespace BytemountsAiStudio.Worker.Tests;

/// Çözülemeyen iş akışı konu havuzunu boşaltmamalı (P3-10).
///
/// GERÇEK HATA: `StartRunAsync` önce konuyu alıyordu, sonra iş
/// akışını çözüyordu. `TopicPool.TakeNextAsync` konuyu `InProgress`
/// yapıp KAYDEDİYOR — yani okumuyor, ALIYOR. İş akışı sonra
/// çözülemezse metot geri dönüyor ve konu kimsenin üretmediği bir
/// durumda asılı kalıyordu.
///
/// Bedeli zamanlayıcının hızıyla çarpılıyordu: tur dakikada bir
/// dönüyor, yani yanlış yazılmış TEK bir `workflow_key` havuzu günde
/// bin dört yüz konu boşaltırdı — ve tek bir video üretilmezdi.
/// Panelde görünen tablo "konular üretimde" olurdu.
///
/// SIRA BİR DAVRANIŞ, kaynakta okunabilen tek yer de o sıra: iki
/// çağrının yeri değişirse hata sessizce geri gelir. Bu yüzden test
/// kaynağa bakıyor.
public sealed class TopicNotBurnedTests
{
    [Fact]
    public void IsAkisi_KonudanOnceCozuluyor()
    {
        var source = ReadOrchestratorSource();

        var resolve = source.IndexOf("ResolveWorkflowAsync(services, channel", StringComparison.Ordinal);
        var take = source.IndexOf("pool.TakeNextAsync(", StringComparison.Ordinal);

        Assert.True(resolve > 0, "İş akışı çözümü kaynakta bulunamadı.");
        Assert.True(take > 0, "Konu alma kaynakta bulunamadı.");

        Assert.True(
            resolve < take,
            "İş akışı konudan SONRA çözülüyor: çözülemezse alınmış konu "
            + "asılı kalır ve havuz her turda bir konu kaybeder.");
    }

    /// Ve çözülemediğinde SEBEP kayda geçiyor.
    ///
    /// "Run başlatılamadı" tek başına ne yapılacağını söylemiyor;
    /// yanlış yazılmış bir anahtarla hiç tanımlanmamış bir iş akışı
    /// aynı satırı üretirdi.
    [Fact]
    public void SecilemedigindeSebep_KaydaGeciyor()
    {
        var source = ReadOrchestratorSource();

        Assert.Contains("LogNoWorkflow(logger, channel.Name, choice.Problem", source, StringComparison.Ordinal);
        Assert.Contains("{Problem}", source, StringComparison.Ordinal);
    }

    private static string ReadOrchestratorSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName, "src", "BytemountsAiStudio.Worker", "OrchestratorService.cs");

            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("OrchestratorService.cs bulunamadı.");
    }
}
