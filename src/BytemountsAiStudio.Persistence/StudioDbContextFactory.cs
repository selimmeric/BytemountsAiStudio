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

        builder.UseNpgsql(connectionString, ConfigureNpgsql);

        // Kolon ve tablo adları snake_case: psql'den bakan insan tırnak
        // işareti kullanmak zorunda kalmasın.
        builder.UseSnakeCaseNamingConvention();

        return builder;
    }

    /// Npgsql tarafının kurulumu — DI kaydı (`AddStudioPersistence`) de
    /// bunu çağırıyor, böylece testlerin kurulumu üretimin kurulumu.
    ///
    /// YENİDEN DENEYEN YÜRÜTME STRATEJİSİ YOK ve bu bir eksiklik
    /// değil, bir karar. `EnableRetryOnFailure` bir süre yalnızca DI
    /// tarafında açıktı ve EF, yeniden deneyen bir strateji altında
    /// kullanıcının açtığı transaction'a izin vermiyor:
    /// `WorkflowEngine` başarı yolunda tam olarak onu açıyor. Worker'da
    /// her node çalıştırması istisna atıyordu.
    ///
    /// Geçici veritabanı hatası KAYBOLMUYOR, sadece başka bir katmanda
    /// karşılanıyor: iş düşüyor, kuyruk `Transient` sınıflandırmasıyla
    /// onu geri alıp yeniden deniyor (ADR-011). Orada deneme sayısı,
    /// bekleme ve ölü mektup kutusu zaten var — bağlantı seviyesindeki
    /// sessiz tekrar bunların hiçbirini görmüyordu.
    public static void ConfigureNpgsql(Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.NpgsqlDbContextOptionsBuilder npgsql)
    {
        npgsql.UseVector();
        npgsql.MigrationsAssembly(typeof(StudioDbContextFactory).Assembly.FullName);
    }
}
