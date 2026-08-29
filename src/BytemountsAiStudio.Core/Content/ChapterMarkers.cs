using System.Globalization;
using System.Text;
using BytemountsAiStudio.Core.Time;

namespace BytemountsAiStudio.Core.Content;

/// Yayın platformu için bölüm işaretleri (P3-04).
///
/// SAF: bir liste alıyor, bir metin veriyor. Platform bu metni video
/// AÇIKLAMASINDAN okuyor — ayrı bir API alanı yok, biçim tam olarak
/// tutmak zorunda.
///
/// YouTube'un kuralları katı ve bir tanesi bile tutmazsa bölümler HİÇ
/// görünmüyor — üstelik hata da vermiyor. Sessiz başarısızlığın ders
/// kitabı örneği: açıklamaya yazdığınız satırlar orada duruyor ama
/// oynatıcıda hiçbir şey çıkmıyor.
public static class ChapterMarkers
{
    /// İlk işaret SIFIRDAN başlamak zorunda.
    ///
    /// YouTube ilk zaman damgası `0:00` değilse bölüm listesini hiç
    /// göstermiyor. Giriş bölümü için ayrı bir işaret üretmemizin
    /// sebebi bu — planda giriş bir bölüm değil ama listede olmak
    /// zorunda.
    public const string IntroTitle = "Giriş";

    /// En az kaç işaret gerekiyor.
    ///
    /// ÜÇ: YouTube'un kuralı. İki bölümlü bir video için liste hiç
    /// görünmüyor ve bunu ancak yayınlayıp bakarak fark edersiniz.
    public const int MinimumMarkers = 3;

    /// Bir bölümün en kısa hâli (saniye).
    ///
    /// ON SANİYE: YouTube'un kuralı. Daha kısa bir aralık listeyi
    /// tamamen geçersiz kılıyor.
    public const int MinimumSeconds = 10;

    /// Açıklama metni için işaret satırları üretir.
    ///
    /// Kurallara uymuyorsa `null` DÖNÜYOR, bozuk bir liste değil:
    /// yarım bir liste hiç görünmüyor ve "yazdım ama çıkmıyor"
    /// sorusunun cevabı hiçbir yerde olmazdı. Çağıran `Validate` ile
    /// sebebi öğreniyor.
    public static string? Render(IReadOnlyList<Chapter> chapters, Ms totalDuration)
    {
        ArgumentNullException.ThrowIfNull(chapters);

        var markers = Build(chapters, totalDuration);

        if (Validate(markers, totalDuration).Count > 0)
        {
            return null;
        }

        var builder = new StringBuilder();

        foreach (var (start, title) in markers)
        {
            builder.Append(Timestamp(start)).Append(' ').AppendLine(title);
        }

        return builder.ToString();
    }

    /// ***İŞARETLER GERÇEK SAHNE SINIRLARINDAN TÜRETİLİYOR, PLANDAN
    /// DEĞİL.***
    ///
    /// Bölüm planı bir HEDEF veriyor (`start_ms`); sahneler gerçek
    /// seslendirme sürelerinden doğuyor ve ikisi asla tam tutmuyor —
    /// plan 144.000 ms diyor, sahne sınırı 141.320 ms'de. Planı
    /// yazsaydık işaretler videonun içindeki gerçek geçişlerin birkaç
    /// saniye ötesine düşerdi: izleyici bir bölüme atlıyor ve önceki
    /// bölümün son cümlesini dinliyor. Daha kötüsü, plan toplam süreyi
    /// aşarsa YouTube bütün listeyi geçersiz sayıyor.
    ///
    /// Aynı eşleştirmeyi bölüm geçişleri de kullanıyor
    /// (`ChapterBoundaries`) ve tek kaynaktan gelmeleri şart: geçişin
    /// uzadığı yer ile işaretin gösterdiği yer ayrışırsa, ikisi de
    /// doğru görünürken video yanlış olurdu.
    public static IReadOnlyList<(Ms Start, string Title)> Align(
        IReadOnlyList<Chapter> chapters,
        IReadOnlyList<int> sceneStartsMs,
        IReadOnlyList<int> sceneEndsMs)
    {
        ArgumentNullException.ThrowIfNull(chapters);
        ArgumentNullException.ThrowIfNull(sceneStartsMs);
        ArgumentNullException.ThrowIfNull(sceneEndsMs);

        var boundaries = ChapterBoundaries.Match(
            sceneEndsMs, [.. chapters.Select(c => c.Start.Value)]);

        // SINIR SIRALANIYOR: `Match` bir küme dönüyor ve kümenin sırası
        // yok. Sırasız bir liste, ikinci bölümün birincinin önüne
        // geçmesi demek olurdu ve YouTube o listeyi hiç göstermez.
        var ordered = boundaries.Order().ToList();

        var aligned = new List<Chapter>();
        var index = 0;

        foreach (var chapter in chapters)
        {
            if (chapter.Start.Value <= 0)
            {
                // SIFIRDA BAŞLAYAN BÖLÜM ZATEN GİRİŞ: `Match` onu
                // sınır saymıyor ve burada da atlanıyor, yoksa
                // eşleşmeler bir kayardı.
                aligned.Add(chapter);
                continue;
            }

            if (index >= ordered.Count)
            {
                // EŞLEŞMEYEN BÖLÜM ATILIYOR, PLANDAKİ YERİNE
                // KONMUYOR: sahneler bölüm sayısından az olabiliyor
                // (kısa bölümler birleşiyor) ve plandaki değeri
                // yazmak, videonun içinde karşılığı olmayan bir
                // işaret üretmekti.
                continue;
            }

            // Sahne `i` sınırsa, bölüm `i+1`. sahnenin başında
            // başlıyor.
            var scene = ordered[index++] + 1;

            if (scene < sceneStartsMs.Count)
            {
                aligned.Add(chapter with { Start = new Ms(sceneStartsMs[scene]) });
            }
        }

        return Build(aligned, new Ms(sceneEndsMs.Count > 0 ? sceneEndsMs[^1] : 0));
    }

    /// Bölümleri işaret listesine çevirir — giriş dâhil.
    public static IReadOnlyList<(Ms Start, string Title)> Build(
        IReadOnlyList<Chapter> chapters, Ms totalDuration)
    {
        ArgumentNullException.ThrowIfNull(chapters);

        var markers = new List<(Ms, string)>();

        // GİRİŞ İÇİN AYRI İŞARET: ilk bölüm sıfırda başlamıyor (giriş
        // için yer ayrılmış) ama liste sıfırdan başlamak ZORUNDA.
        // Bunu atlamak bütün listeyi görünmez kılardı.
        if (chapters.Count == 0 || chapters[0].Start.Value > 0)
        {
            markers.Add((new Ms(0), IntroTitle));
        }

        markers.AddRange(chapters.Select(c => (c.Start, c.Title)));

        return markers;
    }

    /// Kurallara uymayan ne varsa listeler.
    ///
    /// SEBEP AYRI DÖNÜYOR çünkü "liste görünmüyor" tek başına hangi
    /// kuralın çiğnendiğini söylemiyor ve üç ayrı kural var.
    public static IReadOnlyList<string> Validate(
        IReadOnlyList<(Ms Start, string Title)> markers, Ms totalDuration)
    {
        ArgumentNullException.ThrowIfNull(markers);

        var problems = new List<string>();

        if (markers.Count < MinimumMarkers)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture,
                $"{markers.Count} işaret var, en az {MinimumMarkers} gerekiyor"));
        }

        if (markers.Count > 0 && markers[0].Start.Value != 0)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture,
                $"ilk işaret {markers[0].Start.Value} ms'de, 0 olmalı"));
        }

        for (var i = 1; i < markers.Count; i++)
        {
            var gap = markers[i].Start.Value - markers[i - 1].Start.Value;

            if (gap < MinimumSeconds * 1000)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture,
                    $"'{markers[i].Title}' önceki işaretten {gap / 1000.0:0.#} sn sonra, en az {MinimumSeconds} sn gerekiyor"));
            }
        }

        // SON İŞARET VİDEONUN İÇİNDE OLMALI: sürenin ötesine düşen bir
        // işaret listeyi geçersiz kılıyor ve bu, plan ile gerçek süre
        // ayrıştığında oluyor (ADR-006: süre ÖLÇÜLÜYOR, plan bir hedef).
        if (markers.Count > 0 && markers[^1].Start.Value >= totalDuration.Value)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture,
                $"son işaret {markers[^1].Start.Value} ms'de ama video {totalDuration.Value} ms"));
        }

        return problems;
    }

    /// `1:05:03` ya da `4:12`.
    ///
    /// SAAT YALNIZCA GEREKİYORSA: `0:04:12` biçimi de geçerli ama
    /// okunması zor ve uzun videolarımızın çoğu bir saatin altında.
    /// Dakika ve saniye HER ZAMAN iki hane — `4:2` biçimi platformda
    /// tanınmıyor.
    public static string Timestamp(Ms position)
    {
        var total = position.Value / 1000;
        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var seconds = total % 60;

        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes}:{seconds:00}");
    }
}
