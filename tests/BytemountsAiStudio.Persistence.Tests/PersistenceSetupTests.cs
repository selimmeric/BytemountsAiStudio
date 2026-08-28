using BytemountsAiStudio.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace BytemountsAiStudio.Persistence.Tests;

/// Testlerin kurulumu üretimin kurulumu mu?
///
/// BU SORU BİR SÜRE "HAYIR" CEVABI VERİYORDU ve kimse sormamıştı.
///
/// `AddStudioPersistence` (API + Worker) `EnableRetryOnFailure`
/// açıyordu; `StudioDbContextFactory.Build` (CLI + BÜTÜN TESTLER)
/// açmıyordu. İki kurulum, iki farklı davranış — ve testlerin gördüğü
/// hep kolay olanıydı.
///
/// Bedeli: EF, yeniden deneyen bir yürütme stratejisi altında
/// kullanıcının açtığı transaction'a izin vermiyor ve `WorkflowEngine`
/// başarı yolunda tam olarak onu açıyor. Worker'da her node
/// çalıştırması `InvalidOperationException` atıyordu, yani hiçbir
/// video üretilemezdi. Bin dört yüz test yeşildi.
///
/// Hata kodda değil, İKİ KURULUMUN VAR OLMASINDAYDI. Bu test o
/// ikiliğin geri gelmesini engelliyor.
public sealed class PersistenceSetupTests
{
    private const string Connection =
        "Host=localhost;Port=5432;Database=bmai_setup_test;Username=bmai;Password=bmai_dev";

    /// İKİ YOLDAN KURULAN BAĞLAM AYNI YÜRÜTME STRATEJİSİNİ KULLANIYOR.
    ///
    /// Strateji seçilen tek ayar değil ama AYRIŞMANIN GÖRÜLDÜĞÜ yer
    /// orasıydı: biri `NpgsqlRetryingExecutionStrategy` kuruyor,
    /// diğeri kurmuyordu ve fark yalnızca transaction açan kodda
    /// ortaya çıkıyordu.
    [Fact]
    public void DiVeFabrika_AyniYurutmeStratejisi()
    {
        using var fromFactory = new StudioDbContext(
            StudioDbContextFactory.Build(Connection).Options);

        var services = new ServiceCollection();
        services.AddStudioPersistence(Connection);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var fromDi = scope.ServiceProvider.GetRequiredService<StudioDbContext>();

        Assert.Equal(
            StrategyName(fromFactory),
            StrategyName(fromDi));
    }

    /// VE O STRATEJİ AÇIK TRANSACTION'A İZİN VERİYOR.
    ///
    /// `WorkflowEngine` başarı yolunda çıktıyı, ilerlemeyi ve kuyruk
    /// kaydını TEK transaction'da yazıyor: ikisi ayrı yazılsaydı bir
    /// çökme "node bitti ama ilerleme yazılmadı" bırakırdı.
    ///
    /// Yani transaction vazgeçilebilir değil — o zaman onu imkânsız
    /// kılan strateji de olamaz.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HerIkiKurulum_AcikTransactionaIzinVeriyor(bool viaDi)
    {
        using var db = viaDi ? FromDi() : new StudioDbContext(
            StudioDbContextFactory.Build(Connection).Options);

        var strategy = db.Database.CreateExecutionStrategy();

        Assert.False(
            strategy.RetriesOnFailure,
            "Yeniden deneyen strateji açık transaction'ı imkânsız kılıyor; "
            + "geçici hata kuyruk katmanında karşılanıyor (ADR-011).");
    }

    private static StudioDbContext FromDi()
    {
        var services = new ServiceCollection();
        services.AddStudioPersistence(Connection);

        var provider = services.BuildServiceProvider();

        return provider.GetRequiredService<StudioDbContext>();
    }

    private static string StrategyName(DbContext db)
        => db.Database.CreateExecutionStrategy().GetType().Name;
}
