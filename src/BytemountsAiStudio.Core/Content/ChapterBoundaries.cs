namespace BytemountsAiStudio.Core.Content;

/// Bölüm sınırlarının hangi sahne sınırına düştüğü (P3-04).
///
/// NEDEN EŞLEŞTİRME GEREKİYOR: bölüm planı bir HEDEF veriyor
/// (`start_ms`), sahneler ise gerçek seslendirme sürelerinden
/// doğuyor. İkisi asla tam tutmuyor — plan 144.000 ms diyor, sahne
/// sınırı 141.320 ms'de. Eşitlik araması hiçbir sınır bulamazdı ve
/// "bölüm geçişleri var" iddiası sessizce boş kalırdı.
///
/// TOLERANS DEĞİL, EN YAKIN: bir tolerans sayısı (±3 sn gibi)
/// uydurmak, uzun bölümlerde tutup kısa bölümlerde tutmayan bir
/// kural olurdu. Her bölüm başlangıcı için EN YAKIN sahne sınırını
/// seçmek, ölçekten bağımsız çalışıyor ve her bölüm tam olarak bir
/// sınır işaretliyor.
public static class ChapterBoundaries
{
    /// Bölüm başlangıçlarını sahne sınırlarına eşler.
    ///
    /// Dönen küme, GEÇİŞİ UZATILACAK sahnelerin indeksleri: sahne
    /// `i` kümedeyse, `i` ile `i+1` arasında bölüm değişiyor.
    ///
    /// SON SAHNE HİÇ İŞARETLENMİYOR: onun `TransitionOut`'u videonun
    /// kapanışı, bir bölüm geçişi değil. İkisini aynı yere yazmak,
    /// kapanışı bölüm geçişi uzunluğuna kısaltırdı.
    public static IReadOnlySet<int> Match(
        IReadOnlyList<int> sceneEndsMs, IReadOnlyList<int> chapterStartsMs)
    {
        ArgumentNullException.ThrowIfNull(sceneEndsMs);
        ArgumentNullException.ThrowIfNull(chapterStartsMs);

        var marked = new HashSet<int>();

        // Son sahnenin sınırı aday değil; iki sahneden azsa hiç
        // bölüm geçişi olamaz.
        var candidates = sceneEndsMs.Count - 1;

        if (candidates < 1)
        {
            return marked;
        }

        foreach (var start in chapterStartsMs)
        {
            // SIFIRDAN BAŞLAYAN BÖLÜM SINIR DEĞİL: videonun başı
            // zaten bir geçiş ve orada "önceki bölüm" yok.
            if (start <= 0)
            {
                continue;
            }

            var best = -1;
            var bestDistance = int.MaxValue;

            for (var i = 0; i < candidates; i++)
            {
                var distance = Math.Abs(sceneEndsMs[i] - start);

                // EŞİTLİKTE ÖNCEKİ KAZANIYOR (`<`, `<=` değil): iki
                // sınır aynı uzaklıktaysa seçim belirli olmalı,
                // yoksa aynı girdi iki farklı video üretirdi.
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }

            if (best >= 0)
            {
                // İKİ BÖLÜM AYNI SINIRA DÜŞEBİLİR (çok kısa bir
                // bölüm, birkaç uzun sahne). `HashSet` bunu kendi
                // hâlinde tekilleştiriyor: sınır ya bölüm sınırı ya
                // değil, "iki kere bölüm sınırı" diye bir şey yok.
                marked.Add(best);
            }
        }

        return marked;
    }
}
