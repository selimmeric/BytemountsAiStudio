using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BytemountsAiStudio.Persistence;

/// `dotnet ef` araçlarının DbContext üretebilmesi için.
///
/// Uygulama host'unu ayağa kaldırmadan çalışır; migration üretmek için API'yi
/// başlatmak gerekmesi, migration'ı uygulamanın çalışabilirliğine bağlardı.
public sealed class StudioDbContextFactory : IDesignTimeDbContextFactory<StudioDbContext>
{
    /// Geliştirme bağlantısı. Üretimde konfigürasyondan gelir; burada sabit
    /// olması sorun değil çünkü bu sınıf yalnızca araç zamanında kullanılır.
    public const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=bmai;Username=bmai;Password=bmai_dev";

    public StudioDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("BMAI_CONNECTION") ?? DefaultConnectionString;

        return new StudioDbContext(Build(connectionString).Options);
    }

    /// Uygulama ve testler aynı seçenek kurulumunu kullansın diye ortak nokta.
    /// İki yerde ayrı kurulsaydı biri `UseVector()` çağırmayı unutur ve fark
    /// ancak gömme vektörü yazılırken ortaya çıkardı.
    public static DbContextOptionsBuilder<StudioDbContext> Build(string connectionString)
    {
        var builder = new DbContextOptionsBuilder<StudioDbContext>();

        builder.UseNpgsql(connectionString, npgsql =>
        {
            npgsql.UseVector();
            npgsql.MigrationsAssembly(typeof(StudioDbContextFactory).Assembly.FullName);
        });

        // Kolon ve tablo adları snake_case: psql'den bakan insan tırnak
        // işareti kullanmak zorunda kalmasın.
        builder.UseSnakeCaseNamingConvention();

        return builder;
    }
}
