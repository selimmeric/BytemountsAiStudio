using System.Text.Json;
using BytemountsAiStudio.Workflow.Expressions;

namespace BytemountsAiStudio.Workflow.Tests;

/// İfade dilinin testleri.
///
/// En önemlileri sondaki güvenlik testleri: bu dil KASITLI OLARAK zayıf ve
/// öyle kalmalı. §19.1/R7 — kendi engine'ini yazan projelerin çoğu farkında
/// olmadan bir programlama dili yazmaya başlar. Bu testler o kaymayı
/// mekanik olarak engelliyor.
public sealed class ExpressionTests
{
    private static readonly JsonElement Context = JsonDocument.Parse("""
        {
          "qc":      { "passed": true, "score": 82, "retry_target": "render" },
          "script":  { "sections": 10, "language": "tr-TR" },
          "channel": { "mode": "approval" },
          "empty":   null
        }
        """).RootElement;

    private static bool Eval(string expression)
    {
        var parsed = ExpressionParser.TryParse(expression);
        Assert.True(parsed.IsSuccess, parsed.IsFailure ? parsed.Error.Message : string.Empty);
        return parsed.Value.EvaluateAsBoolean(Context);
    }

    [Theory]
    [InlineData("qc.passed", true)]
    [InlineData("!qc.passed", false)]
    [InlineData("qc.score > 80", true)]
    [InlineData("qc.score > 90", false)]
    [InlineData("qc.score >= 82", true)]
    [InlineData("qc.score < 82", false)]
    [InlineData("qc.retry_target == 'render'", true)]
    [InlineData("qc.retry_target != 'render'", false)]
    [InlineData("channel.mode == 'approval' && qc.passed", true)]
    [InlineData("channel.mode == 'auto' || qc.passed", true)]
    [InlineData("channel.mode == 'auto' && qc.passed", false)]
    [InlineData("(qc.score > 90 || script.sections == 10) && qc.passed", true)]
    public void TemelIfadeler_DogruDegerlendirilir(string expression, bool expected)
        => Assert.Equal(expected, Eval(expression));

    [Fact]
    public void OlmayanYol_NullDonerPatlamaz()
    {
        // Workflow'un ilk node'unda henüz var olmayan çıktılara referans normal.
        Assert.False(Eval("hicyok.filan"));
        Assert.False(Eval("qc.olmayan_alan"));
    }

    [Fact]
    public void SayiVeMetin_KarsilastirmadaUyusur()
    {
        // `qc.score == "82"` ile `qc.score == 82` aynı sonucu vermeli;
        // aksi hâlde JSON'da tipin nasıl yazıldığı davranışı değiştirirdi.
        Assert.True(Eval("qc.score == 82"));
        Assert.True(Eval("qc.score == '82'"));
    }

    [Fact]
    public void MetinlerdeSiralamaKarsilastirmasi_YapilmazFalseDoner()
    {
        // Kültüre bağlı sıralama sürprizlerinden kaçınmak için ("i" < "I"?).
        Assert.False(Eval("channel.mode > 'a'"));
    }

    [Theory]
    [InlineData("qc.passed &&")]
    [InlineData("(qc.passed")]
    [InlineData("== 5")]
    [InlineData("")]
    [InlineData("   ")]
    public void BozukIfade_Reddedilir(string expression)
        => Assert.True(ExpressionParser.TryParse(expression).IsFailure);

    // ---- dilin sınırları: bunlar GEÇMEMELİ ----

    [Theory]
    [InlineData("System.IO.File.Delete('x')")]        // metot çağrısı
    [InlineData("qc.passed; drop table jobs")]        // ifade ayırıcı
    [InlineData("qc.score + 1 > 80")]                 // aritmetik
    [InlineData("$(rm -rf /)")]                       // kabuk enjeksiyonu
    [InlineData("qc.passed = false")]                 // atama
    [InlineData("`whoami`")]                          // komut ikamesi
    [InlineData("qc.score++")]                        // artırma
    public void KodCalistirmaDenemeleri_Reddedilir(string expression)
    {
        // Bu testler dilin BÜYÜMESİNİ engelliyor. Biri "şuraya küçük bir
        // fonksiyon eklesek" derse, önce buradan geçmesi gerekecek.
        var parsed = ExpressionParser.TryParse(expression);

        Assert.True(parsed.IsFailure,
            $"'{expression}' kabul edildi — ifade dili genişlemiş olabilir.");
    }

    [Fact]
    public void FonksiyonCagrisi_ParantezOlsaBileReddedilir()
    {
        // `len(x)` — parantez destekleniyor ama çağrı olarak DEĞİL.
        Assert.True(ExpressionParser.TryParse("len(qc.score) > 2").IsFailure);
    }

    [Fact]
    public void CokDerinIfade_YiginiTasirmaz()
    {
        // Kötü niyetli ya da kazara üretilmiş derin ifade süreç çökertmemeli.
        var deep = string.Concat(Enumerable.Repeat("(", 200))
                   + "qc.passed"
                   + string.Concat(Enumerable.Repeat(")", 200));

        var parsed = ExpressionParser.TryParse(deep);

        // Ayrıştırılsa da ayrıştırılmasa da patlamamalı.
        Assert.True(parsed.IsSuccess || parsed.IsFailure);
    }
}
