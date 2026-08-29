using System.Globalization;
using System.Text.Json;
using BytemountsAiStudio.Contracts.Prompts;
using BytemountsAiStudio.Core;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Persistence.Providers;

/// Bir istem sürümünün ölçülen performansı (P5-05).
public sealed record PromptVersionRow(
    string Key,
    int Version,
    int Runs,
    int RandomizedRuns,
    double? MeanRetentionSeconds,
    double? MeanCtr,
    DateTimeOffset? FirstUsed,
    DateTimeOffset? LastUsed);

/// İstem performans raporu (P5-05).
public sealed record PromptReport(
    IReadOnlyList<PromptVersionRow> Versions,
    IReadOnlyList<string> Notes);

/// Hangi istem sürümü ne kadar iş görüyor (P5-05).
///
/// RAPORUN ASIL İŞİ, YANLIŞ SONUÇ ÇIKARMAYI ENGELLEMEK.
///
/// İstem sürümleri SIRAYLA yayına alınıyor: v1 haziranda, v2
/// temmuzda. "v2 daha iyi" cümlesi bu veriden çıkarıldığında aslında
/// TEMMUZ'un hazirandan iyi olduğunu söylüyor — kanal büyüdü, konular
/// değişti, platform sıralamayı değiştirdi. İstem bunlardan yalnızca
/// biri.
///
/// Bu yüzden rapor iki tür karşılaştırmayı AYIRIYOR:
///
///   RASTGELE ATANMIŞ (bir istem deneyi koştu): aynı dönemde, aynı
///   kanalda, kura ile bölünmüş iki grup. Nedensel iddia edilebilir.
///
///   GÖZLEMSEL (sürüm sırayla değişti): yalnızca betimleyici. Rapor
///   bunu her seferinde yazıyor, okuyanın hatırlamasına bırakmıyor.
///
/// GRUPLAMA ATANAN KOLA GÖRE DEĞİL, ÇIKTIYA YAZILAN GERÇEK DAMGAYA
/// GÖRE. Bir handler kendi kısıtı yüzünden deneyin istediği sürümü
/// kullanamayabiliyor (araştırma yoksa `script.generate` v3
/// kullanılamıyor). O run'ı atandığı kolda saymak, tedaviyi almamış
/// bir videoyu o kolun ortalamasına katmak olurdu.
public sealed class PromptPerformanceReport(StudioDbContext db)
{
    /// Bir örneğin sayılması için gereken en az izlenme (P5-04 ile aynı).
    public const int MinimumViews = TopicWeightCalibrator.MinimumViews;

    public async Task<Result<PromptReport>> BuildAsync(
        Guid channelId, CancellationToken cancellationToken)
    {
        var randomized = await db.ExperimentAssignments.AsNoTracking()
            .Where(a => db.Experiments.Any(e => e.Id == a.ExperimentId && e.Dimension == "prompt"))
            .Select(a => a.RunId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var randomizedSet = randomized.ToHashSet();

        var rows = await db.NodeExecutions.AsNoTracking()
            .Where(n => n.OutputJson != null)
            .Join(db.Runs.AsNoTracking().Where(r => r.ChannelId == channelId),
                n => n.RunId, r => r.Id,
                (n, r) => new { n.RunId, n.OutputJson, n.FinishedAt })
            .Join(db.PublicationMetrics.AsNoTracking()
                    .Where(m => m.DayOffset == ExperimentService.MetricDay && m.Views >= MinimumViews),
                x => x.RunId, m => m.RunId,
                (x, m) => new Row(
                    x.RunId, x.OutputJson!, x.FinishedAt, m.Views, m.WatchSeconds, m.Impressions, m.Clicks))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var groups = new Dictionary<(string Key, int Version), List<Row>>();

        foreach (var row in rows)
        {
            var stamp = PromptStamp.TryParse(StampOf(row.OutputJson));

            if (stamp is null)
            {
                // İSTEM KULLANMAYAN NODE'LAR BURADA ELENİYOR.
                //
                // Render, TTS, kapak — çoğu node istem çağırmıyor ve
                // çıktısında damga yok. Onları "sürümü bilinmeyen" diye
                // saymak, raporu anlamsız bir çoğunlukla doldururdu.
                continue;
            }

            var key = (stamp.Value.Key, stamp.Value.Version);

            if (!groups.TryGetValue(key, out var list))
            {
                groups[key] = list = [];
            }

            list.Add(row);
        }

        var versions = groups
            .Select(g => Summarize(g.Key.Key, g.Key.Version, g.Value, randomizedSet))
            .OrderBy(v => v.Key, StringComparer.Ordinal)
            .ThenBy(v => v.Version)
            .ToList();

        return Result.Success(new PromptReport(versions, Notes(versions)));
    }

    /// Karşılaştırmanın ne kadarına güvenilebileceği.
    ///
    /// Not listesi raporun en önemli kısmı: sayılar tek başına her
    /// zaman bir "kazanan" gösteriyor.
    internal static IReadOnlyList<string> Notes(IReadOnlyList<PromptVersionRow> versions)
    {
        var notes = new List<string>();

        foreach (var group in versions.GroupBy(v => v.Key, StringComparer.Ordinal))
        {
            var list = group.OrderBy(v => v.Version).ToList();

            if (list.Count < 2)
            {
                notes.Add($"{group.Key}: tek sürüm ölçülmüş; karşılaştırılacak bir şey yok.");
                continue;
            }

            if (list.All(v => v.RandomizedRuns == v.Runs && v.Runs > 0))
            {
                notes.Add(
                    $"{group.Key}: bütün run'lar rastgele atanmış "
                    + $"({string.Join(" / ", list.Select(v => $"v{v.Version}: {v.Runs}"))}). "
                    + "Karşılaştırma NEDENSEL.");

                continue;
            }

            var overlapping = Overlaps(list);

            notes.Add(
                $"{group.Key}: sürümler rastgele atanmamış"
                + (overlapping
                    ? " ama kullanım dönemleri örtüşüyor"
                    : " ve kullanım dönemleri ÖRTÜŞMÜYOR")
                + ". Bu karşılaştırma NEDENSEL DEĞİL: "
                + (overlapping
                    ? "sürüm seçimi bir kurala bağlıysa (örneğin araştırma varsa v3) "
                        + "farkı yaratan o kural olabilir."
                    : "farkı yaratan istem değil, aradan geçen zaman olabilir — "
                        + "kanal büyüdü, konular değişti, platform sıralamayı değiştirdi."));
        }

        return notes;
    }

    /// İki sürümün kullanım dönemleri kesişiyor mu.
    private static bool Overlaps(List<PromptVersionRow> versions)
    {
        for (var i = 0; i < versions.Count; i++)
        {
            for (var j = i + 1; j < versions.Count; j++)
            {
                var a = versions[i];
                var b = versions[j];

                if (a.FirstUsed is null || a.LastUsed is null
                    || b.FirstUsed is null || b.LastUsed is null)
                {
                    continue;
                }

                if (a.FirstUsed <= b.LastUsed && b.FirstUsed <= a.LastUsed)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static PromptVersionRow Summarize(
        string key, int version, List<Row> rows, HashSet<Guid> randomized)
    {
        var runs = rows.Select(r => r.RunId).Distinct().Count();

        return new PromptVersionRow(
            key,
            version,
            runs,
            rows.Select(r => r.RunId).Distinct().Count(randomized.Contains),

            // İKİ METRİK BİRDEN, çünkü hangisinin ilgili olduğu isteme
            // bağlı: `seo.generate` başlığı yazıyor ve tıklanmayı
            // etkiliyor; `script.generate` metni yazıyor ve tutmayı.
            // Tek metrik göstermek, istemlerin yarısını yanlış ölçmek
            // olurdu.
            rows.Count > 0 ? rows.Average(r => (double)r.WatchSeconds / r.Views) : null,
            rows.Count > 0 && rows.All(r => r.Impressions > 0)
                ? rows.Average(r => (double)r.Clicks / r.Impressions)
                : null,

            rows.Min(r => r.FinishedAt),
            rows.Max(r => r.FinishedAt));
    }

    /// Node çıktısındaki istem damgası.
    internal static string? StampOf(string? outputJson)
    {
        if (string.IsNullOrWhiteSpace(outputJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(outputJson);

            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("prompt", out var prompt)
                && prompt.ValueKind == JsonValueKind.String
                ? prompt.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// Rapor satırının insan okur hâli.
    public static string Format(PromptVersionRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return string.Create(CultureInfo.InvariantCulture,
            $"{row.Key}@{row.Version}  {row.Runs,4} run  ")
            + (row.RandomizedRuns > 0
                ? string.Create(CultureInfo.InvariantCulture, $"({row.RandomizedRuns} rastgele)  ")
                : "(gözlemsel)      ")
            + string.Create(CultureInfo.InvariantCulture,
                $"tutma {row.MeanRetentionSeconds:0.0}s  CTR {row.MeanCtr:P2}");
    }

    private readonly record struct Row(
        Guid RunId,
        string OutputJson,
        DateTimeOffset? FinishedAt,
        int Views,
        long WatchSeconds,
        int Impressions,
        int Clicks);
}
