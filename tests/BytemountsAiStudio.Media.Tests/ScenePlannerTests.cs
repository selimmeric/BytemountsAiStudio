using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Time;
using BytemountsAiStudio.Media.Planning;

namespace BytemountsAiStudio.Media.Tests;

/// Sahne planlayıcı testleri (P1-16, ADR-006).
///
/// Buradaki regresyon testi mimarinin tek kuralını koruyor: sahne
/// SINIRLARI senaryodan, SÜRELER ölçülen sesten. Ters kurulursa
/// ses–görsel kayması sahte veriyle görünmez, gerçek seslendirmede
/// ortaya çıkar ve teşhisi zor olur.
public sealed class ScenePlannerTests
{
    private static readonly LanguageTag Turkish = LanguageTag.Create("tr-TR");

    private static ScenePlan Plan(string[] sentences, int[] milliseconds)
    {
        var result = ScenePlanner.Plan(
            sentences,
            [.. milliseconds.Select(m => new Ms(m))],
            "Göbeklitepe",
            Turkish,
            VisualStyle.Documentary);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);

        return result.Value;
    }

    /// ADR-006'nın regresyon testi: süreler ÖLÇÜLEN değerlerin ta
    /// kendisi, metinden türetilmiş bir tahmin değil.
    [Fact]
    public void Sureler_OlculenSestenGelir()
    {
        var plan = Plan(
            ["Kısa cümle.", "Bu cümle çok daha uzun olduğu için daha uzun sürüyor."],
            [3000, 7000]);

        Assert.Equal(new Ms(3000), plan.Scenes[0].Duration);
        Assert.Equal(new Ms(7000), plan.Scenes[1].Duration);

        // Uzunluk sırası metin uzunluğuyla aynı yönde diye tesadüfen
        // geçmesin: ters ölçümle de aynı sonuç gelmeli.
        var reversed = Plan(
            ["Kısa cümle.", "Bu cümle çok daha uzun olduğu için daha uzun sürüyor."],
            [7000, 3000]);

        Assert.Equal(new Ms(7000), reversed.Scenes[0].Duration);
        Assert.Equal(new Ms(3000), reversed.Scenes[1].Duration);
    }

    [Fact]
    public void SahneSinirlari_SenaryodanGelir()
    {
        var plan = Plan(["Birinci.", "İkinci.", "Üçüncü."], [2000, 2000, 2000]);

        Assert.Equal(3, plan.Scenes.Count);
        Assert.Equal("Birinci.", plan.Scenes[0].Text);
        Assert.Equal("Üçüncü.", plan.Scenes[2].Text);
    }

    [Fact]
    public void Sahneler_BosluksuzArdArda()
    {
        var plan = Plan(["Bir.", "İki.", "Üç."], [2000, 3000, 1500]);

        Assert.Equal(Ms.Zero, plan.Scenes[0].Start);
        Assert.Equal(plan.Scenes[0].End, plan.Scenes[1].Start);
        Assert.Equal(plan.Scenes[1].End, plan.Scenes[2].Start);
        Assert.Equal(new Ms(6500), plan.Total);
    }

    /// Toplam süre, ölçülen sürelerin toplamına EŞİT olmak zorunda.
    /// Eşit değilse bir yerde süre uydurulmuş demektir.
    [Theory]
    [InlineData(new[] { 2000, 3000, 1500 })]
    [InlineData(new[] { 500, 400, 300, 8000 })]
    [InlineData(new[] { 9000 })]
    public void ToplamSure_OlculenlerinToplamiylaAyni(int[] milliseconds)
    {
        var sentences = milliseconds.Select((_, i) => $"Cümle {i}.").ToArray();
        var plan = Plan(sentences, milliseconds);

        Assert.Equal(new Ms(milliseconds.Sum()), plan.Total);
    }

    /// Kısa sahneler İLERİ yönde birleşiyor.
    [Fact]
    public void KisaSahneler_SonrakiyleBirlesir()
    {
        var plan = Plan(["Çok kısa.", "Bu da kısa.", "Bu uzun bir cümle."], [400, 500, 5000]);

        // Üçü tek sahnede toplanmadı; ilk ikisi 900 ms ile eşiğin
        // altında kaldığı için üçüncüyle birleşti.
        Assert.Single(plan.Scenes);
        Assert.Equal(new Ms(5900), plan.Scenes[0].Duration);
        Assert.Contains("Çok kısa.", plan.Scenes[0].Text, StringComparison.Ordinal);
        Assert.Contains("Bu uzun bir cümle.", plan.Scenes[0].Text, StringComparison.Ordinal);
    }

    /// İLK cümle kısa olduğunda geriye birleştirecek bir şey yok.
    /// İleri birleştirme tam da bunun için.
    [Fact]
    public void IlkCumleKisaysa_SonrakiyleBirlesir()
    {
        var plan = Plan(["Kısa.", "Bu yeterince uzun bir cümle.", "Bu da uzun bir cümle."],
            [400, 5000, 5000]);

        Assert.Equal(2, plan.Scenes.Count);
        Assert.StartsWith("Kısa.", plan.Scenes[0].Text, StringComparison.Ordinal);
        Assert.Equal(new Ms(5400), plan.Scenes[0].Duration);
    }

    /// SON cümle kısaysa istisna: sonrası yok, öncekine ekleniyor.
    [Fact]
    public void SonCumleKisaysa_OncekiyleBirlesir()
    {
        var plan = Plan(["Uzun bir cümle.", "Kısa."], [5000, 400]);

        Assert.Single(plan.Scenes);
        Assert.Equal(new Ms(5400), plan.Scenes[0].Duration);
    }

    /// Senaryonun tamamı eşiğin altındaysa eşiği dayatmak videoyu
    /// tamamen görselsiz bırakırdı.
    [Fact]
    public void TumSenaryoKisaysa_TekSahneKalir()
    {
        var plan = Plan(["Kısa.", "Bu da."], [300, 400]);

        Assert.Single(plan.Scenes);
        Assert.Equal(new Ms(700), plan.Scenes[0].Duration);
    }

    /// Sessizce kırpmak, sondaki cümlelerin sessizce düşmesi demek
    /// olurdu — video kısalır ve kimse sebebini bilmez.
    [Fact]
    public void CumleVeOlcumSayisiTutmazsa_Reddedilir()
    {
        var result = ScenePlanner.Plan(
            ["Bir.", "İki.", "Üç."],
            [new Ms(1000), new Ms(2000)],
            "konu", Turkish, VisualStyle.Documentary);

        Assert.True(result.IsFailure);
        Assert.Equal("scene.count_mismatch", result.Error.Code);
    }

    [Fact]
    public void BosSenaryo_Reddedilir()
    {
        var result = ScenePlanner.Plan([], [], "konu", Turkish, VisualStyle.Documentary);

        Assert.True(result.IsFailure);
        Assert.Equal("scene.no_sentences", result.Error.Code);
    }

    [Fact]
    public void HerSahnenin_GorselYonergesiVar()
    {
        var plan = Plan(["Göbeklitepe dünyanın en eski tapınağıdır."], [5000]);

        var direction = plan.Scenes[0].Direction;

        Assert.Equal(0, direction.SceneIndex);
        Assert.Contains("göbeklitepe", direction.SearchQuery, StringComparison.Ordinal);
        Assert.Equal("documentary", direction.StyleHint);
    }

    /// Aynı senaryo her koşuda aynı planı vermeli — render önbelleğini
    /// ve determinizmi anlamlı kılan şey bu.
    [Fact]
    public void AyniGirdi_AyniPlan()
    {
        string[] sentences = ["Birinci cümle.", "İkinci cümle."];
        int[] durations = [3000, 4000];

        var first = Plan(sentences, durations);
        var second = Plan(sentences, durations);

        Assert.Equal(
            first.Scenes.Select(s => s.Direction.ImagePrompt),
            second.Scenes.Select(s => s.Direction.ImagePrompt));
        Assert.Equal(
            first.Scenes.Select(s => s.Direction.Seed),
            second.Scenes.Select(s => s.Direction.Seed));
    }
}
