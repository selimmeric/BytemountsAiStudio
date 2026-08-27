using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Media.Planning;

namespace BytemountsAiStudio.Media.Tests;

/// Görsel yönetmen testleri (P1-16).
///
/// Bunlar yazılmadan önce görsel istemi `"{konu} — sahne {n}"` idi ve
/// üretilen kareler cümleyle hiç ilgili değildi. Buradaki testler
/// istemin cümleden BESLENDİĞİNİ sabitliyor.
public sealed class VisualDirectorTests
{
    private static readonly LanguageTag Turkish = LanguageTag.Create("tr-TR");
    private static readonly LanguageTag English = LanguageTag.Create("en-US");

    private static VisualDirection Direct(string sentence, LanguageTag? language = null)
        => VisualDirector.Direct(sentence, "Göbeklitepe", language ?? Turkish, VisualStyle.Documentary, 0);

    [Fact]
    public void AramaSorgusu_CumledekiTasiyiciKelimelerdenKurulur()
    {
        var direction = Direct("Bu tapınak yaklaşık on bir bin yıl önce inşa edildi.");

        Assert.Contains("tapınak", direction.SearchQuery, StringComparison.Ordinal);

        // Bağlaç ve zamir girmiyor: "bu" araması hiçbir şey ifade etmez.
        Assert.DoesNotContain("bu ", direction.SearchQuery, StringComparison.Ordinal);
        Assert.DoesNotContain("önce", direction.SearchQuery, StringComparison.Ordinal);
    }

    /// Stok araması KISA terim istiyor; uzun sorgu hiçbir şey bulmuyor.
    [Fact]
    public void AramaSorgusu_DortTerimiAsmaz()
    {
        var direction = Direct(
            "Arkeologlar Şanlıurfa yakınlarındaki tepede devasa dikilitaşlar kabartmalar ve tapınaklar buldular.");

        Assert.True(direction.SearchQuery.Split(' ').Length <= 4,
            $"Sorgu cok uzun: {direction.SearchQuery}");
    }

    /// AI istemi tam tersine bağlam ve üslup istiyor.
    [Fact]
    public void AiIstemi_KonuUslupVeOlumsuzYonergeIcerir()
    {
        var direction = Direct("Tapınak dikilitaşlardan oluşuyor.");

        Assert.Contains("Göbeklitepe", direction.ImagePrompt, StringComparison.Ordinal);
        Assert.Contains("cinematic", direction.ImagePrompt, StringComparison.Ordinal);
        Assert.Contains("no text", direction.ImagePrompt, StringComparison.Ordinal);
    }

    /// İkisi AYRI olmak zorunda: ihtiyaçları zıt.
    [Fact]
    public void SorguVeIstem_AyniDegil()
    {
        var direction = Direct("Tapınak dikilitaşlardan oluşuyor.");

        Assert.NotEqual(direction.SearchQuery, direction.ImagePrompt);
        Assert.True(direction.ImagePrompt.Length > direction.SearchQuery.Length);
    }

    /// Üretilen görsellerde uydurma yazı çıkması en sık kusur ve o yazı
    /// videoda okunuyor.
    [Fact]
    public void OlumsuzYonergeler_HerZamanVar()
    {
        foreach (var sentence in new[] { "Kısa.", "Uzun bir cümle burada duruyor.", "12345" })
        {
            Assert.Contains("no watermark", Direct(sentence).ImagePrompt, StringComparison.Ordinal);
        }
    }

    /// Boş bir sorgu, sağlayıcıdan rastgele bir kare almak demek olurdu.
    [Fact]
    public void AnlamliKelimeYoksa_KonuyaDusulur()
    {
        var direction = Direct("Bu da bir şey.");

        Assert.Equal("Göbeklitepe", direction.SearchQuery);
    }

    [Fact]
    public void IngilizceDurakKelimeler_DeElenir()
    {
        var direction = VisualDirector.Direct(
            "The temple was built with massive stone pillars.",
            "Antikythera", English, VisualStyle.Documentary, 0);

        Assert.Contains("temple", direction.SearchQuery, StringComparison.Ordinal);
        Assert.DoesNotContain("the ", direction.SearchQuery, StringComparison.Ordinal);
        Assert.DoesNotContain("was", direction.SearchQuery, StringComparison.Ordinal);
    }

    /// Türkçede `I` → `ı`, İngilizcede `I` → `i`. Değişmez kültürle
    /// küçültmek "İSTANBUL"u bozar ve arama sonucu değişir.
    [Fact]
    public void Kucultme_DileDuyarli()
    {
        var turkish = VisualDirector.Keywords("İSTANBUL şehri", Turkish);

        Assert.Contains("istanbul", turkish, StringComparer.Ordinal);
    }

    [Fact]
    public void AyniKelimeIkiKez_TekTerimSayilir()
    {
        var terms = VisualDirector.Keywords("Tapınak tapınak TAPINAK yapısı", Turkish);

        Assert.Equal(2, terms.Count);
    }

    /// Cümlenin başındaki öge genellikle konudur; sıralamayı bozmak
    /// anlamı dağıtırdı.
    [Fact]
    public void TerimSirasi_CumledekiSirayiKorur()
    {
        var terms = VisualDirector.Keywords("Arkeologlar tapınak buldular", Turkish);

        Assert.Equal("arkeologlar", terms[0]);
        Assert.Equal("tapınak", terms[1]);
    }

    /// Aynı senaryo her koşuda aynı görseli üretmeli.
    [Fact]
    public void AyniCumle_AyniYonerge()
    {
        var first = Direct("Tapınak dikilitaşlardan oluşuyor.");
        var second = Direct("Tapınak dikilitaşlardan oluşuyor.");

        Assert.Equal(first.ImagePrompt, second.ImagePrompt);
        Assert.Equal(first.SearchQuery, second.SearchQuery);
        Assert.Equal(first.Seed, second.Seed);
    }

    /// Sahneler birbirinin aynısı olmamalı.
    [Fact]
    public void FarkliSahneler_FarkliTohum()
    {
        var first = VisualDirector.Direct("aynı cümle", "konu", Turkish, VisualStyle.Documentary, 0);
        var second = VisualDirector.Direct("aynı cümle", "konu", Turkish, VisualStyle.Documentary, 1);

        Assert.NotEqual(first.Seed, second.Seed);
    }

    /// Aynı kanalın videoları birbirine benzemeli, farklı kanallarınki
    /// benzememeli (§3).
    [Fact]
    public void Uslup_IstemeYansir()
    {
        var documentary = VisualDirector.Direct("tapınak", "konu", Turkish, VisualStyle.Documentary, 0);
        var illustration = VisualDirector.Direct("tapınak", "konu", Turkish, VisualStyle.Illustration, 0);

        Assert.NotEqual(documentary.ImagePrompt, illustration.ImagePrompt);
        Assert.Contains("illustration", illustration.ImagePrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BilinmeyenUslup_BelgeselVarsayilir()
    {
        Assert.Equal(VisualStyle.Documentary, VisualStyle.Get("boyle-bir-uslup-yok"));
        Assert.Equal(VisualStyle.Documentary, VisualStyle.Get(null));
        Assert.Equal(VisualStyle.Illustration, VisualStyle.Get("illustration"));
    }

    /// AI üretimi yüzler hâlâ güvenilmez; tek bir bozuk yüz videoyu
    /// izlenemez kılıyor.
    [Fact]
    public void BelgeselUslubu_InsanIstemiyor()
    {
        Assert.Contains("no people", VisualStyle.Documentary.PromptSuffix, StringComparison.Ordinal);
    }
}
