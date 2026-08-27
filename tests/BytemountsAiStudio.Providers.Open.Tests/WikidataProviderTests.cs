using System.Net;
using System.Text;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Providers.Open;

namespace BytemountsAiStudio.Providers.Open.Tests;

/// Sorgu parametresine göre cevap veren sahte işleyici.
///
/// Wikidata iki farklı `action` ile çağrılıyor ve olgu sorgusu ikinci
/// bir tur daha atıyor (etiket çözümü); üçünü ayrı ayrı ayarlamak
/// gerekiyor.
internal sealed class WikidataHandler : HttpMessageHandler
{
    private readonly List<(string Contains, HttpStatusCode Status, string Body)> _routes = [];

    public List<string> Requested { get; } = [];

    public WikidataHandler Route(string contains, string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes.Add((contains, status, body));
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        Requested.Add(url);

        foreach (var (contains, status, body) in _routes)
        {
            if (url.Contains(contains, StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(status)
                {
                    RequestMessage = request,
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
            }
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });
    }
}

/// Wikidata sağlayıcı testleri (P1-05).
///
/// Buradaki tarih testlerinin üçü CANLI SORGUDA bulunan hataları
/// sabitliyor. Üçü de sentetik testlerden geçerdi çünkü hatayı ancak
/// gerçek Wikidata cevabının biçimi ortaya çıkarıyordu.
public sealed class WikidataProviderTests
{
    private static readonly LanguageTag Turkish = LanguageTag.Create("tr-TR");
    private static readonly LanguageTag English = LanguageTag.Create("en-US");

    private static WikidataProvider Provider(WikidataHandler handler)
        => new(new HttpClient(handler));

    private static string Entity(string id, string claims)
        => $$"""
            { "entities": { "{{id}}": {
                "labels": { "tr": { "value": "Test" } },
                "claims": { {{claims}} } } } }
            """;

    private static string TimeClaim(string property, string time, int precision)
        => $$"""
            "{{property}}": [ { "mainsnak": {
                "datatype": "time",
                "datavalue": { "value": { "time": "{{time}}", "precision": {{precision}} } } } } ]
            """;

    private static async Task<string?> FactValueAsync(
        string time, int precision, LanguageTag? language = null)
    {
        var handler = new WikidataHandler()
            .Route("wbgetentities", Entity("Q1", TimeClaim("P571", time, precision)));

        var facts = await Provider(handler)
            .FactsAsync("Q1", language ?? Turkish, CancellationToken.None);

        Assert.True(facts.IsSuccess, facts.IsFailure ? facts.Error.Message : string.Empty);

        return facts.Value.SingleOrDefault()?.Value;
    }

    [Fact]
    public async Task Arama_VarlikDondurur()
    {
        var handler = new WikidataHandler().Route("wbsearchentities", """
            { "search": [
                { "id": "Q214944", "label": "Göbeklitepe", "description": "arkeolojik alan" } ] }
            """);

        var result = await Provider(handler).SearchAsync(
            new SearchQuery { Text = "Göbeklitepe", Language = Turkish },
            ProviderContext.ForTest(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var hit = Assert.Single(result.Value.Value);
        Assert.Equal("Göbeklitepe", hit.Title);
        Assert.Equal("arkeolojik alan", hit.Snippet);
        Assert.Equal(SourceType.Encyclopedia, hit.SourceType);
        Assert.EndsWith("Q214944", hit.Url.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SonucYok_BosListeDoner()
    {
        var handler = new WikidataHandler().Route("wbsearchentities", """{ "search": [] }""");

        var result = await Provider(handler).SearchAsync(
            new SearchQuery { Text = "yok" }, ProviderContext.ForTest(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.Value);
    }

    /// CANLI SORGUDA BULUNDU: gün hassasiyetli bir tarihin GÜNÜ
    /// düşüyordu. Atatürk'ün ölüm tarihi "11/1938" çıkıyordu, oysa
    /// kayıtta 10 Kasım 1938 var.
    [Fact]
    public async Task GunHassasiyeti_GunuKorur()
    {
        Assert.Equal("10/11/1938", await FactValueAsync("+1938-11-10T00:00:00Z", 11));
    }

    /// CANLI SORGUDA BULUNDU: saat kısmı atılmayınca gün alanı
    /// "10T00:00:00Z" çıkıyordu. Wikidata her tarihi tam ISO damgası
    /// gibi döndürüyor ama saat bilgisi dolgu.
    [Fact]
    public async Task SaatKismi_Atilir()
    {
        var value = await FactValueAsync("+1938-11-10T00:00:00Z", 11);

        Assert.DoesNotContain("T00:00", value!, StringComparison.Ordinal);
        Assert.DoesNotContain("Z", value!, StringComparison.Ordinal);
    }

    /// CANLI SORGUDA BULUNDU: İngilizce sıra eki "2th" çıkıyordu.
    /// Türkçe tarafta "2. yüzyıl" doğru olduğu için fark edilmiyordu.
    [Theory]
    [InlineData(150, "2nd century BCE")]
    [InlineData(50, "1st century BCE")]
    [InlineData(250, "3rd century BCE")]
    [InlineData(350, "4th century BCE")]
    [InlineData(1050, "11th century BCE")]
    [InlineData(1250, "13th century BCE")]
    public async Task IngilizceSiraEki_Dogru(int year, string expected)
    {
        var time = FormattableString.Invariant($"-{year:0000}-01-01T00:00:00Z");

        Assert.Equal(expected, await FactValueAsync(time, 7, English));
    }

    [Fact]
    public async Task TurkceYuzyil_NoktaliYazilir()
    {
        Assert.Equal("2. yüzyıl MÖ", await FactValueAsync("-0150-01-01T00:00:00Z", 7));
    }

    /// Gün bilgisi YOK, yalnızca dolgu: ay hassasiyetinde günü
    /// yazmak "1 Ocak" gibi uydurma bir kesinlik üretirdi.
    [Fact]
    public async Task AyHassasiyeti_GunYazmaz()
    {
        Assert.Equal("11/1938", await FactValueAsync("+1938-11-10T00:00:00Z", 10));
    }

    [Theory]
    [InlineData(9, "1938")]
    [InlineData(8, "1930'lar")]
    [InlineData(6, "~1938")]
    public async Task DusukHassasiyet_UygunBicimde(int precision, string expected)
    {
        Assert.Equal(expected, await FactValueAsync("+1938-01-01T00:00:00Z", precision));
    }

    [Fact]
    public async Task MiladdanOnce_Isaretlenir()
    {
        Assert.Equal("9999 MÖ", await FactValueAsync("-9999-01-01T00:00:00Z", 9));
        Assert.Equal("9999 BCE", await FactValueAsync("-9999-01-01T00:00:00Z", 9, English));
    }

    /// Öğe referansları TEK ek çağrıyla çözülüyor. Çözmeseydik modele
    /// "ülke: Q43" giderdi ve bu hiçbir işe yaramaz.
    [Fact]
    public async Task OgeReferanslari_EtiketeCevrilir()
    {
        var handler = new WikidataHandler()
            .Route("ids=Q1&", Entity("Q1", """
                "P17": [ { "mainsnak": {
                    "datatype": "wikibase-item",
                    "datavalue": { "value": { "id": "Q43" } } } } ]
                """))
            .Route("props=labels", """
                { "entities": { "Q43": { "labels": { "tr": { "value": "Türkiye" } } } } }
                """);

        var facts = await Provider(handler).FactsAsync("Q1", Turkish, CancellationToken.None);

        Assert.True(facts.IsSuccess);
        Assert.Equal("Türkiye", Assert.Single(facts.Value).Value);
    }

    /// Bir ek isteğin başarısız olması bütün araştırmayı düşürmemeli.
    [Fact]
    public async Task EtiketCozumuDuserse_HamKimlikDoner()
    {
        var handler = new WikidataHandler()
            .Route("ids=Q1&", Entity("Q1", """
                "P17": [ { "mainsnak": {
                    "datatype": "wikibase-item",
                    "datavalue": { "value": { "id": "Q43" } } } } ]
                """))
            .Route("props=labels", "hata", HttpStatusCode.InternalServerError);

        var facts = await Provider(handler).FactsAsync("Q1", Turkish, CancellationToken.None);

        Assert.True(facts.IsSuccess);
        Assert.Equal("Q43", Assert.Single(facts.Value).Value);
    }

    /// Seçilmemiş özellikler bağlamı doldurup asıl bilgiyi gömerdi.
    [Fact]
    public async Task SecilmemisOzellik_Atlanir()
    {
        var handler = new WikidataHandler().Route("wbgetentities", Entity("Q1", """
            "P9999": [ { "mainsnak": {
                "datatype": "string", "datavalue": { "value": "ilgisiz" } } } ],
            "P17": [ { "mainsnak": {
                "datatype": "string", "datavalue": { "value": "Türkiye" } } } ]
            """));

        var facts = await Provider(handler).FactsAsync("Q1", Turkish, CancellationToken.None);

        Assert.Equal("P17", Assert.Single(facts.Value).PropertyId);
    }

    /// Modele ham JSON vermek, hiçbir şey vermemekten kötü.
    [Fact]
    public async Task DesteklenmeyenTip_Atlanir()
    {
        var handler = new WikidataHandler().Route("wbgetentities", Entity("Q1", """
            "P17": [ { "mainsnak": {
                "datatype": "bilinmeyen-tip",
                "datavalue": { "value": { "karmasik": "yapi" } } } } ]
            """));

        var facts = await Provider(handler).FactsAsync("Q1", Turkish, CancellationToken.None);

        Assert.True(facts.IsSuccess);
        Assert.Empty(facts.Value);
    }

    [Fact]
    public async Task Koordinat_Bicimlenir()
    {
        var handler = new WikidataHandler().Route("wbgetentities", Entity("Q1", """
            "P625": [ { "mainsnak": {
                "datatype": "globe-coordinate",
                "datavalue": { "value": { "latitude": 37.223055, "longitude": 38.9225 } } } } ]
            """));

        var facts = await Provider(handler).FactsAsync("Q1", Turkish, CancellationToken.None);

        Assert.Equal("37.2231, 38.9225", Assert.Single(facts.Value).Value);
    }

    [Fact]
    public async Task Miktar_ArtiIsaretiniAtar()
    {
        var handler = new WikidataHandler().Route("wbgetentities", Entity("Q1", """
            "P1082": [ { "mainsnak": {
                "datatype": "quantity",
                "datavalue": { "value": { "amount": "+85000000" } } } } ]
            """));

        Assert.Equal("85000000",
            Assert.Single((await Provider(handler).FactsAsync("Q1", Turkish, CancellationToken.None)).Value).Value);
    }

    [Fact]
    public async Task OlmayanVarlik_KaliciHata()
    {
        var handler = new WikidataHandler().Route("wbgetentities", """{ "entities": {} }""");

        var result = await Provider(handler).FactsAsync("Q1", Turkish, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("wikidata.not_found", result.Error.Code);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, ErrorKind.Transient)]
    [InlineData((HttpStatusCode)429, ErrorKind.Transient)]
    [InlineData(HttpStatusCode.BadRequest, ErrorKind.Permanent)]
    public async Task HataSiniflandirmasi(HttpStatusCode status, ErrorKind expected)
    {
        var handler = new WikidataHandler().Route("wbsearchentities", "hata", status);

        var result = await Provider(handler).SearchAsync(
            new SearchQuery { Text = "x" }, ProviderContext.ForTest(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(expected, result.Error.Kind);
    }

    /// Wikimedia tanımlayıcı User-Agent istiyor; vermemek engellenme
    /// sebebi.
    [Fact]
    public async Task TanimlayiciAjan_Gonderilir()
    {
        var captured = new CapturingHandler();

        await new WikidataProvider(new HttpClient(captured)).SearchAsync(
            new SearchQuery { Text = "x" }, ProviderContext.ForTest(), CancellationToken.None);

        Assert.Contains("BytemountsAiStudio", captured.UserAgent ?? string.Empty, StringComparison.Ordinal);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? UserAgent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            UserAgent = request.Headers.UserAgent.ToString();

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent("""{ "search": [] }""", Encoding.UTF8, "application/json"),
            });
        }
    }
}
