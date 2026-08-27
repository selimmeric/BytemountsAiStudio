using System.Net;
using System.Text;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Providers.Open;

namespace BytemountsAiStudio.Providers.Open.Tests;

/// DuckDuckGo Instant Answer testleri (P1-05).
///
/// Bu sağlayıcı bir web araması DEĞİL ve testler de o beklentiyi
/// kurmuyor: sınanan şey özetin ve ilgili başlıkların doğru okunması.
public sealed class DuckDuckGoProviderTests
{
    private sealed class Stub(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string? RequestUrl { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUrl = request.RequestUri!.ToString();

            return Task.FromResult(new HttpResponseMessage(status)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static async Task<IReadOnlyList<SearchHit>> SearchAsync(string body, int max = 10)
    {
        var result = await new DuckDuckGoProvider(new HttpClient(new Stub(body))).SearchAsync(
            new SearchQuery { Text = "Göbeklitepe", MaxResults = max },
            ProviderContext.ForTest(), CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);

        return result.Value.Value;
    }

    [Fact]
    public async Task Ozet_IlkSonucOlarakDoner()
    {
        var hits = await SearchAsync("""
            {
              "Heading": "Göbekli Tepe",
              "AbstractText": "Göbekli Tepe, Türkiye'de bir neolitik alandır.",
              "AbstractURL": "https://en.wikipedia.org/wiki/G%C3%B6bekli_Tepe",
              "AbstractSource": "Wikipedia",
              "RelatedTopics": []
            }
            """);

        var hit = Assert.Single(hits);
        Assert.Equal("Göbekli Tepe", hit.Title);
        Assert.Contains("neolitik", hit.Snippet!, StringComparison.Ordinal);
        Assert.Equal(SourceType.Encyclopedia, hit.SourceType);
        Assert.Equal(0, hit.Rank);
    }

    [Fact]
    public async Task IlgiliBasliklar_SonucaEklenir()
    {
        var hits = await SearchAsync("""
            {
              "AbstractText": "",
              "RelatedTopics": [
                { "FirstURL": "https://duckduckgo.com/Nevali_Cori",
                  "Text": "Nevalı Çori - Güneydoğu Anadolu'da bir arkeolojik alan." },
                { "FirstURL": "https://duckduckgo.com/Karahan_Tepe",
                  "Text": "Karahantepe - Şanlıurfa'da bir neolitik alan." }
              ]
            }
            """);

        Assert.Equal(2, hits.Count);
        Assert.Equal("Nevalı Çori", hits[0].Title);
        Assert.Equal("Karahantepe", hits[1].Title);
    }

    /// Özet yoksa sonuç listesi ilgili başlıklarla başlıyor; boş özet
    /// bir sonuç olarak eklenmemeli.
    [Fact]
    public async Task BosOzet_SonucUretmez()
    {
        var hits = await SearchAsync("""
            { "AbstractText": "", "AbstractURL": "", "RelatedTopics": [] }
            """);

        Assert.Empty(hits);
    }

    [Fact]
    public async Task GecersizAdres_Atlanir()
    {
        var hits = await SearchAsync("""
            {
              "AbstractText": "metin", "AbstractURL": "bu bir adres degil",
              "RelatedTopics": [ { "FirstURL": "yine degil", "Text": "bir sey" } ]
            }
            """);

        Assert.Empty(hits);
    }

    [Fact]
    public async Task SonucSiniri_Uygulanir()
    {
        var topics = string.Join(",", Enumerable.Range(0, 10).Select(i =>
            $$"""{ "FirstURL": "https://duckduckgo.com/x{{i}}", "Text": "Baslik {{i}} - aciklama" }"""));

        var hits = await SearchAsync($$"""
            { "AbstractText": "", "RelatedTopics": [ {{topics}} ] }
            """, max: 3);

        Assert.Equal(3, hits.Count);
    }

    /// Özet metni HTML etiketleriyle gelebiliyor; iddia çıkarımına
    /// giren metinde etiket olmamalı.
    [Fact]
    public async Task HtmlKapali_Istenir()
    {
        var stub = new Stub("""{ "RelatedTopics": [] }""");

        await new DuckDuckGoProvider(new HttpClient(stub)).SearchAsync(
            new SearchQuery { Text = "x" }, ProviderContext.ForTest(), CancellationToken.None);

        Assert.Contains("no_html=1", stub.RequestUrl!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WikipediaDisiKaynak_BilinmeyenSayilir()
    {
        var hits = await SearchAsync("""
            {
              "Heading": "X", "AbstractText": "metin",
              "AbstractURL": "https://ornek.com/x", "AbstractSource": "Ornek Sozluk",
              "RelatedTopics": []
            }
            """);

        Assert.Equal(SourceType.Unknown, Assert.Single(hits).SourceType);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, ErrorKind.Transient)]
    [InlineData((HttpStatusCode)429, ErrorKind.Transient)]
    [InlineData(HttpStatusCode.BadRequest, ErrorKind.Permanent)]
    public async Task HataSiniflandirmasi(HttpStatusCode status, ErrorKind expected)
    {
        var provider = new DuckDuckGoProvider(new HttpClient(new Stub("hata", status)));

        var result = await provider.SearchAsync(
            new SearchQuery { Text = "x" }, ProviderContext.ForTest(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(expected, result.Error.Kind);
    }

    /// Uç nokta sorgu anlaşılmadığında HTML dönebiliyor.
    [Fact]
    public async Task HtmlCevap_GeciciHata()
    {
        var provider = new DuckDuckGoProvider(new HttpClient(new Stub("<html>hata</html>")));

        var result = await provider.SearchAsync(
            new SearchQuery { Text = "x" }, ProviderContext.ForTest(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("ddg.bad_json", result.Error.Code);
        Assert.Equal(ErrorKind.Transient, result.Error.Kind);
    }
}
