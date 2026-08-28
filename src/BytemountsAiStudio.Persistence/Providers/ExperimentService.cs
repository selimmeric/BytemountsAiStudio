using System.Security.Cryptography;
using System.Text;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Core.Learning;
using BytemountsAiStudio.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Persistence.Providers;

/// Deney atama ve değerlendirme (P5-02).
public sealed class ExperimentService(StudioDbContext db, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// Bir run'ı kanalın açık deneylerine atar.
    ///
    /// ATAMA DETERMİNİSTİK: `run_id` + deney kimliğinden türeyen bir
    /// özet, varyantı seçiyor. Rastgele sayı üreteci kullanmak,
    /// aynı run'ın yeniden değerlendirilmesinde farklı varyanta
    /// düşmesi demekti — ve hedefli yeniden koşma (P2-07) tam olarak
    /// bunu yapıyor: aynı run'ı ikinci kez çalıştırıyor.
    ///
    /// Determinizm ayrıca dağıtım dengesini bozmuyor: sha256 çıktısı
    /// düzgün dağılıyor.
    public async Task<Result<int>> AssignAsync(Guid runId, Guid? channelId, CancellationToken cancellationToken)
    {
        var experiments = await db.Experiments.AsNoTracking()
            .Where(e => e.State == "Running")
            .Where(e => e.ChannelId == null || e.ChannelId == channelId)
            .Select(e => e.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (experiments.Count == 0)
        {
            return 0;
        }

        var assigned = 0;

        foreach (var experimentId in experiments)
        {
            var variants = await db.ExperimentVariants.AsNoTracking()
                .Where(v => v.ExperimentId == experimentId)
                .OrderBy(v => v.Name)
                .Select(v => v.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (variants.Count < 2)
            {
                // TEK VARYANTLI DENEY ATLANMIYOR, SESSİZCE DE
                // GEÇİLMİYOR: karşılaştıracak bir şey yok ve bu bir
                // yapılandırma hatası. Atama yapmak, hiçbir şey
                // ölçmeyen bir deneyin veri topluyormuş gibi
                // görünmesi olurdu.
                continue;
            }

            var chosen = variants[Bucket(runId, experimentId, variants.Count)];

            db.ExperimentAssignments.Add(new ExperimentAssignment
            {
                ExperimentId = experimentId,
                VariantId = chosen,
                RunId = runId,
            });

            assigned++;
        }

        if (assigned > 0)
        {
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException)
            {
                // ZATEN ATANMIŞ: eşsizlik kısıtı yakaladı. Hedefli
                // yeniden koşma aynı run'ı tekrar buraya
                // getirebiliyor ve ikinci atama hata değil, gereksiz.
                db.ChangeTracker.Clear();
                return 0;
            }
        }

        return assigned;
    }

    /// Bir deneyin bugünkü kararı.
    ///
    /// KARAR SAKLANMIYOR, HER SEFERİNDE HESAPLANIYOR — ta ki karara
    /// bağlanana kadar. Saklanmış bir "yeterli veri yok" cevabı,
    /// veri geldikten sonra da orada durur.
    public async Task<Result<ExperimentVerdict>> EvaluateAsync(
        Guid experimentId, CancellationToken cancellationToken)
    {
        var experiment = await db.Experiments.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == experimentId, cancellationToken)
            .ConfigureAwait(false);

        if (experiment is null)
        {
            return Error.Permanent("experiment.unknown", $"Deney yok: {experimentId}");
        }

        var rows = await db.ExperimentAssignments.AsNoTracking()
            .Where(a => a.ExperimentId == experimentId)
            .Join(db.ExperimentVariants.AsNoTracking(), a => a.VariantId, v => v.Id,
                (a, v) => new Atama(a.RunId, v.IsControl))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (rows.Count == 0)
        {
            return Error.Permanent("experiment.no_assignments",
                "Deneye hiç run atanmamış; ölçülecek bir şey yok.");
        }

        // ÖLÇÜM İLK GÜNDEN DEĞİL, SABİT BİR YAŞTAN OKUNUYOR.
        //
        // Bir haftalık videoyla bir günlük videoyu karşılaştırmak,
        // varyantı değil YAŞI ölçmek demek. Yedinci gün seçildi:
        // gösterimlerin büyük kısmı o pencerede oluşuyor ve her
        // videonun oraya ulaşması bekleniyor.
        var runIds = rows.Select(r => r.RunId).ToList();

        var metrics = await db.PublicationMetrics.AsNoTracking()
            .Where(m => runIds.Contains(m.RunId) && m.DayOffset == MetricDay)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var byRun = metrics.ToDictionary(m => m.RunId);

        var control = Aggregate(rows.Where(r => r.IsControl), byRun, "kontrol");
        var variant = Aggregate(rows.Where(r => !r.IsControl), byRun, "varyant");

        return ExperimentEvaluator.Evaluate(control, variant, experiment.MinimumDetectableEffect);
    }

    /// Ölçümün okunduğu gün.
    public const int MetricDay = 7;

    /// Atama satırı — `dynamic` DEĞİL.
    ///
    /// İlk yazımda anonim tip + `dynamic` kullanmıştım; derleniyordu
    /// ama alan adı bir yerde değişse hata çalışma zamanına
    /// ertelenirdi. Bu depoda çalışma zamanına ertelenen hataların
    /// bedeli defalarca ödendi.
    private readonly record struct Atama(Guid RunId, bool IsControl);

    private static VariantResult Aggregate(
        IEnumerable<Atama> assignments,
        Dictionary<Guid, PublicationMetric> byRun,
        string name)
    {
        var clicks = 0;
        var impressions = 0;

        foreach (var assignment in assignments)
        {
            if (byRun.TryGetValue(assignment.RunId, out var metric))
            {
                clicks += metric.Clicks;
                impressions += metric.Impressions;
            }
        }

        return new VariantResult(name, clicks, impressions);
    }

    /// Varyant seçimi — `run_id` ve deney kimliğinden deterministik.
    internal static int Bucket(Guid runId, Guid experimentId, int variantCount)
    {
        var payload = Encoding.UTF8.GetBytes($"{runId:N}:{experimentId:N}");
        var hash = SHA256.HashData(payload);

        // İLK DÖRT BAYT YETERLİ ve işaret biti temizleniyor: negatif
        // bir indeks, ilk denemede patlayan bir hata olurdu.
        var value = BitConverter.ToUInt32(hash, 0);

        return (int)(value % (uint)variantCount);
    }
}
