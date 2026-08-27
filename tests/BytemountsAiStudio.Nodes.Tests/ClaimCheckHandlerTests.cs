using System.Text.Json;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Nodes;

namespace BytemountsAiStudio.Nodes.Tests;

/// İddia çıkarma ve doğrulama ayrıştırmasının testleri (P1-10).
///
/// Model çağrılmıyor — sınanan şey model ÇIKTISININ nasıl yorumlandığı,
/// ki asıl kırılgan yer orası. Modelin ne söylediği değil, tanımadığımız
/// bir cevap geldiğinde HANGİ YÖNE yanıldığımız önemli.
public sealed class ClaimCheckHandlerTests
{
    private static Claim Sample(string text = "Göbeklitepe on bir bin yıllıktır.")
        => new() { Text = text, SentenceIndex = 0 };

    private static string Verdict(string verdict, string reason = "gerekce")
        => JsonSerializer.Serialize(new { verdict, reason });

    // ---- Çıkarım ----

    [Fact]
    public void IddiaListesi_Ayristirilir()
    {
        var payload = JsonSerializer.Serialize(new
        {
            claims = new[]
            {
                new { text = "Birinci iddia.", sentence_index = 0 },
                new { text = "İkinci iddia.", sentence_index = 1 },
            },
        });

        var result = ClaimCheckHandler.ParseClaims(payload, sentenceCount: 3);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);
        Assert.Equal(2, result.Value.Count);
        Assert.Equal("Birinci iddia.", result.Value[0].Text);
        Assert.Equal(1, result.Value[1].SentenceIndex);
    }

    /// İddiasız senaryo geçerli: kanca ve kapanış cümleleri olgu
    /// taşımıyor ve taşımaması normal.
    [Fact]
    public void BosIddiaListesi_Gecerli()
    {
        var result = ClaimCheckHandler.ParseClaims("""{"claims":[]}""", 3);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value);
    }

    /// Model uydurma bir indeks verebiliyor. Sınıra sıkıştırılmazsa
    /// hedefli düzeltme var olmayan bir cümleye giderdi.
    [Fact]
    public void UydurmaIndeks_SiniraSikistirilir()
    {
        var payload = JsonSerializer.Serialize(new
        {
            claims = new[]
            {
                new { text = "a", sentence_index = 99 },
                new { text = "b", sentence_index = -5 },
            },
        });

        var claims = ClaimCheckHandler.ParseClaims(payload, sentenceCount: 3).Value;

        Assert.Equal(2, claims[0].SentenceIndex);
        Assert.Equal(0, claims[1].SentenceIndex);
    }

    [Fact]
    public void BosMetinliIddia_Atlanir()
    {
        var payload = JsonSerializer.Serialize(new
        {
            claims = new[]
            {
                new { text = "gecerli", sentence_index = 0 },
                new { text = "   ", sentence_index = 0 },
            },
        });

        Assert.Single(ClaimCheckHandler.ParseClaims(payload, 2).Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ bozuk json")]
    [InlineData("""{"baska_alan":[]}""")]
    public void BozukCikarim_GeciciHata(string? payload)
    {
        var result = ClaimCheckHandler.ParseClaims(payload, 2);

        Assert.True(result.IsFailure);
        Assert.Equal(Core.Errors.ErrorKind.Transient, result.Error.Kind);
    }

    // ---- Doğrulama ----

    [Theory]
    [InlineData("supported", ClaimVerdict.Supported)]
    [InlineData("unsupported", ClaimVerdict.Unsupported)]
    [InlineData("contradicted", ClaimVerdict.Contradicted)]
    [InlineData("SUPPORTED", ClaimVerdict.Supported)]
    [InlineData("  contradicted  ", ClaimVerdict.Contradicted)]
    public void Karar_Ayristirilir(string text, ClaimVerdict expected)
    {
        var result = ClaimCheckHandler.ParseVerdict(Sample(), Verdict(text), "https://kaynak");

        Assert.True(result.IsSuccess);
        Assert.Equal(expected, result.Value.Verdict);
    }

    /// TANINMAYAN karar DESTEKSİZ sayılıyor, desteklenmiş değil.
    ///
    /// Yön önemli: belirsizlikte iyimser davranmak, doğrulanmamış bir
    /// iddianın yayına çıkması demek. Kötümser davranmak yalnızca
    /// gereksiz bir düzeltme turu.
    [Theory]
    [InlineData("belki")]
    [InlineData("")]
    [InlineData("partially_supported")]
    public void TaninmayanKarar_DesteksizSayilir(string text)
    {
        var result = ClaimCheckHandler.ParseVerdict(Sample(), Verdict(text), "https://kaynak");

        Assert.Equal(ClaimVerdict.Unsupported, result.Value.Verdict);
    }

    /// Desteksiz bir iddiaya kaynak atanmamalı: kaynak "bunu şurada
    /// buldum" demek, oysa bulamadık.
    [Fact]
    public void DesteksizIddia_KaynakAlmaz()
    {
        var result = ClaimCheckHandler.ParseVerdict(Sample(), Verdict("unsupported"), "https://kaynak");

        Assert.Null(result.Value.SourceUrl);
    }

    [Fact]
    public void DesteklenenIddia_KaynakAlir()
    {
        var result = ClaimCheckHandler.ParseVerdict(Sample(), Verdict("supported"), "https://kaynak");

        Assert.Equal("https://kaynak", result.Value.SourceUrl);
    }

    /// Bir iddia neden desteklenmedi sorusunun cevabı insan onayı
    /// ekranında gösteriliyor.
    [Fact]
    public void Gerekce_Korunur()
    {
        var result = ClaimCheckHandler.ParseVerdict(
            Sample(), Verdict("contradicted", "kaynak 9500 diyor, iddia 5000"), "https://kaynak");

        Assert.Contains("9500", result.Value.Reason!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{ bozuk")]
    public void BozukKarar_GeciciHata(string? payload)
    {
        var result = ClaimCheckHandler.ParseVerdict(Sample(), payload, "https://kaynak");

        Assert.True(result.IsFailure);
        Assert.Equal(Core.Errors.ErrorKind.Transient, result.Error.Kind);
    }

    [Fact]
    public void Ayristirma_IddiaMetniniKorur()
    {
        var claim = Sample("Özgün iddia metni.");

        var result = ClaimCheckHandler.ParseVerdict(claim, Verdict("supported"), "https://kaynak");

        Assert.Equal("Özgün iddia metni.", result.Value.Text);
        Assert.Equal(claim.SentenceIndex, result.Value.SentenceIndex);
    }
}
