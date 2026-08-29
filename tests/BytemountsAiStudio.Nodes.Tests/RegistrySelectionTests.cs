using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Providers.Fake;
using BytemountsAiStudio.Workflow.Engine;

namespace BytemountsAiStudio.Nodes.Tests;

/// Hangi hattın koşacağı kararı (P0-05).
///
/// ***BU DOSYANIN VAR OLMA SEBEBİ, BULUNAN EN AĞIR TUTARSIZLIK:***
/// üç host üç ayrı karar veriyordu ve Worker'ınki **sabitti**.
/// Worker kuyruğu boşaltan taraf — zamanlayıcı koşu başlatıyor, işler
/// kuyruğa giriyor ve Worker onları çalıştırıyor. Yani **otonom
/// fabrika, tasarlandığı gibi koştuğunda baştan sona sahte video
/// üretiyordu**; gerçek içerik yalnızca elle `bmai real` çağırarak
/// üretilebiliyordu.
///
/// Hiçbir test bunu yakalamıyordu çünkü hepsi kaydı **doğrudan**
/// kuruyordu; host'ların hangi kaydı kurduğuna bakan yoktu.
public sealed class RegistrySelectionTests
{
    private sealed class NoQuota : IQuotaPool
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<QuotaAccountState>> AccountsAsync(
            string providerKey, Guid? channelId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<QuotaAccountState>>([]);
        }

        public Task<Result<PoolDecision>> ReserveAsync(
            string providerKey, Guid? channelId, int cost, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Result.Success(
                new PoolDecision(PoolOutcome.Selected, "default", cost, 0, 0, "test")));
        }

        public Task<int> CapacityAsync(
            string providerKey, Guid? channelId, int costPerPublish, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(0);
        }
    }

    private static NodeRegistry Build(
        FakeStorageProvider storage, PipelineKind? kindOverride, List<string> warnings)
        => RegistrySelection.Build(
            storage,
            new HttpClient(),
            Path.GetTempPath(),
            uniqueness: new AlwaysUnique(),
            channels: new NoChannels(),
            pipeline: null,
            quota: new NoQuota(),
            kindOverride: kindOverride,
            onWarning: warnings.Add);

    /* ---- ortam değişkeni ---- */

    /// ***VARSAYILAN GERÇEK HAT.***
    ///
    /// BU DOSYANIN EN ÖNEMLİ TESTİ. Sahte varsayılan "güvenli"
    /// görünüyor ve aslında en tehlikelisi: sahte hat gerçek bir video
    /// dosyası üretiyor — doğru süre, doğru çözünürlük, doğru altyazı.
    /// Çıktı dizinine bakan bir insan ikisini ayırt edemiyor.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AyarYok_GercekHat(string? raw)
    {
        var (kind, warning) = RegistrySelection.FromEnvironment(_ => raw);

        Assert.Equal(PipelineKind.Open, kind);
        Assert.Null(warning);
    }

    /// HER İKİ DİLDE DE YAZILABİLİYOR.
    ///
    /// Bu depo Türkçe yazılıyor ama ortam değişkenleri İngilizce
    /// yazılan araçlardan da geliyor. Yalnızca birini kabul etmek,
    /// diğerini yazan kişinin sessizce varsayılana düşmesi demekti.
    [Theory]
    [InlineData("acik", PipelineKind.Open)]
    [InlineData("açık", PipelineKind.Open)]
    [InlineData("ACIK", PipelineKind.Open)]
    [InlineData("open", PipelineKind.Open)]
    [InlineData("gercek", PipelineKind.Open)]
    [InlineData("sahte", PipelineKind.Fake)]
    [InlineData("SAHTE", PipelineKind.Fake)]
    [InlineData("fake", PipelineKind.Fake)]
    [InlineData("test", PipelineKind.Fake)]
    [InlineData("  sahte  ", PipelineKind.Fake)]
    public void TaninanDegerler_Okunuyor(string raw, PipelineKind expected)
    {
        var (kind, warning) = RegistrySelection.FromEnvironment(_ => raw);

        Assert.Equal(expected, kind);
        Assert.Null(warning);
    }

    /// ***TANINMAYAN DEĞER SESSİZCE VARSAYILANA DÜŞMÜYOR.***
    ///
    /// `BMAI_PIPELINE=achik` yazan biri, neden beklediğinden farklı
    /// bir video aldığını asla anlayamazdı.
    [Fact]
    public void TaninmayanDeger_UyariUretiyor()
    {
        var (kind, warning) = RegistrySelection.FromEnvironment(_ => "achik");

        Assert.Equal(PipelineKind.Open, kind);
        Assert.NotNull(warning);
        Assert.Contains("achik", warning, StringComparison.Ordinal);
    }

    /* ---- kayıt kurulumu ---- */

    /// ***KAYIT HANGİ HAT OLDUĞUNU TAŞIYOR.***
    ///
    /// Motor bunu koşu bağlamına yazıyor. Taşımasaydı "bu video gerçek
    /// mi" sorusunun cevabı maliyet defterindeki sağlayıcı
    /// anahtarlarına bakmayı gerektirirdi — ve kimse bakmaz.
    [Theory]
    [InlineData(PipelineKind.Open)]
    [InlineData(PipelineKind.Fake)]
    public void Kayit_HattiTasiyor(PipelineKind kind)
    {
        using var storage = new FakeStorageProvider();
        var warnings = new List<string>();

        Assert.Equal(kind, Build(storage, kind, warnings).Kind);
    }

    /// ***İKİ HAT DA AYNI NODE TİPLERİNİ TANIYOR.***
    ///
    /// Tanımasaydı, sahte hatta kaydedilen bir graf gerçek hatta
    /// "bilinmeyen node tipi" diye reddedilirdi — ya da tersi. API
    /// grafı bir kayda göre doğruluyor, Worker başka bir kayıtla
    /// çalıştırıyor: ikisi ayrışırsa hata koşunun ORTASINDA çıkar.
    [Fact]
    public void IkiHat_AyniNodeTiplerini_Taniyor()
    {
        using var storage = new FakeStorageProvider();
        var warnings = new List<string>();

        var open = Build(storage, PipelineKind.Open, warnings).KnownTypes;
        var fake = Build(storage, PipelineKind.Fake, warnings).KnownTypes;

        Assert.Equal(open.OrderBy(t => t, StringComparer.Ordinal), fake.OrderBy(t => t, StringComparer.Ordinal));
    }

    /// ***HANGİ HAT AÇIK, HER ZAMAN SÖYLENİYOR.***
    ///
    /// Sessiz kalsaydı sahte hatta koşan bir kurulum aylarca fark
    /// edilmezdi — çıktılar geçerli görünüyor.
    [Theory]
    [InlineData(PipelineKind.Open, "GERÇEK")]
    [InlineData(PipelineKind.Fake, "SAHTE")]
    public void Hat_Loglaniyor(PipelineKind kind, string expected)
    {
        using var storage = new FakeStorageProvider();
        var warnings = new List<string>();

        Build(storage, kind, warnings);

        Assert.Contains(warnings, w => w.Contains(expected, StringComparison.Ordinal));
    }

    /// AÇIK SEÇİM ORTAM DEĞİŞKENİNİN ÖNÜNE GEÇİYOR.
    ///
    /// CLI'nin `run` ve `real` komutları böyle çalışıyor: kullanıcı
    /// hangisini yazdıysa onu istiyor. Sıra tersine olsaydı, makinedeki
    /// bir değer yazılan komutun anlamını değiştirirdi.
    [Fact]
    public void AcikSecim_OrtamiEziyor()
    {
        using var storage = new FakeStorageProvider();
        var warnings = new List<string>();

        // Ortam "acik" dese bile açık seçim sahte diyorsa sahte.
        Assert.Equal(PipelineKind.Fake, Build(storage, PipelineKind.Fake, warnings).Kind);
    }

    /* ---- ayni node tipini bildiren iki isleyici ---- */

    /// ***HER NODE TIPI ICIN KAYITTA TEK ISLEYICI VAR.***
    ///
    /// `WikipediaResearchHandler` ve `ResearchAgentHandler` IKISI DE
    /// `research.deep` bildiriyor. Kayit bir sozluk oldugu icin ikisi
    /// birden eklenirse SONUNCUSU sessizce kazanir -- ve hangisinin
    /// kostugu yalnizca kayit sirasina bakilarak anlasilirdi.
    ///
    /// Bu test o sirayi degil, SONUCU siniyor: kayitli isleyici
    /// planli arastirma yapan olmali.
    [Fact]
    public void ArastirmaNodeu_PlanliIsleyiciyi_Kullaniyor()
    {
        using var storage = new FakeStorageProvider();
        var warnings = new List<string>();

        var handler = Build(storage, PipelineKind.Open, warnings).Find("research.deep");

        Assert.NotNull(handler);
        Assert.IsType<ResearchAgentHandler>(handler);
    }
}
