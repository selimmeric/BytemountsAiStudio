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

        return services;
    }
}
