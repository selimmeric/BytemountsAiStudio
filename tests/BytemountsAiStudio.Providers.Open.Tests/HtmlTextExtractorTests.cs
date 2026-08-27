using BytemountsAiStudio.Providers.Open;

namespace BytemountsAiStudio.Providers.Open.Tests;

/// Ana metin çıkarımının testleri (P1-06).
///
/// Asıl mesele menü ve altbilgi metninin DIŞARIDA kalması: "Gizlilik
/// Politikası" bir olgu değil, ama iddia çıkarıcıya girerse öyle
/// muamele görür ve kaynak güvenilirliği çöker.
public sealed class HtmlTextExtractorTests
{
    [Fact]
    public void Baslik_Okunur()
    {
        Assert.Equal("Göbeklitepe - Vikipedi",
            HtmlTextExtractor.ExtractTitle("<html><head><title>Göbeklitepe - Vikipedi</title></head></html>"));
    }

    [Fact]
    public void BaslikYoksa_BosDoner()
    {
        Assert.Equal(string.Empty, HtmlTextExtractor.ExtractTitle("<html><body>metin</body></html>"));
    }

    [Fact]
    public void BetikVeStil_Atilir()
    {
        var text = HtmlTextExtractor.ExtractMainText("""
            <html><body>
              <script>var gizli = "BU GORUNMEMELI";</script>
              <style>.x { color: red; }</style>
              <p>Gerçek metin burada.</p>
            </body></html>
            """);

        Assert.DoesNotContain("BU GORUNMEMELI", text, StringComparison.Ordinal);
        Assert.DoesNotContain("color", text, StringComparison.Ordinal);
        Assert.Contains("Gerçek metin burada.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void MenuVeAltbilgi_Atilir()
    {
        var text = HtmlTextExtractor.ExtractMainText("""
            <html><body>
              <nav>Anasayfa Hakkimizda Iletisim</nav>
              <p>Göbeklitepe dünyanın bilinen en eski tapınağıdır.</p>
              <footer>Gizlilik Politikasi - Tum haklari saklidir</footer>
            </body></html>
            """);

        Assert.DoesNotContain("Hakkimizda", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Gizlilik Politikasi", text, StringComparison.Ordinal);
        Assert.Contains("en eski tapınağıdır", text, StringComparison.Ordinal);
    }

    /// Sayfanın kendi işaretlediği ana içerik, bizim tahminimizden iyi.
    [Fact]
    public void ArticleVarsa_YalnizcaOAlinir()
    {
        var text = HtmlTextExtractor.ExtractMainText("""
            <html><body>
              <div>Yan sutun: ilgili baglantilar reklam alani</div>
              <article><p>Asil makale metni burada duruyor ve yeterince uzun.</p></article>
              <div>Yorumlar bolumu</div>
            </body></html>
            """);

        Assert.Contains("Asil makale metni", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Yan sutun", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Yorumlar", text, StringComparison.Ordinal);
    }

    [Fact]
    public void MainEtiketi_ArticleYoksaKullanilir()
    {
        var text = HtmlTextExtractor.ExtractMainText(
            "<html><body><div>kenar</div><main><p>ana icerik</p></main></body></html>");

        Assert.Contains("ana icerik", text, StringComparison.Ordinal);
        Assert.DoesNotContain("kenar", text, StringComparison.Ordinal);
    }

    /// İç içe aynı etiketi saymasaydık ilk kapanışta durur ve sayfanın
    /// yarısını yerdik.
    [Fact]
    public void IcIceAyniEtiket_DogruKapanir()
    {
        var text = HtmlTextExtractor.ExtractMainText("""
            <html><body>
              <nav>menu <nav>alt menu</nav> devam</nav>
              <p>Kalmasi gereken metin.</p>
            </body></html>
            """);

        Assert.DoesNotContain("menu", text, StringComparison.Ordinal);
        Assert.DoesNotContain("devam", text, StringComparison.Ordinal);
        Assert.Contains("Kalmasi gereken metin.", text, StringComparison.Ordinal);
    }

    /// `<div>` aranırken `<divider>` bulunmamalı.
    [Fact]
    public void BenzerBaslayanEtiket_Karistirilmaz()
    {
        var text = HtmlTextExtractor.ExtractMainText(
            "<html><body><navbar>bu bir nav degil</navbar><p>metin</p></body></html>");

        Assert.Contains("bu bir nav degil", text, StringComparison.Ordinal);
    }

    [Fact]
    public void HtmlVarliklari_Cozulur()
    {
        var text = HtmlTextExtractor.ExtractMainText("<p>5 &lt; 10 &amp; 10 &gt; 5 &mdash; do&#287;ru</p>");

        Assert.Contains("5 < 10 & 10 > 5", text, StringComparison.Ordinal);
        Assert.Contains("doğru", text, StringComparison.Ordinal);
    }

    /// Blok sınırlarında satır sonu: iki bağımsız cümle yapışmasın,
    /// yoksa cümle bölme adımı yanlış çalışır.
    [Fact]
    public void BloklarArasinda_SatirSonuVar()
    {
        var text = HtmlTextExtractor.ExtractMainText("<p>Birinci cümle.</p><p>İkinci cümle.</p>");

        Assert.DoesNotContain("cümle.İkinci", text, StringComparison.Ordinal);
        Assert.Contains("Birinci cümle.\nİkinci cümle.", text, StringComparison.Ordinal);
    }

    /// Öznitelik değeri içinde `>` geçebiliyor.
    ///
    /// Wikipedia'da gerçek bir sayfa çekilirken görüldü: `data-mw`
    /// özniteliğinde JSON taşınıyor, JSON içinde `>` var, ve basit bir
    /// `IndexOf('>')` etiketi orada kapatıp kalan özniteliği gövde
    /// metniymiş gibi çıktıya sızdırıyordu.
    [Fact]
    public void OznitelikIcindekiBuyuktur_EtiketiKapatmaz()
    {
        var text = HtmlTextExtractor.ExtractMainText(
            """<p data-mw='{"parts":[{"a":"x > y"}]}'>görünen metin</p>""");

        Assert.Equal("görünen metin", text);
    }

    [Fact]
    public void OznitelikIcindekiBuyuktur_BasliktaDaKapatmaz()
    {
        var title = HtmlTextExtractor.ExtractTitle(
            """<html><head><title data-x="a>b">Gerçek Başlık</title></head></html>""");

        Assert.Equal("Gerçek Başlık", title);
    }

    [Fact]
    public void Yorumlar_Atilir()
    {
        var text = HtmlTextExtractor.ExtractMainText("<p>görünen<!-- GIZLI NOT -->metin</p>");

        Assert.DoesNotContain("GIZLI NOT", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FazlaBosluk_Toparlanir()
    {
        var text = HtmlTextExtractor.ExtractMainText("<p>   çok      fazla \n\n\n   boşluk   </p>");

        Assert.Equal("çok fazla boşluk", text);
    }

    [Theory]
    [InlineData("<p>abonelere özel içerik</p>")]
    [InlineData("<div class=\"paywall\">devam</div>")]
    [InlineData("<p>Subscription required</p>")]
    public void OdemeDuvariIsaretleri_Yakalanir(string html)
    {
        Assert.True(HtmlTextExtractor.LooksPaywalled(html, "kisa metin"));
    }

    /// Uzun HTML + çok kısa metin: içeriğin gizlendiğinin klasik
    /// işareti. Yanlış pozitifin bedeli bir kaynağı atlamak; yanlış
    /// negatifin bedeli yarım metinden iddia çıkarmak — ikincisi pahalı.
    [Fact]
    public void UzunHtmlKisaMetin_OdemeDuvariSayilir()
    {
        var html = new string('x', 50_000);

        Assert.True(HtmlTextExtractor.LooksPaywalled(html, "cok kisa"));
        Assert.False(HtmlTextExtractor.LooksPaywalled(html, new string('a', 1000)));
    }

    [Fact]
    public void NormalSayfa_OdemeDuvariSayilmaz()
    {
        Assert.False(HtmlTextExtractor.LooksPaywalled("<p>normal içerik</p>", new string('a', 2000)));
    }
}
