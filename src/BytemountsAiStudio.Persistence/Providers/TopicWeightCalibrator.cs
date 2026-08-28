using System.Text.Json;
using System.Text.Json.Nodes;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Core.Learning;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Persistence.Providers;

/// Konu skorlama ağırlıklarını ölçülen performansla kalibre eder (P5-04).
///
/// HANGİ METRİK — ve bu seçim kalibrasyonun tamamını belirliyor.
///
/// Tıklanma oranı KULLANILMIYOR. CTR kapağı ve başlığı ölçüyor,
/// konuyu değil: iyi paketlenmiş kötü bir konu yüksek CTR alır.
/// Ağırlıkları CTR'ye göre ayarlamak, kapak deneyinin (P5-03) etkisini
/// konu skoruna yazmak olurdu — aynı şeyi iki kez ölçüp ikisine birden
/// inanmak.
///
/// KULLANILAN: izlenme başına izlenen saniye. Paketleme insanı
/// tıklatıyor, konu tutuyor. "Bu konuyu üretmeye değer miydi"
/// sorusuna en yakın ölçü bu.
public sealed class TopicWeightCalibrator(StudioDbContext db)
{
    /// Bir örneğin sayılması için gereken en az izlenme.
    ///
    /// Üç izlenmeyle %90 tutma oranı bir şey söylemiyor; o üç kişi
    /// tesadüfen sonuna kadar izlemiş olabilir. Filtre olmadan en
    /// gürültülü videolar listenin başına çıkardı.
    public const int MinimumViews = 50;

    /// Kalibrasyon kararını hesaplar.
    ///
    /// `apply` verilmedikçe hiçbir şey yazılmıyor: kararı görmek ile
    /// uygulamak ayrı iki işlem, çünkü ağırlık değişimi kanalın
    /// bundan sonra ürettiği her videoyu etkiliyor.
    public async Task<Result<CalibrationVerdict>> CalibrateAsync(
        Guid channelId, bool apply, CancellationToken cancellationToken)
    {
        var channel = await db.Channels
            .FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken)
            .ConfigureAwait(false);

        if (channel is null)
        {
            return Error.Permanent("calibration.no_channel", $"Kanal yok: {channelId}");
        }

        var current = ChannelSettings.Parse(channel.SettingsJson).ScoreWeights;

        var samples = await SamplesAsync(channelId, cancellationToken).ConfigureAwait(false);
        var verdict = WeightCalibration.Evaluate(samples, current);

        if (apply && verdict.Changed)
        {
            var written = Write(channel, verdict.Weights);

            if (written.IsFailure)
            {
                return Result.Failure<CalibrationVerdict>(written.Error);
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return Result.Success(verdict);
    }

    /// Ölçülmüş, deneye girmemiş videolar.
    internal async Task<IReadOnlyList<CalibrationSample>> SamplesAsync(
        Guid channelId, CancellationToken cancellationToken)
    {
        // PAKETLEME DENEYİNE GİREN RUN'LAR DIŞARIDA.
        //
        // O videoların kapağı ya da başlığı KASTEN değiştirildi;
        // performansları konunun değil, denenen kolun sonucu. İçeride
        // bırakmak, kapak deneyinin etkisini konu ağırlıklarına
        // sızdırmak olurdu.
        var assigned = db.ExperimentAssignments.AsNoTracking().Select(a => a.RunId);

        var rows = await db.Runs.AsNoTracking()
            .Where(r => r.ChannelId == channelId && r.TopicId != null)
            .Where(r => !assigned.Contains(r.Id))
            .Join(db.Topics.AsNoTracking(), r => r.TopicId, t => t.Id,
                (r, t) => new { r.Id, t.ScoresJson })
            .Join(db.PublicationMetrics.AsNoTracking()
                    .Where(m => m.DayOffset == ExperimentService.MetricDay
                        && m.Views >= MinimumViews),
                r => r.Id, m => m.RunId,
                (r, m) => new Row(r.Id, r.ScoresJson, m.Views, m.WatchSeconds))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var samples = new List<CalibrationSample>(rows.Count);

        foreach (var row in rows)
        {
            var score = ParseScore(row.ScoresJson);

            if (score is null)
            {
                // OKUNAMAYAN SKOR ATLANIYOR, SIFIR SAYILMIYOR.
                //
                // Sıfır saymak, o videoyu "bütün boyutlarda en kötü"
                // yapar ve korelasyonu tek başına bozar.
                continue;
            }

            samples.Add(new CalibrationSample(
                row.RunId, score, (double)row.WatchSeconds / row.Views));
        }

        return samples;
    }

    private readonly record struct Row(Guid RunId, string ScoresJson, int Views, long WatchSeconds);

    /// `topics.scores_json` → skor.
    ///
    /// SKOR YENİDEN OKUNUYOR, `overall_score` KOLONU KULLANILMIYOR.
    /// O kolon, konunun kabul edildiği ANDAKİ ağırlıklarla
    /// hesaplanmıştı; ağırlıklar bir kez değişince aynı kolon farklı
    /// çağların karışımı olur ve kalibrasyon kendi geçmiş kararını
    /// ölçmeye başlar.
    internal static TopicScore? ParseScore(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var score = new TopicScore
            {
                Demand = Int(root, "demand"),
                Fit = Int(root, "fit"),
                Sourceability = Int(root, "sourceability"),
                Visualizability = Int(root, "visualizability"),
                Freshness = Int(root, "freshness"),
                Risk = Int(root, "risk"),
            };

            return score.IsValid ? score : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int Int(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : -1;

    /// Yeni ağırlıkları kanal ayarına yazar.
    ///
    /// Belgenin GERİ KALANI KORUNUYOR: ayarın tamamını yeniden yazmak,
    /// ses ve tempo ayarlarını sessizce silmek olurdu.
    internal static Result Write(Entities.Channel channel, ScoreWeights weights)
    {
        JsonNode? root;

        try
        {
            root = JsonNode.Parse(
                string.IsNullOrWhiteSpace(channel.SettingsJson) ? "{}" : channel.SettingsJson);
        }
        catch (JsonException ex)
        {
            // BOZUK AYAR ÜZERİNE YAZILMIYOR.
            //
            // Yazmak, okunamayan ama içinde bir şeyler olan bir belgeyi
            // yalnızca ağırlıklardan ibaret bir belgeyle değiştirmek
            // olurdu.
            return Error.Permanent("calibration.bad_settings", ex.Message);
        }

        if (root is not JsonObject obj)
        {
            return Error.Permanent("calibration.bad_settings", "Kanal ayarı bir nesne değil.");
        }

        obj["score_weights"] = new JsonObject
        {
            [ScoreWeights.Dimensions.Demand] = weights.Demand,
            [ScoreWeights.Dimensions.Fit] = weights.Fit,
            [ScoreWeights.Dimensions.Sourceability] = weights.Sourceability,
            [ScoreWeights.Dimensions.Visualizability] = weights.Visualizability,
            [ScoreWeights.Dimensions.Freshness] = weights.Freshness,
            ["risk_penalty"] = weights.RiskPenalty,
        };

        channel.SettingsJson = obj.ToJsonString();

        return Result.Success();
    }
}
