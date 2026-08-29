using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using BytemountsAiStudio.Contracts.Prompts;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Core.Learning;
using BytemountsAiStudio.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Persistence.Providers;

/// Deney atama ve değerlendirme (P5-02).
public sealed class ExperimentService(
    StudioDbContext db,
    TimeProvider? timeProvider = null,
    PromptRegistry? prompts = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// İSTEM KAYDI VARSAYILAN OLARAK BAĞLI (P5-05).
    ///
    /// Opsiyonel bir doğrulayıcı olsaydı, geçirmeyi unutan her çağrı
    /// istem deneylerini doğrulanmadan geçirirdi — ve var olmayan bir
    /// sürüme işaret eden kol sessizce varsayılana düşüp iki kolu
    /// aynılaştırırdı.
    private readonly Result<PromptRegistry> _prompts =
        prompts is not null ? Result.Success(prompts) : PromptRegistry.Embedded;

    /// Bir run'ı kanalın açık deneylerine atar ve ALDIĞI KOLLARI döner.
    ///
    /// Kolları dönmesi şart: çağıran (`WorkflowEngine`) bunları run
    /// bağlamına yazıyor ve kapak/başlık node'ları oradan okuyor.
    /// Sadece sayı dönseydi atama yapılır, hiçbir node'a ulaşmaz ve
    /// deney iki kolda da aynı videoyu üretirdi.
    ///
    /// ATAMA DETERMİNİSTİK: `run_id` + deney kimliğinden türeyen bir
    /// özet, varyantı seçiyor. Rastgele sayı üreteci kullanmak,
    /// aynı run'ın yeniden değerlendirilmesinde farklı varyanta
    /// düşmesi demekti — ve hedefli yeniden koşma (P2-07) tam olarak
    /// bunu yapıyor: aynı run'ı ikinci kez çalıştırıyor.
    public async Task<Result<IReadOnlyList<AssignedVariant>>> AssignAsync(
        Guid runId, Guid? channelId, CancellationToken cancellationToken)
    {
        // TRACKED: geçersiz bir deneyi burada KAPATIYORUZ. Bozuk bir
        // deneyi atlayıp bırakmak, her run'da aynı hatayı sessizce
        // tekrarlamak olurdu.
        var experiments = await db.Experiments
            .Where(e => e.State == "Running")
            .Where(e => e.ChannelId == null || e.ChannelId == channelId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (experiments.Count == 0)
        {
            return Result.Success<IReadOnlyList<AssignedVariant>>([]);
        }

        // AYNI BOYUTTA İKİ AÇIK DENEY: ikisi de uygulanamaz.
        //
        // Veritabanı kısıtı kanal+boyut ikilisini tekil tutuyor ama
        // KANALSIZ (tüm kanallara açık) bir deney, kanala özel bir
        // deneyle aynı boyutta çakışabiliyor — farklı satırlar, aynı
        // boyut. İkisini birden uygulamak tek değişken kuralını
        // deneylerin ARASINDA kırardı; birini seçmek keyfî olurdu.
        var conflicting = experiments
            .GroupBy(e => e.Dimension, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .SelectMany(g => g)
            .ToHashSet();

        foreach (var experiment in conflicting)
        {
            experiment.State = "Invalid";
            experiment.Reason =
                $"'{experiment.Dimension}' boyutunda birden fazla açık deney var; "
                + "hangisinin uygulanacağı belirsiz.";
            experiment.DecidedAt = _time.GetUtcNow();
        }

        var assigned = new List<AssignedVariant>();

        foreach (var experiment in experiments.Where(e => !conflicting.Contains(e)))
        {
            var variants = await db.ExperimentVariants.AsNoTracking()
                .Where(v => v.ExperimentId == experiment.Id)
                .OrderBy(v => v.Name)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var checkedConfigs = Validate(experiment, variants);

            if (checkedConfigs.IsFailure)
            {
                // DENEY KAPANIYOR, RUN DÜŞMÜYOR.
                //
                // Bozuk bir deney yüzünden fabrikayı durdurmak, bir
                // ölçüm hatasına üretimi feda etmek olurdu. Ama sessizce
                // atlamak da olmaz: atlanan deney haftalarca "koşuyor"
                // görünür, veri toplar ve "fark yok" der. Deney
                // GÖRÜNÜR biçimde kapatılıyor.
                experiment.State = "Invalid";
                experiment.Reason = checkedConfigs.Error.Message;
                experiment.DecidedAt = _time.GetUtcNow();
                continue;
            }

            var chosen = variants[Bucket(runId, experiment.Id, variants.Count)];

            db.ExperimentAssignments.Add(new ExperimentAssignment
            {
                ExperimentId = experiment.Id,
                VariantId = chosen.Id,
                RunId = runId,
            });

            assigned.Add(new AssignedVariant(
                experiment.Id, chosen.Id, experiment.Dimension, chosen.Name, chosen.ConfigJson));
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // ZATEN ATANMIŞ: eşsizlik kısıtı yakaladı. Hedefli yeniden
            // koşma aynı run'ı tekrar buraya getirebiliyor ve ikinci
            // atama hata değil, gereksiz.
            db.ChangeTracker.Clear();
            return Result.Success<IReadOnlyList<AssignedVariant>>([]);
        }

        return Result.Success<IReadOnlyList<AssignedVariant>>(assigned);
    }

    /// Bir deneyin GERÇEKTEN ölçebilir olduğunu doğrular.
    ///
    /// Üç kontrol, üçü de "hiçbir şey ölçmeyen deney" üretiyor:
    ///   1. Tek varyant — karşılaştıracak bir şey yok.
    ///   2. Tanınmayan ayar — sessizce düşer, iki kol aynı çıktıyı verir.
    ///   3. İki boyutta ayrışma — kazanan bilinir, NEDEN kazandığı bilinmez.
    ///
    /// Üçüncüsü `ExperimentEvaluator.SingleChangedDimension` ile
    /// yapılıyor; o fonksiyon P5-02'de yazılmıştı ve HİÇBİR YERDEN
    /// ÇAĞRILMIYORDU. Kural, çağrılana kadar bir niyet beyanıydı.
    private Result Validate(Experiment experiment, List<ExperimentVariant> variants)
    {
        if (variants.Count < 2)
        {
            return Error.Permanent("experiment.single_variant",
                "Deneyde tek varyant var; karşılaştıracak bir şey yok.");
        }

        var vocabulary = VariantVocabulary.For(experiment.Dimension);

        if (vocabulary.IsFailure)
        {
            return Result.Failure(vocabulary.Error);
        }

        var configs = new Dictionary<Guid, IReadOnlyDictionary<string, string>>();

        foreach (var variant in variants)
        {
            var parsed = VariantConfig.Parse(variant.ConfigJson);

            if (parsed.IsFailure)
            {
                return Result.Failure(parsed.Error);
            }

            var valid = VariantConfig.Validate(parsed.Value, vocabulary.Value);

            if (valid.IsFailure)
            {
                return Result.Failure(valid.Error);
            }

            if (experiment.Dimension == "prompt")
            {
                var wired = PromptExists(variant.ConfigJson);

                if (wired.IsFailure)
                {
                    return Result.Failure(wired.Error);
                }
            }

            configs[variant.Id] = parsed.Value;
        }

        var control = variants.Find(v => v.IsControl);

        if (control is null)
        {
            return Error.Permanent("experiment.no_control",
                "Deneyde kontrol kolu yok; karşılaştırmanın tabanı belirsiz.");
        }

        foreach (var variant in variants.Where(v => !v.IsControl))
        {
            var single = ExperimentEvaluator.SingleChangedDimension(
                configs[control.Id], configs[variant.Id]);

            if (single.IsFailure)
            {
                return Result.Failure(single.Error);
            }
        }

        return Result.Success();
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

    /// Deneyi karara bağlar ve KAZANANI UYGULAR (P5-07).
    ///
    /// BİR DENEYİN KAZANMASI, KAZANANIN UYGULANDIĞI ANLAMINA GELMİYOR.
    /// Karar verilip hiçbir şey değişmezse öğrenme döngüsü kapanmıyor:
    /// sistem "soru başlıklar daha iyi" diye rapor yazar ve ertesi gün
    /// yine düz başlık üretir. Faz 5'in kabul kriteri tam olarak bu
    /// halkanın kapanması.
    ///
    /// KARAR VERİLMEMİŞSE HİÇBİR ŞEY YAZILMIYOR. Saklanmış bir "yeterli
    /// veri yok" cevabı, veri geldikten sonra da orada durur.
    public async Task<Result<ExperimentVerdict>> ConcludeAsync(
        Guid experimentId, bool apply, CancellationToken cancellationToken)
    {
        var verdict = await EvaluateAsync(experimentId, cancellationToken).ConfigureAwait(false);

        if (verdict.IsFailure || !verdict.Value.IsDecided || !apply)
        {
            return verdict;
        }

        var experiment = await db.Experiments
            .FirstOrDefaultAsync(e => e.Id == experimentId, cancellationToken)
            .ConfigureAwait(false);

        if (experiment is null)
        {
            return Error.Permanent("experiment.unknown", $"Deney yok: {experimentId}");
        }

        experiment.State = "Concluded";
        experiment.Outcome = verdict.Value.Outcome.ToString();
        experiment.Reason = verdict.Value.Reason;
        experiment.DecidedAt = _time.GetUtcNow();

        if (verdict.Value.Outcome == ExperimentOutcome.VariantWins)
        {
            var applied = await ApplyWinnerAsync(experiment, cancellationToken).ConfigureAwait(false);

            if (applied.IsFailure)
            {
                return Result.Failure<ExperimentVerdict>(applied.Error);
            }
        }

        // KONTROL KAZANDIYSA HİÇBİR ŞEY UYGULANMIYOR ve bu doğru
        // davranış: kontrol zaten yürürlükteki ayar. Onu "kazanan"
        // diye yeniden yazmak, hiçbir şeyin değişmediği bir değişiklik
        // kaydı üretirdi.
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return verdict;
    }

    /// Kazanan kolun ayarını kanalın varsayılanı yapar.
    private async Task<Result> ApplyWinnerAsync(
        Experiment experiment, CancellationToken cancellationToken)
    {
        if (experiment.ChannelId is null)
        {
            // KANALSIZ DENEY UYGULANAMIYOR ve bu SESSİZ GEÇİLMİYOR.
            // Varsayılan yazılacak bir yer yok; "kazandı ama
            // uygulanmadı" durumunu gizlemek, öğrenme döngüsünün
            // kapandığı izlenimi verirdi.
            return Error.Permanent("experiment.no_channel",
                "Deney bir kanala bağlı değil; kazanan varyant hiçbir yere yazılamaz.");
        }

        var winner = await db.ExperimentVariants.AsNoTracking()
            .Where(v => v.ExperimentId == experiment.Id && !v.IsControl)
            .Select(v => v.ConfigJson)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (winner is null)
        {
            return Error.Permanent("experiment.no_variant", "Deneyde varyant kolu yok.");
        }

        var channel = await db.Channels
            .FirstOrDefaultAsync(c => c.Id == experiment.ChannelId, cancellationToken)
            .ConfigureAwait(false);

        if (channel is null)
        {
            return Error.Permanent("experiment.no_channel", $"Kanal yok: {experiment.ChannelId}");
        }

        JsonNode? root;

        try
        {
            root = JsonNode.Parse(
                string.IsNullOrWhiteSpace(channel.SettingsJson) ? "{}" : channel.SettingsJson);
        }
        catch (JsonException ex)
        {
            return Error.Permanent("experiment.bad_settings", ex.Message);
        }

        if (root is not JsonObject settings)
        {
            return Error.Permanent("experiment.bad_settings", "Kanal ayarı bir nesne değil.");
        }

        if (settings["default_variants"] is not JsonObject defaults)
        {
            defaults = [];
            settings["default_variants"] = defaults;
        }

        JsonNode? config;

        try
        {
            config = JsonNode.Parse(string.IsNullOrWhiteSpace(winner) ? "{}" : winner);
        }
        catch (JsonException ex)
        {
            return Error.Permanent("experiment.bad_config", ex.Message);
        }

        defaults[experiment.Dimension] = config;

        // AYARIN GERİ KALANI KORUNUYOR: belgenin tamamını yeniden
        // yazmak, ses ve tempo ayarlarını sessizce silmek olurdu.
        channel.SettingsJson = settings.ToJsonString();

        return Result.Success();
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

    /// Kolun işaret ettiği istem sürümünün GERÇEKTEN var olduğunu doğrular.
    ///
    /// Olmayan bir sürüme işaret eden kol, `Get` hatası verip run'ı
    /// düşürürdü — ya da daha kötüsü, bir yerde varsayılana düşülüp
    /// iki kol aynı istemi kullanırdı. İkisi de deneyin ölçtüğü şeyi
    /// yok ediyor; hatayı KAYIT anında görmek, ilk videoyu üretmeden
    /// önce görmek demek.
    private Result PromptExists(string configJson)
    {
        var parsed = PromptVariant.Parse(configJson);

        if (parsed.IsFailure)
        {
            return Result.Failure(parsed.Error);
        }

        if (_prompts.IsFailure)
        {
            return Result.Failure(_prompts.Error);
        }

        var template = _prompts.Value.Get(parsed.Value.Key, parsed.Value.Version);

        return template.IsFailure
            ? Result.Failure(template.Error)
            : Result.Success();
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
