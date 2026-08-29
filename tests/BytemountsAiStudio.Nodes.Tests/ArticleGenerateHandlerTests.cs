using System.Text.Json;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Nodes;
using BytemountsAiStudio.Persistence;
using BytemountsAiStudio.Providers.Fake;
using BytemountsAiStudio.Workflow.Engine;

namespace BytemountsAiStudio.Nodes.Tests;

/// Blog makalesi üretimi (P6-04).
///
/// MAKALE SENARYO DEĞİL. Senaryoyu bir sayfaya yapıştırmak "blog
/// içerik türü eklendi" demek değil: yapıştırılmış senaryo okunduğunda
/// garip ve kaynaksız bir metin oluyor ve garipliği kimse bir hata
/// olarak raporlamıyor.
///
/// Bu testlerin çoğu MODELİN ÇIKTISINI DENETLEYEN kodu sınıyor —
/// isteme "yalnızca verilen kaynakları kullan" yazmak çoğu zaman işe
/// yarıyor; işe yaramadığı sefer uydurulmuş bir atıf yayına giriyor.
/// Her konuyu tekil sayan sahte.
///
/// Testin konusu makale üretimi; tekillik ayrı bir yerde sınanıyor ve
/// buraya veritabanı sokmak, veritabanı olmayan bir makinede bu testin
/// hiç koşmaması demekti.
internal sealed class AlwaysUnique : Contracts.Providers.ITopicUniqueness
{
    public Task<Core.Result<Contracts.Providers.UniquenessVerdict>> CheckAsync(
        Guid? channelId, string language, string title, CancellationToken cancellationToken)
        => Task.FromResult(Core.Result.Success(
            new Contracts.Providers.UniquenessVerdict { IsUnique = true, Method = "test" }));
}

/// Kanalı olmayan koşu: mod ve ayar yok.
internal sealed class NoChannels : Contracts.Providers.IChannelPolicy
{
    public Task<ChannelMode?> ModeAsync(Guid channelId, CancellationToken cancellationToken)
        => Task.FromResult<ChannelMode?>(null);

    public Task<ChannelSettings?> SettingsAsync(Guid channelId, CancellationToken cancellationToken)
        => Task.FromResult<ChannelSettings?>(null);
}

public sealed class ArticleGenerateHandlerTests
{
    private static readonly ArticleSource[] Sources =
    [
        new("https://ornek.test/1", "Birinci kaynak", "alinti"),
        new("https://ornek.test/2", "İkinci kaynak", "alinti"),
    ];

    private static string Article(string body)
        => "# Baslik\n\n" + body + "\n\n## Bolum bir\n\n"
            + string.Join(" ", Enumerable.Repeat("Kaynaga dayali bir cumle daha yaziliyor [1].", 30))
            + "\n\n## Bolum iki\n\n"
            + string.Join(" ", Enumerable.Repeat("Ikinci bolumde de kaynak gosteriliyor [2].", 30));

    private static JsonElement Ok(string markdown)
    {
        var result = ArticleGenerateHandler.Build(markdown, Sources, "article.generate@1#x");

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);

        return result.Value;
    }

    /* ---- biçim denetimi ---- */

    /// GEÇERLİ MAKALE GEÇİYOR VE ÖLÇÜLEN DEĞERLER KAYDA GİRİYOR.
    [Fact]
    public void GecerliMakale_OlculenDegerler()
    {
        var output = Ok(Article("Giris paragrafi tek basina anlasilir [1]."));

        Assert.Equal("Baslik", output.GetProperty("title").GetString());
        Assert.True(output.GetProperty("word_count").GetInt32() > 200);
        Assert.Equal(2, output.GetProperty("heading_count").GetInt32());

        // KULLANILAN kaynak sayısı ayrı: üç kaynak verilip birine atıf
        // yapılmışsa araştırmanın üçte ikisi boşa gitmiş demektir.
        Assert.Equal(2, output.GetProperty("cited_source_count").GetInt32());
    }

    /// BAŞLIKSIZ METİN DUVARI REDDEDİLİYOR.
    ///
    /// Yapıştırılmış senaryonun en belirgin işareti: senaryoda başlık
    /// yok, çünkü kimse sesli bir videoda "İkinci bölüm" diye bir
    /// başlık duymuyor.
    [Fact]
    public void BasliksizMetin_Reddediliyor()
    {
        var wall = string.Join(" ", Enumerable.Repeat("Bu bir cumle [1].", 100));
        var result = ArticleGenerateHandler.Build(wall, Sources, "x");

        Assert.True(result.IsFailure);
        Assert.Equal("article.no_headings", result.Error.Code);
    }

    /// KISA MAKALE REDDEDİLİYOR.
    ///
    /// Kelime sayısı ÖLÇÜLÜYOR, istenen sayıya güvenilmiyor: model
    /// "800 kelime" isteğine 200 kelimeyle cevap verdiğinde ortaya
    /// yayınlanabilir görünen, aslında yarım bir makale çıkıyor.
    [Fact]
    public void KisaMakale_Reddediliyor()
    {
        var result = ArticleGenerateHandler.Build(
            "# Baslik\n\n## Bir\n\nKisa [1].\n\n## Iki\n\nYine kisa [2].", Sources, "x");

        Assert.True(result.IsFailure);
        Assert.Equal("article.too_short", result.Error.Code);
    }

    /// ATIFSIZ MAKALE REDDEDİLİYOR.
    ///
    /// Videoda iddia doğrulama ayrı bir adım; makalede kaynak metnin
    /// İÇİNDE ve yoksa okuyucunun elinde hiçbir şey kalmıyor.
    [Fact]
    public void AtifsizMakale_Reddediliyor()
    {
        var text = "# Baslik\n\n## Bir\n\n"
            + string.Join(" ", Enumerable.Repeat("Atifsiz bir cumle yaziliyor.", 40))
            + "\n\n## Iki\n\n"
            + string.Join(" ", Enumerable.Repeat("Yine atifsiz.", 40));

        var result = ArticleGenerateHandler.Build(text, Sources, "x");

        Assert.True(result.IsFailure);
        Assert.Equal("article.no_citations", result.Error.Code);
    }

    /// ***UYDURULMUŞ ATIF YAKALANIYOR.***
    ///
    /// BU DOSYANIN EN ÖNEMLİ TESTİ. İki kaynak verilip `[7]` yazılması,
    /// modelin var olmayan bir kaynağa dayandığını söylüyor. Sessiz
    /// geçirmek, okuyucunun tıklayacağı bir yeri olmayan bir "kaynak"
    /// göstermek ve makaleyi doğrulanamaz kılmak olurdu.
    [Fact]
    public void UydurulmusAtif_Reddediliyor()
    {
        var result = ArticleGenerateHandler.Build(
            Article("Uydurma kaynak [7]."), Sources, "x");

        Assert.True(result.IsFailure);
        Assert.Equal("article.bad_citation", result.Error.Code);

        // HANGİ atıf olduğu yazılı: "atıf hatalı" tek başına hangisini
        // düzelteceğini söylemiyor.
        Assert.Contains("[7]", result.Error.Message, StringComparison.Ordinal);
    }

    /// SIFIR ATFI DA UYDURMA.
    [Fact]
    public void SifirAtfi_Reddediliyor()
    {
        var result = ArticleGenerateHandler.Build(Article("Sifirinci kaynak [0]."), Sources, "x");

        Assert.True(result.IsFailure);
        Assert.Equal("article.bad_citation", result.Error.Code);
    }

    /// BOŞ ÇIKTI GEÇİCİ HATA.
    [Fact]
    public void BosCikti_GeciciHata()
    {
        var result = ArticleGenerateHandler.Build("   ", Sources, "x");

        Assert.True(result.IsFailure);
        Assert.Equal(Core.Errors.ErrorKind.Transient, result.Error.Kind);
    }

    /* ---- kaynak listesi ---- */

    /// NUMARALANDIRMA BİZDEN, MODELDEN DEĞİL.
    ///
    /// Modelin kendi numaralandırması iki çağrı arasında değişir ve
    /// atıflar kaynaklarla eşleşmezdi.
    [Fact]
    public void Kaynaklar_NumaralandirilmisGidiyor()
    {
        var text = ArticleGenerateHandler.Numbered(Sources);

        Assert.Contains("[1] Birinci kaynak", text, StringComparison.Ordinal);
        Assert.Contains("[2] İkinci kaynak", text, StringComparison.Ordinal);
    }

    /// KAYNAKSIZ KOŞU MAKALE ÜRETMİYOR.
    [Fact]
    public async Task KaynakYok_Reddediliyor()
    {
        using var document = JsonDocument.Parse("""{"topic":{"topic":"x","language":"tr-TR"}}""");

        var result = await new ArticleGenerateHandler(new FakeLlmProvider()).ExecuteAsync(
            new NodeContext
            {
                RunId = Guid.CreateVersion7(),
                NodeId = "article",
                NodeType = "article.generate",
                Attempt = 1,
                Config = JsonDocument.Parse("{}").RootElement.Clone(),
                RunContext = document.RootElement.Clone(),
                IdempotencyKey = "t",
                CorrelationId = "t",
            },
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("article.no_sources", result.Error.Code);
    }

    /* ---- sahte hat uçtan uca ---- */

    /// SAHTE HAT GERÇEK DENETİMDEN GEÇİYOR.
    ///
    /// Sahte modelin çıktısı `Build`'in bütün kurallarına uyuyor.
    /// Denetimi atlatan bir sahte çıktı, denetimin çalışıp
    /// çalışmadığını da gizlerdi.
    [Fact]
    public async Task SahteHat_MakaleUretiyor()
    {
        using var storage = new FakeStorageProvider();

        var registry = NodeHandlerRegistration.BuildFakeRegistry(
            storage,
            Path.GetTempPath(),
            uniqueness: new AlwaysUnique(),
            channels: new NoChannels(),
            // ZİNCİRSİZ: zincir maliyet defterine, defter veritabanına
            // bağlı. Bu test yalnızca makale üretimini sınıyor ve
            // veritabanı istemiyor.
            pipeline: null);

        var handler = registry.Find("article.generate");
        Assert.NotNull(handler);

        var context = """
            {
              "topic": { "topic": "Göbeklitepe", "language": "tr-TR" },
              "research": { "sources": [
                { "url": "https://ornek.test/1", "title": "Kaynak bir", "excerpt": "alinti" },
                { "url": "https://ornek.test/2", "title": "Kaynak iki", "excerpt": "alinti" }
              ] }
            }
            """;

        using var document = JsonDocument.Parse(context);

        var result = await handler!.ExecuteAsync(
            new NodeContext
            {
                RunId = Guid.CreateVersion7(),
                NodeId = "article",
                NodeType = "article.generate",
                Attempt = 1,
                Config = JsonDocument.Parse("{}").RootElement.Clone(),
                RunContext = document.RootElement.Clone(),
                IdempotencyKey = "t",
                CorrelationId = "t",
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);
        Assert.True(result.Value.GetProperty("word_count").GetInt32() >= 200);
        Assert.True(result.Value.GetProperty("heading_count").GetInt32() >= 2);

        // CÜMLELER DE ÇIKIYOR: iddia doğrulama bunları okuyor ve
        // yoksa blog hattında doğrulama sessizce kapanırdı.
        Assert.NotEmpty(result.Value.GetProperty("sentences").EnumerateArray());
    }

    /* ---- graf ---- */

    /// BLOG GRAFINDA VİDEO NODE'U YOK.
    ///
    /// TTS, görsel, timeline ve render'ı "zararsız" diye bırakmak, her
    /// makale için dakikalarca ffmpeg koşturmak olurdu.
    [Fact]
    public void BlogGrafi_VideoNodeuIcermiyor()
    {
        using var graph = JsonDocument.Parse(DatabaseSeeder.BlogGraphJson);

        var types = graph.RootElement.GetProperty("nodes")
            .EnumerateArray()
            .Select(n => n.GetProperty("type").GetString()!)
            .ToList();

        Assert.Contains("article.generate", types);
        Assert.DoesNotContain("tts.synthesize", types);
        Assert.DoesNotContain("media.render", types);
        Assert.DoesNotContain("timeline.compile", types);

        // VE İDDİA DOĞRULAMA VAR: kaynaksız bir makale, kaynaksız bir
        // videodan daha kötü — kaynak metnin içinde görünüyor.
        Assert.Contains("claim.check", types);
    }

    /// BLOG GRAFININ HER NODE'U TANINIYOR.
    [Fact]
    public void BlogGrafi_TumNodelarTanimli()
    {
        using var graph = JsonDocument.Parse(DatabaseSeeder.BlogGraphJson);

        var unknown = graph.RootElement.GetProperty("nodes")
            .EnumerateArray()
            .Select(n => n.GetProperty("type").GetString()!)
            .Where(t => !NodeHandlerRegistration.KnownNodeTypes.Contains(t))
            .ToList();

        Assert.True(unknown.Count == 0, "Tanınmayan: " + string.Join(", ", unknown));
    }
}
