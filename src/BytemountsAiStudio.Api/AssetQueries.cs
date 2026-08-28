using System.Text.Json;
using BytemountsAiStudio.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Api;

/// Varlık gezgini ve lisans raporu (P3-08).
///
/// NEDEN AYRI BİR EKRAN: lisans bir metadata değil, bir UYUM KAYDI
/// (§2.3/14). "Bu videoda kullandığımız görselin lisansı neydi"
/// sorusu bir talep geldiğinde soruluyor ve o an aramaya başlamak çok
/// geç — üstelik varlık kırk videoda kullanılmış olabiliyor.
///
/// LİSANSSIZ VARLIKLAR ÖNCE: rapor bir envanter değil, bir risk
/// listesi. Alfabetik sıralamak, tek bir eksik kaydı yüzlerce satırın
/// arasına gömerdi.
public static class AssetQueries
{
    /// Kaç varlık listeleniyor.
    ///
    /// Sınırsız bir liste, birkaç yüz videodan sonra hem paneli hem
    /// veritabanını kilitlerdi. Risk listesi zaten en tepede.
    public const int DefaultLimit = 200;

    public static async Task<AssetReport> BuildAsync(
        StudioDbContext db, string? kind, bool onlyRisky, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        var query = db.Assets.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(kind))
        {
            query = query.Where(a => a.Kind == kind);
        }

        var rows = await query
            .OrderByDescending(a => a.CreatedAt)
            .Take(Math.Clamp(limit, 1, 1000))
            .Select(a => new
            {
                a.Sha256,
                a.Kind,
                a.MimeType,
                a.Bytes,
                a.Width,
                a.Height,
                a.SourceProvider,
                a.SourceUrl,
                a.LicenseJson,
                a.CreatedAt,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var entries = rows
            .Select(a =>
            {
                var license = LicenseOf(a.LicenseJson);

                return new AssetEntry(
                    a.Sha256,
                    a.Kind,
                    a.MimeType,
                    a.Bytes,
                    a.Width,
                    a.Height,
                    a.SourceProvider,
                    a.SourceUrl,
                    license.Name,
                    license.Author,
                    license.RequiresAttribution,
                    // RİSK METİN, BAYRAK DEĞİL: "riskli" tek başına
                    // ne yapılacağını söylemiyor ve iki ayrı sebep iki
                    // ayrı düzeltme gerektiriyor.
                    Risk(a.Kind, license, a.SourceUrl),
                    a.CreatedAt);
            })
            .ToList();

        if (onlyRisky)
        {
            entries = [.. entries.Where(e => e.Risk is not null)];
        }

        // RİSKLİLER ÖNCE, sonra yeni olanlar.
        entries = [.. entries
            .OrderByDescending(e => e.Risk is not null)
            .ThenByDescending(e => e.CreatedAt)];

        var byLicense = entries
            .GroupBy(e => e.LicenseName ?? "(lisans yok)", StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .Select(g => new LicenseCount(g.Key, g.Count()))
            .ToList();

        return new AssetReport(
            entries.Count,
            entries.Count(e => e.Risk is not null),
            entries.Sum(e => e.Bytes),
            byLicense,
            entries);
    }

    /// Bir varlığın uyum riski. `null` = sorun yok.
    ///
    /// AYIRAN ŞEY TÜR DEĞİL, KAYNAK: dışarıdan gelen varlık lisans
    /// istiyor, kendi ürettiğimiz istemiyor — çünkü onun kaynağı
    /// biziz.
    ///
    /// İLK YAZIMDA TÜRE BAKIYORDUM ("ses ve müzikte lisans zorunlu")
    /// ve gerçek veriye bakınca yanlış olduğu hemen görüldü: kendi
    /// ürettiğimiz 38 seslendirme dosyası "uyum riski" olarak
    /// işaretlenmişti. Yüzlerce yanlış uyarı, raporu okunmaz yapardı
    /// ve gerçek bir risk o gürültünün içinde kaybolurdu.
    ///
    /// Kaynak adresi zaten kayıtlı: `SourceUrl` dolu olan dışarıdan
    /// geldi, boş olan bizim.
    internal static string? Risk(string kind, LicenseFacts license, string? sourceUrl)
    {
        if (sourceUrl is null)
        {
            // KENDİ ÜRETTİĞİMİZ: seslendirme, kapak, üretilen görsel.
            // Lisans kaydı olmaması normal.
            return null;
        }

        if (license.Name is null)
        {
            // DIŞ KAYNAKLI VE LİSANSSIZ. Müzikte bu daha ağır: Content
            // ID müziği otomatik tanıyor ve bir talep kanalın o
            // videodan gelen gelirinin tamamını götürüyor.
            return kind is "Audio" or "Music"
                ? "dış kaynaklı müzik ama lisans kaydı yok — bloklayıcı"
                : "dış kaynaklı ama lisans kaydı yok";
        }

        if (license.RequiresAttribution && string.IsNullOrWhiteSpace(license.Author))
        {
            // Atıf gerekiyorsa yazar adı ŞART: "CC BY" deyip yazarı
            // bilmemek, atfı yapılamaz kılıyor ve lisansı ihlal
            // ediyor.
            return "atıf gerekiyor ama yazar kayıtlı değil";
        }

        return null;
    }

    internal readonly record struct LicenseFacts(string? Name, string? Author, bool RequiresAttribution);

    /// Lisans kaydını okur.
    ///
    /// OKUNAMAYAN KAYIT "LİSANS YOK" SAYILIYOR, "sorun yok" değil:
    /// bozuk bir JSON'u geçerli saymak, uyum kaydını olmadığı hâlde
    /// varmış gibi göstermekti.
    internal static LicenseFacts LicenseOf(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return default;
            }

            return new LicenseFacts(
                Text(root, "Name") ?? Text(root, "name"),
                Text(root, "Author") ?? Text(root, "author"),
                Bool(root, "RequiresAttribution") ?? Bool(root, "requires_attribution") ?? false);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? Bool(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
           && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.ValueKind == JsonValueKind.True
            : null;
}
