using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BytemountsAiStudio.Persistence;

public static class PersistenceRegistration
{
    /// Tek kayıt noktası. API, Worker ve CLI aynı kurulumu kullanır; üç yerde
    /// ayrı kurulsaydı biri `UseVector()` ya da snake_case sözleşmesini
    /// atlar ve fark ancak çalışma zamanında ortaya çıkardı.
    public static IServiceCollection AddStudioPersistence(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddDbContext<StudioDbContext>(options =>
        {
            options.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.UseVector();
                npgsql.MigrationsAssembly(typeof(StudioDbContext).Assembly.FullName);

                // Geçici ağ hatalarında EF kendi içinde yeniden dener. Bu,
                // iş kuyruğundaki retry'ın yerini almaz — o iş seviyesinde,
                // bu bağlantı seviyesinde.
                npgsql.EnableRetryOnFailure(maxRetryCount: 3, TimeSpan.FromSeconds(2), null);
            });

            options.UseSnakeCaseNamingConvention();
        });

        return services;
    }
}
