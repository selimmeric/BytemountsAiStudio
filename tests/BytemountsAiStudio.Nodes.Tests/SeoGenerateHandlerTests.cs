using System.Text.Json;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Nodes;

namespace BytemountsAiStudio.Nodes.Tests;

/// SEO metadata testleri (P1-22).
///
/// Kabul kriteri: "100 karakteri aşan başlık kırpılıyor, upload reddi
/// olmuyor." Model çağrılmıyor — sınanan şey model ÇIKTISININ nasıl
/// sınırlara sığdırıldığı, ki asıl kırılgan yer orası.
public sealed class SeoGenerateHandlerTests
{
    private static string Payload(string title, string description = "açıklama", params string[] tags)
        => JsonSerializer.Serialize(new { title, description, tags });

    private static JsonElement Build(string payload)
    {
        var result = SeoGenerateHandler.Build(payload, "seo.generate@1#test");

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);

        return result.Value;
    }

    private static string Text(JsonElement element, string name)
        => element.GetProperty(name).GetString()!;

    [Fact]
    public void NormalCikti_OlduguGibiGecer()
    {
        var output = Build(Payload("Göbeklitepe: Dünyanın En Eski Tapınağı", "Kısa anlatı.", "tarih", "arkeoloji"));

        Assert.Equal("Göbeklitepe: Dünyanın En Eski Tapınağı", Text(output, "title"));
        Assert.Equal(2, output.GetProperty("tags").GetArrayLength());
        Assert.False(output.GetProperty("title_trimmed").GetBoolean());
    }

    /// ASIL KABUL KRİTERİ: uzun başlık kırpılıyor ve sonuç sınırın
    /// altında kalıyor.
    [Fact]
    public void UzunBaslik_KirpilirVeSiniraSigar()
    {
        var longTitle = string.Join(' ', Enumerable.Repeat("uzunbaslikkelimesi", 20));

        var output = Build(Payload(longTitle));

        Assert.True(Text(output, "title").Length <= PlatformLimits.TitleMaxLength);
        Assert.True(output.GetProperty("title_trimmed").GetBoolean());
    }

    /// Kırpma DEVREYE GİRDİ Mİ kayda geçiyor: sürekli kırpılan bir
    /// kanal, istemin sınırı yeterince baskılamadığını söylüyor.
    [Fact]
    public void KirpmaBayragi_YalnizcaKirpildigindaAcilir()
    {
        Assert.False(Build(Payload("Kısa başlık")).GetProperty("title_trimmed").GetBoolean());
        Assert.True(Build(Payload(new string('a', 200))).GetProperty("title_trimmed").GetBoolean());
    }

    [Fact]
    public void FazlaEtiket_AtilirVeSayisiKaydedilir()
    {
        var tags = Enumerable.Range(0, 40).Select(i => $"etiket{i}-" + new string('x', 30)).ToArray();

        var output = Build(Payload("Başlık", "açıklama", tags));

        Assert.True(output.GetProperty("tags_dropped").GetInt32() > 0);
    }

    /// Kırpma sonrası HİÇBİR sınır ihlali kalmamalı — kalırsa hata yine
    /// upload sırasında görülürdü.
    [Fact]
    public void KirpmaSonrasi_HicIhlalKalmaz()
    {
        var output = Build(Payload(
            new string('a', 500),
            new string('d', 30_000),
            [.. Enumerable.Range(0, 100).Select(i => $"etiket{i}")]));

        var tags = output.GetProperty("tags").EnumerateArray().Select(e => e.GetString()!).ToList();

        Assert.Empty(PlatformLimits.Violations(Text(output, "title"), Text(output, "description"), tags));
    }

    [Fact]
    public void BosBaslik_GeciciHata()
    {
        var result = SeoGenerateHandler.Build(Payload("   "), "x");

        Assert.True(result.IsFailure);
        Assert.Equal("seo.no_title", result.Error.Code);
        Assert.Equal(Core.Errors.ErrorKind.Transient, result.Error.Kind);
    }

    /// Zorunlu araç şemasına rağmen bozuk JSON gelebiliyor; ikinci
    /// deneme genellikle geçerli çıkıyor.
    [Fact]
    public void BozukJson_GeciciHata()
    {
        var result = SeoGenerateHandler.Build("{ bu gecerli json degil", "x");

        Assert.True(result.IsFailure);
        Assert.Equal("seo.bad_json", result.Error.Code);
        Assert.Equal(Core.Errors.ErrorKind.Transient, result.Error.Kind);
    }

    [Fact]
    public void BosCikti_Reddedilir()
    {
        Assert.True(SeoGenerateHandler.Build(null, "x").IsFailure);
        Assert.True(SeoGenerateHandler.Build("   ", "x").IsFailure);
    }

    [Fact]
    public void EtiketAlaniYoksa_BosListeDoner()
    {
        var output = Build(JsonSerializer.Serialize(new { title = "Başlık", description = "x" }));

        Assert.Equal(0, output.GetProperty("tags").GetArrayLength());
    }

    /// İstem damgası çıktıda: "bu metadata hangi istemle üretildi"
    /// sorusu kayıttan cevaplanabilsin (P1-07).
    [Fact]
    public void IstemDamgasi_CiktidaDurur()
    {
        Assert.Equal("seo.generate@1#test", Text(Build(Payload("Başlık")), "prompt"));
    }
}
