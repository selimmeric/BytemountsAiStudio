using System.Globalization;
using BytemountsAiStudio.Core.Time;

namespace BytemountsAiStudio.Media.Timeline;

public sealed record ValidationIssue(string Code, string Message)
{
    public override string ToString() => $"{Code}: {Message}";
}

/// Timeline'ın kendi içinde tutarlı olup olmadığını söyler.
///
/// Neden ayrı ve saf bir aşama: bu kontroller FFmpeg çalıştırmadan, milisaniye
/// içinde koşar. Aynı hataları render sırasında yakalamak dakikalar sürer ve
/// hata mesajı "Invalid argument" olur — nerede yanlış olduğunu söylemez.
///
/// Kural seti bilerek dar: burada "video güzel mi" sorulmaz, yalnızca
/// "bu belge render edilebilir mi" sorulur.
public static class TimelineValidator
{
    public static IReadOnlyList<ValidationIssue> Validate(TimelineDocument timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);

        var issues = new List<ValidationIssue>();

        ValidateBasics(timeline, issues);
        ValidateScenes(timeline, issues);
        ValidateAudio(timeline, issues);
        ValidateCaptions(timeline, issues);
        ValidateStyleReferences(timeline, issues);

        return issues;
    }

    private static void ValidateBasics(TimelineDocument t, List<ValidationIssue> issues)
    {
        if (t.SchemaVersion != 1)
        {
            issues.Add(new("timeline.schema_version",
                $"Desteklenmeyen şema sürümü: {t.SchemaVersion}. Bu motor 1 sürümünü okuyor."));
        }

        if (t.Duration.Value <= 0)
        {
            issues.Add(new("timeline.duration", $"Süre pozitif olmalı, {t.Duration} geldi."));
        }

        if (t.Scenes.Count == 0)
        {
            issues.Add(new("timeline.no_scenes", "En az bir sahne gerekli."));
        }

        // ***BİLİNMEYEN KONTEYNER REDDEDİLİYOR.***
        //
        // Uzantı doğrudan dosya adına gidiyor; serbest bırakmak
        // `container: "../../etc"` yazan bir timeline'ın çıktı yolunu
        // dizin dışına taşıması demekti. Yazım hatası (`"mp"`) da
        // ffmpeg tarafında anlaşılmaz bir hata üretirdi.
        if (!OutputSpec.KnownContainers.Contains(t.Output.Container, StringComparer.Ordinal))
        {
            issues.Add(new("timeline.unknown_container",
                $"Bilinmeyen konteyner: '{t.Output.Container}'. "
                + $"Geçerli değerler: {string.Join(", ", OutputSpec.KnownContainers)}"));
        }

        if (t.FontStack.Count == 0)
        {
            // Zincir boşsa eksik glif yerine tofu çizilir ve bunu ancak
            // izleyici fark eder (§20.4).
            issues.Add(new("timeline.no_font", "Font zinciri boş olamaz."));
        }
    }

    private static void ValidateScenes(TimelineDocument t, List<ValidationIssue> issues)
    {
        var ordered = t.Scenes.OrderBy(s => s.Range.Start.Value).ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            var scene = ordered[i];

            if (scene.Range.Duration.Value <= 0)
            {
                issues.Add(new("scene.empty",
                    $"{scene.Index}. sahnenin süresi sıfır ya da negatif: {scene.Range}"));
            }

            if (scene.Range.End > t.Duration)
            {
                issues.Add(new("scene.exceeds_duration",
                    $"{scene.Index}. sahne video süresini aşıyor: {scene.Range.End} > {t.Duration}"));
            }

            if (i > 0 && ordered[i - 1].Range.Overlaps(scene.Range))
            {
                issues.Add(new("scene.overlap",
                    $"{ordered[i - 1].Index}. ve {scene.Index}. sahneler çakışıyor: " +
                    $"{ordered[i - 1].Range} / {scene.Range}"));
            }

            // Sahneler arasında boşluk kalırsa o anda ekranda hiçbir şey yok
            // demektir; FFmpeg siyah kare üretir ve bu genellikle bir hatadır.
            if (i > 0 && ordered[i - 1].Range.End < scene.Range.Start)
            {
                issues.Add(new("scene.gap",
                    $"{ordered[i - 1].Index}. ve {scene.Index}. sahneler arasında boşluk var: " +
                    $"{ordered[i - 1].Range.End} -> {scene.Range.Start}"));
            }

            ValidateMotion(scene, issues);

            foreach (var overlay in scene.Overlays)
            {
                if (!Covers(scene.Range, overlay.Range))
                {
                    issues.Add(new("overlay.outside_scene",
                        $"{scene.Index}. sahnedeki '{overlay.Text}' katmanı sahne dışına taşıyor: " +
                        $"{overlay.Range} ⊄ {scene.Range}"));
                }
            }

            if (scene.TransitionOut is { } transition
                && transition.Kind != TransitionKind.None
                && transition.Duration > scene.Range.Duration)
            {
                issues.Add(new("transition.too_long",
                    $"{scene.Index}. sahnenin geçişi sahneden uzun: {transition.Duration} > {scene.Range.Duration}"));
            }

            if (scene.TransitionIn is { } opening
                && opening.Kind != TransitionKind.None
                && opening.Duration > scene.Range.Duration)
            {
                issues.Add(new("transition.too_long",
                    $"{scene.Index}. sahnenin açılması sahneden uzun: {opening.Duration} > {scene.Range.Duration}"));
            }

            // AÇILMA VE KARARMA BİRLİKTE SAHNEYİ AŞMAMALI.
            //
            // İkisi ayrı ayrı sahneden kısa olabiliyor ama toplamları
            // aşarsa üst üste binerler: görüntü açılırken kararmaya
            // başlar ve sahne hiçbir zaman tam parlaklığa çıkmaz.
            // Tek tek bakan bir kontrol bunu göremezdi.
            var opens = scene.TransitionIn is { Kind: not TransitionKind.None } o ? o.Duration.Value : 0;
            var closes = scene.TransitionOut is { Kind: not TransitionKind.None } c ? c.Duration.Value : 0;

            if (opens + closes > scene.Range.Duration.Value)
            {
                issues.Add(new("transition.overlap",
                    $"{scene.Index}. sahnenin açılması ve kararması üst üste biniyor: " +
                    $"{opens}ms + {closes}ms > {scene.Range.Duration}"));
            }
        }

        if (ordered.Count > 0)
        {
            if (ordered[0].Range.Start.Value != 0)
            {
                issues.Add(new("scene.late_start",
                    $"İlk sahne sıfırdan başlamalı, {ordered[0].Range.Start} geldi."));
            }

            var lastEnd = ordered[^1].Range.End;
            if (lastEnd != t.Duration)
            {
                issues.Add(new("scene.short_coverage",
                    $"Sahneler videonun tamamını kaplamıyor: son sahne {lastEnd}, video {t.Duration}."));
            }
        }
    }

    private static void ValidateMotion(Scene scene, List<ValidationIssue> issues)
    {
        if (scene.Visual.Motion is not { } motion)
        {
            return;
        }

        // Ölçek 1'in altına inerse görsel kadrajı dolduramaz ve kenarlarda
        // siyah şerit oluşur.
        if (motion.FromScale < 1.0 || motion.ToScale < 1.0)
        {
            issues.Add(new("motion.scale_below_one",
                $"{scene.Index}. sahnede ölçek 1.0'ın altında ({motion.FromScale} → {motion.ToScale}); " +
                "kadrajda siyah şerit oluşur."));
        }

        foreach (var (name, value) in new[]
                 {
                     ("FromX", motion.FromX), ("FromY", motion.FromY),
                     ("ToX", motion.ToX), ("ToY", motion.ToY),
                 })
        {
            if (value is < -1.0 or > 1.0)
            {
                issues.Add(new("motion.pan_out_of_range",
                    $"{scene.Index}. sahnede {name} [-1, 1] aralığı dışında: " +
                    value.ToString("F2", CultureInfo.InvariantCulture)));
            }
        }
    }

    private static void ValidateAudio(TimelineDocument t, List<ValidationIssue> issues)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var segment in t.Audio.VoiceSegments)
        {
            if (!ids.Add(segment.Id))
            {
                issues.Add(new("audio.duplicate_id", $"Ses parçası kimliği tekrarlanıyor: {segment.Id}"));
            }

            if (segment.Duration.Value <= 0)
            {
                issues.Add(new("audio.empty_segment", $"'{segment.Id}' parçasının süresi sıfır."));
            }

            if (segment.End > t.Duration)
            {
                issues.Add(new("audio.exceeds_duration",
                    $"'{segment.Id}' parçası video süresini aşıyor: {segment.End} > {t.Duration}"));
            }
        }

        var ordered = t.Audio.VoiceSegments.OrderBy(s => s.Start.Value).ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            var previous = new TimeRange(ordered[i - 1].Start, ordered[i - 1].End);
            var current = new TimeRange(ordered[i].Start, ordered[i].End);

            if (previous.Overlaps(current))
            {
                issues.Add(new("audio.overlap",
                    $"'{ordered[i - 1].Id}' ve '{ordered[i].Id}' parçaları çakışıyor — " +
                    "iki ses üst üste binerdi."));
            }
        }

        // Sahneler ses parçalarına kimlikle bağlanıyor; olmayan bir kimliğe
        // referans, sessiz bir sahne olarak render edilirdi.
        foreach (var scene in t.Scenes)
        {
            foreach (var id in scene.VoiceSegmentIds.Where(id => !ids.Contains(id)))
            {
                issues.Add(new("scene.unknown_segment",
                    $"{scene.Index}. sahne olmayan bir ses parçasına başvuruyor: {id}"));
            }
        }
    }

    private static void ValidateCaptions(TimelineDocument t, List<ValidationIssue> issues)
    {
        if (t.Captions is not { } captions)
        {
            return;
        }

        var ordered = captions.Cues.OrderBy(c => c.Range.Start.Value).ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Range.Duration.Value <= 0)
            {
                issues.Add(new("caption.empty", $"'{ordered[i].Text}' altyazısının süresi sıfır."));
            }

            if (ordered[i].Range.End > t.Duration)
            {
                issues.Add(new("caption.exceeds_duration",
                    $"'{ordered[i].Text}' altyazısı video süresini aşıyor."));
            }

            if (i > 0 && ordered[i - 1].Range.Overlaps(ordered[i].Range))
            {
                issues.Add(new("caption.overlap",
                    $"'{ordered[i - 1].Text}' ve '{ordered[i].Text}' altyazıları çakışıyor."));
            }
        }
    }

    private static void ValidateStyleReferences(TimelineDocument t, List<ValidationIssue> issues)
    {
        var referenced = t.Scenes
            .SelectMany(s => s.Overlays.Select(o => o.StyleRef))
            .Concat(t.Captions is null ? [] : new[] { t.Captions.StyleRef })
            .Distinct(StringComparer.Ordinal);

        foreach (var styleRef in referenced.Where(r => !t.Styles.ContainsKey(r)))
        {
            issues.Add(new("style.missing",
                $"Tanımlı olmayan stile başvuru: '{styleRef}'. Stiller: " +
                (t.Styles.Count == 0 ? "(yok)" : string.Join(", ", t.Styles.Keys))));
        }
    }

    private static bool Covers(TimeRange outer, TimeRange inner)
        => inner.Start >= outer.Start && inner.End <= outer.End;
}
