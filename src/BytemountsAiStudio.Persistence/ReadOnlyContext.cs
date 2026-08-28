using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Persistence;

/// Okuma replikasına bağlanan bağlam (P4-06).
///
/// AYRI BİR TİP, ayrı bir bağlantı dizesi değil — çünkü ayrım
/// derleme zamanında görülmeli. Aynı `StudioDbContext` tipini iki
/// farklı bağlantıyla kaydetmek, hangi sorgunun nereye gittiğini
/// çağrı yerinden okunamaz yapardı ve bir yazma işlemi sessizce
/// replikaya gidip "cannot execute INSERT in a read-only
/// transaction" ile düşerdi — üretimde, ilk yazmada.
///
/// NE ZAMAN KULLANILIR: panelin ağır ve TAZELİK GEREKTİRMEYEN
/// sorguları — gece raporu, varlık gezgini, iş akışı sürümleri.
///
/// NE ZAMAN KULLANILMAZ: onay kuyruğu ve koşan run detayı. Oradaki
/// replikasyon gecikmesi, az önce onayladığı videoyu hâlâ "onay
/// bekliyor" gören bir insan demek — ve o insan ikinci kez onaylamaya
/// çalışır.
public sealed class ReadOnlyDbContext(DbContextOptions<ReadOnlyDbContext> options)
    : StudioDbContext(options)
{
    // Model ve DbSet'ler `StudioDbContext`'ten geliyor: panel
    // sorguları hiç değişmeden bu bağlamla çalışıyor.

    /// YAZMA ÇAĞRISI DERLENİYOR AMA ÇALIŞMIYOR — bunu erken ve
    /// anlaşılır biçimde söylüyoruz.
    ///
    /// Replika `SaveChanges`'i zaten "read-only transaction" hatasıyla
    /// reddediyor, ama o hata sunucudan geliyor ve nereye
    /// bakılacağını söylemiyor. Buradaki istisna, yanlış bağlamı
    /// kullandığını doğrudan söylüyor.
    public override int SaveChanges()
        => throw new InvalidOperationException(
            "Okuma replikasına yazılamaz. Yazma işlemleri için `StudioDbContext` kullanın.");

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(
            "Okuma replikasına yazılamaz. Yazma işlemleri için `StudioDbContext` kullanın.");
}
