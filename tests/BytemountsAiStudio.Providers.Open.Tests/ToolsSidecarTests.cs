using System.Net;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Providers.Open;

namespace BytemountsAiStudio.Providers.Open.Tests;

/// Araçlar yan-servisi istemcisinin testleri (P1-04).
///
/// Ağa çıkılmıyor ve Python tarafı çalıştırılmıyor. Asıl sınanan şey
/// HATA SINIFLANDIRMASI: kuyruğun kararını o belirliyor ve bir yetenek
/// eksikliğini kalıcı hata saymak, yan-servis düzeltildiğinde bile
/// çalışmayacak bir işe dönüşürdü.
public sealed class ToolsSidecarTests
{
    private static ToolsSidecar Sidecar(RouteHandler handler)
        => new(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8099") },
            new ToolsSidecarOptions { BaseAddress = new Uri("http://localhost:8099") });

    private static ProviderContext Context => ProviderContext.ForTest("tools");

    private const string HealthBody = """
        {"status":"ok","version":"0.1.0","capabilities":[
          {"name":"search","available":true,"detail":"http://localhost:8888"},
          {"name":"fetch","available":false,"detail":"playwright kurulu degil"},
          {"name":"align","available":true,"detail":"small / cpu"}
        ]}
        """;

    [Fact]
    public async Task Saglik_YetenekleriDeOkur()
    {
        var handler = new RouteHandler().Route("/health", HealthBody, mime: "application/json");

        var health = await Sidecar(handler).HealthAsync(CancellationToken.None);

        Assert.True(health.IsSuccess);
        Assert.Equal("0.1.0", health.Value.Version);
        Assert.True(health.Value.Can("search"));
        Assert.False(health.Value.Can("fetch"));
    }

    /// "fetch: false" tek başına teşhis edilemez bir bilgi.
    [Fact]
    public async Task KapaliYetenek_NedeniniTasir()
    {
        var handler = new RouteHandler().Route("/health", HealthBody, mime: "application/json");

        var health = await Sidecar(handler).HealthAsync(CancellationToken.None);

        Assert.Contains("playwright", health.Value.Why("fetch"), StringComparison.Ordinal);
    }

    /// Bilinmeyen bir yetenek KULLANILAMAZ sayılıyor: yan-servisin eski
    /// bir sürümü onu hiç sunmuyor olabilir ve "var" varsaymak, çağrıyı
    /// çalışma anına ertelenmiş bir hataya çevirirdi.
    [Fact]
    public async Task BilinmeyenYetenek_KullanilamazSayilir()
    {
        var handler = new RouteHandler().Route("/health", HealthBody, mime: "application/json");

        var health = await Sidecar(handler).HealthAsync(CancellationToken.None);

        Assert.False(health.Value.Can("boyle-bir-yetenek-yok"));
        Assert.NotEmpty(health.Value.Why("boyle-bir-yetenek-yok"));
    }

    /// 503 = yetenek eksik. KAYNAK hatası, başarısızlık değil (ADR-011):
    /// iş ertelenmeli, çünkü biri playwright kurduğunda çalışacak.
    [Fact]
    public async Task EksikYetenek_KaynakHatasi()
    {
        var handler = new RouteHandler().Route("/fetch",
            """{"detail":"playwright kurulu degil"}""",
            HttpStatusCode.ServiceUnavailable, "application/json");

        var result = await Sidecar(handler).FetchAsync(
            new Uri("https://ornek.com/a"), Context, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Resource, result.Error.Kind);
    }

    /// 403 = robots.txt yasaklıyor. KALICI: yeniden denemek dosyayı
    /// değiştirmez.
    [Fact]
    public async Task RobotsYasagi_KaliciHata()
    {
        var handler = new RouteHandler().Route("/fetch",
            """{"detail":"robots.txt bu yolu yasakliyor"}""",
            HttpStatusCode.Forbidden, "application/json");

        var result = await Sidecar(handler).FetchAsync(
            new Uri("https://ornek.com/gizli"), Context, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Permanent, result.Error.Kind);
    }

    [Fact]
    public async Task SunucuHatasi_GeciciHata()
    {
        var handler = new RouteHandler().Route("/fetch", "{}",
            HttpStatusCode.BadGateway, "application/json");

        var result = await Sidecar(handler).FetchAsync(
            new Uri("https://ornek.com/a"), Context, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Transient, result.Error.Kind);
    }

    /// FastAPI hatayı `detail` içinde veriyor. Gövdeyi atmak, teşhis
    /// için tek kullanışlı bilgiyi atmak olurdu.
    [Fact]
    public async Task HataMesaji_GovdedenOkunur()
    {
        var handler = new RouteHandler().Route("/fetch",
            """{"detail":"sema yasak: file"}""",
            HttpStatusCode.BadRequest, "application/json");

        var result = await Sidecar(handler).FetchAsync(
            new Uri("https://ornek.com/a"), Context, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Contains("sema yasak", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cekme_BelgeyeCevrilir()
    {
        var handler = new RouteHandler().Route("/fetch",
            """
            {"url":"https://ornek.com/a","final_url":"https://ornek.com/son",
             "title":"Baslik","text":"Govde metni","html_length":900,
             "rendered":true,"truncated":false}
            """, mime: "application/json");

        var result = await Sidecar(handler).FetchAsync(
            new Uri("https://ornek.com/a"), Context, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Govde metni", result.Value.Value.MainText);
        // YÖNLENDİRME sonrası adres kaydediliyor: kaynak listesinde
        // istenen adres değil, gerçekten okunan sayfa görünmeli.
        Assert.Equal(new Uri("https://ornek.com/son"), result.Value.Value.Url);
        Assert.NotEmpty(result.Value.Value.ContentHash);
    }

    [Fact]
    public async Task Hizalama_KelimeZamanlarinaCevrilir()
    {
        var handler = new RouteHandler().Route("/align",
            """
            {"words":[{"word":"Bir","start_ms":0,"end_ms":400,"confidence":0.9},
                      {"word":"iki","start_ms":400,"end_ms":900,"confidence":0.8}],
             "language":"tr","duration_ms":900,"model":"small","device":"cpu"}
            """, mime: "application/json");

        var result = await Sidecar(handler).AlignAsync(
            new AlignRequest
            {
                AudioPath = "ses.wav",
                Transcript = "Bir iki",
                Language = LanguageTag.Create("tr-TR"),
            },
            Context,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Value.Words.Count);
        Assert.Equal(900, result.Value.Value.Duration.Value);
    }

    /// Sıfır kelime bir hizalama DEĞİL. Başarı olarak dönerse çağıran
    /// taraf onu "ölçüldü" sayar ve tahmine düşmez — sonuç altyazısız
    /// bir video olur ve hiçbir şey kırılmaz. Bu depoda tam olarak bu
    /// oldu (P1-15a).
    [Fact]
    public async Task BosHizalama_BasariSayilmaz()
    {
        var handler = new RouteHandler().Route("/align",
            """{"words":[],"language":"tr","duration_ms":0,"model":"small","device":"cpu"}""",
            mime: "application/json");

        var result = await Sidecar(handler).AlignAsync(
            new AlignRequest
            {
                AudioPath = "ses.wav",
                Transcript = "Bir iki",
                Language = LanguageTag.Create("tr-TR"),
            },
            Context,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Transient, result.Error.Kind);
    }

    [Fact]
    public void BozukAralik_Duzeltilir()
    {
        var words = ToolsSidecar.ToWordTimings(
        [
            new ToolsSidecar.WordPayload("bir", -50, 100, 1),
            new ToolsSidecar.WordPayload("iki", 200, 200, 1),
            new ToolsSidecar.WordPayload("  ", 300, 400, 1),
        ]);

        Assert.Equal(2, words.Count);
        Assert.Equal(0, words[0].Start.Value);
        Assert.True(words[1].End.Value > words[1].Start.Value);
    }

    [Fact]
    public async Task Arama_SonuclaraCevrilir()
    {
        var handler = new RouteHandler().Route("/search",
            """
            {"hits":[{"url":"https://tr.wikipedia.org/wiki/X","title":"X","snippet":"ozet",
                      "engine":"wikipedia","source_type":"encyclopedia"},
                     {"url":"gecersiz url","title":"Y","source_type":"web"}],
             "query":"x","total_available":2}
            """, mime: "application/json");

        var result = await Sidecar(handler).SearchAsync(
            new SearchQuery { Text = "x" }, Context, CancellationToken.None);

        Assert.True(result.IsSuccess);
        // Geçersiz URL'li sonuç atlanıyor: çekilemeyecek bir kaynak
        // listede yer kaplamamalı.
        var hit = Assert.Single(result.Value.Value);
        Assert.Equal(SourceType.Encyclopedia, hit.SourceType);
    }

    [Fact]
    public void KaynakTuru_BilinmeyenDegerdeUnknown()
    {
        Assert.Equal(SourceType.Academic, ToolsSidecar.ParseSourceType("academic"));
        Assert.Equal(SourceType.Unknown, ToolsSidecar.ParseSourceType("boyle-bir-tur-yok"));
        Assert.Equal(SourceType.Unknown, ToolsSidecar.ParseSourceType(null));
    }

    /// Filodaki zayıf makineler yan-servisi güçlü makinede çağırıyor
    /// (docs/DONANIM-VE-MODEL.md).
    [Fact]
    public void UzakAdres_OrtamdanOkunur()
    {
        var options = ToolsSidecarOptions.From(
            name => name == "BMAI_TOOLS_URL" ? "http://192.168.1.40:8099" : null);

        Assert.Equal(new Uri("http://192.168.1.40:8099"), options.BaseAddress);
    }

    [Fact]
    public void OrtamBos_YerelAdres()
    {
        Assert.Equal(new Uri("http://localhost:8099"), ToolsSidecarOptions.From(_ => null).BaseAddress);
    }

    /// Bozuk bir adres yüzünden süreç hiç başlamamaktansa yerele
    /// düşmesi yeğ; hata ilk çağrıda ve okunur biçimde geliyor.
    [Fact]
    public void BozukAdres_VarsayilanaDuser()
    {
        Assert.Equal(new Uri("http://localhost:8099"),
            ToolsSidecarOptions.From(_ => "adres degil bu").BaseAddress);
    }
}

/// Windows konuşma sağlayıcısının testleri.
///
/// PowerShell çağrılmıyor: sınanan şey, betiğin çıktısının nasıl
/// yorumlandığı. Asıl mesele KULLANILAN sesin raporlanması — istenen
/// ses ile kullanılan ses farklı olabiliyor ve fark görünmezse
/// İngilizce metni Türkçe sesle okuyan bir video hiçbir yerde
/// yakalanmaz.
public sealed class WindowsSpeechVoiceTests
{
    [Fact]
    public void KullanilanSes_CiktidanOkunur()
    {
        Assert.Equal("Microsoft Tolga (tr-TR)",
            WindowsSpeechTtsProvider.VoiceFrom("OK 96044 | Microsoft Tolga (tr-TR)"));
    }

    /// Ayraç yoksa ses bilgisi de yok: uydurmak yerine null dönüyor.
    [Theory]
    [InlineData("OK 96044")]
    [InlineData("")]
    [InlineData("OK 96044 |")]
    public void SesBilgisiYoksa_NullDoner(string output)
    {
        Assert.Null(WindowsSpeechTtsProvider.VoiceFrom(output));
    }
}
