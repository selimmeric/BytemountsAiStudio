using BytemountsAiStudio.Providers.Open;

namespace BytemountsAiStudio.Providers.Open.Tests;

/// robots.txt ayrıştırıcısının testleri (P1-06).
///
/// Bu kuralları yanlış uygulamak iki yönde de pahalı: gevşek olursak
/// yasaklı sayfa çekeriz, sıkı olursak serbest kaynakları kaçırırız.
/// Standardın kenar durumları burada tek tek sabitleniyor.
public sealed class RobotsTxtTests
{
    private static RobotsTxt Parse(string content, string agent = "BytemountsAiStudio")
        => RobotsTxt.Parse(content, agent);

    [Fact]
    public void YasakliYol_Reddedilir()
    {
        var robots = Parse("User-agent: *\nDisallow: /gizli/");

        Assert.False(robots.IsAllowed("/gizli/sayfa"));
        Assert.True(robots.IsAllowed("/acik/sayfa"));
    }

    /// En UZUN eşleşen kural kazanıyor. `Disallow: /` ile
    /// `Allow: /wiki/` yan yana geldiğinde doğru cevap "çekilebilir".
    [Fact]
    public void EnUzunKuralKazanir()
    {
        var robots = Parse("User-agent: *\nDisallow: /\nAllow: /wiki/");

        Assert.True(robots.IsAllowed("/wiki/Göbeklitepe"));
        Assert.False(robots.IsAllowed("/baska"));
    }

    /// Eşit uzunlukta Allow kazanıyor (RFC 9309).
    [Fact]
    public void EsitUzunluktaAllowKazanir()
    {
        var robots = Parse("User-agent: *\nDisallow: /a\nAllow: /a");

        Assert.True(robots.IsAllowed("/a"));
    }

    /// Boş `Disallow:` "hiçbir şey yasak değil" demek. Kural olarak
    /// eklenseydi her yolu yasaklardı — tam tersi anlam.
    [Fact]
    public void BosDisallow_HicbirSeyiYasaklamaz()
    {
        var robots = Parse("User-agent: *\nDisallow:");

        Assert.True(robots.IsAllowed("/herhangi/yol"));
        Assert.Equal(0, robots.RuleCount);
    }

    [Fact]
    public void DisallowSlash_HerSeyiYasaklar()
    {
        var robots = Parse("User-agent: *\nDisallow: /");

        Assert.False(robots.IsAllowed("/"));
        Assert.False(robots.IsAllowed("/herhangi"));
    }

    /// Bize özel konmuş bir grup varsa `*` grubu hiç okunmuyor.
    /// Tersi, bize yazılmış bir yasağı sessizce yok saymak olurdu.
    [Fact]
    public void BizeOzelGrup_YildizGrubunuEzer()
    {
        var robots = Parse("""
            User-agent: *
            Disallow:

            User-agent: BytemountsAiStudio
            Disallow: /
            """);

        Assert.False(robots.IsAllowed("/herhangi"));
    }

    [Fact]
    public void BaskaBotunGrubu_BiziIlgilendirmez()
    {
        var robots = Parse("""
            User-agent: GPTBot
            Disallow: /

            User-agent: *
            Disallow: /admin/
            """);

        Assert.True(robots.IsAllowed("/makale"));
        Assert.False(robots.IsAllowed("/admin/panel"));
    }

    /// Art arda gelen User-agent satırları TEK grup.
    [Fact]
    public void ArtArdaAgentSatirlari_TekGrup()
    {
        var robots = Parse("""
            User-agent: GPTBot
            User-agent: BytemountsAiStudio
            Disallow: /kapali/
            """);

        Assert.False(robots.IsAllowed("/kapali/x"));
    }

    [Fact]
    public void YildizJokeri_Eslesir()
    {
        var robots = Parse("User-agent: *\nDisallow: /*.pdf");

        Assert.False(robots.IsAllowed("/dosyalar/rapor.pdf"));
        Assert.True(robots.IsAllowed("/dosyalar/rapor.html"));
    }

    /// `$` yol sonunu bağlıyor: `/sayfa$` yalnızca tam `/sayfa`.
    [Fact]
    public void DolarIsareti_YolSonunuBaglar()
    {
        var robots = Parse("User-agent: *\nDisallow: /sayfa$");

        Assert.False(robots.IsAllowed("/sayfa"));
        Assert.True(robots.IsAllowed("/sayfa/alt"));
    }

    [Fact]
    public void Yorumlar_Atlanir()
    {
        var robots = Parse("""
            # bu bir yorum
            User-agent: *   # satir ici yorum
            Disallow: /gizli/
            """);

        Assert.False(robots.IsAllowed("/gizli/x"));
    }

    [Fact]
    public void BosDosya_HerSeyeIzinVerir()
    {
        Assert.True(Parse(string.Empty).IsAllowed("/herhangi"));
    }

    /// Sitemap, Crawl-delay gibi tanımadığımız alanlar ayrıştırmayı
    /// bozmamalı.
    [Fact]
    public void BilinmeyenAlanlar_Yoksayilir()
    {
        var robots = Parse("""
            Sitemap: https://ornek.com/sitemap.xml
            User-agent: *
            Crawl-delay: 10
            Disallow: /gizli/
            """);

        Assert.False(robots.IsAllowed("/gizli/x"));
        Assert.True(robots.IsAllowed("/acik"));
    }

    /// Desende geçen düzenli ifade karakterleri düz metin sayılmalı.
    /// Genel bir regex'e çevirseydik ya patlar ya yanlış eşleşirdi.
    [Fact]
    public void DuzenliIfadeKarakterleri_DuzMetinSayilir()
    {
        var robots = Parse("User-agent: *\nDisallow: /ara?q=(x)+");

        Assert.False(robots.IsAllowed("/ara?q=(x)+abc"));
        Assert.True(robots.IsAllowed("/ara?q=x"));
    }

    [Fact]
    public void BuyukKucukHarf_AgentAdindaOnemsiz()
    {
        var robots = RobotsTxt.Parse("User-agent: bytemountsaistudio\nDisallow: /", "BytemountsAiStudio");

        Assert.False(robots.IsAllowed("/x"));
    }

    [Fact]
    public void HazirDegerler_BeklendigiGibi()
    {
        Assert.True(RobotsTxt.AllowAll.IsAllowed("/herhangi"));
        Assert.False(RobotsTxt.DenyAll.IsAllowed("/herhangi"));
    }
}
