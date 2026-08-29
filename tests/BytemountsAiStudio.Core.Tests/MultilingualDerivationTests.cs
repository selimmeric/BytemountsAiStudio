using System.Text.Json;
using BytemountsAiStudio.Core.Content;

namespace BytemountsAiStudio.Core.Tests;

/// Tek bilgi tabanından N dilde içerik (P6-06, §20.7).
///
/// ***ÇEVİRİ TÜREV DEĞİL.*** Türkçe senaryoyu İngilizceye çevirmek,
/// İngilizce kelimelerle Türkçe cümle ritmi üretiyor: açılış cümlesi
/// Türk izleyici için kurulmuş, örnekler Türkiye'den, esprinin
/// çevirisi espri değil. Metin "İngilizce" oluyor ama İngilizce
/// konuşan biri için yazılmamış oluyor — ve bunu ancak izlenme oranı
/// söylüyor.
///
/// Bu testler NEYİN TAŞINDIĞINI ve daha önemlisi NEYİN TAŞINMADIĞINI
/// sabitliyor.
public sealed class MultilingualDerivationTests
{
    private const string Source = """
        {
          "topic": { "topic": "Göbeklitepe", "language": "tr-TR", "score": 82 },
          "research": {
            "sources": [ { "url": "https://ornek.test/1", "title": "Kaynak" } ],
            "facts": [ "on bir bin yıl" ],
            "source_count": 1
          },
          "script": { "sentences": ["Göbeklitepe dünyanın en eski tapınağıdır."] },
          "tts": { "segments": [ { "id": "s1" } ] },
          "timeline": { "timeline_asset": "a://x" },
          "render": { "output_path": "video.mp4" },
          "seo": { "title": "Başlık" },
          "claims": { "verified": 3 },
          "experiments": { "title": { "name": "kol-b" } }
        }
        """;

    private static JsonDocument Derive(string language = "en-US", string? source = null)
    {
        var result = MultilingualDerivation.InitialContext(
            source ?? Source, LanguageTag.Create(language));

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);

        return JsonDocument.Parse(result.Value);
    }

    /* ---- ne taşınıyor ---- */

    /// ARAŞTIRMA AYNEN TAŞINIYOR.
    ///
    /// Kaynaklar dilden bağımsız: bir Wikipedia sayfası Türkçe koşuda
    /// da İngilizce koşuda da aynı sayfa. Yeniden çekmek hem para hem
    /// zaman.
    [Fact]
    public void Arastirma_Tasiniyor()
    {
        using var derived = Derive();

        var research = derived.RootElement.GetProperty("research");

        Assert.Equal(1, research.GetProperty("sources").GetArrayLength());
        Assert.Equal("https://ornek.test/1",
            research.GetProperty("sources")[0].GetProperty("url").GetString());
        Assert.Equal(1, research.GetProperty("facts").GetArrayLength());
    }

    /// KONU AYNI, DİL DEĞİŞİYOR.
    ///
    /// Konu kimliği taşınmazsa iki koşu birbirinin türevi olduğunu
    /// bilmez ve öğrenme döngüsü onları bağımsız sanardı.
    [Fact]
    public void Konu_AyniDilFarkli()
    {
        using var derived = Derive();

        var topic = derived.RootElement.GetProperty("topic");

        Assert.Equal("Göbeklitepe", topic.GetProperty("topic").GetString());
        Assert.Equal("en-US", topic.GetProperty("language").GetString());
        Assert.Equal(82, topic.GetProperty("score").GetInt32());
    }

    /// TÜREV OLDUĞU KAYDA GİRİYOR.
    ///
    /// "Bu video neden araştırma adımı koşmadan başladı" sorusunun
    /// cevabı burada; olmadan atlanan bir adım hata gibi görünürdü.
    [Fact]
    public void TurevKaydi_Yaziliyor()
    {
        using var derived = Derive();

        var derivation = derived.RootElement.GetProperty("derivation");

        Assert.Equal("tr-TR", derivation.GetProperty("from_language").GetString());
        Assert.Equal("en-US", derivation.GetProperty("to_language").GetString());

        // ÇEVİRİ DEĞİL, YENİDEN ÜRETİM — ve kayıtta öyle yazıyor.
        Assert.Equal("regenerated", derivation.GetProperty("method").GetString());
    }

    /* ---- ne taşınmıyor ---- */

    /// ***SENARYO TAŞINMIYOR.***
    ///
    /// BU DOSYANIN EN ÖNEMLİ TESTİ. Senaryo taşınsaydı türev koşu onu
    /// hazır bulur, senaryo adımını atlar ve Türkçe cümlelerle
    /// İngilizce video üretirdi.
    [Fact]
    public void Senaryo_Tasinmiyor()
    {
        using var derived = Derive();

        Assert.False(derived.RootElement.TryGetProperty("script", out _));
    }

    /// ***DOĞRULANMIŞ İDDİALAR TAŞINMIYOR — ve bu bilinçli.***
    ///
    /// Doğrulama bir CÜMLEYE yapılıyor, bir olguya değil: "1453'te
    /// fethedildi" cümlesi doğrulandıysa İngilizce karşılığı henüz
    /// doğrulanmadı. Taşımak, hiç kimsenin okumadığı bir cümleyi
    /// "kaynakla desteklendi" diye işaretlemek olurdu.
    [Fact]
    public void Iddialar_Tasinmiyor()
    {
        using var derived = Derive();

        Assert.False(derived.RootElement.TryGetProperty("claims", out _));
    }

    /// ÜRETİLMİŞ HİÇBİR ÇIKTI TAŞINMIYOR.
    [Theory]
    [InlineData("tts")]
    [InlineData("timeline")]
    [InlineData("render")]
    [InlineData("seo")]
    public void UretilmisCiktilar_Tasinmiyor(string node)
    {
        using var derived = Derive();

        Assert.False(derived.RootElement.TryGetProperty(node, out _));
    }

    /// DENEY KOLU TAŞINMIYOR.
    ///
    /// Kol ataması `run_id`'den deterministik (P5-02); taşınan bir kol,
    /// iki farklı run'ı aynı kolda sayar ve deneyin dengesini bozardı.
    [Fact]
    public void DeneyKolu_Tasinmiyor()
    {
        using var derived = Derive();

        Assert.False(derived.RootElement.TryGetProperty("experiments", out _));
    }

    /// TAŞINMAYANLAR LİSTESİ KODLA TUTARLI.
    ///
    /// Liste belge değil kontrol: `Carries` bu listeyi kullanıyor.
    [Fact]
    public void TasinmayanListesi_KodlaTutarli()
    {
        Assert.False(MultilingualDerivation.Carries("script"));
        Assert.False(MultilingualDerivation.Carries("claims"));
        Assert.True(MultilingualDerivation.Carries("research"));
        Assert.True(MultilingualDerivation.Carries("topic"));
    }

    /* ---- reddedilenler ---- */

    /// ARAŞTIRMASIZ TÜREV YOK.
    ///
    /// Türetmenin tek kazancı araştırmayı yeniden kullanmak; o yoksa
    /// yapılacak şey yeni bir koşu başlatmak, "türev" demek değil.
    [Fact]
    public void ArastirmaYok_Reddediliyor()
    {
        var result = MultilingualDerivation.InitialContext(
            """{"topic":{"topic":"x","language":"tr-TR"}}""",
            LanguageTag.Create("en-US"));

        Assert.True(result.IsFailure);
        Assert.Equal("derivation.no_research", result.Error.Code);
    }

    /// AYNI DİLE TÜREV, TÜREV DEĞİL.
    ///
    /// Tekillik kontrolü kanal+dil kapsamında (§20.5), yani bu ikinci
    /// koşu tekrar sayılmaz ve sessizce AYNI videoyu ikinci kez
    /// üretirdi.
    [Fact]
    public void AyniDil_Reddediliyor()
    {
        var result = MultilingualDerivation.InitialContext(Source, LanguageTag.Create("tr-TR"));

        Assert.True(result.IsFailure);
        Assert.Equal("derivation.same_language", result.Error.Code);
    }

    /// BÜYÜK-KÜÇÜK HARF FARKI AYNI DİLİ GİZLEMİYOR.
    [Fact]
    public void AyniDilFarkliYazim_Reddediliyor()
    {
        var result = MultilingualDerivation.InitialContext(Source, LanguageTag.Create("TR-tr"));

        Assert.True(result.IsFailure);
        Assert.Equal("derivation.same_language", result.Error.Code);
    }

    [Fact]
    public void BozukBaglam_Reddediliyor()
    {
        var result = MultilingualDerivation.InitialContext("{bozuk", LanguageTag.Create("en-US"));

        Assert.True(result.IsFailure);
        Assert.Equal("derivation.bad_context", result.Error.Code);
    }
}
