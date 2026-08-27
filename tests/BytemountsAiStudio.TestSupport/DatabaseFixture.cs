using Xunit;
using System.Globalization;
using BytemountsAiStudio.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BytemountsAiStudio.TestSupport;

/// Gerçek PostgreSQL'e karşı koşan testler için ortak kurulum.
///
/// Ayrı bir derlemede, çünkü hem kalıcılık hem kuyruk testleri kullanıyor.
/// `CollectionDefinition` burada DEĞİL: xUnit koleksiyon tanımlarını yalnızca
/// testin bulunduğu derlemede arıyor, o yüzden her test projesi kendi
/// tanımını yapıyor.
///
/// Neden bellek içi sağlayıcı değil: bu şemanın değerli kısımları — pgvector
/// kolonu, kısmi indeksler, JSONB, `SKIP LOCKED` — bellek içi sağlayıcıda
/// YOK. Orada geçen bir test, üretimde çalışacağına dair hiçbir şey söylemez.
///
/// Her test sınıfı kendi veritabanını alır ve sonunda düşürür; testler
/// birbirinin verisini görmez.
public sealed class DatabaseFixture : IAsyncLifetime
{
    private const string AdminConnection =
        "Host=localhost;Port=5432;Database=postgres;Username=bmai;Password=bmai_dev";

    private readonly string _databaseName =
        "bmai_test_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12];

    public string ConnectionString { get; private set; } = string.Empty;

    /// Postgres erişilebilir değilse testler atlanır, kırmızıya dönmez.
    /// Docker'ı kapatmış birinin tüm takımı kırmızı görmesi yanlış sinyal olurdu.
    public bool Available { get; private set; }

    public string? UnavailableReason { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            await using (var admin = new NpgsqlConnection(GetAdminConnectionString()))
            {
                await admin.OpenAsync().ConfigureAwait(false);
                await using var create = new NpgsqlCommand($"CREATE DATABASE \"{_databaseName}\"", admin);
                await create.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            ConnectionString = GetAdminConnectionString().Replace(
                "Database=postgres", $"Database={_databaseName}", StringComparison.Ordinal);

            await using var db = CreateContext();
            await db.Database.MigrateAsync().ConfigureAwait(false);

            Available = true;
        }
        catch (NpgsqlException ex)
        {
            Available = false;
            UnavailableReason = ex.Message;
        }
        catch (InvalidOperationException ex)
        {
            Available = false;
            UnavailableReason = ex.Message;
        }
    }

    public StudioDbContext CreateContext() => new(StudioDbContextFactory.Build(ConnectionString).Options);

    public async Task DisposeAsync()
    {
        if (!Available)
        {
            return;
        }

        try
        {
            NpgsqlConnection.ClearAllPools();

            await using var admin = new NpgsqlConnection(GetAdminConnectionString());
            await admin.OpenAsync().ConfigureAwait(false);
            await using var drop = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        catch (NpgsqlException)
        {
            // Test veritabanı düşürülemediyse test sonucunu etkilememeli.
        }
    }

    /// CI'da bağlantı ortam değişkeninden gelir; yerelde compose varsayılanı.
    private static string GetAdminConnectionString()
        => Environment.GetEnvironmentVariable("BMAI_TEST_CONNECTION") ?? AdminConnection;
}
