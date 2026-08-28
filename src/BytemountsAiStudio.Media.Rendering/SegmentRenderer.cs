using System.Globalization;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Core.Time;
using BytemountsAiStudio.Media.Planning;
using BytemountsAiStudio.Media.Timeline;

namespace BytemountsAiStudio.Media.Rendering;

/// Bölüm bazlı render'ın sonucu (P2-11).
public sealed record SegmentRenderOutcome
{
    public required string OutputPath { get; init; }

    /// Kaç segment yeniden render edildi.
    public required int Rendered { get; init; }

    /// Kaç segment önbellekten geldi.
    ///
    /// KABUL KRİTERİ SAYI OLARAK BURADA: "tek sahne değişince yalnız o
    /// segment yeniden render ediliyor" iddiası, bu iki sayı
    /// görülmediği sürece bir iddia.
    public required int Reused { get; init; }

    public required TimeSpan Duration { get; init; }
}

/// Sahneleri ayrı ayrı render edip birleştiren yürütücü (P2-11).
///
/// NEDEN: render bu hattın en yavaş adımı ve QC bir retry istediğinde
/// çoğu sahne hiç değişmiyor. Tamamını yeniden render etmek, tek bir
/// bozuk kare için yirmi sahnenin bedelini yeniden ödemek demek.
///
/// SES BİRLEŞTİRMEDEN SONRA BİNİYOR. Segmentler sessiz render ediliyor,
/// birleştiriliyor ve ses TEK seferde takılıyor. Sesi de bölmek,
/// cümlelerin segment sınırlarında kesilmesi ve her sınırda duyulur bir
/// tıklama demekti — konuşma sahne sınırlarına saygı duymuyor.
///
/// ANAHTAR SAHNENİN GÖRÜNTÜSÜNE BAĞLI, sırasına ya da mutlak zamanına
/// değil (`SegmentCache`). Girselerdi, önündeki bir sahne uzayınca
/// görüntüsü hiç değişmemiş bütün segmentler geçersiz olurdu ve
/// önbellek hiç yokmuş gibi davranırdı.
public sealed class SegmentRenderer(
    string cacheDirectory,
    string ffmpegPath = "ffmpeg",
    string ffprobePath = "ffprobe")
{
    private readonly FfmpegExecutor _executor = new(ffmpegPath, ffprobePath);

    public async Task<Result<SegmentRenderOutcome>> RenderAsync(
        TimelineDocument timeline,
        IReadOnlyDictionary<string, string> resolvedPaths,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(resolvedPaths);

        if (timeline.Scenes.Count == 0)
        {
            return Error.Permanent("segment.no_scenes", "Timeline'da sahne yok.");
        }

        Directory.CreateDirectory(cacheDirectory);

        var keys = SegmentCache.KeysFor(timeline);
        var ordered = timeline.Scenes.OrderBy(s => s.Range.Start.Value).ToList();

        var files = new List<string>();
        int rendered = 0, reused = 0;

        var started = DateTimeOffset.UtcNow;

        for (var i = 0; i < ordered.Count; i++)
        {
            var scene = ordered[i];
            var key = keys[i];
            var path = Path.Combine(cacheDirectory, $"{key.Value}.mp4");

            if (File.Exists(path))
            {
                // ÖNBELLEK İSABETİ: dosya var ve anahtarı bu sahneyi
                // birebir tanımlıyor. Anahtar sahnenin görüntüsünü
                // belirleyen her şeyi kapsıyor; kapsamasaydı bayat bir
                // kare kullanılırdı ve bayat kare, sessiz olduğu için
                // hiç önbellek olmamasından kötü.
                files.Add(path);
                reused++;
                continue;
            }

            var segment = await RenderSegmentAsync(
                timeline, scene, resolvedPaths, path, cancellationToken).ConfigureAwait(false);

            if (segment.IsFailure)
            {
                return Result.Failure<SegmentRenderOutcome>(segment.Error);
            }

            files.Add(path);
            rendered++;
        }

        var concatenated = Path.Combine(cacheDirectory, $"birlesik-{Guid.CreateVersion7():N}.mp4");

        var concat = await ConcatAsync(files, concatenated, cancellationToken).ConfigureAwait(false);

        if (concat.IsFailure)
        {
            return Result.Failure<SegmentRenderOutcome>(concat.Error);
        }

        try
        {
            var final = await MuxAudioAsync(
                timeline, resolvedPaths, concatenated, outputPath, cancellationToken).ConfigureAwait(false);

            if (final.IsFailure)
            {
                return Result.Failure<SegmentRenderOutcome>(final.Error);
            }

            return Result.Success(new SegmentRenderOutcome
            {
                OutputPath = final.Value.OutputPath,
                Rendered = rendered,
                Reused = reused,
                Duration = DateTimeOffset.UtcNow - started,
            });
        }
        finally
        {
            // Birleşik ara dosya ÖNBELLEKTE KALMIYOR: segmentlerin
            // aksine yeniden kullanılamaz (her koşuda sıra ya da içerik
            // değişebiliyor) ve kalsaydı önbellek dizini her koşuda bir
            // tam video kadar büyürdü.
            TryDelete(concatenated);
        }
    }

    /// Tek bir sahneyi SESSİZ olarak render eder.
    ///
    /// Sahne, kendi başına bir timeline'a çevriliyor: zamanı sıfırdan
    /// başlıyor ve sesi yok. Aynı planlayıcı kullanılıyor, yani Ken
    /// Burns, geçiş ve tuval kuralları tek yerde tanımlı kalıyor.
    private async Task<Result> RenderSegmentAsync(
        TimelineDocument timeline,
        Scene scene,
        IReadOnlyDictionary<string, string> resolvedPaths,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var duration = scene.Range.Duration;

        var single = timeline with
        {
            Duration = duration,
            Scenes =
            [
                scene with
                {
                    Index = 0,
                    Range = new TimeRange(new Ms(0), duration),
                    // ÜST YAZILAR SAHNEYE GÖRE KAYDIRILIYOR: mutlak
                    // zamanlarını korusaydık, segmentin kendi zaman
                    // ekseninde hiç görünmezlerdi.
                    Overlays = [.. scene.Overlays.Select(o => o with
                    {
                        Range = new TimeRange(
                            new Ms(Math.Max(o.Range.Start.Value - scene.Range.Start.Value, 0)),
                            new Ms(Math.Max(o.Range.End.Value - scene.Range.Start.Value, 1))),
                    })],
                    VoiceSegmentIds = [],
                },
            ],
            // SES YOK: segmentler sessiz. Ses birleştirmeden sonra tek
            // seferde biniyor, çünkü konuşma sahne sınırlarına saygı
            // duymuyor.
            Audio = new AudioTrack { VoiceSegments = [] },
            Captions = null,
        };

        var plan = RenderPlanner.PlanVideoOnly(single, resolvedPaths);

        if (!plan.IsSuccess)
        {
            return Error.Permanent("segment.plan_failed",
                $"{scene.Index}. segment planlanamadı: " + string.Join(" | ", plan.Issues));
        }

        var result = await _executor
            .RenderAsync(plan.Plan!.Graph, plan.Plan.Output, outputPath, null, cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure ? Result.Failure(result.Error) : Result.Success();
    }

    /// Segmentleri birleştirir.
    ///
    /// `concat` DEMUXER, filtre değil: segmentlerin hepsi aynı kodek ve
    /// çözünürlükte olduğu için yeniden kodlamaya gerek yok. Filtreyle
    /// birleştirmek her segmenti yeniden kodlardı — yani önbelleğin
    /// kazandırdığı zamanı geri harcardı.
    private async Task<Result> ConcatAsync(
        IReadOnlyList<string> files, string outputPath, CancellationToken cancellationToken)
    {
        var listPath = Path.Combine(cacheDirectory, $"liste-{Guid.CreateVersion7():N}.txt");

        // Yollar TEK TIRNAK İÇİNDE ve içindeki tırnaklar kaçırılıyor:
        // boşluk içeren bir yol (Windows'ta kural) aksi hâlde iki ayrı
        // dosya gibi okunurdu.
        await File.WriteAllLinesAsync(
            listPath,
            files.Select(f => "file '" + f.Replace("'", @"'\''", StringComparison.Ordinal) + "'"),
            cancellationToken).ConfigureAwait(false);

        try
        {
            return await _executor.RunRawAsync(
                ["-f", "concat", "-safe", "0", "-i", listPath, "-c", "copy", outputPath],
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(listPath);
        }
    }

    private Task<Result<RenderOutcome>> MuxAudioAsync(
        TimelineDocument timeline,
        IReadOnlyDictionary<string, string> resolvedPaths,
        string videoPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var plan = RenderPlanner.PlanOverVideo(timeline, resolvedPaths, videoPath);

        if (!plan.IsSuccess)
        {
            return Task.FromResult(Result.Failure<RenderOutcome>(Error.Permanent(
                "segment.audio_plan_failed",
                "Ses planı üretilemedi: " + string.Join(" | ", plan.Issues))));
        }

        return _executor.RenderAsync(
            plan.Plan!.Graph, plan.Plan.Output, outputPath, null, cancellationToken);
    }

    /// Kullanılmayan segmentleri siler.
    ///
    /// Önbellek sınırsız büyüyemez: her koşu yeni anahtarlar üretiyor ve
    /// eski segmentler bir daha hiç kullanılmıyor. Temizlemeyi
    /// unutmak, diskin sessizce dolması demek — ve bu, üretimi
    /// durduran ama sebebi hiçbir logda yazmayan bir arıza.
    public int Prune(TimeSpan olderThan, TimeProvider? timeProvider = null)
    {
        if (!Directory.Exists(cacheDirectory))
        {
            return 0;
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var removed = 0;

        foreach (var file in Directory.EnumerateFiles(cacheDirectory, "*.mp4"))
        {
            if (now - File.GetLastWriteTimeUtc(file) <= olderThan)
            {
                continue;
            }

            if (TryDelete(file))
            {
                removed++;
            }
        }

        return removed;
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }
        }
        catch (IOException)
        {
            // Silinemeyen bir ara dosya koşuyu düşürmemeli: render
            // bitti, video hazır. Kalan dosya bir sonraki temizlikte
            // gidecek.
        }
        catch (UnauthorizedAccessException)
        {
        }

        return false;
    }

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"SegmentRenderer({cacheDirectory})");
}
