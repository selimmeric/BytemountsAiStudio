using System.Text.Json;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Persistence.Providers;

/// Bir çekim turunun sonucu.
public sealed record CollectionSummary(
    int Collected,
    int NotSettled,
    int AlreadyHave,
    int NoData,
    int Failed)
{
    public override string ToString()
        => $"{Collected} yazıldı, {NotSettled} henüz oturmadı, {AlreadyHave} zaten vardı, "
            + $"{NoData} veri yok, {Failed} hata";
}

/// Yayınlanmış videoların günlük ölçümünü toplar (P5-01).
///
/// ÖĞRENME DÖNGÜSÜNÜN VERİ KAYNAĞI. P5-02'den P5-07'ye kadar yazılan
/// her şey — deney kararı, ağırlık kalibrasyonu, istem raporu — bu
/// tablodan besleniyor ve şimdiye kadar tablo yalnızca `bmai ogrenme
/// olcum` ile elle dolduruluyordu. Aynı tablo, aynı sütunlar; değişen
/// tek şey verinin KAYNAĞI.
///
/// ***ERKEN ÇEKİM, SESSİZ BİR TUZAK.*** YouTube'un raporları iki güne
/// kadar geriden geliyor: yedinci günün sayılarını yedinci gün çekmek,
/// tamamlanmamış bir sayıyı tam sanmak demek. Sayı makul görünüyor,
/// kimse şüphelenmiyor ve deney o eksik sayıyla karar veriyor.
/// Oturmamış günler ATLANIYOR, sıfır yazılmıyor.
public sealed class MetricsCollector(
    StudioDbContext db, IDailyMetricsSource analytics, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// Ölçümün okunduğu gün — deney değerlendirmesiyle AYNI sabit.
    ///
    /// İki ayrı sayı olsaydı, çekilen gün ile okunan gün ayrışır ve
    /// deney "veri yok" derken tabloda veri dururdu.
    public const int MetricDay = ExperimentService.MetricDay;

    public async Task<Result<CollectionSummary>> CollectAsync(CancellationToken cancellationToken)
    {
        var published = await PublishedAsync(cancellationToken).ConfigureAwait(false);

        var today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);

        int collected = 0, notSettled = 0, already = 0, noData = 0, failed = 0;

        foreach (var video in published)
        {
            var metricDate = DateOnly.FromDateTime(video.PublishedAt.UtcDateTime).AddDays(MetricDay);

            if (!analytics.IsSettled(metricDate, today))
            {
                notSettled++;
                continue;
            }

            // AYNI GÜN İKİ KEZ YAZILMIYOR.
            //
            // Veritabanı kısıtı zaten engelliyor ama sorgulamadan
            // yazmak, her turda bir hata üretip logu doldururdu.
            var exists = await db.PublicationMetrics.AsNoTracking()
                .AnyAsync(m => m.RunId == video.RunId && m.DayOffset == MetricDay, cancellationToken)
                .ConfigureAwait(false);

            if (exists)
            {
                already++;
                continue;
            }

            var daily = await analytics.DailyAsync(video.ExternalId, metricDate, cancellationToken)
                .ConfigureAwait(false);

            if (daily.IsFailure)
            {
                failed++;
                continue;
            }

            if (daily.Value is not { } metric)
            {
                // SATIR YOK: "hiç izlenme yok" değil, "veri gelmedi".
                // Sıfır yazmak, gelmemiş bir günü ölçülmüş saymak ve
                // bütün ortalamaları aşağı çekmek olurdu.
                noData++;
                continue;
            }

            db.PublicationMetrics.Add(new PublicationMetric
            {
                RunId = video.RunId,
                DayOffset = MetricDay,
                Views = (int)Math.Min(metric.Views, int.MaxValue),

                // DAKİKA SANİYEYE ÇEVRİLİYOR: Analytics dakika veriyor,
                // tablo saniye tutuyor. Birim karışması, tutma
                // oranını altmış kat yanlış gösterirdi.
                WatchSeconds = metric.EstimatedMinutesWatched * 60,
            });

            collected++;
        }

        if (collected > 0)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return Result.Success(new CollectionSummary(collected, notSettled, already, noData, failed));
    }

    /// Yayınlanmış videolar — yayın node'unun çıktısından.
    internal async Task<IReadOnlyList<PublishedVideo>> PublishedAsync(CancellationToken cancellationToken)
    {
        var rows = await db.NodeExecutions.AsNoTracking()
            .Where(n => n.NodeType == "publish.upload"
                && n.State == NodeState.Succeeded
                && n.OutputJson != null
                && n.FinishedAt != null)
            .Select(n => new { n.RunId, n.OutputJson, n.FinishedAt })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var videos = new List<PublishedVideo>();

        foreach (var row in rows)
        {
            var parsed = Parse(row.OutputJson!);

            if (parsed is { } video)
            {
                videos.Add(new PublishedVideo(row.RunId, video.ExternalId, row.FinishedAt!.Value));
            }
        }

        return videos;
    }

    /// Yayın çıktısından video kimliği.
    ///
    /// YALNIZCA YOUTUBE: Analytics yalnızca YouTube'u biliyor. Başka
    /// platformların ölçümü kendi API'lerinden gelecek ve onları
    /// buraya karıştırmak, hiç çekilmemiş bir platformu "veri yok"
    /// diye raporlamak olurdu.
    internal static (string ExternalId, string Platform)? Parse(string outputJson)
    {
        try
        {
            using var document = JsonDocument.Parse(outputJson);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("external_id", out var id)
                || id.GetString() is not { Length: > 0 } externalId)
            {
                return null;
            }

            var platform = root.TryGetProperty("platform", out var p) ? p.GetString() : null;

            return string.Equals(platform, "youtube", StringComparison.OrdinalIgnoreCase)
                ? (externalId, platform!)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public readonly record struct PublishedVideo(
        Guid RunId, string ExternalId, DateTimeOffset PublishedAt);
}
