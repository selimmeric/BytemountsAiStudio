using BytemountsAiStudio.Persistence.Storage;
using BytemountsAiStudio.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Persistence.Tests;

/// Aylık bölüm bakımı (P4-06).
///
/// EN TEHLİKELİ TUZAK: PostgreSQL'de kapsayan bir bölüm yoksa INSERT
/// DÜŞÜYOR. Bölümleri elle açıp unutmak, sistemin ayın birinde saat
/// 00:00'da tamamen durması demek — kimsenin ayakta olmadığı bir
/// saatte, "no partition of relation found for row" hatasıyla.
[Collection(DatabaseCollection.Name)]
public sealed class PartitionMaintenanceTests(DatabaseFixture fixture)
{
    private void RequireDatabase()
        => Assert.True(fixture.Available, $"PostgreSQL erişilemiyor ({fixture.UnavailableReason}).");

    /// Tablo/bölüm adları burada DEĞER olarak sorgulanıyor,
    /// tanımlayıcı olarak gömülmüyor — yani parametreli sorgu
    /// kullanılabiliyor ve enjeksiyon uyarısını bastırmaya gerek
    /// kalmıyor. Üretim kodunda DDL için bu mümkün değil (tanımlayıcı
    /// parametre olamıyor) ama burada testin sorduğu şey zaten
    /// "böyle bir nesne var mı".
    private static async Task<bool> ExistsAsync(StudioDbContext db, string name)
    {
        var rows = await db.Database
            .SqlQuery<int>($"SELECT count(*) AS \"Value\" FROM pg_class WHERE relname = {name}")
            .ToListAsync(CancellationToken.None);

        return rows[0] > 0;
    }

    /// MİGRATION BÖLÜMLERİ GERÇEKTEN KURUYOR.
    ///
    /// Bölümlü olmayan bir tabloda bütün bakım kodu sessizce hiçbir
    /// şey yapmazdı ve "bölümleme var" iddiası boş kalırdı.
    [Fact]
    public async Task Tablolar_GercektenBolumlu()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();

        foreach (var table in PartitionMaintenance.Tables)
        {
            var kind = await db.Database
                .SqlQuery<char>($"SELECT relkind AS \"Value\" FROM pg_class WHERE relname = {table}")
                .ToListAsync(CancellationToken.None);

            // 'p' = bölümlenmiş tablo, 'r' = düz tablo.
            Assert.Equal('p', kind[0]);
        }
    }

    /// VARSAYILAN BÖLÜM VAR.
    ///
    /// Olmasaydı, bakım bir ay geri kaldığında INSERT'ler düşerdi ve
    /// sistem tamamen dururdu. Varsayılan bölüm bunu bir arızadan
    /// bir uyarıya çeviriyor.
    [Fact]
    public async Task VarsayilanBolum_Var()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();

        foreach (var table in PartitionMaintenance.Tables)
        {
            Assert.True(await ExistsAsync(db, table + "_varsayilan"));
        }
    }

    /// İLERİ DÖNÜK BÖLÜMLER AÇILIYOR.
    ///
    /// Üç ay ileri: bakım işi iki ay boyunca hiç koşmasa bile
    /// INSERT'ler doğru bölüme gidiyor.
    [Fact]
    public async Task Bakim_IleriDonukBolumAciyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();

        var result = await PartitionMaintenance.EnsureAsync(
            db, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);

        // Bugünden itibaren dört ay (bu ay + üç) kurulu olmalı.
        for (var offset = 0; offset <= PartitionMaintenance.MonthsAhead; offset++)
        {
            var month = DateTime.UtcNow.AddMonths(offset);
            Assert.True(await ExistsAsync(db, $"run_events_{month:yyyyMM}"),
                $"{month:yyyyMM} bölümü açılmamış.");
        }
    }

    /// BAKIM İKİ KEZ KOŞTURULABİLİYOR.
    ///
    /// İki worker aynı anda açılıyor ve ikisi de bakım yapıyor.
    /// İkinci koşunun düşmesi, worker'lardan birinin her açılışta
    /// hata loglaması demekti.
    [Fact]
    public async Task Bakim_IkinciKosudaHataVermiyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();

        await PartitionMaintenance.EnsureAsync(db, DateTimeOffset.UtcNow, CancellationToken.None);
        var second = await PartitionMaintenance.EnsureAsync(db, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.True(second.IsSuccess);

        // İkinci koşu HİÇBİR bölüm açmıyor: hepsi zaten var.
        Assert.Equal(0, second.Value);
    }

    /// GELECEKTEKİ BİR TARİH İÇİN DE AÇILIYOR.
    ///
    /// Bakım işi aylarca koşmamış bir kurulumda, açılıştaki tek
    /// çağrının eksiği kapatması gerekiyor.
    [Fact]
    public async Task GelecekTarih_BolumAciyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();

        var future = DateTimeOffset.UtcNow.AddMonths(10);
        var result = await PartitionMaintenance.EnsureAsync(db, future, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value > 0, "Gelecekteki aylar için hiç bölüm açılmadı.");
    }

    /// SAKLAMA: AYIN SONUNA BAKILIYOR, BAŞINA DEĞİL.
    ///
    /// Başına bakmak, içinde hâlâ saklama süresi dolmamış satırlar
    /// olan bir bölümü düşürmek demekti — ve düşen bir bölüm geri
    /// gelmiyor.
    [Fact]
    public async Task Saklama_HenuzDolmamisBolumuDusurmuyor()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();

        await PartitionMaintenance.EnsureAsync(db, DateTimeOffset.UtcNow, CancellationToken.None);

        // Bu ayın BAŞINI eşik alıyoruz: bu ayın bölümü henüz
        // dolmadığı için düşmemeli.
        var cutoff = new DateTimeOffset(
            DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, TimeSpan.Zero);

        var dropped = await PartitionMaintenance.DropOlderThanAsync(db, cutoff, CancellationToken.None);

        Assert.True(dropped.IsSuccess);

        Assert.True(await ExistsAsync(db, $"run_events_{DateTime.UtcNow:yyyyMM}"),
            "Henüz dolmamış bölüm düşürülmüş.");
    }
}
