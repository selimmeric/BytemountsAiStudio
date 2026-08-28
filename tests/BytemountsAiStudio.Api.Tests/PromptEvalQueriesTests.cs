using BytemountsAiStudio.Api;

namespace BytemountsAiStudio.Api.Tests;

/// İstem değerlendirme ekranı (P3-07).
///
/// EKRANIN EN TEHLİKELİ HÂLİ YEŞİL AMA BOŞ OLANI: fixture'lar
/// bulunamadığında "0 düştü" göstermek, hiç sınanmamış bir istem
/// setini sınanıp geçmiş gibi okuturdu. `Ran` alanı tam olarak bu
/// ayrımı taşıyor.
public sealed class PromptEvalQueriesTests : IDisposable
{
    private readonly string _original = Environment.GetEnvironmentVariable("BMAI_PROMPTS") ?? string.Empty;

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(
            "BMAI_PROMPTS", _original.Length == 0 ? null : _original);
    }

    /// AÇIK AYAR HER ŞEYİ YENİYOR.
    ///
    /// Dağıtımda dosyalar başka bir yerde olabilir ve tahmin etmek
    /// yerine söylenmeli. Ayar okunmasaydı, doğru yolu yazan biri
    /// ekranın yine "koşmadı" demesini görürdü ve sebebini
    /// bulamazdı.
    [Fact]
    public void AcikAyar_Kullaniliyor()
    {
        var path = Path.Combine(Path.GetTempPath(), "bmai-istemler-testi");

        Environment.SetEnvironmentVariable("BMAI_PROMPTS", path);

        Assert.Equal(path, PromptEvalQueries.DirectoryPath);
    }

    /// DİZİN YOKSA "KOŞMADI" DİYOR, "HEPSİ GEÇTİ" DEMİYOR.
    ///
    /// İkisi de sıfır düşüş gösteriyor; farkı yalnızca `Ran`
    /// taşıyor. Taşımasaydı yeşil bir ekran, hiçbir şey
    /// sınanmamışken güven verirdi.
    [Fact]
    public void DizinYok_KosmadiDiyor()
    {
        Environment.SetEnvironmentVariable(
            "BMAI_PROMPTS", Path.Combine(Path.GetTempPath(), "bmai-olmayan-" + Guid.NewGuid().ToString("N")[..8]));

        var screen = PromptEvalQueries.Build();

        Assert.False(screen.Ran);
        Assert.Equal(0, screen.Passed);
        Assert.Equal(0, screen.Failed);
        Assert.Empty(screen.Rows);

        // Ve sebep yazılı: "koşmadı" tek başına ne yapılacağını
        // söylemiyor.
        Assert.NotNull(screen.Problem);
        Assert.Contains("Fixture dizini yok", screen.Problem, StringComparison.Ordinal);
    }

    /// GERÇEK FIXTURE'LAR BULUNUYOR VE KOŞUYOR.
    ///
    /// Bu test aynı zamanda dizin aramasını sınıyor: test çalışma
    /// dizini `bin/Debug/net10.0`, depo kökü değil. Yukarı doğru
    /// arama olmasaydı ekran üretimde de her zaman "koşmadı"
    /// derdi — dürüst ama işe yaramaz.
    [Fact]
    public void GercekFixturelar_Kosuyor()
    {
        Environment.SetEnvironmentVariable("BMAI_PROMPTS", null);

        var screen = PromptEvalQueries.Build();

        Assert.True(screen.Ran, screen.Problem ?? "sebep yok");
        Assert.True(screen.Rows.Count > 0, "Hiç fixture bulunamadı.");

        // Depodaki fixture'lar geçiyor: geçmeseydi CI zaten kırmızı
        // olurdu, yani bu satır ekranın DOĞRU okuduğunu söylüyor.
        Assert.Equal(0, screen.Failed);
        Assert.Equal(screen.Rows.Count, screen.Passed);
    }

    /// DÜŞENLER ÜSTTE.
    ///
    /// Ekran bir envanter değil, bir sorun listesi: yirmi geçen
    /// fixture'ın arasına gömülmüş tek bir düşüş, gömülü kaldığı
    /// sürece düzeltilmiyor.
    [Fact]
    public void Siralama_DusenleriOneAliyor()
    {
        Environment.SetEnvironmentVariable("BMAI_PROMPTS", null);

        var rows = PromptEvalQueries.Build().Rows;

        // Hepsi geçtiğinde de sıralama kuralı geçerli olmalı:
        // "geçti" satırları arasında bir "düştü" görünmemeli.
        var firstPassed = rows.ToList().FindIndex(r => r.Passed);
        var lastFailed = rows.ToList().FindLastIndex(r => !r.Passed);

        Assert.True(firstPassed < 0 || lastFailed < firstPassed,
            "Düşen bir fixture, geçenlerin arasına karışmış.");
    }

    /// HER SATIR HANGİ SÜRÜMÜ SINADIĞINI SÖYLÜYOR.
    ///
    /// Fixture sürüm sabitlemiyorsa EN YÜKSEK sürüm koşuyor, yani
    /// yeni bir sürüm eklendiğinde aynı fixture başka bir metni
    /// sınamaya başlıyor. Damga yazılmasaydı "geçti" hangi metin
    /// için geçti belirsiz kalırdı.
    [Fact]
    public void HerSatir_DamgaTasiyor()
    {
        Environment.SetEnvironmentVariable("BMAI_PROMPTS", null);

        var rows = PromptEvalQueries.Build().Rows;

        Assert.All(rows, r => Assert.False(string.IsNullOrWhiteSpace(r.Stamp)));
    }
}
