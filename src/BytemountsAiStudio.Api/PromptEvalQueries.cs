using BytemountsAiStudio.Contracts.Prompts;

namespace BytemountsAiStudio.Api;

/// İstem değerlendirme sonuçları ekranı (P3-07).
///
/// NE GÖSTERİYOR: fixture'lar geçiyor mu. Fixture'lar DOLDURULMUŞ
/// İSTEMİ doğruluyor, model çıktısını değil — bir yer tutucu
/// düşürülmüş mü, bir kural silinmiş mi, metin bağlam sınırını
/// taşırıyor mu. Üçü de modelsiz yakalanıyor ve milisaniyeler
/// sürüyor, o yüzden ekran açılırken koşturmak makul.
///
/// NEDEN İSTEK ANINDA KOŞUYOR: sonuç, diskteki istem dosyalarının o
/// ANKİ hâline bağlı. Bir kez koşup saklamak, istem düzenlendikten
/// sonra ekranın eski sonucu göstermesi demekti — ve "geçiyor" yazan
/// eski bir sonuç, hiç sonuç olmamasından daha kötü.
public static class PromptEvalQueries
{
    /// Yukarı doğru kaç dizin aranıyor.
    ///
    /// Sınırsız yürümek, kök dizinde rastgele bir `prompts` klasörü
    /// bulup onu bizimmiş gibi koşturmak demekti.
    private const int SearchDepth = 6;

    /// Fixture'ların bulunduğu dizin.
    ///
    /// İSTEMLER DERLEMEYE GÖMÜLÜ AMA FIXTURE'LAR DEĞİL: gömülü
    /// çalışan bir API'nin fixture'ı yok. Bu bir eksiklik değil,
    /// dağıtımın doğal sonucu — ama EKRANIN BUNU SÖYLEMESİ gerekiyor.
    ///
    /// Söylemeseydi "0 fixture, hepsi geçti" gibi görünürdü: hiç
    /// sınanmamış bir istem seti, sınanıp geçmiş gibi okunurdu.
    ///
    /// YUKARI DOĞRU ARANIYOR, çünkü API'nin çalışma dizini kendi
    /// proje klasörü — depo kökü değil. İlk yazımda yalnızca
    /// `cwd/prompts` bakılıyordu ve ekran HER ZAMAN "koşmadı"
    /// diyordu: dürüst ama işe yaramaz bir ekran.
    public static string DirectoryPath
    {
        get
        {
            // Açık ayar her şeyi yener: dağıtımda dosyalar başka bir
            // yerde olabilir ve tahmin etmek yerine söylenmeli.
            var configured = Environment.GetEnvironmentVariable("BMAI_PROMPTS");

            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured;
            }

            var directory = new DirectoryInfo(Directory.GetCurrentDirectory());

            for (var depth = 0; depth < SearchDepth && directory is not null; depth++)
            {
                var candidate = Path.Combine(directory.FullName, "prompts");

                // YALNIZCA `evals` İÇEREN BİR `prompts` KABUL EDİLİYOR.
                //
                // Yalnız ada bakmak, başka bir projenin `prompts`
                // klasörünü bulup boş bir rapor üretmek olurdu — ve o
                // rapor "hepsi geçti" derdi.
                if (Directory.Exists(candidate)
                    && Directory.EnumerateDirectories(candidate, "evals", SearchOption.AllDirectories).Any())
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            // Bulunamadıysa en anlaşılır yolu bildiriyoruz: hata
            // mesajında "aradım ve yoktu" demek, var olmayan bir
            // yolu göstermekten iyi.
            return Path.Combine(Directory.GetCurrentDirectory(), "prompts");
        }
    }

    public static EvalScreen Build()
    {
        var directory = DirectoryPath;

        if (!Directory.Exists(directory))
        {
            return new EvalScreen(
                false,
                directory,
                0,
                0,
                [],
                $"Fixture dizini yok: {directory}. İstemler derlemeye gömülü çalışıyor, "
                + "değerlendirme için dosyalar gerekiyor.");
        }

        var registry = PromptRegistry.Load(directory);

        if (registry.IsFailure)
        {
            return new EvalScreen(false, directory, 0, 0, [], registry.Error.Message);
        }

        var report = PromptEvaluator.RunAll(registry.Value, directory);

        if (report.IsFailure)
        {
            return new EvalScreen(false, directory, 0, 0, [], report.Error.Message);
        }

        // DÜŞENLER ÖNCE. Ekran bir envanter değil, bir sorun listesi:
        // yirmi geçen fixture'ın arasına gömülmüş tek bir düşüş,
        // gömülü kaldığı sürece düzeltilmiyor.
        var rows = report.Value.Results
            .OrderBy(r => r.Passed)
            .ThenBy(r => r.PromptKey, StringComparer.Ordinal)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .Select(r => new EvalRow(
                r.Name,
                r.PromptKey,
                r.Stamp,
                r.Passed,
                r.RenderedChars,
                [.. r.Failures]))
            .ToList();

        return new EvalScreen(
            true,
            directory,
            report.Value.Passed,
            report.Value.Failed,
            rows,
            null);
    }
}
