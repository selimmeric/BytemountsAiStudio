using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BytemountsAiStudio.Persistence;

public static class PersistenceRegistration
{
    /// Tek kayıt noktası. API, Worker ve CLI aynı kurulumu kullanır; üç yerde
    /// ayrı kurulsaydı biri `UseVector()` ya da snake_case sözleşmesini
    /// atlar ve fark ancak çalışma zamanında ortaya çıkardı.
    ///
    /// BU CÜMLE BİR SÜRE DOĞRU DEĞİLDİ ve bedeli tam olarak anlattığı
    /// şey oldu. Burada `EnableRetryOnFailure` açıktı,
    /// `StudioDbContextFactory.Build` içinde değildi. CLI ve TESTLER
    /// fabrikayı kullanıyor, API ve Worker burayı: yani her test
    /// üretimden farklı bir kurulumla koşuyordu.
    ///
    /// Sonuç: `WorkflowEngine` açık bir transaction açıyor ve EF,
    /// yeniden deneyen bir yürütme stratejisi altında buna izin
    /// vermiyor. Worker'da HER node çalıştırması istisna atıyordu —
    /// hiçbir video üretilemezdi. Bin dört yüz test yeşildi, çünkü
    /// hepsi öteki kurulumu kullanıyordu.
    ///
    /// Artık kurulum GERÇEKTEN tek yerde: burası fabrikayı çağırıyor.
    public static IServiceCollection AddStudioPersistence(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddDbContext<StudioDbContext>(builder =>
            builder.UseNpgsql(connectionString, StudioDbContextFactory.ConfigureNpgsql)
                   .UseSnakeCaseNamingConvention());

        // OKUMA REPLİKASI (P4-06): `BMAI_CONNECTION_READ` doluysa oraya,
        // boşsa BİRİNCİLE bağlanıyor.
        //
        // Replika yoksa kayıt YAPILMAMASI seçenek değildi: o zaman
        // `ReadOnlyDbContext` isteyen her yer, replikası olmayan bir
        // kurulumda açılışta çözülemeyen bağımlılık hatası verirdi.
        // Birincile düşmek, replikanın bir OPTİMİZASYON olduğunu ve
        // doğruluğun ona bağlı olmadığını koda yazıyor.
        var readConnection = ReadConnectionString(connectionString);

        services.AddDbContext<ReadOnlyDbContext>(builder =>
            builder.UseNpgsql(readConnection, StudioDbContextFactory.ConfigureNpgsql)
                   .UseSnakeCaseNamingConvention()
                   // DEĞİŞİKLİK İZLEME KAPALI: bu bağlam yalnızca
                   // okuyor ve izleme, okunan her satır için gereksiz
                   // bellek ve karşılaştırma demek. Panelin varlık
                   // gezgini tek sorguda bin satır okuyabiliyor.
                   .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking));

        return services;
    }

    /// Okuma bağlantısı — tanımlı değilse birincil.
    public static string ReadConnectionString(string writeConnectionString)
        => Environment.GetEnvironmentVariable("BMAI_CONNECTION_READ") is { Length: > 0 } read
            ? read
            : writeConnectionString;

    /// Okuma replikası ayrı bir sunucuda mı.
    ///
    /// Panel bunu gösteriyor: replikaya bağlı olduğunu bilmeyen biri,
    /// gecikmeden gelen eski bir sayıyı hata sanabilir.
    public static bool UsesReadReplica
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BMAI_CONNECTION_READ"));
}
