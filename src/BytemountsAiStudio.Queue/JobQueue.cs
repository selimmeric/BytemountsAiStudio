using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Core.Observability;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Persistence;
using BytemountsAiStudio.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Queue;

public sealed record EnqueueRequest
{
    public required QueueClass Queue { get; init; }

    public Guid? RunId { get; init; }

    public string? NodeId { get; init; }

    public Guid? ChannelId { get; init; }

    public int Priority { get; init; }

    public string PayloadJson { get; init; } = "{}";

    public int MaxAttempts { get; init; } = 3;

    public DateTimeOffset? RunAfter { get; init; }
}

public sealed record LeasedJob
{
    public required Guid Id { get; init; }

    public required QueueClass Queue { get; init; }

    public Guid? RunId { get; init; }

    public string? NodeId { get; init; }

    public required string PayloadJson { get; init; }

    public required int Attempt { get; init; }

    public required int MaxAttempts { get; init; }

    public required DateTimeOffset LeaseExpiresAt { get; init; }

    /// Son deneme mi. Başarısızlıkta DLQ'ya mı gideceğini belirler.
    public bool IsFinalAttempt => Attempt >= MaxAttempts;
}

/// PostgreSQL destekli iş kuyruğu (mimari §8.2).
///
/// Temel mekanizma KİRALAMA (lease), basit "dequeue" değil. Worker işi alır,
/// bir süre için kiralar ve çalışırken kiralamayı uzatır. Worker çökerse
/// kiralama süresi dolar ve iş yeniden dağıtılır — kurtarma mekanizmasının
/// tamamı budur, ayrıca bir "worker öldü mü" tespitine gerek kalmaz.
///
/// `FOR UPDATE SKIP LOCKED` sayesinde N worker aynı anda çekebilir ve
/// hiçbiri diğerini beklemez; kilitli satırı atlar. Bu olmadan kuyruk tek
/// worker'lık olurdu.
public sealed class JobQueue(StudioDbContext db, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<Guid> EnqueueAsync(EnqueueRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var job = new Job
        {
            Queue = request.Queue,
            RunId = request.RunId,
            NodeId = request.NodeId,
            ChannelId = request.ChannelId,
            Priority = request.Priority,
            PayloadJson = request.PayloadJson,
            MaxAttempts = request.MaxAttempts,
            RunAfter = request.RunAfter ?? _time.GetUtcNow(),
            // Kanal başına adil dağıtım: tek kuyrukta bir kanalın diğerlerini
            // aç bırakmasını engelliyor (§8.2).
            FairKey = request.ChannelId?.ToString(),
        };

        db.Jobs.Add(job);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return job.Id;
    }

    /// Kanal başına aynı anda kaç iş.
    ///
    /// Tavan olmasaydı tek kanalın yirmi işi aynı anda kiralanıp
    /// diğer kanallar boş worker bulamazdı — P2-05'in tam olarak
    /// önlemeye çalıştığı durum.
    public const int MaxLeasedPerChannel = 2;

    /// Bir iş kirala. Uygun iş yoksa null.
    ///
    /// Sorgu ham SQL, çünkü `FOR UPDATE SKIP LOCKED` EF LINQ'ta ifade
    /// edilemiyor ve bu cümlenin tamamı kuyruğun doğruluğunu taşıyor.
    ///
    /// ÖNCE ADALET, SONRA SIRA (P2-05). Yalnızca öncelik ve yaşa
    /// bakan bir sıra, çok işi olan bir kanalın diğerlerini aç
    /// bırakması demekti: yirmi videoluk bir kampanya başlatan kanal,
    /// günde bir video üreten kanalın işini saatlerce bekletiyor ve
    /// ikincisi hiçbir zaman "hata" vermiyor — sadece hiç sıra
    /// alamıyor.
    public async Task<LeasedJob?> LeaseAsync(
        QueueClass queue,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        var expiresAt = now.Add(leaseDuration);

        var channel = await NextChannelAsync(queue, now, cancellationToken).ConfigureAwait(false);

        var rows = await LeaseRowsAsync(queue, workerId, now, expiresAt, channel, cancellationToken)
            .ConfigureAwait(false);

        if (rows.Count == 0 && channel is not null)
        {
            // ADALET CANLILIĞI ENGELLEMİYOR.
            //
            // Seçilen kanalın işi bu arada başkası tarafından alınmış
            // olabilir ve kanala bağlı olmayan işler (bakım, deneme)
            // hiçbir kanalın payına girmiyor. İkinci deneme olmasaydı
            // worker eli boş dönerdi: adalet uğruna hiç iş yapmamak,
            // adaletsizlikten kötü.
            rows = await LeaseRowsAsync(queue, workerId, now, expiresAt, null, cancellationToken)
                .ConfigureAwait(false);
        }

        if (rows.Count == 0)
        {
            return null;
        }

        var row = rows[0];

        return new LeasedJob
        {
            Id = row.Id,
            Queue = Enum.Parse<QueueClass>(row.Queue),
            RunId = row.RunId,
            NodeId = row.NodeId,
            PayloadJson = row.PayloadJson,
            Attempt = row.Attempt,
            MaxAttempts = row.MaxAttempts,
            LeaseExpiresAt = row.LeaseExpiresAt,
        };
    }

    /// Sıradaki işi hangi kanaldan almalı (P2-05).
    ///
    /// Karar SAF bir fonksiyonda (`FairScheduler.NextChannel`); burası
    /// yalnızca sayıları topluyor. Ayrım, adaleti üç kanallı bir yük
    /// testi koşturmadan sınayabilmek için — ve o testte gerçek bir
    /// tasarım açığı bulundu.
    private async Task<Guid?> NextChannelAsync(
        QueueClass queue, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var queueName = queue.ToString();

        // TEK SORGUDA üç sayı. Ayrı sorgular, arada değişen bir
        // kuyrukta tutarsız bir resim verirdi: bekleyen sayısını bir
        // andan, koşan sayısını başka bir andan okumak.
        var loads = await db.Jobs.AsNoTracking()
            .Where(j => j.Queue == queue && j.ChannelId != null)
            .Where(j => (j.State == JobState.Pending && j.RunAfter <= now)
                        || j.State == JobState.Leased)
            .GroupBy(j => j.ChannelId!.Value)
            .Select(g => new
            {
                ChannelId = g.Key,
                Running = g.Count(j => j.State == JobState.Leased),
                Waiting = g.Count(j => j.State == JobState.Pending),
                Oldest = g.Where(j => j.State == JobState.Pending)
                    .Min(j => (DateTimeOffset?)j.CreatedAt),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (loads.Count <= 1)
        {
            // TEK KANAL VARSA ADALET SORUSU YOK ve sormamak gerekiyor:
            // ikinci bir sorgu, tek kanallı bir kurulumda her kiralama
            // için boşuna maliyet olurdu.
            return null;
        }

        // GEÇMİŞ PAY ayrı bir sorgu: bitmiş işler yukarıdaki
        // kümede yok. Bu ölçüt olmadan, işler hızlı bittiğinde koşan
        // sayısı hep sıfır kalıyor ve seçim kimlik sırasına düşüyor —
        // en küçük kimlikli kanal her turu kazanıp diğerlerini aç
        // bırakıyor.
        var since = now - RecentWindow;

        var served = await db.Jobs.AsNoTracking()
            .Where(j => j.Queue == queue && j.ChannelId != null
                        && j.State == JobState.Succeeded && j.CompletedAt >= since)
            .GroupBy(j => j.ChannelId!.Value)
            .Select(g => new { ChannelId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ChannelId, x => x.Count, cancellationToken)
            .ConfigureAwait(false);

        var input = loads
            .Select(l => new ChannelLoad(l.ChannelId, l.Running, l.Waiting, l.Oldest)
            {
                RecentlyServed = served.GetValueOrDefault(l.ChannelId),
            })
            .ToList();

        return FairScheduler.NextChannel(input, MaxLeasedPerChannel);
    }

    /// "Yakın geçmiş" ne kadar.
    ///
    /// ON DAKİKA: geçmiş payın amacı uzun vadeli hakkaniyet değil,
    /// az önce sıra almış bir kanalın hemen tekrar almasını
    /// engellemek. Uzun bir pencere, sabah çok iş almış bir kanalı
    /// akşama kadar cezalandırırdı.
    private static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(10);

    private Task<List<JobRow>> LeaseRowsAsync(
        QueueClass queue, string workerId, DateTimeOffset now, DateTimeOffset expiresAt,
        Guid? channelId, CancellationToken cancellationToken)
        => db.Database
            .SqlQuery<JobRow>($"""
                UPDATE jobs
                SET state = 'Leased',
                    leased_by = {workerId},
                    lease_expires_at = {expiresAt},
                    attempt = attempt + 1
                WHERE id = (
                    SELECT j.id
                    FROM jobs j
                    LEFT JOIN channels c ON c.id = j.channel_id
                    WHERE j.state = 'Pending'
                      AND j.queue = {queue.ToString()}
                      AND j.run_after <= {now}
                      AND COALESCE(c.is_paused, false) = false
                      AND ({channelId}::uuid IS NULL OR j.channel_id = {channelId})
                    ORDER BY j.priority DESC, j.run_after, j.created_at
                    FOR UPDATE OF j SKIP LOCKED
                    LIMIT 1
                )
                RETURNING id, queue, run_id, node_id, payload_json,
                          attempt, max_attempts, lease_expires_at
                """)
            .ToListAsync(cancellationToken);

    /// Kiralamayı uzat (heartbeat).
    ///
    /// Uzun süren işler — özellikle render — kiralama süresinden uzun sürer.
    /// Heartbeat olmadan ya kiralama süresini saatlerce vermek (çöken worker'ın
    /// işi saatlerce takılı kalır) ya da uzun işi kaybetmek gerekirdi.
    public async Task<bool> HeartbeatAsync(
        Guid jobId, string workerId, TimeSpan extension, CancellationToken cancellationToken = default)
    {
        var expiresAt = _time.GetUtcNow().Add(extension);

        var affected = await db.Database.ExecuteSqlAsync($"""
            UPDATE jobs
            SET lease_expires_at = {expiresAt}
            WHERE id = {jobId} AND state = 'Leased' AND leased_by = {workerId}
            """, cancellationToken).ConfigureAwait(false);

        return affected == 1;
    }

    public async Task CompleteAsync(Guid jobId, CancellationToken cancellationToken = default)
        => await db.Database.ExecuteSqlAsync($"""
            UPDATE jobs
            SET state = 'Succeeded', leased_by = NULL, lease_expires_at = NULL,
                last_error = NULL, completed_at = {_time.GetUtcNow()}
            WHERE id = {jobId}
            """, cancellationToken).ConfigureAwait(false);

    /// Başarısızlığı hata SINIFINA göre işle (§8.4).
    ///
    /// Burası kuyruğun en kolay yanlış yapılan yeri: dört hata sınıfının
    /// dördü de farklı davranır.
    public async Task<JobDisposition> FailAsync(
        LeasedJob job, Error error, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(error);

        var now = _time.GetUtcNow();

        // Hata metni `last_error` kolonuna gidiyor; bir saglayici istisnasi
        // istegin basligini ya da URL'sini icerebiliyor. Suzgec cikista
        // duruyor (P1-01) - metin veritabanina girmeden once.
        var text = SecretRedactor.Redact(error.ToString());

        // Kaynak hatası bir BAŞARISIZLIK DEĞİL. Kota dolduğunda ya da bütçe
        // bittiğinde işi tüketmek yanlış olurdu; deneme sayacı bile artmamalı.
        if (error.Kind == ErrorKind.Resource)
        {
            var retryAt = now.Add(error.RetryAfter ?? TimeSpan.FromMinutes(15));

            await db.Database.ExecuteSqlAsync($"""
                UPDATE jobs
                SET state = 'Pending', leased_by = NULL, lease_expires_at = NULL,
                    run_after = {retryAt}, attempt = attempt - 1, last_error = {text}
                WHERE id = {job.Id}
                """, cancellationToken).ConfigureAwait(false);

            return JobDisposition.Deferred;
        }

        // Kalıcı hata tekrar denenmez: aynı girdiyle aynı sonuç gelir,
        // tek kazancı boşa harcanan para ve zaman olurdu.
        var isRetryable = error.Kind == ErrorKind.Transient && !job.IsFinalAttempt;

        if (isRetryable)
        {
            var delay = Backoff(job.Attempt, error.RetryAfter);

            await db.Database.ExecuteSqlAsync($"""
                UPDATE jobs
                SET state = 'Pending', leased_by = NULL, lease_expires_at = NULL,
                    run_after = {now.Add(delay)}, last_error = {text}
                WHERE id = {job.Id}
                """, cancellationToken).ConfigureAwait(false);

            return JobDisposition.Retried;
        }

        var deadLetter = error.Kind is ErrorKind.Poison || job.IsFinalAttempt;

        await db.Database.ExecuteSqlAsync($"""
            UPDATE jobs
            SET state = {(deadLetter ? nameof(JobState.DeadLettered) : nameof(JobState.Failed))},
                leased_by = NULL, lease_expires_at = NULL, last_error = {text},
                completed_at = {now}
            WHERE id = {job.Id}
            """, cancellationToken).ConfigureAwait(false);

        return deadLetter ? JobDisposition.DeadLettered : JobDisposition.Failed;
    }

    /// Süresi dolmuş kiralamaları geri al.
    ///
    /// Worker çökme kurtarmasının tamamı bu: ölen worker'ı tespit etmeye
    /// çalışmıyoruz, yalnızca kiralamanın süresinin dolmasını bekliyoruz.
    /// Deneme sayacı zaten kiralama sırasında arttığı için sonsuz döngü olmaz.
    public async Task<int> ReclaimExpiredAsync(CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();

        return await db.Database.ExecuteSqlAsync($"""
            UPDATE jobs
            SET state = CASE WHEN attempt >= max_attempts THEN 'DeadLettered' ELSE 'Pending' END,
                leased_by = NULL,
                lease_expires_at = NULL,
                last_error = COALESCE(last_error, 'Kiralama suresi doldu; worker cokmus olabilir.')
            WHERE state = 'Leased' AND lease_expires_at < {now}
            """, cancellationToken).ConfigureAwait(false);
    }

    /// Üstel geri çekilme + sabit jitter.
    ///
    /// Jitter olmadan aynı anda başarısız olan yüz iş aynı anda tekrar dener
    /// ve sağlayıcıyı ikinci kez devirir (thundering herd). Rastgelelik yerine
    /// iş kimliğinden türetilse determinizm korunurdu; burada deneme sayısına
    /// bağlı sabit bir kayma yeterli.
    internal static TimeSpan Backoff(int attempt, TimeSpan? retryAfter)
    {
        if (retryAfter is { } explicitDelay)
        {
            return explicitDelay;
        }

        var seconds = Math.Min(300, Math.Pow(2, Math.Clamp(attempt, 1, 8)));
        return TimeSpan.FromSeconds(seconds + (attempt % 3));
    }

    private sealed record JobRow(
        Guid Id,
        string Queue,
        Guid? RunId,
        string? NodeId,
        string PayloadJson,
        int Attempt,
        int MaxAttempts,
        DateTimeOffset LeaseExpiresAt);
}

public enum JobDisposition
{
    /// Yeniden denenecek (geçici hata).
    Retried = 0,

    /// Ertelendi (kaynak yok) — başarısızlık değil.
    Deferred = 1,

    /// Kalıcı hata, tekrar denenmeyecek.
    Failed = 2,

    /// Ölü mektup kuyruğunda; insan müdahalesi gerekiyor.
    DeadLettered = 3,
}
