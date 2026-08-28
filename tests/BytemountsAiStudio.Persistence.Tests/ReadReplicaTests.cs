using BytemountsAiStudio.Persistence;
using BytemountsAiStudio.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BytemountsAiStudio.Persistence.Tests;

/// Okuma replikası yönlendirmesi (P4-06).
///
/// REPLİKA BİR OPTİMİZASYON, DOĞRULUK KOŞULU DEĞİL. Testlerin çoğu
/// bunu koruyor: replikası olmayan bir kurulumda her şey aynen
/// çalışmalı, yalnızca okumalar birincile gitmeli.
public sealed class ReadReplicaTests : IDisposable
{
    private readonly string? _original =
        Environment.GetEnvironmentVariable("BMAI_CONNECTION_READ");

    public void Dispose()
        => Environment.SetEnvironmentVariable("BMAI_CONNECTION_READ", _original);

    private const string Write = "Host=birincil;Port=5432;Database=bmai;Username=bmai;Password=x";
    private const string Read = "Host=replika;Port=5433;Database=bmai;Username=bmai;Password=x";

    /// AYAR YOKSA OKUMA DA BİRİNCİLE GİDİYOR.
    ///
    /// Kayıt YAPILMAMASI seçenek değildi: o zaman `ReadOnlyDbContext`
    /// isteyen her uç, replikası olmayan bir kurulumda açılışta
    /// çözülemeyen bağımlılık hatası verirdi.
    [Fact]
    public void AyarYok_BirincileDusuyor()
    {
        Environment.SetEnvironmentVariable("BMAI_CONNECTION_READ", null);

        Assert.Equal(Write, PersistenceRegistration.ReadConnectionString(Write));
        Assert.False(PersistenceRegistration.UsesReadReplica);
    }

    /// AYAR VARSA REPLİKAYA GİDİYOR.
    [Fact]
    public void AyarVar_ReplikayaGidiyor()
    {
        Environment.SetEnvironmentVariable("BMAI_CONNECTION_READ", Read);

        Assert.Equal(Read, PersistenceRegistration.ReadConnectionString(Write));
        Assert.True(PersistenceRegistration.UsesReadReplica);
    }

    /// İKİ BAĞLAM DA KAYITLI VE FARKLI SUNUCULARA BAKIYOR.
    ///
    /// Aynı sunucuya bakıyorlarsa yönlendirme yapılmamış demektir ve
    /// panelin ağır sorguları üretim döngüsüyle aynı veritabanında
    /// koşmaya devam ediyordur — yani özellik yazılmış ama
    /// bağlanmamış.
    [Fact]
    public void IkiBaglam_FarkliSunucularaBakiyor()
    {
        Environment.SetEnvironmentVariable("BMAI_CONNECTION_READ", Read);

        var services = new ServiceCollection();
        services.AddStudioPersistence(Write);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var write = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
        var read = scope.ServiceProvider.GetRequiredService<ReadOnlyDbContext>();

        Assert.Contains("birincil", write.Database.GetConnectionString(), StringComparison.Ordinal);
        Assert.Contains("replika", read.Database.GetConnectionString(), StringComparison.Ordinal);
    }

    /// OKUMA BAĞLAMI DEĞİŞİKLİK İZLEMİYOR.
    ///
    /// Panelin varlık gezgini tek sorguda bin satır okuyabiliyor;
    /// izleme, okunan her satır için gereksiz bellek ve karşılaştırma
    /// demek.
    [Fact]
    public void OkumaBaglami_DegisiklikIzlemiyor()
    {
        Environment.SetEnvironmentVariable("BMAI_CONNECTION_READ", Read);

        var services = new ServiceCollection();
        services.AddStudioPersistence(Write);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var read = scope.ServiceProvider.GetRequiredService<ReadOnlyDbContext>();

        Assert.Equal(QueryTrackingBehavior.NoTracking,
            read.ChangeTracker.QueryTrackingBehavior);
    }

    /// YAZMA DENEMESİ NEREYE BAKILACAĞINI SÖYLÜYOR.
    ///
    /// Replika `SaveChanges`'i zaten reddediyor ama o hata sunucudan
    /// geliyor ("cannot execute INSERT in a read-only transaction") ve
    /// yanlış BAĞLAMI kullandığını söylemiyor.
    [Fact]
    public void OkumaBaglaminaYazma_AnlasilirHata()
    {
        Environment.SetEnvironmentVariable("BMAI_CONNECTION_READ", Read);

        var services = new ServiceCollection();
        services.AddStudioPersistence(Write);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var read = scope.ServiceProvider.GetRequiredService<ReadOnlyDbContext>();

        var error = Assert.Throws<InvalidOperationException>(() => read.SaveChanges());

        Assert.Contains("StudioDbContext", error.Message, StringComparison.Ordinal);
    }

    /// OKUMA BAĞLAMI `StudioDbContext` YERİNE GEÇEBİLİYOR.
    ///
    /// Panel sorguları `StudioDbContext` alıyor ve DEĞİŞMEDEN
    /// replikaya yönlendirilebilmeleri bunun sayesinde. Ayrı bir
    /// DbSet arayüzü çıkarmak, aynı sorguların iki biçimde yazılması
    /// demekti — ve ikisi zamanla ayrışırdı.
    [Fact]
    public void OkumaBaglami_StudioDbContextYerineGeciyor()
        => Assert.True(typeof(StudioDbContext).IsAssignableFrom(typeof(ReadOnlyDbContext)));
}
