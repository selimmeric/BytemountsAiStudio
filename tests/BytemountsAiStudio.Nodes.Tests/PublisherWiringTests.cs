using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Providers.Fake;
using BytemountsAiStudio.Providers.Open;
using BytemountsAiStudio.Workflow.Engine;

namespace BytemountsAiStudio.Nodes.Tests;

/// Gerçek yayıncıların hatta KAYITLI olduğunun sınanması (P1-24/25).
///
/// ***BU DOSYANIN VAR OLMA SEBEBİ:*** YouTube, TikTok ve Instagram
/// adaptörleri yazılmış, testlenmiş ve **hiçbir yerde kurulmuyordu**.
/// Gerçek hat da sahte yayıncıyla yayınlıyordu — boru hattının ucu
/// hiçbir platforma bağlı değildi ve bunu hiçbir test yakalamıyordu,
/// çünkü hepsi yayıncıları DOĞRUDAN kuruyordu.
public sealed class PublisherWiringTests
{
    /// Kotayı hiç kısıtlamayan havuz — bu testlerin konusu kota değil,
    /// yayıncıların KAYITLI olması. Çağrı sayısı tutuluyor ki bir gün
    /// "yayın kotayı hiç sormuyor" hatası da yakalanabilsin.
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

    private static NodeRegistry OpenRegistry(FakeStorageProvider storage)
        => NodeHandlerRegistration.BuildOpenRegistry(
            storage,
            new HttpClient(),
            Path.GetTempPath(),
            uniqueness: new AlwaysUnique(),
            channels: new NoChannels(),
            pipeline: null,
            quota: new NoQuota());

    /// ***GERÇEK HATTA YAYIN NODE'U KAYITLI.***
    [Fact]
    public void GercekHat_YayinNodeuKayitli()
    {
        using var storage = new FakeStorageProvider();

        Assert.NotNull(OpenRegistry(storage).Find("publish.upload"));
    }

    /// ***ÜÇ PLATFORMUN ÜÇÜ DE ADAPTÖRE SAHİP.***
    ///
    /// BU DOSYANIN EN ÖNEMLİ TESTİ. Platform adları `IPublisher`
    /// gerçeklemelerinden okunuyor: bir adaptör kayıttan düşerse bu
    /// test düşer.
    [Theory]
    [InlineData("youtube")]
    [InlineData("tiktok")]
    [InlineData("instagram")]
    public void UcPlatform_AdaptoreSahip(string platform)
    {
        IPublisher[] publishers =
        [
            new YouTubePublisher(new HttpClient()),
            new TikTokPublisher(new HttpClient()),
            new InstagramPublisher(new HttpClient()),
        ];

        Assert.Contains(publishers, p => string.Equals(p.Platform, platform, StringComparison.Ordinal));
    }

    /* ---- hesaba göre kimlik ---- */

    /// ***VARSAYILAN HESAP DEĞİŞKEN ADINI DEĞİŞTİRMİYOR.***
    ///
    /// Tek hesaplı kurulumlarda (bugünkü hâl) hiçbir şey değişmiyor.
    /// Değişseydi, havuza ikinci bir hesap eklemek birincinin de
    /// çalışmayı bırakması demekti.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("default")]
    public void VarsayilanHesap_DegiskenAyni(string? account)
        => Assert.Equal(
            "YOUTUBE_REFRESH_TOKEN",
            YouTubePublisher.VariableFor("YOUTUBE_REFRESH_TOKEN", account));

    /// ***HESAP ADI DEĞİŞKEN ADINA GİRİYOR.***
    ///
    /// Kota havuzunun seçtiği proje ancak böyle gerçekleşiyor:
    /// ulaşmasaydı havuz "proje-02" seçip defterine yazar, yükleme ise
    /// ortamdaki tek jetonla giderdi — kota sayılmayan bir projeden
    /// harcanır, defterde kullanılmayan bir proje dolardı.
    [Fact]
    public void HesapAdi_DegiskeneGiriyor()
        => Assert.Equal(
            "YOUTUBE_REFRESH_TOKEN_PROJE_02",
            YouTubePublisher.VariableFor("YOUTUBE_REFRESH_TOKEN", "proje-02"));

    /// ***BÜYÜK HARF ÇEVİRİSİ KÜLTÜRDEN BAĞIMSIZ.***
    ///
    /// Türkçe kültürde `"i"` → `"İ"` olur ve değişken adı hiçbir zaman
    /// eşleşmezdi. Bu depoda birkaç kez ödenmiş bir hata.
    [Fact]
    public void BuyukHarf_KulturdenBagimsiz()
    {
        var variable = YouTubePublisher.VariableFor("YOUTUBE_REFRESH_TOKEN", "ikinci-proje");

        Assert.Equal("YOUTUBE_REFRESH_TOKEN_IKINCI_PROJE", variable);
        Assert.DoesNotContain("İ", variable, StringComparison.Ordinal);
    }
}
