using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Core.Time;

namespace BytemountsAiStudio.Media.Planning;

/// Sahne planlayıcı (P1-16, ADR-006).
///
/// TEK KURAL, ve her şey ondan çıkıyor:
///   sahne SINIRLARI senaryodan, sahne SÜRELERİ ÖLÇÜLEN sesten.
///
/// Ters kurulsaydı — yani süreler senaryodan tahmin edilseydi — ses ile
/// görsel arasındaki kayma sahte veriyle görünmez, ancak gerçek
/// seslendirmede ortaya çıkardı. Ve o noktada hata, sesin kendisinde
/// değil planlamada olduğu için teşhisi zor olurdu.
///
/// Süre TAHMİN EDİLMİYOR, ölçülüyor: TTS her parçanın gerçek uzunluğunu
/// döndürüyor ve plan onu olduğu gibi kullanıyor.
public static class ScenePlanner
{
    /// Bir sahnenin görselinin ekranda kalabileceği en kısa süre.
    ///
    /// Bunun altında görsel değişimi göz için titreşim oluyor. Çok kısa
    /// bir cümle geldiğinde sahne bir sonrakiyle BİRLEŞTİRİLİYOR —
    /// süreyi uzatmak ses ile kaymaya yol açardı, ki tam da kaçındığımız
    /// şey bu.
    public static readonly Ms MinimumSceneDuration = new Ms(1200);

    public static Result<ScenePlan> Plan(
        IReadOnlyList<string> sentences,
        IReadOnlyList<Ms> measuredDurations,
        string topic,
        LanguageTag language,
        VisualStyle style)
    {
        ArgumentNullException.ThrowIfNull(sentences);
        ArgumentNullException.ThrowIfNull(measuredDurations);

        if (sentences.Count == 0)
        {
            return Error.Permanent("scene.no_sentences", "Senaryo bos; sahne planlanamaz.");
        }

        // Sayılar tutmalı. Tutmuyorsa sessizce kırpmak, sonundaki
        // cümlelerin sessizce düşmesi demek olurdu — video kısalır ve
        // kimse sebebini bilmez.
        if (sentences.Count != measuredDurations.Count)
        {
            return Error.Permanent(
                "scene.count_mismatch",
                $"Senaryoda {sentences.Count} cumle var ama {measuredDurations.Count} ses parcasi olculdu.");
        }

        var merged = Merge(sentences, measuredDurations);
        var scenes = new List<PlannedScene>(merged.Count);
        var cursor = Ms.Zero;

        for (var i = 0; i < merged.Count; i++)
        {
            var scene = merged[i];

            scenes.Add(new PlannedScene
            {
                Index = i,
                Text = scene.Text,
                Start = cursor,
                Duration = scene.Duration,
                SourceSegments = scene.Sources,
                Direction = VisualDirector.Direct(scene.Text, topic, language, style, i),
            });

            cursor += scene.Duration;
        }

        return Result.Success(new ScenePlan
        {
            Scenes = scenes,
            Total = cursor,
            Topic = topic,
            Language = language,
            Style = style.Name,
        });
    }

    /// Çok kısa sahneleri komşusuyla birleştirir.
    ///
    /// Birleştirme İLERİ yönde: kısa bir cümle, eşiği aşana kadar
    /// sonrakileri topluyor. Geriye doğru birleştirmek ilk cümleyi
    /// açıkta bırakırdı — birleşecek bir öncesi yok — ve videonun tam
    /// açılışında titreyen bir kare kalırdı.
    ///
    /// Artan son parça İSTİSNA: onun bir sonrakisi yok, o yüzden
    /// öncekine ekleniyor.
    ///
    /// Toplam süre her durumda KORUNUYOR: birleştirme sesi kısaltmıyor,
    /// yalnızca görsel değişim sayısını azaltıyor.
    private static List<MergedScene> Merge(
        IReadOnlyList<string> sentences, IReadOnlyList<Ms> durations)
    {
        var result = new List<MergedScene>();
        MergedScene? pending = null;

        for (var i = 0; i < sentences.Count; i++)
        {
            pending = pending is { } open
                ? open with
                {
                    Text = open.Text + " " + sentences[i],
                    Duration = open.Duration + durations[i],
                    Sources = [.. open.Sources, i],
                }
                : new MergedScene(sentences[i], durations[i], [i]);

            if (pending.Value.Duration >= MinimumSceneDuration)
            {
                result.Add(pending.Value);
                pending = null;
            }
        }

        if (pending is not { } leftover)
        {
            return result;
        }

        if (result.Count == 0)
        {
            // Senaryonun TAMAMI eşiğin altında. Birleştirecek bir şey
            // yok; tek sahne olarak kalıyor. Eşiği burada dayatmak
            // videoyu tamamen görselsiz bırakırdı.
            result.Add(leftover);

            return result;
        }

        var last = result[^1];

        result[^1] = last with
        {
            Text = last.Text + " " + leftover.Text,
            Duration = last.Duration + leftover.Duration,
            Sources = [.. last.Sources, .. leftover.Sources],
        };

        return result;
    }
}

public sealed record ScenePlan
{
    public required IReadOnlyList<PlannedScene> Scenes { get; init; }

    /// Planın toplam süresi. Ölçülen ses sürelerinin toplamına EŞİT
    /// olmak zorunda — eşit değilse bir yerde süre uydurulmuş demektir.
    public required Ms Total { get; init; }

    public required string Topic { get; init; }

    public required LanguageTag Language { get; init; }

    public required string Style { get; init; }
}

public sealed record PlannedScene
{
    public required int Index { get; init; }

    public required string Text { get; init; }

    public required Ms Start { get; init; }

    public required Ms Duration { get; init; }

    /// Bu sahneye giren SES PARÇALARININ indeksleri.
    ///
    /// Birleştirme yüzünden sahne sayısı ses parçası sayısından az
    /// olabiliyor. Timeline'ın hangi sesin hangi sahnede çaldığını
    /// bilmesi gerekiyor; birebir varsayarsak birleşen sahnelerde
    /// eşleşme kayar ve ses ile görsel ayrışır.
    public required IReadOnlyList<int> SourceSegments { get; init; }

    public required VisualDirection Direction { get; init; }

    public Ms End => Start + Duration;
}

/// Birleştirme sırasında taşınan ara değer.
internal readonly record struct MergedScene(string Text, Ms Duration, IReadOnlyList<int> Sources);
