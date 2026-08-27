using System.Net;
using System.Text;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Providers.Open;

namespace BytemountsAiStudio.Providers.Open.Tests;

/// Sabit cevap dönen ve isteği yakalayan işleyici.
internal sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
{
    public Uri? LastUrl { get; private set; }

    public string? LastAuthorization { get; private set; }

    public string? LastApiKey { get; private set; }

    public string? LastBody { get; private set; }

    public IDictionary<string, string> ResponseHeaders { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastUrl = request.RequestUri;

        LastAuthorization = request.Headers.TryGetValues("Authorization", out var auth)
            ? auth.FirstOrDefault()
            : null;

        LastApiKey = request.Headers.TryGetValues("xi-api-key", out var key) ? key.FirstOrDefault() : null;

        LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

        var response = new HttpResponseMessage(status)
        {
            RequestMessage = request,
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        foreach (var (name, value) in ResponseHeaders)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return response;
    }
}

internal sealed class Credentials(string? value) : ICredentialSource
{
    public string? Get(string name) => value;
}

/// Pexels adaptörünün testleri (P1-17).
///
/// Anahtar GEREKMİYOR: sınanan şey isteğin şekli, lisans kaydı ve
/// hata sınıflandırması.
public sealed class PexelsImageProviderTests
{
    private const string Reply = """
        {"photos":[
          {"width":1080,"height":1920,"photographer":"Ayşe K.","alt":"antik tapınak",
           "src":{"original":"https://x/o.jpg","large":"https://x/l.jpg","large2x":"https://x/l2.jpg"}},
          {"width":0,"height":0,"src":{"large":"https://x/bozuk.jpg"}}
        ]}
        """;

    private static PexelsImageProvider Provider(StubHandler handler, string? key = "pk-test")
        => new(new HttpClient(handler), new Credentials(key));

    private static ImageQuery Query(double? aspect = null)
        => new() { Terms = "antik tapınak", MaxResults = 5, PreferredAspectRatio = aspect };

    [Fact]
    public async Task Sonuclar_LisansKaydiylaDonuyor()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Reply);

        var result = await Provider(handler).FindAsync(
            Query(), ProviderContext.ForTest(), CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);

        // Ölçüsü olmayan sonuç ATLANIYOR: kadraja uydurma kararı
        // en–boy oranına bakıyor ve bilinmeyen bir oran onu kör
        // bırakırdı.
        var candidate = Assert.Single(result.Value.Value);

        Assert.Equal("Pexels License", candidate.License.Name);
        Assert.Equal("Ayşe K.", candidate.License.Author);
        Assert.NotEqual(default, candidate.License.CapturedAt);
    }

    /// Büyük boy tercih ediliyor: dikey videoda küçük bir kare
    /// büyütüldüğünde bulanık çıkıyor.
    [Fact]
    public async Task BuyukBoy_TercihEdiliyor()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Reply);

        var result = await Provider(handler).FindAsync(
            Query(), ProviderContext.ForTest(), CancellationToken.None);

        Assert.Equal("https://x/l2.jpg", result.Value.Value[0].Url.ToString());
    }

    /// Yatay bir kareyi 9:16'ya kırpmak, karenin çoğunu atmak demek —
    /// ve atılan kısım genellikle konunun kendisi.
    [Theory]
    [InlineData(0.5625, "portrait")]
    [InlineData(1.7777, "landscape")]
    [InlineData(1.0, "square")]
    public void DikeyVideo_DikeySonucIstiyor(double aspect, string expected)
    {
        Assert.Contains(expected, PexelsImageProvider.Orientation(aspect), StringComparison.Ordinal);
    }

    [Fact]
    public void OranBelirtilmemis_YonBelirtilmiyor()
    {
        Assert.Empty(PexelsImageProvider.Orientation(null));
    }

    /// Pexels `Authorization` başlığını ŞEMASIZ istiyor; "Bearer"
    /// öneki eklemek 401 veriyor.
    [Fact]
    public async Task Anahtar_SemasizGonderiliyor()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Reply);

        await Provider(handler).FindAsync(Query(), ProviderContext.ForTest(), CancellationToken.None);

        Assert.Equal("pk-test", handler.LastAuthorization);
    }

    [Fact]
    public async Task AnahtarYoksa_AnahtarsizYoluSoyluyor()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Reply);

        var result = await Provider(handler, key: null).FindAsync(
            Query(), ProviderContext.ForTest(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Contains("Openverse", result.Error.Message, StringComparison.Ordinal);
    }

    /// Kota SAATLİK: erteleme, başarısızlık değil.
    [Fact]
    public async Task Kota_KaynakHatasi()
    {
        var handler = new StubHandler(HttpStatusCode.TooManyRequests, "{}");

        var result = await Provider(handler).FindAsync(
            Query(), ProviderContext.ForTest(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Resource, result.Error.Kind);
    }

    /// Sunucunun söylediği sıfırlama anı, tahmin edilene tercih
    /// ediliyor.
    [Fact]
    public void SifirlamaAni_BasliktanOkunuyor()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        var epoch = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds();
        response.Headers.TryAddWithoutValidation("X-Ratelimit-Reset", epoch.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var wait = PexelsImageProvider.ResetAfter(response);

        Assert.InRange(wait, TimeSpan.FromMinutes(25), TimeSpan.FromMinutes(35));
    }

    /// GEÇMİŞTE kalmış bir sıfırlama anı ya saat kayması ya da bozuk
    /// bir başlık; hemen denemek yerine varsayılana düşülüyor.
    [Theory]
    [InlineData("0")]
    [InlineData("sayi degil")]
    public void BozukSifirlamaBasligi_VarsayilanaDuser(string value)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("X-Ratelimit-Reset", value);

        Assert.Equal(TimeSpan.FromHours(1), PexelsImageProvider.ResetAfter(response));
    }

    /// Sessizce boş dönmek, çağıran tarafta "görsel bulunamadı" gibi
    /// görünür ve asıl sebep gizlenirdi.
    [Fact]
    public async Task Uretim_KaliciHataVeriyor()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{}");

        var result = await Provider(handler).GenerateAsync(
            new ImagePrompt { Text = "x", Width = 1080, Height = 1920 },
            ProviderContext.ForTest(),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Permanent, result.Error.Kind);
    }
}

/// ElevenLabs adaptörünün testleri (P1-14).
///
/// Asıl mesele KELİME ZAMANLAMASI: bu sağlayıcının tek gerçek üstünlüğü
/// o ve doğru çevrilmezse hattın ASR adımını atlaması anlamsızlaşır.
public sealed class ElevenLabsTtsProviderTests
{
    private const string AlignmentJson = """
        "alignment":{
          "characters":["B","i","r"," ","i","k","i"],
          "character_start_times_seconds":[0.0,0.1,0.2,0.3,0.4,0.5,0.6],
          "character_end_times_seconds":[0.1,0.2,0.3,0.4,0.5,0.6,0.75]}
        """;

    private static string Reply(string audioBase64)
        => "{\"audio_base64\":\"" + audioBase64 + "\"," + AlignmentJson + "}";

    private static string BigAudio() => Convert.ToBase64String(new byte[2048]);

    private static ElevenLabsTtsProvider Provider(StubHandler handler, string? key = "el-test")
        => new(new HttpClient(handler), new ElevenLabsOptions(), new Credentials(key));

    private static TtsRequest Request() => new()
    {
        SpeechText = "Bir iki",
        VoiceId = "ses-1",
        Language = LanguageTag.Create("tr-TR"),
    };

    [Fact]
    public async Task Ses_VeKelimeZamanlariDonuyor()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Reply(BigAudio()));

        var result = await Provider(handler).SynthesizeAsync(
            Request(), ProviderContext.ForTest(), CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);
        Assert.Equal(2, result.Value.Value.WordTimings.Count);
        Assert.Equal("Bir", result.Value.Value.WordTimings[0].Text);
        Assert.Equal("iki", result.Value.Value.WordTimings[1].Text);
        Assert.Equal("ses-1", result.Value.Value.VoiceUsed);
    }

    /// Boşluk kelime SINIRI ama kelimenin parçası DEĞİL: sonunu bir
    /// önceki karakterin bitişi belirliyor, yoksa her kelime bir
    /// sonrakine kadar uzar ve altyazı geç sönerdi.
    [Fact]
    public void KelimeSonu_BosluguIcermiyor()
    {
        var alignment = new ElevenLabsTtsProvider.Alignment(
            ["B", "i", "r", " ", "i", "k", "i"],
            [0.0, 0.1, 0.2, 0.3, 0.4, 0.5, 0.6],
            [0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.75]);

        var timings = ElevenLabsTtsProvider.ToWordTimings(alignment);

        // "Bir" 0.3'te değil 0.3'ten ÖNCE bitiyor: boşluğun bitişi
        // dahil edilseydi 400 ms olurdu.
        Assert.Equal(300, timings[0].End.Value);
        Assert.Equal(400, timings[1].Start.Value);
        Assert.Equal(750, timings[1].End.Value);
    }

    [Fact]
    public void HizalamaYoksa_BosListe()
    {
        Assert.Empty(ElevenLabsTtsProvider.ToWordTimings(null));
        Assert.Empty(ElevenLabsTtsProvider.ToWordTimings(
            new ElevenLabsTtsProvider.Alignment([], [], [])));
    }

    /// Dizi uzunlukları tutmazsa ÇÖKMÜYOR: en kısası kadar okunuyor.
    /// Bozuk bir yanıt yüzünden koşunun düşmesi, o yanıtın hiç
    /// gelmemesinden daha kötü olurdu.
    [Fact]
    public void UyumsuzDiziler_Cokmuyor()
    {
        var alignment = new ElevenLabsTtsProvider.Alignment(
            ["a", "b", "c"], [0.0], [0.1, 0.2]);

        Assert.Single(ElevenLabsTtsProvider.ToWordTimings(alignment));
    }

    /// Sessiz bir video hiçbir şeyi kırmadan yayına gider.
    [Fact]
    public async Task CokKucukSes_BasariSayilmiyor()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Reply(Convert.ToBase64String(new byte[10])));

        var result = await Provider(handler).SynthesizeAsync(
            Request(), ProviderContext.ForTest(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Transient, result.Error.Kind);
    }

    /// TEK ÇAĞRI ile hem ses hem zamanlama: ayrı istemek ikinci kez
    /// para harcamak ve iki çağrının farklı ses üretme riski demekti.
    [Fact]
    public async Task ZamanDamgaliUcNoktasi_Kullaniliyor()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Reply(BigAudio()));

        await Provider(handler).SynthesizeAsync(Request(), ProviderContext.ForTest(), CancellationToken.None);

        Assert.Contains("with-timestamps", handler.LastUrl!.ToString(), StringComparison.Ordinal);
        Assert.Equal("el-test", handler.LastApiKey);
    }

    /// KOTA BİTMESİ DE 401 DÖNÜYOR ve tek ayırt edici gövdedeki
    /// `quota_exceeded`. Kalıcı sayılsaydı, kota yenilendikten sonra
    /// bile çalışmayacak bir işe dönüşürdü.
    [Fact]
    public async Task KotaBitti_KaynakHatasi()
    {
        var handler = new StubHandler(HttpStatusCode.Unauthorized,
            """{"detail":{"status":"quota_exceeded","message":"kota bitti"}}""");

        var result = await Provider(handler).SynthesizeAsync(
            Request(), ProviderContext.ForTest(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Resource, result.Error.Kind);
    }

    /// Aynı durum kodu ama gövdede kota yok: anahtar gerçekten
    /// geçersiz.
    [Fact]
    public async Task GecersizAnahtar_KaliciHata()
    {
        var handler = new StubHandler(HttpStatusCode.Unauthorized,
            """{"detail":{"status":"invalid_api_key"}}""");

        var result = await Provider(handler).SynthesizeAsync(
            Request(), ProviderContext.ForTest(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Permanent, result.Error.Kind);
    }

    [Fact]
    public async Task AnahtarYoksa_AnahtarsizYoluSoyluyor()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Reply(BigAudio()));

        var result = await Provider(handler, key: null).SynthesizeAsync(
            Request(), ProviderContext.ForTest(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Contains("Piper", result.Error.Message, StringComparison.Ordinal);
    }

    /// Bu sağlayıcı kullanıldığında hizalama adımı HİÇ çalışmıyor;
    /// bayrak bir seçim kriteri (ADR-002r).
    [Fact]
    public void KelimeZamani_Destekleniyor()
    {
        Assert.True(new ElevenLabsTtsProvider(new HttpClient()).SupportsWordTimings);
    }
}

/// Openverse müzik sağlayıcısının testleri (P2-09).
///
/// Ağa çıkılmıyor. Asıl sınanan şey LİSANS SÜZGECİ: lisans kanıtı
/// olmayan müzik yayına giremez ve Content ID talebi kanalın gelirini
/// götürüyor — bu, görsellerdeki atıf eksikliğinden farklı olarak
/// düzeltilemez bir hasar.
public sealed class OpenverseMusicProviderTests
{
    private const string Reply = """
        {"results":[
          {"title":"Ambient Dance","url":"https://x/a.mp3","creator":"Zeropage",
           "license":"by","license_version":"3.0","license_url":"https://x/by",
           "duration":228000},
          {"title":"Serbest","url":"https://x/b.mp3","creator":"Kimse",
           "license":"cc0","license_version":"1.0","duration":90000},
          {"title":"Paylas-ayni","url":"https://x/c.mp3","license":"by-sa","duration":300000},
          {"title":"Ticari degil","url":"https://x/d.mp3","license":"by-nc","duration":300000},
          {"title":"Suresi yok","url":"https://x/e.mp3","license":"cc0"},
          {"title":"Cok kisa","url":"https://x/f.mp3","license":"cc0","duration":5000}
        ]}
        """;

    private static OpenverseMusicProvider Provider(StubHandler handler)
        => new(new HttpClient(handler));

    private static MusicQuery Query(int minimumMs = 60_000)
        => new() { Mood = "ambient", MinimumDuration = new BytemountsAiStudio.Core.Time.Ms(minimumMs) };

    /// ShareAlike LİSTEDE DEĞİL: türev eserin aynı lisansla yayılmasını
    /// istiyor ve arka plan müziği videonun tamamını türev hâline
    /// getiriyor — kanalın kendi içeriğini de o lisansa bağlamak
    /// demek.
    [Fact]
    public async Task YasakLisanslar_Eleniyor()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Reply);

        var found = await Provider(handler).FindAsync(
            Query(), ProviderContext.ForTest(), CancellationToken.None);

        Assert.True(found.IsSuccess);

        var titles = found.Value.Value.Select(t => t.Title).ToList();

        Assert.DoesNotContain("Paylas-ayni", titles);
        Assert.DoesNotContain("Ticari degil", titles);
    }

    /// SÜRESİ BİLİNMEYEN parça atlanıyor: videodan kısa bir müzik
    /// ortada kesiliyor ve o kesinti izleyicinin fark ettiği ilk şey
    /// oluyor.
    [Fact]
    public async Task SuresizVeKisaParcalar_Eleniyor()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Reply);

        var found = await Provider(handler).FindAsync(
            Query(minimumMs: 60_000), ProviderContext.ForTest(), CancellationToken.None);

        var titles = found.Value.Value.Select(t => t.Title).ToList();

        Assert.DoesNotContain("Suresi yok", titles);
        Assert.DoesNotContain("Cok kisa", titles);
    }

    /// ATIF İSTEMEYEN tercih ediliyor. Bu bir kolaylık değil risk
    /// azaltma: CC BY'de atıf açıklamaya girmek zorunda ve o açıklama
    /// sonradan kısalırsa lisans ihlal ediliyor.
    [Fact]
    public async Task Secim_AtifIstemeyeniTercihEdiyor()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Reply);

        var selected = await Provider(handler).SelectAsync(
            Query(), ProviderContext.ForTest(), CancellationToken.None);

        Assert.True(selected.IsSuccess);
        Assert.Equal("Serbest", selected.Value.Value.Title);
        Assert.False(selected.Value.Value.License.RequiresAttribution);
    }

    /// CC BY atıf zorunlu kılıyor ve bu bilgi videonun açıklamasına
    /// girmek zorunda.
    [Fact]
    public async Task CcBy_AtifZorunluIsaretleniyor()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Reply);

        var found = await Provider(handler).FindAsync(
            Query(), ProviderContext.ForTest(), CancellationToken.None);

        var cc = found.Value.Value.Single(t => t.Title == "Ambient Dance");

        Assert.True(cc.License.RequiresAttribution);
        Assert.Equal("Zeropage", cc.License.Author);
        Assert.Equal("CC BY 3.0", cc.License.Name);
    }

    /// Müzik bulunamaması GEÇİCİ: arama başka bir zaman başka sonuç
    /// verebiliyor ve kalıcı saymak o kanalın bir daha hiç müzik
    /// denememesi demekti.
    [Fact]
    public async Task HicUygunParcaYok_GeciciHata()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"results":[]}""");

        var selected = await Provider(handler).SelectAsync(
            Query(), ProviderContext.ForTest(), CancellationToken.None);

        Assert.True(selected.IsFailure);
        Assert.Equal(ErrorKind.Transient, selected.Error.Kind);
    }

    /// Sürüm ÖNEMLİ: CC BY 2.0 ile 4.0'ın atıf gereklilikleri farklı.
    [Theory]
    [InlineData("by", "3.0", "CC BY 3.0")]
    [InlineData("cc0", "1.0", "CC0 1.0")]
    [InlineData("pdm", null, "Public Domain Mark")]
    public void LisansAdi_SurumleBirlikte(string license, string? version, string expected)
    {
        Assert.Equal(expected, OpenverseMusicProvider.LicenseName(license, version));
    }

    [Fact]
    public void RuhHali_AramaTerimineCevriliyor()
    {
        Assert.Equal("cinematic", OpenverseMusicProvider.MoodToTerms("cinematic"));
        Assert.Equal("suspense", OpenverseMusicProvider.MoodToTerms("SUSPENSE"));
    }

    /// TEK KELİME, ve bu canlı sorgularla öğrenildi: Openverse
    /// terimleri VE ile birleştiriyor. "ambient documentary
    /// underscore" sıfır sonuç veriyor, "documentary" tek başına 240 —
    /// ve boş sonuç sessizce "müzik yok" olarak geçiyordu.
    [Theory]
    [InlineData("cinematic")]
    [InlineData("documentary")]
    [InlineData("suspense")]
    [InlineData("emotional")]
    [InlineData("energetic")]
    [InlineData("ambient")]
    [InlineData("bilinmeyen")]
    public void AramaTerimi_TekKelime(string mood)
    {
        Assert.DoesNotContain(' ', OpenverseMusicProvider.MoodToTerms(mood));
    }

    /// Bilinmeyen bir ruh hâli için ambient: arka planda en az dikkat
    /// çeken tür ve yanlış seçim en az zarar veriyor.
    [Theory]
    [InlineData("boyle-bir-ruh-hali-yok")]
    [InlineData("")]
    [InlineData(null)]
    public void BilinmeyenRuhHali_AmbientaDusuyor(string? mood)
    {
        Assert.Contains("ambient", OpenverseMusicProvider.MoodToTerms(mood), StringComparison.Ordinal);
    }
}
