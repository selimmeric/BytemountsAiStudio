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
