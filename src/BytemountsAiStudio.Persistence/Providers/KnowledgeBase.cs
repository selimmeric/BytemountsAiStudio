using System.Text.Json;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Persistence.Providers;

/// Kaynak ve iddia kayıtları (P1-11, §2.3).
///
/// Run bağlamı JSONB olarak zaten duruyor; buradaki kayıtların ayrı
/// olmasının sebebi SORGULANABİLİRLİK. "Bu videonun tüm kaynakları",
/// "şu kaynağa dayanan bütün iddialar", "kaç iddia desteksiz kaldı"
/// sorularının hiçbiri JSONB üzerinden makul bir maliyetle
/// cevaplanamıyor.
public sealed class KnowledgeBase(StudioDbContext db)
{
    /// Araştırma çıktısındaki kaynakları kaydeder.
    ///
    /// Tekilleştirme İÇERİK ÖZETİYLE: aynı sayfa iki farklı adresten
    /// gelebiliyor ve aynı içeriği iki kez saklamak kaynak sayımını
    /// bozardı.
    public async Task<Result<IReadOnlyList<Guid>>> RecordSourcesAsync(
        JsonElement researchOutput, CancellationToken cancellationToken)
    {
        if (!researchOutput.TryGetProperty("sources", out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return Result.Success<IReadOnlyList<Guid>>([]);
        }

        var ids = new List<Guid>();

        foreach (var element in array.EnumerateArray())
        {
            var parsed = ReadSource(element);

            if (parsed is null)
            {
                continue;
            }

            var existing = await db.Sources
                .FirstOrDefaultAsync(s => s.ContentHash == parsed.ContentHash, cancellationToken)
                .ConfigureAwait(false);

            if (existing is not null)
            {
                ids.Add(existing.Id);
                continue;
            }

            db.Sources.Add(parsed);
            ids.Add(parsed.Id);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success<IReadOnlyList<Guid>>(ids);
    }

    /// İddia doğrulama çıktısını kaydeder.
    ///
    /// Kaynak eşleştirmesi ADRESE göre: iddia çıktısı kaynağın adresini
    /// taşıyor, kimliğini değil — iki node birbirinin veritabanı
    /// kimliklerini bilmek zorunda kalmasın.
    public async Task<Result<int>> RecordClaimsAsync(
        Guid runId, JsonElement claimOutput, CancellationToken cancellationToken)
    {
        if (!claimOutput.TryGetProperty("claims", out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return Result.Success(0);
        }

        var sameModel = claimOutput.TryGetProperty("same_model", out var s)
                        && s.ValueKind == JsonValueKind.True;

        // Aynı run iki kez yazılmasın: node yeniden koşabiliyor
        // (retry, hedefli düzeltme) ve iddialar birikirdi.
        var previous = await db.Claims
            .Where(c => c.RunId == runId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (previous.Count > 0)
        {
            db.Claims.RemoveRange(previous);
        }

        var added = 0;

        foreach (var element in array.EnumerateArray())
        {
            var text = element.TryGetProperty("text", out var t) ? t.GetString() : null;

            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var url = element.TryGetProperty("source", out var u) ? u.GetString() : null;

            var sourceId = url is null
                ? null
                : (await db.Sources
                    .Where(x => x.Url == url)
                    .Select(x => (Guid?)x.Id)
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false));

            db.Claims.Add(new ClaimRecord
            {
                RunId = runId,
                Text = Truncate(text, 1000)!,
                SentenceIndex = element.TryGetProperty("sentence", out var i) && i.TryGetInt32(out var index)
                    ? index
                    : 0,
                Verdict = element.TryGetProperty("verdict", out var v)
                    ? v.GetString() ?? "unsupported"
                    : "unsupported",
                SourceId = sourceId,
                Reason = element.TryGetProperty("reason", out var r) ? Truncate(r.GetString(), 2000) : null,
                SameModel = sameModel,
            });

            added++;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success(added);
    }

    /// Bir videonun tüm kaynakları — TEK sorguyla.
    ///
    /// P1-11'in kabul kriteri bu. İddia→kaynak bağı üzerinden gidiyor:
    /// araştırmada çekilip senaryoda hiç kullanılmayan bir kaynak
    /// buraya girmiyor, ki doğrusu da bu — "bu video neye dayanıyor"
    /// sorusunun cevabı kullanılan kaynaklar.
    public Task<List<Source>> SourcesForRunAsync(Guid runId, CancellationToken cancellationToken)
        => db.Claims
            .AsNoTracking()
            .Where(c => c.RunId == runId && c.Source != null)
            .Select(c => c.Source!)
            .Distinct()
            .ToListAsync(cancellationToken);

    /// Bir run'ın iddia özeti. QC'nin `ClaimCoverage` girdisi buradan
    /// besleniyor.
    public async Task<(int Total, int Supported, int Contradicted)> ClaimSummaryAsync(
        Guid runId, CancellationToken cancellationToken)
    {
        var rows = await db.Claims
            .AsNoTracking()
            .Where(c => c.RunId == runId)
            .Select(c => c.Verdict)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return (
            rows.Count,
            rows.Count(v => string.Equals(v, "supported", StringComparison.OrdinalIgnoreCase)),
            rows.Count(v => string.Equals(v, "contradicted", StringComparison.OrdinalIgnoreCase)));
    }

    /// Kaynak türünden güven skoru.
    ///
    /// Kaba ve BİLEREK öyle: gerçek kalibrasyon performans verisiyle
    /// yapılacak (P5-04). Şimdilik amaç, ansiklopedi ile blog arasında
    /// bir sıralama olması — hiç ayrım yapmamak, QC'nin kaynak
    /// kalitesine hiç bakamaması demekti.
    internal static double TrustFor(string sourceType)
        => sourceType.ToLowerInvariant() switch
        {
            "academic" => 0.95,
            "official" => 0.90,
            "encyclopedia" => 0.85,
            "news" => 0.70,
            "community" => 0.45,
            "blog" => 0.35,
            _ => 0.50,
        };

    private static Source? ReadSource(JsonElement element)
    {
        var url = element.TryGetProperty("url", out var u) ? u.GetString() : null;
        var hash = element.TryGetProperty("content_hash", out var h) ? h.GetString() : null;

        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(hash))
        {
            return null;
        }

        var type = element.TryGetProperty("source_type", out var t)
            ? t.GetString() ?? "Unknown"
            : "Unknown";

        return new Source
        {
            Url = Truncate(url, 2000)!,
            Title = Truncate(
                element.TryGetProperty("title", out var ti) ? ti.GetString() : null, 500) ?? url,
            SourceType = type,
            ContentHash = hash,
            Excerpt = Truncate(
                element.TryGetProperty("excerpt", out var e) ? e.GetString() : null, 4000),
            ContentLength = element.TryGetProperty("length", out var l) && l.TryGetInt32(out var length)
                ? length
                : 0,
            TrustScore = TrustFor(type),
        };
    }

    private static string? Truncate(string? text, int max)
        => text is null ? null : text.Length <= max ? text : text[..max];
}
