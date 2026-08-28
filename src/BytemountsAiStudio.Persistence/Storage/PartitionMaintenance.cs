using System.Globalization;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Persistence.Storage;

/// Aylık bölüm bakımı (P4-06).
///
/// EN TEHLİKELİ TUZAK: PostgreSQL'de kapsayan bir bölüm yoksa INSERT
/// DÜŞÜYOR. Bölümleri elle açıp unutmak, sistemin ayın birinde saat
/// 00:00'da tamamen durması demek — hiçbir video üretilemez ve hata
/// "no partition of relation found for row" olur.
///
/// İKİ KATMANLI KORUMA, çünkü tek katman yetmiyor:
///
///   1. VARSAYILAN BÖLÜM: kapsanmayan her satır oraya düşüyor. INSERT
///      asla düşmüyor. Ama orada satır birikmesi bir ARIZA işareti —
///      bakım geri kalmış demek.
///   2. İLERİ DÖNÜK AÇMA: her açılışta ve günde bir, önümüzdeki
///      aylar açılıyor.
///
/// Yalnızca varsayılan bölüm olsaydı sistem çalışırdı ama bütün veri
/// tek bir bölümde toplanır ve bölümlemenin faydası (bölüm budama,
/// ucuz silme) kaybolurdu — sessizce.
public static class PartitionMaintenance
{
    /// Bölümlenmiş tablolar.
    /// YALNIZCA `run_events`.
    ///
    /// `node_executions` bilincli olarak bolumlenmedi: bolum anahtari
    /// esizlik kisitinin parcasi olmak zorunda ve `created_at`
    /// eklendiginde kisit isini yapmayi biraktigi olculdu -- ayni
    /// adimin iki kez yazilmasi artik engellenmiyordu. Var olan bir
    /// test bunu hemen yakaladi.
    public static readonly string[] Tables = ["run_events"];

    /// Kaç ay ileriye açılıyor.
    ///
    /// Üç ay: bakım işi iki ay boyunca hiç koşmasa bile INSERT'ler
    /// doğru bölüme gidiyor. Bir ay olsaydı, ayın son gününde düşen
    /// bir bakım işi ertesi gün varsayılan bölüme yazmaya başlardı.
    public const int MonthsAhead = 3;

    /// Eksik bölümleri açar. Var olanlara dokunmuyor.
    public static async Task<Result<int>> EnsureAsync(
        StudioDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        var created = 0;

        foreach (var table in Tables)
        {
            for (var offset = 0; offset <= MonthsAhead; offset++)
            {
                var start = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc)
                    .AddMonths(offset);

                var end = start.AddMonths(1);
                var name = SafeIdentifier($"{table}_{start:yyyyMM}");

                // `IF NOT EXISTS` YOK: `CREATE TABLE ... PARTITION OF`
                // onu desteklemiyor. Önce varlığa bakıyoruz.
                var exists = await ExistsAsync(db, name, cancellationToken).ConfigureAwait(false);

                if (exists)
                {
                    continue;
                }

                try
                {
#pragma warning disable EF1002 // Tanimlayici `SafeIdentifier`'dan geciyor; tarihler kod uretimi.
                    await db.Database.ExecuteSqlRawAsync(
                        $"""
                        CREATE TABLE "{name}" PARTITION OF "{table}"
                        FOR VALUES FROM ('{start:yyyy-MM-dd}') TO ('{end:yyyy-MM-dd}')
                        """,
                        cancellationToken).ConfigureAwait(false);
#pragma warning restore EF1002

                    created++;
                }
                catch (Npgsql.PostgresException ex) when (ex.SqlState == "42P07")
                {
                    // Başka bir worker aynı anda açtı: yarış normal ve
                    // zararsız. İki worker'ın ikisi de açılışta bakım
                    // yapıyor.
                }
            }
        }

        return created;
    }

    /// Varsayılan bölümde satır var mı — VARSA BAKIM GERİ KALMIŞ.
    ///
    /// Bu sayı sıfırdan büyükse sistem çalışıyor ama bölümleme işini
    /// yapmıyor: veri tek yerde birikiyor, bölüm budama çalışmıyor ve
    /// eski veriyi ucuza silmek imkânsız. Sessizce olmasın diye
    /// ölçülüyor.
    public static async Task<int> DefaultRowsAsync(
        StudioDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        var total = 0;

        foreach (var table in Tables)
        {
            var name = SafeIdentifier($"{table}_varsayilan");

#pragma warning disable EF1002 // Tanimlayici `SafeIdentifier`'dan geciyor.
            var rows = await db.Database
                .SqlQueryRaw<long>($"SELECT count(*) AS \"Value\" FROM \"{name}\"")
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
#pragma warning restore EF1002

            total += (int)rows[0];
        }

        return total;
    }

    /// Saklama süresini aşan bölümleri düşürür.
    ///
    /// `DROP` KULLANILIYOR, `DELETE` DEĞİL ve fark ölçüldü: 6.000
    /// satırlık bir ay için DELETE 175 ms, DROP 3,5 ms. Asıl fark
    /// hızda değil YERDE: `DELETE` ölü satır bırakıyor ve tablo
    /// küçülmüyor — 165 MB'lık tablo, 6.000 satır silindikten sonra
    /// hâlâ 165 MB. Bölüm düşürüldüğünde yer hemen geri veriliyor.
    public static async Task<Result<int>> DropOlderThanAsync(
        StudioDbContext db, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        var dropped = 0;

        foreach (var table in Tables)
        {
            var parent = SafeIdentifier(table);

#pragma warning disable EF1002 // Tanimlayici `SafeIdentifier`'dan geciyor.
            var partitions = await db.Database
                // Desendeki `[0-9]{6}` C# ham dizesinde iki kez
                // kaclaniyor; onun yerine LIKE + uzunluk kullaniliyor
                // ve okunmasi da daha kolay.
                .SqlQueryRaw<string>(
                    $"""
                    SELECT c.relname AS "Value"
                    FROM pg_class c
                    JOIN pg_inherits i ON i.inhrelid = c.oid
                    WHERE i.inhparent = '{parent}'::regclass
                      AND c.relname LIKE '{parent}\_%'
                      AND length(c.relname) = {parent.Length + 7}
                      AND right(c.relname, 6) ~ '^[0-9]+$'
                    """)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
#pragma warning restore EF1002

            foreach (var partition in partitions)
            {
                var stamp = partition[^6..];

                if (!DateTime.TryParseExact(stamp, "yyyyMM", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var month))
                {
                    continue;
                }

                // AYIN SONU KARŞILAŞTIRILIYOR, BAŞI DEĞİL.
                //
                // Başına bakmak, içinde hâlâ saklama süresi dolmamış
                // satırlar olan bir bölümü düşürmek demekti — ve
                // düşen bir bölüm geri gelmiyor.
                if (month.AddMonths(1) > cutoff.UtcDateTime)
                {
                    continue;
                }

#pragma warning disable EF1002 // Tanimlayici `SafeIdentifier`'dan geciyor.
                await db.Database.ExecuteSqlRawAsync(
                    $"DROP TABLE \"{SafeIdentifier(partition)}\"", cancellationToken)
                    .ConfigureAwait(false);
#pragma warning restore EF1002

                dropped++;
            }
        }

        return dropped;
    }

    /// DDL TANIMLAYICISI DOĞRULANIYOR — iddia değil, KONTROL.
    ///
    /// Tablo ve bölüm adları SQL'de parametre olamıyor; tek yol
    /// metne gömmek ve bu, EF'in enjeksiyon uyarısını haklı olarak
    /// tetikliyor.
    ///
    /// "Bu değerler bizden geliyor, güvenli" demek yeterli değildi:
    /// yarın biri `Tables` listesine yapılandırmadan gelen bir ad
    /// eklerse yorum hâlâ orada durur ama doğru olmaz. Bu yüzden
    /// gömmeden ÖNCE doğrulanıyor — harf, rakam ve alt çizgi
    /// dışında hiçbir şey geçmiyor.
    private static string SafeIdentifier(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 63)
        {
            throw new ArgumentException($"Geçersiz tanımlayıcı: '{value}'", nameof(value));
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character != '_')
            {
                throw new ArgumentException($"Geçersiz tanımlayıcı: '{value}'", nameof(value));
            }
        }

        return value;
    }

    private static async Task<bool> ExistsAsync(
        StudioDbContext db, string name, CancellationToken cancellationToken)
    {
#pragma warning disable EF1002 // Tanimlayici `SafeIdentifier`'dan geciyor.
        var rows = await db.Database
            .SqlQueryRaw<int>(
                $"SELECT count(*) AS \"Value\" FROM pg_class WHERE relname = '{SafeIdentifier(name)}'")
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
#pragma warning restore EF1002

        return rows[0] > 0;
    }
}
