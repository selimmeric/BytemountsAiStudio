using System.Text.Json;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace BytemountsAiStudio.Persistence.Providers;

/// Konu havuzu ve tekillik kontrolü (P1-08, ADR-003, §20.5).
///
/// Tekillik EMBEDDING benzerliğiyle: "Dünyanın En Tehlikeli 10 Yeri"
/// ile "En Tehlikeli 10 Bölge" metin olarak farklı, anlam olarak aynı.
/// Dizge karşılaştırması bunu yakalayamıyor ve yakalayamadığı için
/// aynı konu ikinci kez üretiliyordu.
///
/// Kapsam KANAL + DİL (§20.5): TR kanalında yayınlanan bir konu, EN
/// kanalında tekrar DEĞİL — farklı izleyici. Kapsamı daraltmamak,
/// çok dilli üretimi ilk günden imkânsız kılardı.
public sealed class TopicPool(StudioDbContext db)
{
    /// Tekillik kontrolünde bakılacak en fazla kayıt.
    ///
    /// pgvector sıralamayı veritabanında yapıyor; buradaki sınır
    /// yalnızca "en benzer kaç tanesine bakalım" sorusu. Bir tanesi
    /// yeter ama birkaçını görmek, eşiğin doğru yerde olup olmadığını
    /// anlamayı kolaylaştırıyor.
    private const int NeighbourCount = 5;

    /// Verilen konuya en benzer YAYINLANMIŞ konular.
    ///
    /// Yalnızca `Published` sayılıyor: reddedilmiş ya da başarısız bir
    /// konu tekrar engeli olmamalı — zaten yayınlanmadı.
    public async Task<Result<IReadOnlyList<(string Title, double Similarity)>>> SimilarPublishedAsync(
        Guid? channelId,
        string language,
        IReadOnlyList<float> embedding,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(embedding);

        if (embedding.Count == 0)
        {
            // Gömme yoksa tekillik kontrolü YAPILAMAZ. Boş liste dönmek
            // "benzer yok" demek olurdu ve bu yanlış bir güvence;
            // çağıran taraf farkı bilsin diye hata dönüyor.
            return Error.Permanent("topic.no_embedding",
                "Gömme vektörü yok; tekillik kontrolü yapılamaz.");
        }

        var vector = new Vector(embedding.ToArray().AsMemory());

        var neighbours = await db.Topics
            .AsNoTracking()
            .Where(t => t.State == TopicState.Published
                        && t.Language == language
                        && t.ChannelId == channelId
                        && t.Embedding != null)
            // pgvector'ün kosinüs MESAFESİ: 0 = aynı, 2 = zıt.
            // Benzerlik = 1 - mesafe.
            .OrderBy(t => t.Embedding!.CosineDistance(vector))
            .Take(NeighbourCount)
            .Select(t => new { t.Title, Distance = t.Embedding!.CosineDistance(vector) })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Result.Success<IReadOnlyList<(string, double)>>(
            [.. neighbours.Select(n => (n.Title, 1.0 - n.Distance))]);
    }

    /// Aday konuyu havuza alır ya da reddeder.
    ///
    /// Karar SAF bir fonksiyonda (`TopicPolicy.Decide`); burası yalnızca
    /// veriyi toplayıp kararı uyguluyor. Ayrım, eşikleri veritabanı
    /// olmadan sınayabilmek için.
    public async Task<Result<TopicDecision>> AdmitAsync(
        Guid? channelId,
        string language,
        string title,
        TopicScore score,
        IReadOnlyList<float>? embedding,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(score);

        double? similarity = null;

        if (embedding is { Count: > 0 })
        {
            var similar = await SimilarPublishedAsync(channelId, language, embedding, cancellationToken)
                .ConfigureAwait(false);

            if (similar.IsSuccess && similar.Value.Count > 0)
            {
                similarity = similar.Value.Max(s => s.Similarity);
            }
        }

        // AĞIRLIKLAR KANALDAN, ÇAĞIRANDAN DEĞİL (P5-04).
        //
        // Parametre olsaydı, geçirmeyi unutan her çağrı sessizce
        // varsayılan ağırlıkları kullanırdı ve kanalın ayarı hiçbir
        // yerde işe yaramazdı. Bu depoda aynı hata ses ve yazı tipi
        // ayarlarında bir kez yapıldı (P3-01).
        var weights = await WeightsAsync(channelId, cancellationToken).ConfigureAwait(false);

        var decision = TopicPolicy.Decide(score, similarity, weights);

        db.Topics.Add(new Topic
        {
            ChannelId = channelId,
            Title = title,
            Language = language,
            ScoresJson = JsonSerializer.Serialize(new
            {
                demand = score.Demand,
                fit = score.Fit,
                sourceability = score.Sourceability,
                visualizability = score.Visualizability,
                freshness = score.Freshness,
                risk = score.Risk,
                rationale = score.Rationale,
                // Benzerlik de kaydediliyor: bir konu tekrar diye
                // reddedildiğinde "neye benzedi" sorusu sorulacak.
                similarity,
            }),
            OverallScore = score.Weighted(weights),
            State = decision switch
            {
                TopicDecision.Accept => TopicState.Queued,
                TopicDecision.Reject => TopicState.Rejected,
                _ => TopicState.New,
            },
            RejectedReason = decision == TopicDecision.Reject ? RejectReason(score, similarity, weights) : null,
            Embedding = embedding is { Count: > 0 }
                ? new Vector(embedding.ToArray().AsMemory())
                : null,
        });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success(decision);
    }

    /// Sıradaki konuyu alır ve `InProgress` yapar.
    ///
    /// Okuma ve durum değişikliği TEK işlemde: iki worker aynı konuyu
    /// alıp aynı videoyu iki kez üretmesin. `FOR UPDATE SKIP LOCKED`,
    /// iş kuyruğunda kullandığımız desenin aynısı.
    public async Task<Result<Topic>> TakeNextAsync(
        Guid? channelId, string language, CancellationToken cancellationToken)
    {
        var topic = await db.Topics
            .FromSql($"""
                SELECT * FROM topics
                WHERE state = 'Queued'
                  AND language = {language}
                  AND (channel_id = {channelId} OR ({channelId}::uuid IS NULL AND channel_id IS NULL))
                ORDER BY overall_score DESC, created_at ASC
                LIMIT 1
                FOR UPDATE SKIP LOCKED
                """)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (topic is null)
        {
            // Konu YOKLUĞU bir hata değil, bir durum: havuz boşsa
            // üretim beklemeli, düşmemeli. Kaynak hatası tam olarak bu
            // anlamı taşıyor (ADR-011).
            return Error.Resource("topic.pool_empty",
                $"'{language}' için kuyrukta konu yok.", TimeSpan.FromMinutes(30));
        }

        topic.State = TopicState.InProgress;
        topic.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success(topic);
    }

    /// Havuzda bekleyen konu sayısı. Otomatik doldurma (P2-01) buna
    /// bakacak.
    public Task<int> QueuedCountAsync(Guid? channelId, string language, CancellationToken cancellationToken)
        => db.Topics.CountAsync(
            t => t.State == TopicState.Queued && t.Language == language && t.ChannelId == channelId,
            cancellationToken);

    /// Havuzun doldurma kararı için gereken durumu (P2-01).
    ///
    /// TEK SORGUDA iki sayı: `Queued` hazır konular, `New` ise
    /// üretilmiş ama henüz skorlanmamış olanlar. İkisini ayrı
    /// sorgularla almak, arada değişen bir havuzda tutarsız bir
    /// resim verirdi.
    ///
    /// `New` "üretiliyor" sayılıyor: konu üretildi ama havuza
    /// alınmadı. Onu saymamak, üretim sürerken ikinci bir doldurma
    /// turunun tetiklenmesi demekti.
    public async Task<PoolStatus> StatusAsync(
        Guid? channelId, string language, int dailyTarget, CancellationToken cancellationToken)
    {
        var counts = await db.Topics.AsNoTracking()
            .Where(t => t.Language == language && t.ChannelId == channelId)
            .Where(t => t.State == TopicState.Queued || t.State == TopicState.New)
            .GroupBy(t => t.State)
            .Select(g => new { State = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var ready = counts.FirstOrDefault(c => c.State == TopicState.Queued)?.Count ?? 0;
        var producing = counts.FirstOrDefault(c => c.State == TopicState.New)?.Count ?? 0;

        return new PoolStatus(ready, producing, dailyTarget);
    }

    /// Kanalın skorlama ağırlıkları.
    ///
    /// Kanal yoksa varsayılan: kanalsız konular (genel havuz) da
    /// skorlanıyor ve onlar için tek makul taban bugünkü ağırlıklar.
    private async Task<ScoreWeights> WeightsAsync(Guid? channelId, CancellationToken cancellationToken)
    {
        if (channelId is null)
        {
            return ScoreWeights.Default;
        }

        var settingsJson = await db.Channels.AsNoTracking()
            .Where(c => c.Id == channelId)
            .Select(c => c.SettingsJson)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return ChannelSettings.Parse(settingsJson).ScoreWeights;
    }

    /// Reddin GEREKÇESİ — hangi kural devreye girdi.
    ///
    /// "Skor düşük" yetmez: risk vetosu mu, tekrar mı, yoksa gerçekten
    /// düşük skor mu? Üçü farklı düzeltme gerektiriyor.
    internal static string RejectReason(
        TopicScore score, double? similarity, ScoreWeights? weights = null)
    {
        if (!score.IsValid)
        {
            return "Skor boyutları geçersiz aralıkta.";
        }

        if (score.Risk >= TopicPolicy.RiskVeto)
        {
            return $"Risk skoru {score.Risk}, veto eşiği {TopicPolicy.RiskVeto}.";
        }

        if (similarity >= TopicPolicy.SimilarityThreshold)
        {
            return $"Daha önce yayınlanan bir konuya çok benziyor (benzerlik {similarity:0.###}).";
        }

        return $"Toplam skor {score.Weighted(weights ?? ScoreWeights.Default):0.#}, "
            + $"red eşiği {TopicPolicy.RejectThreshold}.";
    }
}
