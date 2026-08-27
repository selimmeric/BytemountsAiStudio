using BytemountsAiStudio.Core.Content;

namespace BytemountsAiStudio.Core.Tests;

/// Platform sınırı testleri (P1-22).
///
/// Kabul kriteri: "100 karakteri aşan başlık kırpılıyor, upload reddi
/// olmuyor." Buradaki testler ikinci yarıyı da kovalıyor — kırpma
/// SONRASI hâlâ sınır dışında bir şey kalmadığını doğruluyor. Kırpmanın
/// kendisi hatalı olsaydı, hata yine upload sırasında görülürdü.
public sealed class PlatformLimitsTests
{
    // ---- Başlık ----

    /// Sığıyorsa DOKUNULMUYOR: gereksiz normalizasyon modelin kasıtlı
    /// noktalamasını bozardı.
    [Fact]
    public void SiganBaslik_Degismez()
    {
        const string title = "Göbeklitepe: Dünyanın En Eski Tapınağı";

        Assert.Equal(title, PlatformLimits.TrimTitle(title));
    }

    [Fact]
    public void UzunBaslik_SinirinAltinaIner()
    {
        var title = string.Join(' ', Enumerable.Repeat("kelime", 40));

        var trimmed = PlatformLimits.TrimTitle(title);

        Assert.True(trimmed.Length <= PlatformLimits.TitleMaxLength,
            $"kirpma sonrasi {trimmed.Length} karakter");
    }

    /// Kırpma KELİME sınırında: ortadan kesilmiş bir başlık
    /// ("Dünyanın En Tehli") hem okunmuyor hem tıklanmıyor.
    [Fact]
    public void Kirpma_KelimeSinirinda()
    {
        var title = string.Join(' ', Enumerable.Repeat("kelime", 40));

        var trimmed = PlatformLimits.TrimTitle(title).TrimEnd('…');

        Assert.EndsWith("kelime", trimmed, StringComparison.Ordinal);
    }

    [Fact]
    public void Kirpma_UcNoktaEkler()
    {
        var title = new string('a', 50) + " " + new string('b', 80);

        Assert.EndsWith("…", PlatformLimits.TrimTitle(title), StringComparison.Ordinal);
    }

    /// Üç nokta SINIRA DAHİL: eklendikten sonra taşan bir metin,
    /// kırpma yapmamışız gibi reddedilirdi.
    [Fact]
    public void UcNokta_SiniraDahil()
    {
        var title = string.Join(' ', Enumerable.Repeat("uzunkelime", 30));

        var trimmed = PlatformLimits.TrimTitle(title);

        Assert.True(trimmed.Length <= PlatformLimits.TitleMaxLength);
        Assert.Contains("…", trimmed, StringComparison.Ordinal);
    }

    /// Tek uzun kelime: kelime sınırı çok geride kalıyorsa sert
    /// kesiliyor. Yarım kelime, boş bir başlıktan iyidir.
    [Fact]
    public void TekUzunKelime_SertKesilir()
    {
        var title = new string('a', 300);

        var trimmed = PlatformLimits.TrimTitle(title);

        Assert.True(trimmed.Length <= PlatformLimits.TitleMaxLength);
        Assert.True(trimmed.Length > 50, "baslik neredeyse tamamen yok olmus");
    }

    /// Sondaki noktalama temizleniyor: "Dünyanın En, …" kötü görünür.
    [Fact]
    public void SondakiNoktalama_Temizlenir()
    {
        var title = string.Join(' ', Enumerable.Repeat("kelime,", 40));

        var trimmed = PlatformLimits.TrimTitle(title);

        Assert.DoesNotContain(",…", trimmed, StringComparison.Ordinal);
    }

    [Fact]
    public void FazlaBosluk_Toparlanir()
    {
        Assert.Equal("iki kelime", PlatformLimits.TrimTitle("  iki    kelime  "));
    }

    [Fact]
    public void TamSinirdakiBaslik_Degismez()
    {
        var title = new string('a', PlatformLimits.TitleMaxLength);

        Assert.Equal(title, PlatformLimits.TrimTitle(title));
    }

    // ---- Etiketler ----

    [Fact]
    public void SiganEtiketler_Degismez()
    {
        string[] tags = ["tarih", "arkeoloji", "göbeklitepe"];

        Assert.Equal(tags, PlatformLimits.TrimTags(tags));
    }

    /// SONDAN atılıyor, baştan değil: model en alakalı etiketi başa
    /// yazıyor ve baştan atmak en değerlisini atmak olurdu.
    [Fact]
    public void SigmayanEtiketler_SondanAtilir()
    {
        var tags = Enumerable.Range(0, 20).Select(i => $"etiket{i}-" + new string('x', 40)).ToList();

        var trimmed = PlatformLimits.TrimTags(tags);

        Assert.Equal(tags[0], trimmed[0]);
        Assert.True(trimmed.Count < tags.Count);
    }

    /// Ayraçları saymak şart: platform da öyle sayıyor ve unutmak,
    /// sınırın hemen altındaki bir kümeyi reddettirir.
    [Fact]
    public void ToplamUzunluk_AyraclarlaHesaplanir()
    {
        var tags = PlatformLimits.TrimTags(Enumerable.Repeat("etiket", 200));

        Assert.True(PlatformLimits.TagsLength(tags) <= PlatformLimits.TagsTotalMaxLength);
    }

    [Fact]
    public void TekrarlananEtiket_Elenir()
    {
        var trimmed = PlatformLimits.TrimTags(["tarih", "TARIH", "Tarih", "arkeoloji"]);

        Assert.Equal(2, trimmed.Count);
    }

    [Fact]
    public void CokUzunTekEtiket_Elenir()
    {
        var trimmed = PlatformLimits.TrimTags(["kısa", new string('x', 200)]);

        Assert.Single(trimmed);
    }

    [Fact]
    public void BosEtiket_Elenir()
    {
        Assert.Single(PlatformLimits.TrimTags(["tarih", "", "   "]));
    }

    /// Sığmayan bir etiketi atlayıp devam ediliyor: sonraki daha kısa
    /// bir etiket sığabilir ve sınırı boş bırakmanın anlamı yok.
    [Fact]
    public void SigmayanEtiketAtlanir_SonrakiDenenir()
    {
        var tags = new List<string> { new('a', 450), new('b', 90), "kısa" };

        var trimmed = PlatformLimits.TrimTags(tags);

        Assert.Contains("kısa", trimmed, StringComparer.Ordinal);
    }

    [Fact]
    public void BosListe_BosDoner()
    {
        Assert.Empty(PlatformLimits.TrimTags([]));
        Assert.Equal(0, PlatformLimits.TagsLength([]));
    }

    // ---- Açıklama ----

    [Fact]
    public void UzunAciklama_SinirinAltinaIner()
    {
        var description = string.Join(' ', Enumerable.Repeat("cümle", 3000));

        var trimmed = PlatformLimits.TrimDescription(description);

        Assert.True(trimmed.Length <= PlatformLimits.DescriptionMaxLength);
    }

    [Fact]
    public void SiganAciklama_Degismez()
    {
        const string description = "Kısa bir açıklama.\n\nİkinci paragraf.";

        Assert.Equal(description, PlatformLimits.TrimDescription(description));
    }

    // ---- Kırpma sonrası doğrulama ----

    /// ASIL KABUL KRİTERİ: kırpma sonrası HİÇBİR sınır ihlali kalmıyor.
    /// Kalırsa hata yine upload sırasında görülürdü ve o noktada
    /// videonun kalan her adımı zaten yapılmış oluyor.
    [Fact]
    public void KirpmaSonrasi_HicIhlalKalmaz()
    {
        var title = PlatformLimits.TrimTitle(string.Join(' ', Enumerable.Repeat("uzunbaslikkelimesi", 30)));
        var description = PlatformLimits.TrimDescription(new string('d', 20_000));
        var tags = PlatformLimits.TrimTags(Enumerable.Range(0, 100).Select(i => $"etiket{i}"));

        var violations = PlatformLimits.Violations(title, description, tags);

        Assert.Empty(violations);
    }

    [Fact]
    public void Dogrulama_BosBasligiYakalar()
    {
        var violations = PlatformLimits.Violations("   ", "açıklama", []);

        Assert.Contains(violations, v => v.Contains("bos", StringComparison.Ordinal));
    }

    [Fact]
    public void Dogrulama_UzunBasligiYakalar()
    {
        var violations = PlatformLimits.Violations(new string('a', 150), "x", []);

        Assert.Contains(violations, v => v.Contains("baslik", StringComparison.Ordinal));
    }

    [Fact]
    public void Dogrulama_UzunEtiketKumesiniYakalar()
    {
        var tags = Enumerable.Repeat(new string('e', 60), 20).ToList();

        Assert.Contains(
            PlatformLimits.Violations("başlık", "x", tags),
            v => v.Contains("etiketler", StringComparison.Ordinal));
    }

    [Fact]
    public void Dogrulama_TekUzunEtiketiYakalar()
    {
        var violations = PlatformLimits.Violations("başlık", "x", [new string('e', 150)]);

        Assert.NotEmpty(violations);
    }

    [Fact]
    public void SaglikliMetadata_IhlalYok()
    {
        var violations = PlatformLimits.Violations(
            "Göbeklitepe: Dünyanın En Eski Tapınağı",
            "Kısa bir anlatı.",
            ["tarih", "arkeoloji"]);

        Assert.Empty(violations);
    }
}
