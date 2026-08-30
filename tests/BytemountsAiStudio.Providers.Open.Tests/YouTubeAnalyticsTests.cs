using System.Net;
using System.Text;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Providers.Open;

namespace BytemountsAiStudio.Providers.Open.Tests;

/// YouTube Analytics günlük çekim (P5-01).
///
/// ÖĞRENME DÖNGÜSÜNÜN VERİ KAYNAĞI. P5-02'den P5-07'ye kadar yazılan
/// her şey bu ölçümden besleniyor.
///
/// ***VERİ GECİKMELİ GELİYOR VE BU SESSİZ BİR TUZAK.*** Yedinci günün
/// sayılarını yedinci gün çekmek, tamamlanmamış bir sayıyı tam sanmak
/// demek: sayı makul görünüyor, kimse şüphelenmiyor ve deney o eksik
/// sayıyla karar veriyor.
public sealed class YouTubeAnalyticsTests
{
    private sealed class Stub(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? LastUrl { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUrl = request.RequestUri!.ToString();

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static YouTubeAnalyticsProvider Provider(
        Stub stub, YouTubeAnalyticsOptions? options = null)
        => new(
            new HttpClient(stub),
            options ?? new YouTubeAnalyticsOptions
            {
                BaseAddress = new Uri("https://sahte.test/v2/reports"),
            },
            new FixedCredentials(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["YOUTUBE_ACCESS_TOKEN"] = "jeton",
            }));

    /* ---- oturma payı ---- */

    /// ***OTURMAMIŞ GÜN ÇEKİLMİYOR.***
    ///
    /// BU DOSYANIN EN ÖNEMLİ TESTİ. YouTube'un raporları iki güne
    /// kadar geriden geliyor; aynı gün çekilen bir sayı eksik ve
    /// eksikliği görünmüyor.
    [Fact]
    public void OturmamisGun_HazirDegil()
    {
        var provider = Provider(new Stub(HttpStatusCode.OK, "{}"));

        var metricDay = new DateOnly(2026, 8, 20);

        Assert.False(provider.IsSettled(metricDay, metricDay));
        Assert.False(provider.IsSettled(metricDay, metricDay.AddDays(1)));
        Assert.True(provider.IsSettled(metricDay, metricDay.AddDays(2)));
    }

    /// OTURMA PAYI AYARLANABİLİR.
    ///
    /// Platform gecikmesi değişebiliyor; iki günü koda gömmek, o
    /// değiştiğinde yeni bir derleme demekti.
    [Fact]
    public void OturmaPayi_Ayarlanabilir()
    {
        var provider = Provider(
            new Stub(HttpStatusCode.OK, "{}"),
            new YouTubeAnalyticsOptions { SettlingDays = 5 });

        var day = new DateOnly(2026, 8, 20);

        Assert.False(provider.IsSettled(day, day.AddDays(4)));
        Assert.True(provider.IsSettled(day, day.AddDays(5)));
    }

    /* ---- okuma ---- */

    /// ÖLÇÜM OKUNUYOR.
    [Fact]
    public async Task Olcum_Okunuyor()
    {
        var stub = new Stub(HttpStatusCode.OK, """{"rows":[[1200,340,25,4,7]]}""");

        var result = await Provider(stub).DailyAsync(
            "v-1", new DateOnly(2026, 8, 20), CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);

        var metric = result.Value!.Value;

        Assert.Equal(1200, metric.Views);
        Assert.Equal(340, metric.EstimatedMinutesWatched);
        Assert.Equal(25, metric.Likes);

        // TARİH VE VİDEO İSTEĞE GİRİYOR: yanlış günün ya da yanlış
        // videonun sayıları, doğru görünen ama başka bir şeyi ölçen
        // bir rapor demekti.
        Assert.Contains("startDate=2026-08-20", stub.LastUrl!, StringComparison.Ordinal);
        // `==` KAÇIRILMIYOR: Analytics filtre sözdizimi bunu
        // istiyor ve kaçırılmış hâli "geçersiz filtre" hatası veriyor.
        Assert.Contains("filters=video==v-1", stub.LastUrl!, StringComparison.Ordinal);
    }

    /// ***SATIR YOKSA `null`, SIFIR DEĞİL.***
    ///
    /// "O gün hiç izlenme yok" ile "o günün verisi henüz gelmedi"
    /// farklı iki şey. Sıfır yazmak, gelmemiş bir günü ölçülmüş saymak
    /// ve bütün ortalamaları aşağı çekmek olurdu.
    [Fact]
    public async Task SatirYok_SifirDegilNull()
    {
        var result = await Provider(new Stub(HttpStatusCode.OK, """{"rows":[]}""")).DailyAsync(
            "v-1", new DateOnly(2026, 8, 20), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value);
    }

    /// EKSİK SÜTUNLU SATIR DA OKUNMUYOR.
    ///
    /// Ölçüt listesi değişirse satır kısalıyor; eksik sütunu sıfır
    /// saymak, hiç istenmemiş bir ölçütü "sıfır ölçüldü" diye
    /// kaydetmek olurdu.
    [Fact]
    public async Task EksikSutun_Okunmuyor()
    {
        var result = await Provider(new Stub(HttpStatusCode.OK, """{"rows":[[10,20]]}""")).DailyAsync(
            "v-1", new DateOnly(2026, 8, 20), CancellationToken.None);

        Assert.Null(result.Value);
    }

    /* ---- hata sınıfları ---- */

    /// HIZ SINIRI ERTELEME.
    [Fact]
    public async Task HizSiniri_KaynakHatasi()
    {
        var result = await Provider(new Stub(HttpStatusCode.TooManyRequests, "{}")).DailyAsync(
            "v-1", new DateOnly(2026, 8, 20), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Resource, result.Error.Kind);
    }

    /// SUNUCU HATASI GEÇİCİ.
    [Fact]
    public async Task SunucuHatasi_Gecici()
    {
        var result = await Provider(new Stub(HttpStatusCode.InternalServerError, "{}")).DailyAsync(
            "v-1", new DateOnly(2026, 8, 20), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Transient, result.Error.Kind);
    }

    /// YETKİSİZ ERİŞİM KALICI.
    ///
    /// Yeniden denemek kapsamı vermiyor; hatayı geçici saymak, aynı
    /// isteği saatlerce tekrarlamak olurdu.
    [Fact]
    public async Task Yetkisiz_Kalici()
    {
        var result = await Provider(new Stub(HttpStatusCode.Forbidden, "{}")).DailyAsync(
            "v-1", new DateOnly(2026, 8, 20), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Permanent, result.Error.Kind);
    }

    /// JETON YOKSA KALICI HATA.
    [Fact]
    public async Task JetonYok_KaliciHata()
    {
        var provider = new YouTubeAnalyticsProvider(
            new HttpClient(new Stub(HttpStatusCode.OK, "{}")),
            new YouTubeAnalyticsOptions { BaseAddress = new Uri("https://sahte.test/v2/reports") },
            new FixedCredentials(new Dictionary<string, string>(StringComparer.Ordinal)));

        var result = await provider.DailyAsync(
            "v-1", new DateOnly(2026, 8, 20), CancellationToken.None);

        Assert.True(result.IsFailure);
        // ***KOD `no_token` DEGIL ARTIK `no_credentials`.***
        //
        // Eski ad "erisim jetonu yok" diyordu ve saglayici artik
        // YENILEME de deniyor: eksik olan sey jeton degil KIMLIK
        // olabiliyor (yenileme jetonu + istemci kimligi + sir).
        // Ad, olani anlatmali.
        //
        // Onek `analytics.` KALIYOR: ortak jeton kaynagi `google.*`
        // donuyor ve olani oldugu gibi gecirmek, bir operatorun
        // aradigi dizgiyi sessizce kaydirmak olurdu.
        Assert.Equal("analytics.no_credentials", result.Error.Code);
    }
}
