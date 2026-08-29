using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Core.Learning;
using BytemountsAiStudio.Persistence;
using BytemountsAiStudio.Persistence.Providers;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Api;

/// Bir deneyin ekrandaki hâli.
public sealed record ExperimentCard(
    Guid Id,
    string Dimension,
    string Name,
    string State,
    string Outcome,
    string Reason,
    int Assigned,
    int Measured,
    int RequiredPerVariant);

/// Bir kanalın skorlama ağırlıkları ve kalibrasyon durumu.
public sealed record WeightCard(
    Guid ChannelId,
    string ChannelName,
    IReadOnlyDictionary<string, double> Weights,
    bool IsDefault,
    string CalibrationOutcome,
    string CalibrationReason);

/// "Ne işe yarıyor" ekranı (P5-06).
public sealed record LearningSummary(
    int PublishedRuns,
    int MeasuredRuns,
    bool HasData,
    string Headline,
    IReadOnlyList<ExperimentCard> Experiments,
    IReadOnlyList<WeightCard> Weights,
    IReadOnlyList<PromptVersionRow> Prompts,
    IReadOnlyList<string> PromptNotes);

/// Öğrenen sistemin tek ekranı (P5-06).
///
/// EKRANIN EN ÖNEMLİ İŞİ: "VERİ YOK" İLE "ETKİ YOK"U AYIRMAK.
///
/// Ölçüm gelmemişken sıfırlarla dolu bir tablo göstermek, bakan kişiye
/// "denediklerimiz işe yaramıyor" dedirtir. Doğru cümle "henüz hiçbir
/// şey ölçmedik". Aynı ayrım P5-02'de karar katmanında yapılmıştı; bu
/// dosya onu EKRANDA koruyor — çünkü doğru hesaplanan bir sonucu
/// yanlış gösteren bir panel, yanlış hesaplayan bir panelle aynı
/// kararı verdiriyor.
///
/// GEÇERSİZ DENEYLER EN ÜSTTE. Bozuk bir deney sessizce koşmuyor ama
/// görünmezse de kimse düzeltmiyor: kapanmış bir deney, kapatıldığını
/// söylemediği sürece hâlâ veri topluyor sanılır.
public static class LearningReport
{
    public static async Task<LearningSummary> BuildAsync(
        StudioDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        var published = await db.Runs.AsNoTracking()
            .CountAsync(r => r.State == RunState.Completed, cancellationToken)
            .ConfigureAwait(false);

        var measured = await db.PublicationMetrics.AsNoTracking()
            .Where(m => m.DayOffset == ExperimentService.MetricDay)
            .Select(m => m.RunId)
            .Distinct()
            .CountAsync(cancellationToken)
            .ConfigureAwait(false);

        var experiments = await ExperimentsAsync(db, cancellationToken).ConfigureAwait(false);
        var weights = await WeightsAsync(db, cancellationToken).ConfigureAwait(false);
        var prompts = await PromptsAsync(db, cancellationToken).ConfigureAwait(false);

        return new LearningSummary(
            published,
            measured,
            measured > 0,
            Headline(published, measured, experiments),
            experiments,
            weights,
            prompts.Versions,
            prompts.Notes);
    }

    /// Ekranın tek cümlelik özeti.
    ///
    /// SIRALAMA ÖNEMLİ: önce "ölçüm var mı" soruluyor. Ölçüm yokken
    /// deney sayısı vermek, bir şeylerin öğrenildiği izlenimi verirdi.
    internal static string Headline(
        int published, int measured, IReadOnlyList<ExperimentCard> experiments)
    {
        ArgumentNullException.ThrowIfNull(experiments);

        var invalid = experiments.Count(e => e.State == "Invalid");

        if (measured == 0)
        {
            return published == 0
                ? "Henüz yayınlanmış video yok."
                : $"{published} video yayınlandı, HİÇBİRİNİN performansı ölçülmedi. "
                    + "Bu 'işe yaramıyor' değil, 'henüz bilmiyoruz' — ölçüm kaynağı bağlanmadan "
                    + "hiçbir karşılaştırma yapılamaz.";
        }

        var decided = experiments.Count(e => e.Outcome is "VariantWins" or "ControlWins");

        return $"{measured}/{published} videonun performansı ölçüldü. "
            + $"{experiments.Count(e => e.State == "Running")} deney koşuyor, "
            + $"{decided} tanesi karara bağlandı"
            + (invalid > 0 ? $", {invalid} tanesi GEÇERSİZ (aşağıda gerekçesiyle)." : ".");
    }

    private static async Task<IReadOnlyList<ExperimentCard>> ExperimentsAsync(
        StudioDbContext db, CancellationToken cancellationToken)
    {
        var experiments = await db.Experiments.AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var cards = new List<ExperimentCard>(experiments.Count);
        var service = new ExperimentService(db);

        foreach (var experiment in experiments)
        {
            var assigned = await db.ExperimentAssignments.AsNoTracking()
                .CountAsync(a => a.ExperimentId == experiment.Id, cancellationToken)
                .ConfigureAwait(false);

            var measured = await db.ExperimentAssignments.AsNoTracking()
                .Where(a => a.ExperimentId == experiment.Id)
                .CountAsync(
                    a => db.PublicationMetrics.Any(
                        m => m.RunId == a.RunId && m.DayOffset == ExperimentService.MetricDay),
                    cancellationToken)
                .ConfigureAwait(false);

            var outcome = experiment.Outcome ?? "NotEnoughData";
            var reason = experiment.Reason ?? string.Empty;

            if (experiment.State == "Running" && assigned > 0)
            {
                // KARAR SAKLANMIYOR, HER BAKIŞTA HESAPLANIYOR.
                //
                // Saklanmış bir "yeterli veri yok" cevabı, veri
                // geldikten sonra da ekranda öyle durur.
                var verdict = await service.EvaluateAsync(experiment.Id, cancellationToken)
                    .ConfigureAwait(false);

                if (verdict.IsSuccess)
                {
                    outcome = verdict.Value.Outcome.ToString();
                    reason = verdict.Value.Reason;
                }
            }

            cards.Add(new ExperimentCard(
                experiment.Id, experiment.Dimension, experiment.Name, experiment.State,
                outcome, reason, assigned, measured, experiment.RequiredPerVariant));
        }

        // GEÇERSİZLER EN ÜSTTE: bir deneyin bozuk olduğunu görmek,
        // sonucunu görmekten acil.
        return
        [
            .. cards
                .OrderBy(c => c.State == "Invalid" ? 0 : c.State == "Running" ? 1 : 2)
                .ThenBy(c => c.Dimension, StringComparer.Ordinal)
                .ThenBy(c => c.Name, StringComparer.Ordinal),
        ];
    }

    private static async Task<IReadOnlyList<WeightCard>> WeightsAsync(
        StudioDbContext db, CancellationToken cancellationToken)
    {
        var channels = await db.Channels.AsNoTracking()
            .Select(c => new { c.Id, c.Name, c.SettingsJson })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var cards = new List<WeightCard>(channels.Count);
        var calibrator = new TopicWeightCalibrator(db);

        foreach (var channel in channels)
        {
            var weights = ChannelSettings.Parse(channel.SettingsJson).ScoreWeights;

            // `apply: false` — ekran hiçbir şeyi değiştirmiyor.
            // Bakmakla uygulamak ayrı iki işlem: bir panele girmek
            // kanalın ağırlıklarını değiştirmemeli.
            var verdict = await calibrator
                .CalibrateAsync(channel.Id, apply: false, cancellationToken)
                .ConfigureAwait(false);

            cards.Add(new WeightCard(
                channel.Id,
                channel.Name,
                weights.ByDimension,
                weights == ScoreWeights.Default,
                verdict.IsSuccess ? verdict.Value.Outcome.ToString() : "Hata",
                verdict.IsSuccess ? verdict.Value.Reason : verdict.Error.Message));
        }

        return cards;
    }

    private static async Task<PromptReport> PromptsAsync(
        StudioDbContext db, CancellationToken cancellationToken)
    {
        var channels = await db.Channels.AsNoTracking()
            .Select(c => c.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var report = new PromptPerformanceReport(db);
        var versions = new List<PromptVersionRow>();

        foreach (var channelId in channels)
        {
            var built = await report.BuildAsync(channelId, cancellationToken).ConfigureAwait(false);

            if (built.IsSuccess)
            {
                versions.AddRange(built.Value.Versions);
            }
        }

        // KANALLAR BİRLEŞTİRİLİYOR ama uyarı yeniden hesaplanıyor:
        // iki kanalın satırlarını toplayıp eski uyarıları taşımak,
        // "nedensel" etiketini hak etmeyen bir satıra iliştirebilirdi.
        var merged = versions
            .GroupBy(v => (v.Key, v.Version))
            .Select(g => new PromptVersionRow(
                g.Key.Key,
                g.Key.Version,
                g.Sum(v => v.Runs),
                g.Sum(v => v.RandomizedRuns),
                Weighted(g, v => v.MeanRetentionSeconds),
                Weighted(g, v => v.MeanCtr),
                g.Min(v => v.FirstUsed),
                g.Max(v => v.LastUsed)))
            .OrderBy(v => v.Key, StringComparer.Ordinal)
            .ThenBy(v => v.Version)
            .ToList();

        return new PromptReport(merged, PromptPerformanceReport.Notes(merged));
    }

    /// Kanal ortalamalarını RUN SAYISIYLA ağırlıklandırır.
    ///
    /// Düz ortalama almak, üç videosu olan bir kanalı üç yüz videosu
    /// olanla eşit sayardı.
    private static double? Weighted(
        IEnumerable<PromptVersionRow> rows, Func<PromptVersionRow, double?> select)
    {
        var withValue = rows.Where(r => select(r) is not null && r.Runs > 0).ToList();

        if (withValue.Count == 0)
        {
            return null;
        }

        var total = withValue.Sum(r => r.Runs);

        return withValue.Sum(r => select(r)!.Value * r.Runs) / total;
    }
}
