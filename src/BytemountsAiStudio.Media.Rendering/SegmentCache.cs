using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Media.Timeline;

namespace BytemountsAiStudio.Media.Rendering;

/// Tek bir segmentin render kimliği (P2-11).
///
/// Segment = bir sahne + üzerindeki her şey. Anahtar, o segmentin
/// görüntüsünü belirleyen HER ŞEYDEN türetiliyor; biri değişince
/// anahtar değişiyor ve yalnızca o segment yeniden render ediliyor.
public sealed record SegmentKey(string Value, int Index)
{
    public override string ToString() => Value;
}

/// Segment önbelleği anahtarları (P2-11).
///
/// SAF: dosya ve ffmpeg yok. "Bu segment değişti mi" kararı, gerçek
/// bir render yapılarak öğrenilecek bir şey olmamalı — render bu
/// hattın en yavaş adımı ve bir yanlış anahtar, ya bayat kare
/// gösteriyor ya da önbelleği tamamen işe yaramaz kılıyor.
///
/// ANAHTAR NEYE BAĞLI OLMALI: segmentin görüntüsünü belirleyen her
/// şey. Eksik bırakılan tek bir alan, o alan değiştiğinde BAYAT bir
/// segmentin kullanılması demek — ve bayat kare, hiç önbellek
/// olmamasından çok daha kötü çünkü sessiz.
///
/// ANAHTAR NEYE BAĞLI OLMAMALI: segmentin dışındaki hiçbir şey.
/// Videonun toplam süresi ya da komşu sahnelerin içeriği anahtara
/// girseydi, tek bir sahne değişince BÜTÜN segmentler geçersiz olurdu
/// — yani önbellek hiç yokmuş gibi davranırdı.
public static class SegmentCache
{
    /// Anahtar sürümü.
    ///
    /// Render mantığı değiştiğinde (yeni bir filtre, düzeltilen bir
    /// çizim hatası) bu sayı artıyor ve bütün önbellek geçersiz
    /// oluyor. Olmasaydı, düzeltilen bir hata eski segmentlerde
    /// yaşamaya devam ederdi — ve o hata artık kodda görünmediği için
    /// teşhis edilemezdi.
    public const int Version = 1;

    /// Bir sahnenin önbellek anahtarı.
    public static SegmentKey KeyFor(Scene scene, Canvas canvas, IReadOnlyList<string>? fontStack = null)
    {
        ArgumentNullException.ThrowIfNull(scene);

        var builder = new StringBuilder();

        builder.Append(CultureInfo.InvariantCulture, $"v{Version}|");

        // TUVAL: çözünürlük ya da kare hızı değişince segment yeniden
        // üretilmek zorunda.
        builder.Append(CultureInfo.InvariantCulture, $"{canvas.Width}x{canvas.Height}@{canvas.Fps}|");

        // SÜRE: aynı görselin 3 saniyelik ve 5 saniyelik hâli farklı
        // dosyalar ve Ken Burns hareketi de süreye bağlı.
        //
        // MUTLAK ZAMAN DEĞİL SÜRE: sahnenin videoda nerede başladığı
        // görüntüsünü değiştirmiyor. Başlangıç anı anahtara girseydi,
        // önündeki bir sahne uzayınca sonraki bütün segmentler
        // geçersiz olurdu — yani önbellek hiç yokmuş gibi davranırdı.
        builder.Append(CultureInfo.InvariantCulture, $"d{(scene.Range.End - scene.Range.Start).Value}|");

        // GÖRSEL: içerik-adresli varlık referansı. Aynı görselin
        // yeniden indirilmesi anahtarı değiştirmiyor — içerik aynıysa
        // özet de aynı.
        builder.Append(CultureInfo.InvariantCulture, $"a{scene.Visual.Asset.Sha256}|");
        builder.Append(CultureInfo.InvariantCulture, $"f{scene.Visual.Fit}|");

        builder.Append(scene.Visual.Motion is { } motion
            ? string.Create(CultureInfo.InvariantCulture,
                $"m{motion.FromScale}/{motion.ToScale}/{motion.FromX}/{motion.FromY}|")
            : "m-|");

        // ÜSTÜNDEKİ YAZI: aynı görsel üzerinde farklı metin, farklı
        // kare demek. Zaman aralığı SAHNEYE GÖRE değil mutlak
        // veriliyor; sahne başlangıcı çıkarılıyor ki kaydırma
        // anahtarı değiştirmesin.
        foreach (var overlay in scene.Overlays)
        {
            builder.Append(CultureInfo.InvariantCulture,
                $"o[{(overlay.Range.Start - scene.Range.Start).Value}-{(overlay.Range.End - scene.Range.Start).Value}]{overlay.StyleRef}:{overlay.Text}|");
        }

        // GEÇİŞ: sahnenin sonundaki solma da görüntünün parçası.
        builder.Append(scene.TransitionOut is { } transition
            ? string.Create(CultureInfo.InvariantCulture, $"t{transition.Kind}/{transition.Duration.Value}|")
            : "t-|");

        // FONT ZİNCİRİ: eksik glif bir sonrakinden alınıyor, yani
        // zincir değişince çizilen yazı da değişebiliyor (§20.4).
        builder.Append(CultureInfo.InvariantCulture, $"z{string.Join(',', fontStack ?? [])}|");

        // SIRA NUMARASI ANAHTARA GİRMİYOR ve bu bilinçli.
        //
        // Girseydi, sahne sırası değişince görüntüsü hiç değişmemiş
        // segmentler de yeniden render edilirdi. Sıra birleştirme
        // aşamasının bilgisi, segmentin kendisinin değil.
        return new SegmentKey(Hash(builder.ToString()), scene.Index);
    }

    /// Bir timeline'ın bütün segment anahtarları.
    public static IReadOnlyList<SegmentKey> KeysFor(TimelineDocument timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);

        return [.. timeline.Scenes.Select(scene => KeyFor(scene, timeline.Canvas, timeline.FontStack))];
    }

    /// Hangi segmentler yeniden render edilmeli.
    ///
    /// Önceki anahtarlarla karşılaştırıyor. Yeni bir segment ya da
    /// anahtarı değişmiş bir segment listeye giriyor; değişmeyenler
    /// dokunulmadan kalıyor — kabul kriteri tam olarak bu.
    public static IReadOnlyList<SegmentKey> Stale(
        IReadOnlyList<SegmentKey> current, IReadOnlyCollection<string> cached)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(cached);

        var known = new HashSet<string>(cached, StringComparer.Ordinal);

        return [.. current.Where(k => !known.Contains(k.Value))];
    }

    /// Önbelleğin ne kadarını kurtardığı.
    ///
    /// Kabul kriteri ölçülebilir olmalı: "tek sahne değişince yalnız o
    /// segment yeniden render ediliyor" iddiası, sayı olarak
    /// görülmediği sürece bir iddia.
    public static int Reused(IReadOnlyList<SegmentKey> current, IReadOnlyList<SegmentKey> stale)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(stale);

        return current.Count - stale.Count;
    }

    private static string Hash(string text)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
}
