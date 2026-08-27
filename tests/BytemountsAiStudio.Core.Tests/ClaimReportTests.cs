using BytemountsAiStudio.Core.Content;

namespace BytemountsAiStudio.Core.Tests;

/// İddia raporunun testleri (P1-10).
///
/// Saf ve ayrı: skor ve karar hesabı model çağırmadan sınanabilsin.
public sealed class ClaimReportTests
{
    private static Claim Claim(ClaimVerdict verdict, int sentence = 0, string text = "iddia")
        => new() { Text = text, SentenceIndex = sentence, Verdict = verdict };

    private static ClaimReport Report(params Claim[] claims) => new() { Claims = claims };

    [Fact]
    public void HepsiDestekli_KaynakliSayilir()
    {
        var report = Report(
            Claim(ClaimVerdict.Supported),
            Claim(ClaimVerdict.Supported));

        Assert.True(report.AllSourced);
        Assert.Equal(2, report.Supported);
        Assert.False(report.HasContradiction);
    }

    /// İddiasız senaryo geçerli sayılıyor.
    ///
    /// "Bu konu bugün hâlâ tartışılıyor" gibi bir kapanış cümlesi olgu
    /// iddiası taşımıyor. Sıfır iddiayı başarısız saymak, kanca ve
    /// kapanış cümlelerini yasaklamak olurdu.
    [Fact]
    public void HicIddiaYok_KaynakliSayilir()
    {
        var report = Report();

        Assert.True(report.AllSourced);
        Assert.Equal(0, report.Total);
    }

    [Fact]
    public void TekDesteksiz_KaynakliSayilmaz()
    {
        var report = Report(
            Claim(ClaimVerdict.Supported),
            Claim(ClaimVerdict.Unsupported));

        Assert.False(report.AllSourced);
        Assert.Equal(1, report.Unsupported);
    }

    /// ÇELİŞEN iddia, desteklenmemekten AYRI: desteklenmeyen "kaynağımız
    /// yetersiz" demek, çelişen "kaynağımız bunun yanlış olduğunu
    /// söylüyor" demek. İkincisi doğruluk sorunu.
    [Fact]
    public void Celiski_AyriIsaretlenir()
    {
        var report = Report(
            Claim(ClaimVerdict.Supported),
            Claim(ClaimVerdict.Contradicted));

        Assert.True(report.HasContradiction);
        Assert.False(report.AllSourced);
        Assert.Equal(1, report.Contradicted);
        Assert.Equal(0, report.Unsupported);
    }

    [Fact]
    public void DesteksizVarAmaCelistiYok_CeliskiIsaretlenmez()
    {
        var report = Report(Claim(ClaimVerdict.Unsupported));

        Assert.False(report.HasContradiction);
        Assert.False(report.AllSourced);
    }

    /// Hedefli düzeltme (P2-07) sorunlu cümlelere bakacak; liste
    /// tekrarsız ve sıralı olmalı.
    [Fact]
    public void SorunluCumleler_TekrarsizVeSirali()
    {
        var report = Report(
            Claim(ClaimVerdict.Unsupported, sentence: 2),
            Claim(ClaimVerdict.Contradicted, sentence: 0),
            Claim(ClaimVerdict.Unsupported, sentence: 2),
            Claim(ClaimVerdict.Supported, sentence: 1));

        Assert.Equal([0, 2], report.ProblemSentences);
    }

    [Fact]
    public void HepsiDestekli_SorunluCumleYok()
    {
        var report = Report(
            Claim(ClaimVerdict.Supported, sentence: 0),
            Claim(ClaimVerdict.Supported, sentence: 1));

        Assert.Empty(report.ProblemSentences);
    }

    [Fact]
    public void Ozet_SayilariIcerir()
    {
        var report = Report(
            Claim(ClaimVerdict.Supported),
            Claim(ClaimVerdict.Unsupported),
            Claim(ClaimVerdict.Contradicted));

        var text = report.ToString();

        Assert.Contains("1/3", text, StringComparison.Ordinal);
        Assert.Contains("1 desteksiz", text, StringComparison.Ordinal);
        Assert.Contains("1 celiskili", text, StringComparison.Ordinal);
    }
}
