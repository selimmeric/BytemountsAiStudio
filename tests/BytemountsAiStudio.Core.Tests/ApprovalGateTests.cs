using BytemountsAiStudio.Core.Execution;

namespace BytemountsAiStudio.Core.Tests;

/// Onay kapısı kararının testleri (P1-27).
///
/// Saf ve ayrı: "insana sorulacak mı" kararı bir veritabanı kurulumu
/// gerektirmeden sınanabilmeli. Yanlış bir eşik, gerçek bir koşuda kötü
/// bir videonun yayına girmesiyle öğrenilecek bir şey olmamalı.
public sealed class ApprovalGateTests
{
    [Fact]
    public void OtonomKanal_InsanaSormaz()
    {
        var decision = ApprovalGate.Decide(ChannelMode.Auto, score: 0.1, threshold: 0.8);

        Assert.False(decision.Awaiting);
        Assert.NotEmpty(decision.Reason);
    }

    [Fact]
    public void OnayKipi_HerZamanSorar()
    {
        var decision = ApprovalGate.Decide(ChannelMode.Approval, score: 0.99, threshold: 0.5);

        Assert.True(decision.Awaiting);
    }

    [Fact]
    public void SecmeliKip_EsikUstunuGecirir()
    {
        Assert.False(ApprovalGate.Decide(ChannelMode.Selective, 0.85, 0.80).Awaiting);
    }

    [Fact]
    public void SecmeliKip_EsikAltiniSorar()
    {
        Assert.True(ApprovalGate.Decide(ChannelMode.Selective, 0.55, 0.80).Awaiting);
    }

    /// Eşiğin tam üstünde olmak GEÇMEK demek: `>=`. Aksi hâlde eşik
    /// değerinin kendisi hiçbir zaman geçemezdi ve bu, eşiği okuyan
    /// birinin beklediği şey değil.
    [Fact]
    public void SecmeliKip_EsigeEsitGecer()
    {
        Assert.False(ApprovalGate.Decide(ChannelMode.Selective, 0.80, 0.80).Awaiting);
    }

    /// "Ölçülmedi" ile "iyi" aynı şey DEĞİL. QC hiç koşmadıysa otomatik
    /// geçirmek, kalitesi bilinmeyen bir videoyu kimse görmeden yayına
    /// vermek olurdu.
    [Fact]
    public void SecmeliKip_SkorYoksaSorar()
    {
        var decision = ApprovalGate.Decide(ChannelMode.Selective, score: null, threshold: 0.8);

        Assert.True(decision.Awaiting);
        Assert.Contains("skoru yok", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// Gerekçe HER durumda dolu: otomatik geçilen bir kapıda da "neden
    /// bu videoya kimse bakmadı" sorusunun cevabı olmalı.
    [Theory]
    [InlineData(ChannelMode.Auto)]
    [InlineData(ChannelMode.Approval)]
    [InlineData(ChannelMode.Selective)]
    public void GerekceHerZamanDolu(ChannelMode mode)
    {
        Assert.NotEmpty(ApprovalGate.Decide(mode, 0.7, 0.8).Reason);
    }

    /// Gerekçe SAYILARI taşıyor: "eşiğin altında" tek başına, eşiğin
    /// yanlış ayarlandığını mı yoksa videonun gerçekten kötü mü
    /// olduğunu söylemiyor.
    [Fact]
    public void SecmeliKipGerekcesi_SkorVeEsigiIcerir()
    {
        var reason = ApprovalGate.Decide(ChannelMode.Selective, 0.55, 0.80).Reason;

        Assert.Contains("0.55", reason, StringComparison.Ordinal);
        Assert.Contains("0.8", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("auto", ChannelMode.Auto)]
    [InlineData("AUTO", ChannelMode.Auto)]
    [InlineData(" selective ", ChannelMode.Selective)]
    [InlineData("approval", ChannelMode.Approval)]
    public void KipAdi_Okunur(string text, ChannelMode expected)
    {
        Assert.Equal(expected, ApprovalGate.ParseMode(text));
    }

    /// Tanınmayan bir değer ONAY kipine düşüyor: yapılandırmadaki bir
    /// yazım hatası yüzünden kanalın sessizce tam otonom hâle gelmesi,
    /// tersinden çok daha pahalı bir hata.
    [Theory]
    [InlineData("otomatik")]
    [InlineData("")]
    [InlineData(null)]
    public void TaninmayanKip_OnayaDuser(string? text)
    {
        Assert.Equal(ChannelMode.Approval, ApprovalGate.ParseMode(text));
    }
}
