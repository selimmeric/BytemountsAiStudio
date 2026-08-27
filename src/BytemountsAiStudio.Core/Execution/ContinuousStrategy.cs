using System.Globalization;

namespace BytemountsAiStudio.Core.Execution;

/// Bir içerik türü ve hedeflenen payı (P2-12).
public sealed record ContentGenre(string Name, double Share)
{
    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{Name} %{Share * 100:0}");
}

/// Sürekli mod: sistem gün boyu kendi kendine ne üreteceğine karar
/// veriyor (P2-12).
///
/// SAF: veritabanı yok. "Sıradaki video hangi türden olmalı" kararı,
/// on iki saat sistem koşturularak öğrenilecek bir şey olmamalı.
///
/// TÜR KARIŞIMI NEDEN GEREKLİ: tek türe kilitlenen bir kanal, o tür
/// tükendiğinde duruyor ve izleyici de aynı şeyi tekrar tekrar
/// görüyor. Ama karışım rastgele de olamaz — "%60 liste, %30 tarih,
/// %10 gizem" dendiğinde gerçekten o oranda üretilmesi gerekiyor.
public static class ContinuousStrategy
{
    /// Sıradaki türü seçer.
    ///
    /// EN ÇOK GERİDE KALAN tür seçiliyor: hedef payı ile gerçekleşen
    /// pay arasındaki fark en büyük olan.
    ///
    /// Rastgele seçim (paya göre zar atmak) uzun vadede doğru orana
    /// yakınsıyor ama kısa vadede sapıyor — günde beş video üreten bir
    /// kanalda "uzun vade" haftalar demek ve o haftalarda oran gözle
    /// görülür biçimde yanlış oluyor.
    public static string? Next(IReadOnlyList<ContentGenre> genres, IReadOnlyDictionary<string, int> produced)
    {
        ArgumentNullException.ThrowIfNull(genres);
        ArgumentNullException.ThrowIfNull(produced);

        var eligible = genres.Where(g => g.Share > 0).ToList();

        if (eligible.Count == 0)
        {
            return null;
        }

        var total = produced.Values.Sum();

        if (total == 0)
        {
            // İLK VİDEO: en büyük paylı tür. Rastgele seçmek, aynı
            // yapılandırmanın iki koşuda farklı başlaması demekti ve
            // bir sorunun tekrarlanabilirliğini bozardı.
            return eligible.OrderByDescending(g => g.Share).ThenBy(g => g.Name, StringComparer.Ordinal)
                .First().Name;
        }

        var shareSum = eligible.Sum(g => g.Share);

        return eligible
            .OrderByDescending(g =>
            {
                var actual = produced.TryGetValue(g.Name, out var count) ? count / (double)total : 0;
                var target = g.Share / shareSum;

                return target - actual;
            })
            // Eşitlikte ad sırası: karar KARARLI olmalı, yoksa aynı
            // durum iki kez farklı tür üretir ve "neden bu sırayla
            // üretildi" sorusu cevapsız kalır.
            .ThenBy(g => g.Name, StringComparer.Ordinal)
            .First()
            .Name;
    }

    /// Payları normalleştirir.
    ///
    /// Toplam 1 olmak ZORUNDA DEĞİL: yapılandırmada "%60, %30, %10"
    /// yazmak ile "6, 3, 1" yazmak aynı anlama gelmeli. Toplamın tam
    /// 1 olmasını şart koşmak, her ayar değişikliğinde elle toplama
    /// yapmayı gerektirirdi.
    public static IReadOnlyList<ContentGenre> Normalize(IReadOnlyList<ContentGenre> genres)
    {
        ArgumentNullException.ThrowIfNull(genres);

        var positive = genres.Where(g => g.Share > 0).ToList();
        var total = positive.Sum(g => g.Share);

        if (total <= 0)
        {
            return [];
        }

        return [.. positive.Select(g => g with { Share = g.Share / total })];
    }

    /// Günlük hedefe ne kadar kaldı.
    ///
    /// NEGATİF OLAMIYOR: hedefin üstüne çıkmış bir kanalda "eksi iki
    /// video" demek anlamsız ve o sayı bir döngüde geriye sayarsa
    /// sonsuza kadar üretim tetikler.
    public static int Remaining(int dailyTarget, int producedToday)
        => Math.Max(Math.Max(dailyTarget, 0) - Math.Max(producedToday, 0), 0);

    /// Karışımın ne kadar tuttuğu — panelde görünmesi gereken sayı.
    ///
    /// En büyük sapma yüzde puanı olarak. Sıfıra yakınsa karışım
    /// tutuyor demektir; büyükse ya hedefler yeni değişmiş ya da bir
    /// tür sürekli üretilemiyor (konu havuzu boş olabilir) ve
    /// ikincisi sessizce olabilecek bir arıza.
    public static double LargestDrift(
        IReadOnlyList<ContentGenre> genres, IReadOnlyDictionary<string, int> produced)
    {
        ArgumentNullException.ThrowIfNull(genres);
        ArgumentNullException.ThrowIfNull(produced);

        var total = produced.Values.Sum();

        if (total == 0)
        {
            return 0;
        }

        var normalized = Normalize(genres);

        if (normalized.Count == 0)
        {
            return 0;
        }

        return normalized.Max(g =>
        {
            var actual = produced.TryGetValue(g.Name, out var count) ? count / (double)total : 0;

            return Math.Abs(g.Share - actual) * 100;
        });
    }
}
