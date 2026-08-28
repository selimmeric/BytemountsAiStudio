using BytemountsAiStudio.Persistence.Providers;

namespace BytemountsAiStudio.Persistence.Tests;

/// Konu üreticisinin model çıktısını okuması (P2-01).
///
/// SAF: veritabanı ve model yok. Bir yerel modelin ne döndüreceği
/// tahmin edilemez, ama döndürdüğü şeyin nasıl okunacağı tahmin
/// edilebilir olmalı.
public sealed class TopicGeneratorTests
{
    [Fact]
    public void TamCikti_AdaylariOkuyor()
    {
        var candidates = TopicGenerator.Parse(
            """
            {"topics":[
              {"title":"Roma'da bir gun kac saatti","angle":"olcum","demand":70,"fit":85,
               "sourceability":80,"visualizability":60,"freshness":55,"risk":5,
               "rationale":"kaynagi bol, gorseli kurulabilir"},
              {"title":"Antik Misir'da dis hekimligi","demand":60,"fit":75,
               "sourceability":70,"visualizability":65,"freshness":70,"risk":10}
            ]}
            """);

        Assert.Equal(2, candidates.Count);
        Assert.Equal("Roma'da bir gun kac saatti", candidates[0].Title);
        Assert.Equal("olcum", candidates[0].Angle);
        Assert.Equal(80, candidates[0].Score.Sourceability);
        Assert.True(candidates[0].Score.IsValid);
        Assert.Null(candidates[1].Angle);
    }

    /// AYNI BAŞLIK İKİ KEZ GELİRSE İKİNCİSİ DÜŞÜYOR.
    ///
    /// Model bunu yapıyor. İkisini de havuza almak, "beş konu ürettim"
    /// derken üçünün aynı olması demekti.
    [Fact]
    public void TekrarEdenBaslik_BirKezAliniyor()
    {
        var candidates = TopicGenerator.Parse(
            """
            {"topics":[
              {"title":"Ayni konu","demand":50,"fit":50,"sourceability":50,
               "visualizability":50,"freshness":50,"risk":0},
              {"title":"AYNI KONU","demand":60,"fit":60,"sourceability":60,
               "visualizability":60,"freshness":60,"risk":0}
            ]}
            """);

        Assert.Single(candidates);
    }

    /// EKSİK BOYUT SIFIR DEĞİL, GEÇERSİZ.
    ///
    /// Sıfır geçerli bir skor ("bu konuya hiç talep yok"); eksik bir
    /// alanı sıfır saymak, modelin cevaplamadığı boyutu cevaplanmış
    /// gibi göstermek olurdu. Geçersiz aday havuza alınmıyor.
    [Fact]
    public void EksikBoyut_AdayiGecersizYapiyor()
    {
        var candidates = TopicGenerator.Parse(
            """{"topics":[{"title":"Eksik","demand":50,"fit":50,"sourceability":50}]}""");

        Assert.Single(candidates);
        Assert.False(candidates[0].Score.IsValid);
    }

    /// Sıfır skor GEÇERLİ: eksiklikle karıştırılmamalı.
    [Fact]
    public void SifirSkor_Gecerli()
    {
        var candidates = TopicGenerator.Parse(
            """
            {"topics":[{"title":"Sifir","demand":0,"fit":0,"sourceability":0,
             "visualizability":0,"freshness":0,"risk":0}]}
            """);

        Assert.True(candidates[0].Score.IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("json degil")]
    [InlineData("""{"baska":"alan"}""")]
    [InlineData("""{"topics":"dizi degil"}""")]
    [InlineData("""{"topics":[]}""")]
    public void BozukCikti_BosDonuyor(string? json)
        => Assert.Empty(TopicGenerator.Parse(json));

    /// Başlıksız aday atlanıyor: başlıksız bir konu üretilemez ve
    /// havuzda yer kaplaması, doldurma sayacını yanıltırdı.
    [Fact]
    public void BasliksizAday_Atlaniyor()
    {
        var candidates = TopicGenerator.Parse(
            """
            {"topics":[
              {"title":"   ","demand":50,"fit":50,"sourceability":50,
               "visualizability":50,"freshness":50,"risk":0},
              {"demand":50,"fit":50,"sourceability":50,
               "visualizability":50,"freshness":50,"risk":0},
              {"title":"Gecerli","demand":50,"fit":50,"sourceability":50,
               "visualizability":50,"freshness":50,"risk":0}
            ]}
            """);

        Assert.Single(candidates);
        Assert.Equal("Gecerli", candidates[0].Title);
    }

    /// Nesne olmayan öğeler diğerlerini düşürmüyor: modelin bir
    /// satırı bozması bütün turu çöpe atmamalı.
    [Fact]
    public void KarisikDizi_GecerliOlanlariAliyor()
    {
        var candidates = TopicGenerator.Parse(
            """
            {"topics":["metin",42,{"title":"Gecerli","demand":50,"fit":50,
             "sourceability":50,"visualizability":50,"freshness":50,"risk":0}]}
            """);

        Assert.Single(candidates);
    }
}
