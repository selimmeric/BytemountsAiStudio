using System.Text.Json;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Learning;

namespace BytemountsAiStudio.Core.Tests;

/// Kapak ve başlık varyantları (P5-03).
///
/// BU DOSYANIN KORUDUĞU TEK ŞEY: iki kolun GERÇEKTEN farklı olması.
///
/// Bir deneyin başarısız olmasının en sinsi yolu, kolların aynı
/// çıktıyı üretmesi. O zaman deney koşar, veri toplar, örneklemi
/// doldurur ve "fark yok" der. Cümle doğrudur — ama ölçülen şey
/// varyant değil, hiçbir şeydir. Aşağıdaki testlerin çoğu tam olarak
/// bu sessiz başarısızlığı arıyor.
public sealed class ContentVariantTests
{
    /* ---- kapalı sözlük ---- */

    /// TANINMAYAN AYAR SESSİZCE DÜŞMÜYOR.
    ///
    /// Yazım hatası ("konumu") sessizce yok sayılsaydı iki kol aynı
    /// kapağı üretirdi ve deney haftalarca hiçbir şey ölçmezdi.
    [Fact]
    public void TaninmayanAyar_Reddediliyor()
    {
        var result = ThumbnailVariant.Parse("""{"konumu":"alt"}""");

        Assert.True(result.IsFailure);
        Assert.Equal("variant.unknown_key", result.Error.Code);

        // Tanımlı anahtarlar da yazılıyor: hatayı gören kişi doğrusunu
        // aramak zorunda kalmasın.
        Assert.Contains("konum", result.Error.Message, StringComparison.Ordinal);
    }

    /// TANINMAYAN DEĞER DE REDDEDİLİYOR.
    ///
    /// Anahtar doğru ama değer yanlışsa varsayılana düşmek, kolu
    /// sessizce kontrole çevirmek olurdu.
    [Fact]
    public void TaninmayanDeger_Reddediliyor()
    {
        var result = ThumbnailVariant.Parse("""{"konum":"sag"}""");

        Assert.True(result.IsFailure);
        Assert.Equal("variant.unknown_value", result.Error.Code);
        Assert.Contains("alt", result.Error.Message, StringComparison.Ordinal);
    }

    /// BOŞ AYAR = BUGÜNKÜ KAPAK.
    ///
    /// Kontrol kolu ile "hiç deney yok" hâli AYNI kapağı üretmeli;
    /// üretmeseydi deneyin karşılaştırdığı taban, kanalın gerçek
    /// tabanı olmazdı.
    [Fact]
    public void BosAyar_VarsayilanKapak()
    {
        var result = ThumbnailVariant.Parse("{}");

        Assert.True(result.IsSuccess);
        Assert.Equal(ThumbnailVariantSettings.Default, result.Value);
    }

    [Fact]
    public void BozukJson_Reddediliyor()
    {
        var result = ThumbnailVariant.Parse("{bozuk");

        Assert.True(result.IsFailure);
        Assert.Equal("variant.bad_json", result.Error.Code);
    }

    /// HER AYAR GERÇEKTEN FARKLI BİR DEĞER ÜRETİYOR.
    ///
    /// Sözlükte duran ama hiçbir şeyi değiştirmeyen bir seçenek,
    /// denenebilir görünen ölü bir koldur.
    [Theory]
    [InlineData("""{"konum":"alt"}""")]
    [InlineData("""{"harf":"buyuk"}""")]
    [InlineData("""{"karartma":"agir"}""")]
    [InlineData("""{"karartma":"hafif"}""")]
    [InlineData("""{"punto":"buyuk"}""")]
    public void HerSecenek_VarsayilandanFarkli(string config)
    {
        var result = ThumbnailVariant.Parse(config);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);
        Assert.NotEqual(ThumbnailVariantSettings.Default, result.Value);
    }

    /* ---- Türkçe büyük harf ---- */

    /// BÜYÜK HARF DİLE DUYARLI.
    ///
    /// `ToUpperInvariant` Türkçe'de "istanbul"u "ISTANBUL" yapıyor;
    /// doğrusu "İSTANBUL". Kapak, kanalın en çok görülen tek görseli:
    /// oradaki noktasız İ, o kanalın Türkçe yazamadığını söylüyor.
    [Fact]
    public void TurkceBuyukHarf_NoktaliI()
    {
        var upper = ThumbnailVariant.ApplyCase(
            "istanbul ışıkları", LanguageTag.Create("tr-TR"), uppercase: true);

        Assert.Equal("İSTANBUL IŞIKLARI", upper);
    }

    /// İNGİLİZCE'DE KURAL FARKLI ve dile göre değişmesi doğru.
    [Fact]
    public void IngilizceBuyukHarf_NoktasizI()
        => Assert.Equal(
            "ISTANBUL",
            ThumbnailVariant.ApplyCase("istanbul", LanguageTag.Create("en-US"), uppercase: true));

    [Fact]
    public void BuyukHarfKapali_MetinDegismiyor()
        => Assert.Equal(
            "istanbul",
            ThumbnailVariant.ApplyCase("istanbul", LanguageTag.Create("tr-TR"), uppercase: false));

    /* ---- başlık stili ---- */

    [Fact]
    public void BaslikStili_Okunuyor()
        => Assert.Equal("soru", TitleVariant.Parse("""{"stil":"soru"}""").Value);

    [Fact]
    public void BaslikStiliYok_Duz()
        => Assert.Equal(TitleVariant.DefaultStyle, TitleVariant.Parse("{}").Value);

    [Fact]
    public void TaninmayanStil_Reddediliyor()
    {
        var result = TitleVariant.Parse("""{"stil":"bagir"}""");

        Assert.True(result.IsFailure);
        Assert.Equal("variant.unknown_value", result.Error.Code);
    }

    /// STİLİN İSTEME GERÇEKTEN GİRDİĞİ DOĞRULANIYOR.
    ///
    /// İstem şablonu, kendisinde olmayan yer tutuculara verilen
    /// değerleri SESSİZCE YUTUYOR (şablonu geziyor, değerleri değil).
    /// Bu kontrol olmadan `{{baslik_stili}}` içermeyen bir istem
    /// sürümüyle koşan deney, iki kolda da AYNI istemi kullanır.
    [Fact]
    public void YerTutucusuzIstem_Reddediliyor()
    {
        var result = TitleVariant.Verify("Sen kısa video metadatası yazıyorsun.");

        Assert.True(result.IsFailure);
        Assert.Equal("variant.placeholder_missing", result.Error.Code);
    }

    [Fact]
    public void YerTutuculuIstem_Geciyor()
        => Assert.True(TitleVariant.Verify("Başlık stili: {{baslik_stili}}").IsSuccess);

    /* ---- boyut sözlüğü ---- */

    [Fact]
    public void BilinmeyenBoyut_Reddediliyor()
    {
        var result = VariantVocabulary.For("renk");

        Assert.True(result.IsFailure);
        Assert.Equal("variant.unknown_dimension", result.Error.Code);
    }

    /// İSTEM BOYUTU DA BAĞLI (P5-05).
    ///
    /// P5-03'te bilerek reddediliyordu: ayarları doğrulanmayan bir
    /// varyantla ölçüm yapmak, sonucu "fark yok" olan bir deney
    /// koşturmak demekti. Şimdi sözlüğü var ve sürümün gerçekten var
    /// olduğu kayıt anında doğrulanıyor.
    [Fact]
    public void IstemBoyutu_Tanimli()
    {
        var result = VariantVocabulary.For("prompt");

        Assert.True(result.IsSuccess);
        Assert.Contains(PromptVariant.VersionField, result.Value.Keys);
    }

    /* ---- kanal varsayılanları (P5-07) ---- */

    /// KAZANAN VARYANT KANAL AYARINDAN OKUNUYOR.
    [Fact]
    public void KanalVarsayilani_Okunuyor()
    {
        var settings = Execution.ChannelSettings.Parse(
            """{"default_variants":{"title":{"stil":"soru"}}}""");

        Assert.Equal("soru", TitleVariant.Parse(settings.DefaultVariants["title"]).Value);

        // Varsayılan hakkında uyarı YOK. (`daily_target` uyarısı
        // ayrı bir eksiklik ve bu testin konusu değil.)
        Assert.DoesNotContain(settings.Warnings,
            w => w.Contains("default_variants", StringComparison.Ordinal));
    }

    /// BOZUK VARSAYILAN SESSİZCE UYGULANMIYOR — ve söyleniyor.
    ///
    /// Doğrulamamak, kazanan varyantı yazarken bir yazım hatası olsa
    /// bile kanalın onu sessizce yok sayması demekti: deney kazanır,
    /// karar yazılır, hiçbir video değişmez.
    [Fact]
    public void BozukVarsayilan_UygulanmiyorVeUyariyor()
    {
        var settings = Execution.ChannelSettings.Parse(
            """{"default_variants":{"title":{"stl":"soru"}}}""");

        Assert.Empty(settings.DefaultVariants);
        Assert.Contains(settings.Warnings, w => w.Contains("stl", StringComparison.Ordinal));
    }

    /// BİLİNMEYEN BOYUT DA UYARIYOR.
    [Fact]
    public void BilinmeyenBoyutVarsayilani_Uyariyor()
    {
        var settings = Execution.ChannelSettings.Parse(
            """{"default_variants":{"renk":{"a":"b"}}}""");

        Assert.Empty(settings.DefaultVariants);
        Assert.Contains(settings.Warnings, w => w.Contains("renk", StringComparison.Ordinal));
    }

    /* ---- run bağlamı köprüsü ---- */

    /// ATAMA RUN BAĞLAMINA YAZILIYOR VE GERİ OKUNUYOR.
    ///
    /// Bu köprü olmadan atama tabloya yazılır, node'lar okumaz ve
    /// deney iki kolda da aynı videoyu üretir.
    [Fact]
    public void Atama_BaglamaYazilipOkunuyor()
    {
        var merged = ExperimentContext.Merge(
            """{"topic":{"topic":"Göbeklitepe"}}""",
            [
                new AssignedVariant(
                    Guid.CreateVersion7(), Guid.CreateVersion7(), "thumbnail", "b-varyant",
                    """{"konum":"alt"}"""),
            ]);

        Assert.True(merged.IsSuccess);

        using var document = JsonDocument.Parse(merged.Value);

        // VAR OLAN BAĞLAM KORUNUYOR: üzerine yazmak, konu bilgisini
        // silip sonraki node'ları kırardı.
        Assert.Equal("Göbeklitepe",
            document.RootElement.GetProperty("topic").GetProperty("topic").GetString());

        var config = ExperimentContext.ConfigFor(document.RootElement, "thumbnail");

        Assert.NotNull(config);
        Assert.Equal(ThumbnailTextPosition.Lower, ThumbnailVariant.Parse(config).Value.Position);
        Assert.Equal("b-varyant", ExperimentContext.VariantName(document.RootElement, "thumbnail"));
    }

    /// DENEYSİZ RUN'DA AYAR YOK — hata da yok.
    ///
    /// Videoların ezici çoğunluğu hiçbir deneye girmiyor; bu normal
    /// işleyiş ve hata gibi davranmak fabrikayı durdururdu.
    [Fact]
    public void DeneysizBaglam_AyarYok()
    {
        using var document = JsonDocument.Parse("""{"topic":{"topic":"x"}}""");

        Assert.Null(ExperimentContext.ConfigFor(document.RootElement, "thumbnail"));
        Assert.Null(ExperimentContext.VariantName(document.RootElement, "thumbnail"));
    }

    [Fact]
    public void BozukBaglam_Reddediliyor()
    {
        var result = ExperimentContext.Merge("{bozuk", []);

        Assert.True(result.IsFailure);
        Assert.Equal("experiment.bad_context", result.Error.Code);
    }
}
