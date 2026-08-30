using System.Globalization;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Time;
using BytemountsAiStudio.Media.Timeline;

namespace BytemountsAiStudio.Quality;

/// QC'nin girdisi: ölçülen değerler.
///
/// Ölçüm YAPMIYOR, ölçülmüş değeri alıyor. Sebep: ffprobe/ffmpeg
/// çağırmak yan etkili bir iş ve testlerin dış süreç gerektirmesi
/// demek olurdu. Kontroller saf fonksiyon kalınca 12 bozuk senaryoyu
/// milisaniyelerde koşabiliyoruz.
public sealed record QcInput
{
    public required TimelineDocument Timeline { get; init; }

    /// ffprobe çıktısı. Null = render henüz yapılmamış; render'a bağlı
    /// kontroller o zaman düşüyor, "yok sayılıyor" değil.
    public MediaMeasurements? Media { get; init; }

    /// Yayın metadata'sı. Null = henüz üretilmemiş.
    public PublishMetadata? Metadata { get; init; }

    /// Senaryodaki iddiaların kaynağa bağlı olup olmadığı (§2.2/8).
    /// Null = iddia çıkarımı (P1-10) henüz koşmamış.
    public ClaimCoverage? Claims { get; init; }

    /// Aynı konunun daha önce yayınlanıp yayınlanmadığı (ADR-003).
    /// Null = tekillik kontrolü (P1-08) henüz koşmamış.
    public UniquenessCheck? Uniqueness { get; init; }
}

/// ffprobe/ffmpeg ile ÖLÇÜLEN değerler.
public sealed record MediaMeasurements
{
    public required double DurationSeconds { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public required bool HasAudio { get; init; }

    /// Ortalama ses seviyesi (LUFS). Hedef -14…-18.
    public double? LoudnessLufs { get; init; }

    /// Gerçek tepe (dBTP). -1'in üstü kırpılma demek.
    public double? TruePeakDb { get; init; }

    /// Konuşma olan sürenin toplam süreye oranı.
    public double? SpeechRatio { get; init; }

    public long SizeBytes { get; init; }
}

public sealed record PublishMetadata
{
    public required string Title { get; init; }

    public required string Description { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    /// Thumbnail dosyası üretildiyse ölçüleri.
    public ThumbnailInfo? Thumbnail { get; init; }
}

public sealed record ThumbnailInfo(int Width, int Height, long SizeBytes);

public sealed record ClaimCoverage(int TotalClaims, int SourcedClaims)
{
    /// İddiaları doğrulayan model, senaryoyu YAZAN modelle aynı mı.
    ///
    /// ***BU ALAN KAYDEDİLİYOR VE HİÇBİR YERDE OKUNMUYORDU.***
    /// `claims.same_model` kolonuna yazılıyor, node çıktısında
    /// duruyor ve QC girdisine hiç girmiyordu — yani "bu iddiaları
    /// uyduran modelin kendisi doğruladı" bilgisi hiçbir karara
    /// dokunmuyordu.
    ///
    /// Aynı modelin kendi yazdığını doğrulaması ZAYIF bir doğrulama:
    /// model kendi ürettiği hatayı hata olarak görmüyor. Ayrı bir
    /// model kullanmak bu bağı kırıyor.
    public bool SameModel { get; init; }

    public bool AllSourced => TotalClaims == 0 || SourcedClaims >= TotalClaims;
}

public sealed record UniquenessCheck(bool IsUnique, double? Similarity, string? ConflictingTitle);

/// Mekanik kalite kontrolü (P1-21, §14.1).
///
/// On iki kontrol, hepsi SAF FONKSİYON: model çağırmıyor, para
/// harcamıyor, aynı girdiye aynı cevabı veriyor. Semantik kontroller
/// (görsel alaka, ton, tıklama tuzağı) ayrı bir iş ve model gerektiriyor;
/// mekanik olanları oraya karıştırmak, ücretsiz ve kesin olan bir kapıyı
/// pahalı ve olasılıklı bir kapının arkasına koymak olurdu.
public static class MechanicalQc
{
    /// Süre toleransı. Timeline 13.000 ms diyorsa render 12.870–13.130
    /// arasında olmalı.
    ///
    /// %1 dar ama kasıtlı: bundan büyük bir sapma, ses ile görselin
    /// ayrıştığı anlamına geliyor ve bu videoda görülüyor.
    private const double DurationTolerance = 0.01;

    /// YouTube başlık sınırı. Aşarsa upload REDDEDİLİYOR — bu yüzden
    /// bloklayıcı: kırpmayı burada yapmak yerine hatayı burada görmek
    /// istiyoruz, çünkü kırpılmış başlık genellikle anlamsız oluyor.
    private const int MaxTitleLength = 100;

    private const int MaxDescriptionLength = 5000;

    /// Etiketlerin toplam karakter sınırı (ayraçlar dahil).
    private const int MaxTagsTotalLength = 500;

    public static QualityReport Run(QcInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new QualityReport
        {
            Checks =
            [
                DurationMatches(input),
                ResolutionMatches(input),
                HasAudio(input),
                LoudnessInRange(input),
                NoClipping(input),
                SpeechRatioReasonable(input),
                EverySceneHasVisual(input),
                CaptionsWithinAudio(input),
                ClaimsAreSourced(input),
                ClaimsIndependentlyChecked(input),
                MetadataWithinLimits(input),
                ThumbnailValid(input),
                TopicIsUnique(input),
                MusicIsLicensed(input),
            ],
        };
    }

    // 13 — Müzik lisans kanıtı taşıyor (P2-09, §2.3/13)
    //
    // BLOKLAYICI ve görsellerden daha sert bir kural. Content ID
    // sistemi müziği otomatik tanıyor; bir talep, kanalın o videodan
    // gelen gelirinin tamamını götürüyor ve bazen kanalın tamamına
    // ihtar geliyor. Bir görselde atıf eksikliği düzeltilebilir bir
    // kusur, müzikte düzeltilemez bir hasar.
    //
    // MÜZİK YOKSA KONTROL GEÇİYOR: müziksiz bir video tamamen
    // geçerli. Düşürmek, müziksiz her videoyu bloklamak olurdu.
    private static CheckResult MusicIsLicensed(QcInput input)
    {
        const string code = "qc.music_license";
        const string name = "Müzik lisans kanıtı taşıyor";

        if (input.Timeline?.Audio.Music is not { } music)
        {
            return new CheckResult
            {
                Code = code,
                Name = name,
                Passed = true,
                Severity = CheckSeverity.Blocking,
                Weight = 5,
                Detail = "müzik yok",
            };
        }

        var complete = music.License is { } license && license.IsComplete;

        return new CheckResult
        {
            Code = code,
            Name = name,
            Passed = complete,
            Severity = CheckSeverity.Blocking,
            Weight = 5,
            Detail = complete
                ? music.License!.Name
                : music.License is null
                    ? "lisans kaydı YOK"
                    : "lisans eksik: atıf gerekiyor ama yazar bilinmiyor",
            // Müzik seçimi timeline'da yapılıyor; oraya dönmek
            // gerekiyor. Senaryoya dönmek her şeyi yeniden üretirdi.
            Target = complete ? RetryTarget.None : RetryTarget.Timeline,
        };
    }

    // 1 — Video süresi timeline ile uyumlu (±%1)
    private static CheckResult DurationMatches(QcInput input)
    {
        if (input.Media is not { } media)
        {
            return Missing("qc.duration", "Video süresi timeline ile uyumlu", RetryTarget.Render);
        }

        var expected = input.Timeline.Duration.TotalSeconds;
        var drift = Math.Abs(media.DurationSeconds - expected);
        var allowed = Math.Max(expected * DurationTolerance, 0.05);

        return new CheckResult
        {
            Code = "qc.duration",
            Name = "Video süresi timeline ile uyumlu",
            Passed = drift <= allowed,
            Severity = CheckSeverity.Blocking,
            Weight = 12,
            Target = RetryTarget.Render,
            Detail = FormattableString.Invariant(
                $"beklenen {expected:0.###} sn, ölçülen {media.DurationSeconds:0.###} sn (sapma {drift:0.###} sn)"),
        };
    }

    // 2 — Çözünürlük ve en-boy hedefe uygun
    private static CheckResult ResolutionMatches(QcInput input)
    {
        if (input.Media is not { } media)
        {
            return Missing("qc.resolution", "Çözünürlük hedefe uygun", RetryTarget.Render);
        }

        var canvas = input.Timeline.Canvas;
        var matches = media.Width == canvas.Width && media.Height == canvas.Height;

        return new CheckResult
        {
            Code = "qc.resolution",
            Name = "Çözünürlük hedefe uygun",
            Passed = matches,
            Severity = CheckSeverity.Blocking,
            Weight = 10,
            Target = RetryTarget.Render,
            Detail = FormattableString.Invariant(
                $"beklenen {canvas.Width}x{canvas.Height}, ölçülen {media.Width}x{media.Height}"),
        };
    }

    // 3 — Ses kanalı var, sessiz değil
    private static CheckResult HasAudio(QcInput input)
    {
        if (input.Media is not { } media)
        {
            return Missing("qc.audio_present", "Ses kanalı var ve sessiz değil", RetryTarget.Render);
        }

        // Sessizlik ölçülmüşse ona bakılıyor. -60 LUFS pratikte sessiz;
        // yalnızca "kanal var mı" diye bakmak, boş bir ses kanalıyla
        // üretilmiş videoyu geçirirdi ve bu en sinsi hatalardan biri.
        var silent = media.LoudnessLufs is { } lufs && lufs < -60;

        return new CheckResult
        {
            Code = "qc.audio_present",
            Name = "Ses kanalı var ve sessiz değil",
            Passed = media.HasAudio && !silent,
            Severity = CheckSeverity.Blocking,
            Weight = 12,
            Target = RetryTarget.Render,
            Detail = media.HasAudio
                ? FormattableString.Invariant($"ses var, seviye {media.LoudnessLufs?.ToString("0.#", CultureInfo.InvariantCulture) ?? "ölçülmedi"} LUFS")
                : "ses kanalı yok",
        };
    }

    // 4 — Loudness hedef aralıkta (-14…-18 LUFS)
    private static CheckResult LoudnessInRange(QcInput input)
    {
        if (input.Media?.LoudnessLufs is not { } lufs)
        {
            return Missing("qc.loudness", "Ses seviyesi hedef aralıkta", RetryTarget.Render);
        }

        // Aralık platform önerisi. Altında kalırsa izleyici sesi
        // açmak zorunda, üstünde kalırsa platform kendisi kısıyor ve
        // dinamik aralık bozuluyor.
        var inRange = lufs is >= -18 and <= -14;

        return new CheckResult
        {
            Code = "qc.loudness",
            Name = "Ses seviyesi hedef aralıkta (-18…-14 LUFS)",
            Passed = inRange,
            Severity = CheckSeverity.Blocking,
            Weight = 8,
            Target = RetryTarget.Render,
            Detail = FormattableString.Invariant($"ölçülen {lufs:0.#} LUFS"),
        };
    }

    // 5 — Clipping (true peak > -1 dBTP) — UYARI
    private static CheckResult NoClipping(QcInput input)
    {
        if (input.Media?.TruePeakDb is not { } peak)
        {
            return Missing("qc.clipping", "Kırpılma yok", RetryTarget.None, CheckSeverity.Warning);
        }

        return new CheckResult
        {
            Code = "qc.clipping",
            Name = "Kırpılma yok (tepe ≤ -1 dBTP)",
            Passed = peak <= -1.0,
            Severity = CheckSeverity.Warning,
            Weight = 4,
            Target = RetryTarget.Render,
            Detail = FormattableString.Invariant($"tepe {peak:0.#} dBTP"),
        };
    }

    // 6 — Müzik/konuşma oranı makul — UYARI
    private static CheckResult SpeechRatioReasonable(QcInput input)
    {
        if (input.Media?.SpeechRatio is not { } ratio)
        {
            return Missing("qc.speech_ratio", "Konuşma oranı makul", RetryTarget.None, CheckSeverity.Warning);
        }

        // Çok düşükse video sessiz boşluklarla dolu; çok yüksekse nefes
        // alacak yer yok. İkisi de izlenme süresini düşürüyor ama
        // hiçbiri "bozuk video" değil — o yüzden uyarı.
        return new CheckResult
        {
            Code = "qc.speech_ratio",
            Name = "Konuşma oranı makul (%60–%98)",
            Passed = ratio is >= 0.60 and <= 0.98,
            Severity = CheckSeverity.Warning,
            Weight = 3,
            Target = RetryTarget.Timeline,
            Detail = FormattableString.Invariant($"konuşma oranı %{ratio * 100:0}"),
        };
    }

    // 7 — Her sahnede görsel var VE sahneler videoyu boşluksuz kaplıyor
    //
    // Sartname "her sahnede gorsel var" diyor ama tip sistemi bunu
    // zaten garanti ediyor: `Scene.Visual` zorunlu ve `AssetRef` bir
    // struct. Gercekte olabilecek uc sey var ve kontrol onlara bakiyor:
    //   - hic sahne yok
    //   - varlik referansi BOS (default struct) - tipi gecerli, degeri degil
    //   - sahneler arasinda BOSLUK var, ki bu videoda siyah kare demek
    // Yalnizca sartnamenin harfini uygulasaydik hicbir sey sinanmazdi.
    private static CheckResult EverySceneHasVisual(QcInput input)
    {
        var scenes = input.Timeline.Scenes;

        if (scenes.Count == 0)
        {
            return new CheckResult
            {
                Code = "qc.scene_visuals",
                Name = "Her sahnede görsel var",
                Passed = false,
                Severity = CheckSeverity.Blocking,
                Weight = 12,
                Target = RetryTarget.Visuals,
                Detail = "hiç sahne yok",
            };
        }

        var empty = scenes.Count(s => string.IsNullOrEmpty(s.Visual.Asset.Sha256));
        var ordered = scenes.OrderBy(s => s.Range.Start.Value).ToList();
        var gaps = 0;
        var cursor = Ms.Zero;

        foreach (var scene in ordered)
        {
            if (scene.Range.Start != cursor)
            {
                gaps++;
            }

            cursor = scene.Range.End;
        }

        var covers = cursor == input.Timeline.Duration;

        return new CheckResult
        {
            Code = "qc.scene_visuals",
            Name = "Her sahnede görsel var, sahneler boşluksuz",
            Passed = empty == 0 && gaps == 0 && covers,
            Severity = CheckSeverity.Blocking,
            Weight = 12,
            Target = RetryTarget.Visuals,
            Detail = FormattableString.Invariant(
                $"{scenes.Count} sahne; {empty} boş görsel, {gaps} boşluk, kapsam {cursor.Value}/{input.Timeline.Duration.Value} ms"),
        };
    }

    // 8 — Altyazı süresi ses süresini aşmıyor
    private static CheckResult CaptionsWithinAudio(QcInput input)
    {
        var captions = input.Timeline.Captions;

        if (captions is null || captions.Cues.Count == 0)
        {
            // Altyazısız video geçerli bir çıktı olabilir; eksikliği
            // burada bloklamak yanlış olurdu. Ama kayda geçiyor.
            return new CheckResult
            {
                Code = "qc.caption_bounds",
                Name = "Altyazı süresi ses süresini aşmıyor",
                Passed = true,
                Severity = CheckSeverity.Blocking,
                Weight = 8,
                Detail = "altyazı yok",
            };
        }

        var total = input.Timeline.Duration;
        var overflowing = captions.Cues.Count(c => c.Range.End > total);
        var inverted = captions.Cues.Count(c => c.Range.End <= c.Range.Start);

        return new CheckResult
        {
            Code = "qc.caption_bounds",
            Name = "Altyazı süresi ses süresini aşmıyor",
            Passed = overflowing == 0 && inverted == 0,
            Severity = CheckSeverity.Blocking,
            Weight = 8,
            Target = RetryTarget.Timeline,
            Detail = FormattableString.Invariant(
                $"{captions.Cues.Count} ipucu; {overflowing} tanesi videoyu aşıyor, {inverted} tanesi ters"),
        };
    }

    // 9 — Tüm iddiaların kaynağı var
    private static CheckResult ClaimsAreSourced(QcInput input)
    {
        if (input.Claims is not { } claims)
        {
            return Missing("qc.claims_sourced", "Tüm iddiaların kaynağı var", RetryTarget.Script);
        }

        return new CheckResult
        {
            Code = "qc.claims_sourced",
            Name = "Tüm iddiaların kaynağı var",
            Passed = claims.AllSourced,
            Severity = CheckSeverity.Blocking,
            Weight = 15,
            Target = RetryTarget.Script,
            Detail = FormattableString.Invariant(
                $"{claims.TotalClaims} iddianın {claims.SourcedClaims} tanesi kaynaklı"),
        };
    }

    // 9b — İddialar AYRI bir modelle doğrulandı mı (§2.2/8)
    private static CheckResult ClaimsIndependentlyChecked(QcInput input)
    {
        if (input.Claims is not { } claims)
        {
            return Missing(
                "qc.claims_independent", "İddialar ayrı modelle doğrulandı", RetryTarget.Script);
        }

        return new CheckResult
        {
            Code = "qc.claims_independent",
            Name = "İddialar ayrı modelle doğrulandı",

            // İDDİA YOKSA GEÇİYOR: doğrulanacak bir şey olmadığında
            // "bağımsız doğrulanmadı" demek yanlış olurdu.
            Passed = claims.TotalClaims == 0 || !claims.SameModel,

            // ***BLOKLAYICI DEĞİL, UYARI — VE BU BİLİNÇLİ.***
            //
            // Anahtarsız hatta senaryoyu da iddiayı da AYNI model
            // üretiyor (tek LLM var). Bloklayıcı yapmak, bugün her
            // videoyu düşürmek demekti — yani kontrolü kapatmak
            // zorunda kalırdık ve kapalı bir kontrol hiç yokla aynı.
            //
            // Uyarı olarak skordan puan götürüyor: zayıflık GÖRÜNÜR
            // ama üretim durmuyor. İkinci bir model bağlandığında
            // bloklayıcıya çevrilecek tek şey bu satır.
            Severity = CheckSeverity.Warning,
            Weight = 5,
            Target = RetryTarget.Script,
            Detail = claims.SameModel
                ? "İddiaları senaryoyu yazan modelin KENDİSİ doğruladı; "
                  + "kendi hatasını hata olarak görmüyor."
                : "İddialar ayrı bir modelle doğrulandı.",
        };
    }

    // 10 — Metadata sınırları
    private static CheckResult MetadataWithinLimits(QcInput input)
    {
        if (input.Metadata is not { } metadata)
        {
            return Missing("qc.metadata_limits", "Metadata sınırları içinde", RetryTarget.Metadata);
        }

        var problems = new List<string>();

        if (metadata.Title.Length > MaxTitleLength)
        {
            problems.Add(FormattableString.Invariant(
                $"başlık {metadata.Title.Length} karakter (sınır {MaxTitleLength})"));
        }

        if (string.IsNullOrWhiteSpace(metadata.Title))
        {
            problems.Add("başlık boş");
        }

        if (metadata.Description.Length > MaxDescriptionLength)
        {
            problems.Add(FormattableString.Invariant(
                $"açıklama {metadata.Description.Length} karakter (sınır {MaxDescriptionLength})"));
        }

        // Toplam uzunluk ayraçlarla birlikte sayılıyor: platform da
        // öyle sayıyor ve ayraçları unutmak sınırın hemen altındaki
        // bir etiket kümesini reddettirir.
        var tagsLength = metadata.Tags.Sum(t => t.Length) + Math.Max(metadata.Tags.Count - 1, 0);

        if (tagsLength > MaxTagsTotalLength)
        {
            problems.Add(FormattableString.Invariant(
                $"etiketler {tagsLength} karakter (sınır {MaxTagsTotalLength})"));
        }

        return new CheckResult
        {
            Code = "qc.metadata_limits",
            Name = "Metadata sınırları içinde",
            Passed = problems.Count == 0,
            Severity = CheckSeverity.Blocking,
            Weight = 8,
            Target = RetryTarget.Metadata,
            Detail = problems.Count == 0
                ? FormattableString.Invariant($"başlık {metadata.Title.Length}, etiket {tagsLength} karakter")
                : string.Join("; ", problems),
        };
    }

    // 11 — Thumbnail var, boyut/oran doğru
    private static CheckResult ThumbnailValid(QcInput input)
    {
        if (input.Metadata?.Thumbnail is not { } thumbnail)
        {
            return Missing("qc.thumbnail", "Thumbnail var ve ölçüleri doğru", RetryTarget.Metadata);
        }

        var problems = new List<string>();

        if (thumbnail.Width < 1280 || thumbnail.Height < 720)
        {
            problems.Add(FormattableString.Invariant(
                $"{thumbnail.Width}x{thumbnail.Height} çok küçük (en az 1280x720)"));
        }

        // 16:9 bekleniyor. Oran tutmazsa platform kendi kırpıyor ve
        // kırptığı yer genellikle metnin ortası oluyor.
        var ratio = thumbnail.Height == 0 ? 0 : (double)thumbnail.Width / thumbnail.Height;

        if (Math.Abs(ratio - 16.0 / 9.0) > 0.02)
        {
            problems.Add(FormattableString.Invariant($"oran {ratio:0.###}, 16:9 bekleniyor"));
        }

        if (thumbnail.SizeBytes > 2 * 1024 * 1024)
        {
            problems.Add(FormattableString.Invariant($"{thumbnail.SizeBytes / 1024} KB, sınır 2048 KB"));
        }

        return new CheckResult
        {
            Code = "qc.thumbnail",
            Name = "Thumbnail var ve ölçüleri doğru",
            Passed = problems.Count == 0,
            Severity = CheckSeverity.Blocking,
            Weight = 5,
            Target = RetryTarget.Metadata,
            Detail = problems.Count == 0
                ? FormattableString.Invariant($"{thumbnail.Width}x{thumbnail.Height}, {thumbnail.SizeBytes / 1024} KB")
                : string.Join("; ", problems),
        };
    }

    // 12 — Aynı konu daha önce yayınlanmamış
    private static CheckResult TopicIsUnique(QcInput input)
    {
        if (input.Uniqueness is not { } uniqueness)
        {
            return Missing("qc.topic_unique", "Konu daha önce yayınlanmamış", RetryTarget.Script);
        }

        return new CheckResult
        {
            Code = "qc.topic_unique",
            Name = "Konu daha önce yayınlanmamış",
            Passed = uniqueness.IsUnique,
            Severity = CheckSeverity.Blocking,
            Weight = 10,
            Target = RetryTarget.Script,
            Detail = uniqueness.IsUnique
                ? "benzer yayın bulunamadı"
                : FormattableString.Invariant(
                    $"benzerlik {uniqueness.Similarity ?? 0:0.###} — \"{uniqueness.ConflictingTitle}\""),
        };
    }

    /// Ölçülmemiş bir kontrol GEÇMİŞ SAYILMIYOR.
    ///
    /// Bu ayrım QC'nin bel kemiği: "ölçemedim" ile "geçti" aynı şey
    /// değil. Eşitlemek, thumbnail üretilmediği için hiç bakılmayan bir
    /// videonun QC'den tam puanla geçmesi demek olurdu.
    private static CheckResult Missing(
        string code, string name, RetryTarget target, CheckSeverity severity = CheckSeverity.Blocking)
        => new()
        {
            Code = code,
            Name = name,
            Passed = false,
            Severity = severity,
            Weight = 1,
            Target = target,
            Detail = "ölçülmedi",

            // ÖLÇÜLMEDİ, DÜŞMEDİ. Fark retry kararını değiştiriyor:
            // yeniden koşmak eksik bir ölçüm adımını eklemiyor.
            Measured = false,
        };
}
