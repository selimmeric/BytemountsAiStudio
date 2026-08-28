using System.Text.Json;
using BytemountsAiStudio.Nodes;

namespace BytemountsAiStudio.Nodes.Tests;

/// Uzun video node'larının ayrıştırma ve hesap tarafı (P3-02).
///
/// Model çağrısı yok: bir yerel modelin ne döndüreceği tahmin
/// edilemez, ama döndürdüğü şeyin nasıl okunacağı tahmin edilebilir
/// olmalı.
public sealed class LongVideoTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /* ---- Bölüm planı ayrıştırma ---- */

    [Fact]
    public void BolumPlani_BaslikVeSoruOkunuyor()
    {
        var sections = ChapterPlanHandler.ParseSections(
            """{"chapters":[{"title":"Kesif","question":"Kim buldu"},{"title":"Yapim"}]}""");

        Assert.Equal(2, sections.Count);
        Assert.Equal("Kesif", sections[0].Title);
        Assert.Equal("Kim buldu", sections[0].Question);
        Assert.Null(sections[1].Question);
    }

    /// AYNI BAŞLIK İKİ KEZ GELİRSE İKİNCİSİ DÜŞÜYOR.
    ///
    /// Model bunu yapıyor ve iki özdeş bölüm, chapter listesinde aynı
    /// adı iki kez göstermek olurdu.
    [Fact]
    public void TekrarEdenBaslik_BirKezAliniyor()
    {
        var sections = ChapterPlanHandler.ParseSections(
            """{"chapters":[{"title":"Kesif"},{"title":"KESIF"},{"title":"Yapim"}]}""");

        Assert.Equal(2, sections.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("json degil")]
    [InlineData("""{"baska":"alan"}""")]
    [InlineData("""{"chapters":[]}""")]
    [InlineData("""{"chapters":[{"question":"baslik yok"}]}""")]
    public void BozukPlan_BosDonuyor(string? json)
        => Assert.Empty(ChapterPlanHandler.ParseSections(json));

    /* ---- Kaynak özeti ---- */

    /// KAYNAK VERİLMEZSE MODEL UYDURUYOR ve uydurduğu bölüm iddia
    /// doğrulamada düşüyor. Kaynak yoksa bunu açıkça söylemek, boş
    /// bırakmaktan iyi.
    [Fact]
    public void KaynakYok_AcikcaSoyluyor()
    {
        var text = ChapterPlanHandler.SourcesOf(Json("{}"));

        Assert.Contains("kaynak bulunamadı", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Kaynaklar_BasliklaOzetleniyor()
    {
        var text = ChapterPlanHandler.SourcesOf(Json(
            """{"research":{"sources":[{"title":"Schmidt 1995","excerpt":"Kazilar basladi"}]}}"""));

        Assert.Contains("Schmidt 1995", text, StringComparison.Ordinal);
        Assert.Contains("Kazilar basladi", text, StringComparison.Ordinal);
    }

    /// ALINTILAR KISALTILIYOR: tam metinleri vermek istemi binlerce
    /// kelime büyütüyor ve küçük bir yerel model o uzunluktaki
    /// talimatları zaten dikkate almıyor.
    [Fact]
    public void UzunAlinti_Kisaltiliyor()
    {
        var uzun = new string('x', 500);

        var text = ChapterPlanHandler.SourcesOf(Json(
            "{\"research\":{\"sources\":[{\"title\":\"K\",\"excerpt\":\"" + uzun + "\"}]}}"));

        Assert.True(text.Length < 400, $"uzunluk {text.Length}");
        Assert.Contains("…", text, StringComparison.Ordinal);
    }

    /* ---- Cümle sayısı hesabı ---- */

    /// Modele "kaç saniye" demek işe yaramıyor — süreyi tahmin
    /// edemiyor. "Kaç cümle" demek yarıyor ve gerçek süre zaten
    /// seslendirmeden SONRA ölçülüyor (ADR-006).
    [Theory]
    [InlineData(120_000, 20, 28)]
    [InlineData(180_000, 30, 42)]
    [InlineData(90_000, 15, 21)]
    public void CumleSayisi_SureyleOlcekleniyor(int targetMs, int min, int max)
    {
        var count = LongScriptHandler.SentenceCountFor(targetMs);

        Assert.InRange(count, min, max);
    }

    /// EN AZ ÜÇ CÜMLE: iki cümlelik bir "bölüm" bir bölüm değil.
    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    [InlineData(5000)]
    public void CokKisaBolum_EnAzUcCumle(int targetMs)
        => Assert.True(LongScriptHandler.SentenceCountFor(targetMs) >= 3);

    /* ---- Bölüm planını okuma ---- */

    [Fact]
    public void BaglamdanBolumler_Okunuyor()
    {
        var chapters = LongScriptHandler.ChaptersOf(Json(
            """
            {"chapters":{"chapters":[
              {"index":0,"title":"Kesif","question":"Kim","start_ms":30000,"target_ms":120000},
              {"index":1,"title":"Yapim","start_ms":150000,"target_ms":120000}
            ]}}
            """));

        Assert.Equal(2, chapters.Count);
        Assert.Equal("Kesif", chapters[0].Title);
        Assert.Equal(30000, chapters[0].StartMs);
        Assert.Null(chapters[1].Question);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"chapters":{}}""")]
    [InlineData("""{"chapters":{"chapters":[]}}""")]
    [InlineData("""{"chapters":{"chapters":[{"question":"baslik yok"}]}}""")]
    public void BolumPlaniYok_BosDonuyor(string json)
        => Assert.Empty(LongScriptHandler.ChaptersOf(Json(json)));

    /* ---- Cümle ayrıştırma ---- */

    [Fact]
    public void Cumleler_KirpilipOkunuyor()
    {
        var sentences = LongScriptHandler.ParseSentences(
            """{"sentences":["  Bir cumle. ","","Ikinci cumle.",42]}""");

        Assert.Equal(["Bir cumle.", "Ikinci cumle."], sentences);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("bozuk")]
    [InlineData("""{"sentences":"dizi degil"}""")]
    public void BozukCumleCiktisi_BosDonuyor(string? json)
        => Assert.Empty(LongScriptHandler.ParseSentences(json));
}
