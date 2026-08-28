using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Time;

namespace BytemountsAiStudio.Core.Tests;

/// Uzun video bölüm planı (P3-02).
///
/// SAF: model yok, veritabanı yok. "Kaç bölüm olmalı ve her biri ne
/// kadar sürmeli" kararı, on beş dakikalık bir video üretilerek
/// öğrenilecek bir şey olmamalı — o video kırk dakikalık render ve
/// gerçek para demek.
public sealed class ChapterPlannerTests
{
    private static IReadOnlyList<(string, string?)> Sections(int count)
        => [.. Enumerable.Range(1, count).Select(i => ($"Bolum {i}", (string?)$"Soru {i}"))];

    private static ChapterPlanResult Plan(int sections, int minutes)
    {
        var result = ChapterPlanner.Plan(Sections(sections), new Ms(minutes * 60 * 1000));

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);

        return result.Value;
    }

    /// BÖLÜMLER BOŞLUKSUZ VE ÜST ÜSTE BİNMİYOR.
    ///
    /// Chapter işaretleri (P3-04) bu sayılardan üretiliyor: bir boşluk
    /// izleyiciyi hiçbir bölüme ait olmayan bir ana atardı, örtüşme
    /// ise iki işareti aynı yere koyardı.
    [Theory]
    [InlineData(3, 8)]
    [InlineData(5, 10)]
    [InlineData(6, 15)]
    public void Bolumler_BosluksuzVeSirali(int sections, int minutes)
    {
        var plan = Plan(sections, minutes);

        for (var i = 1; i < plan.Count; i++)
        {
            var previous = plan.Chapters[i - 1];

            Assert.Equal(
                previous.Start.Value + previous.TargetDuration.Value,
                plan.Chapters[i].Start.Value);
        }

        Assert.Equal(0, plan.Chapters[0].Index);
        Assert.Equal(plan.Count - 1, plan.Chapters[^1].Index);
    }

    /// SÜRELERİN TOPLAMI GÖVDEYE TAM OTURUYOR.
    ///
    /// Artan milisaniyeler ilk bölümlere dağıtılıyor. Hiç dağıtmamak
    /// toplamın hedeften sapması demekti ve o sapma chapter
    /// işaretlerini videonun sonunda kaydırırdı.
    [Theory]
    [InlineData(3, 8)]
    [InlineData(7, 12)]
    [InlineData(6, 15)]
    public void SurelerToplami_GovdeyeTamOturuyor(int sections, int minutes)
    {
        var plan = Plan(sections, minutes);

        var intro = (int)(plan.TotalDuration.Value * ChapterPlanner.IntroShare);
        var outro = (int)(plan.TotalDuration.Value * ChapterPlanner.OutroShare);
        var body = plan.TotalDuration.Value - intro - outro;

        Assert.Equal(body, plan.Chapters.Sum(c => c.TargetDuration.Value));

        // Son bölüm kapanıştan önce bitiyor.
        var last = plan.Chapters[^1];

        Assert.Equal(plan.TotalDuration.Value - outro, last.Start.Value + last.TargetDuration.Value);
    }

    /// GİRİŞ İÇİN YER AYRILIYOR: ilk bölüm sıfırdan başlamıyor.
    [Fact]
    public void IlkBolum_GiristenSonraBasliyor()
    {
        var plan = Plan(4, 10);

        Assert.True(plan.Chapters[0].Start.Value > 0,
            $"ilk bolum {plan.Chapters[0].Start.Value} ms'de basliyor");
    }

    /// FAZLA BÖLÜM KIRPILIYOR.
    ///
    /// Model sekiz bölüm önerebiliyor ama sekiz dakikalık bir videoda
    /// gövde ~7,2 dakika: sekiz bölüm her birine 54 saniye düşürüyor
    /// ve bu bir bölüm değil, bir paragraf.
    [Fact]
    public void CokFazlaBolum_KirpiliyorVeSayiliyor()
    {
        var sections = Sections(12);
        var plan = ChapterPlanner.Plan(sections, new Ms(8 * 60 * 1000)).Value;

        Assert.True(plan.Count < 12, $"{plan.Count} bolum kaldi");
        Assert.True(ChapterPlanner.Dropped(sections, plan) > 0);

        // Kalan bölümlerin hiçbiri en kısa süreden kısa değil.
        Assert.All(plan.Chapters, c =>
            Assert.True(c.TargetDuration.Value >= ChapterPlanner.MinimumChapter.Value,
                $"{c.Title}: {c.TargetDuration.Value} ms"));
    }

    /// AZ BÖLÜM VİDEOYU KISALTIYOR, BÖLÜMÜ UZATMIYOR.
    ///
    /// Bu testi ilk yazdığımda "iki bölüm on beş dakikaya bölünsün"
    /// bekliyordum ve planlayıcı üç bölüm daha UYDURUYORDU.
    /// Planlayıcının işi zamanı paylaştırmak, içerik icat etmek değil.
    ///
    /// Doğru davranış: elimizdeki bölümlerin taşıyabileceği kadar uzun
    /// bir video. Altı bölüm en fazla 18 dk gövde taşıyor; on beş
    /// dakika sığıyor ve hiçbir bölüm üst sınırı aşmıyor.
    [Fact]
    public void AzBolum_UstSinirAsilmiyor()
    {
        var plan = Plan(6, 15);

        Assert.All(plan.Chapters, c =>
            Assert.True(c.TargetDuration.Value <= ChapterPlanner.MaximumChapter.Value,
                $"{c.Title}: {c.TargetDuration.Value} ms"));
    }

    /// ÇOK AZ BÖLÜM UZUN VİDEO YAPAMIYOR ve bunu SÖYLÜYOR.
    ///
    /// İki bölüm en fazla 6 dakika gövde taşıyor; giriş ve kapanışla
    /// birlikte 6,7 dakika ediyor ve sekiz dakikanın altı "uzun video"
    /// sayılmıyor. Sessizce yedi dakikalık bir video üretmek yerine
    /// modele "daha fazla bölüm gerekiyor" demek doğru.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void CokAzBolum_HataVeGerekce(int sections)
    {
        var result = ChapterPlanner.Plan(Sections(sections), new Ms(15 * 60 * 1000));

        Assert.True(result.IsFailure);
        Assert.Equal("chapter.too_few_sections", result.Error.Code);
        Assert.Contains("Daha fazla bölüm", result.Error.Message, StringComparison.Ordinal);
    }

    /// HEDEF SÜRE KIRPILIYOR, REDDEDİLMİYOR.
    ///
    /// Yapılandırmada 30 dakika yazan biri hiç video alamamak yerine
    /// 15 dakikalık bir video almalı. Sınırın dışına çıkmak bir hata
    /// değil, bir tercih hatası.
    [Theory]
    [InlineData(1, 8)]
    [InlineData(30, 15)]
    [InlineData(11, 11)]
    public void HedefSure_AraligaCekiliyor(int istenen, int beklenen)
        => Assert.Equal(
            beklenen * 60 * 1000,
            ChapterPlanner.Clamp(new Ms(istenen * 60 * 1000)).Value);

    /// Başlık yoksa plan üretilmiyor: boş bir bölüm listesiyle video
    /// yapmak, yapısı olmayan bir uzunluk üretmekti.
    [Fact]
    public void BolumYok_KaliciHata()
    {
        var result = ChapterPlanner.Plan([], new Ms(10 * 60 * 1000));

        Assert.True(result.IsFailure);
        Assert.Equal("chapter.no_sections", result.Error.Code);
    }

    /// Başlık ve soru KORUNUYOR: soru olmadan model başlığı tekrar
    /// eden bir paragraf yazıyor.
    [Fact]
    public void BaslikVeSoru_Korunuyor()
    {
        var plan = ChapterPlanner.Plan(
            [
                ("Kesif", "Kim buldu ve neden yillarca onemsenmedi"),
                ("Yapim", "Nasil insa edildi"),
                ("Miras", "Bugun neyi degistirdi"),
            ],
            new Ms(8 * 60 * 1000)).Value;

        Assert.Equal("Kesif", plan.Chapters[0].Title);
        Assert.Equal("Kim buldu ve neden yillarca onemsenmedi", plan.Chapters[0].Question);
        Assert.Equal("Miras", plan.Chapters[^1].Title);
    }

    /// Aynı girdi aynı planı üretiyor: kararlılık olmadan bir sorunun
    /// tekrarlanabilirliği bozulur.
    [Fact]
    public void AyniGirdi_AyniPlan()
    {
        var a = Plan(5, 12);
        var b = Plan(5, 12);

        Assert.Equal(a.Chapters, b.Chapters);
    }
}
