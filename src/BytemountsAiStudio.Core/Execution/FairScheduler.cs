using System.Globalization;

namespace BytemountsAiStudio.Core.Execution;

/// Bir kanalın kuyruktaki durumu (P2-05).
public sealed record ChannelLoad(Guid ChannelId, int Running, int Waiting, DateTimeOffset? OldestWaitingSince)
{
    /// Yakın geçmişte kaç iş aldı.
    ///
    /// ZORUNLU ve sebebi bir testte ortaya çıktı: yalnızca "şu an
    /// koşan" sayısına bakmak, işler hızlı bittiğinde adaleti
    /// tamamen bozuyor. Her iş anında bitince koşan sayısı hep sıfır
    /// kalıyor, ölçüt eşitleniyor ve son çare olan kimlik sıralaması
    /// devreye giriyor — yani en küçük kimlikli kanal HER TURU
    /// kazanıyor ve diğerleri aç kalıyor.
    ///
    /// Anlık yük ile geçmiş pay ayrı ölçütler: birincisi "şimdi
    /// boğulmasın", ikincisi "uzun vadede hakkını alsın".
    public int RecentlyServed { get; init; }

    public bool HasWork => Waiting > 0;
}

/// Kanal adaleti (P2-05, §8.2).
///
/// SORUN SOMUT: tek bir kuyruk ve öncelik sırası varken, çok işi olan
/// bir kanal diğerlerini AÇ BIRAKIYOR. Yirmi videoluk bir kampanya
/// başlatan kanal, günde bir video üreten kanalın işini saatlerce
/// bekletiyor — ve ikincisi hiçbir zaman "hata" vermiyor, sadece
/// hiç sıra alamıyor.
///
/// SAF: veritabanı yok. Adalet kararı, üç kanallı bir yük testi
/// koşturularak değil, doğrudan sınanabilmeli.
///
/// Seçilen kural üç ölçütlü: **anlık yük → geçmiş pay → bekleme
/// süresi**. İkinci ölçüt sonradan eklendi ve sebebi bir testte
/// ortaya çıktı — işler hızlı bittiğinde koşan sayısı hep sıfır
/// kalıyor, ilk ölçüt hiçbir şey ayırt etmiyor ve seçim kimlik
/// sırasına düşüyor. O hâlde en küçük kimlikli kanal her turu
/// kazanıyor: tam da önlemeye çalıştığımız açlık.
///
/// Round-robin değil: sıradaki kanalın işi yoksa turu boşa harcıyor ve
/// kanal sayısı arttıkça gecikme büyüyor. Ağırlıklı adalet de değil:
/// ağırlık yapılandırma demek ve yanlış ayarlanan bir ağırlık yine
/// açlık üretiyor.
public static class FairScheduler
{
    /// Sıradaki işi hangi kanaldan almalı.
    ///
    /// `null` dönerse bekleyen iş yok demektir.
    public static Guid? NextChannel(IReadOnlyList<ChannelLoad> loads, int maxPerChannel = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(loads);

        var eligible = loads
            .Where(l => l.HasWork)
            // KANAL BAŞINA TAVAN: bir kanal bütün worker'ları
            // kaplayamıyor. Tavan olmasaydı, tek kanalın yirmi işi
            // aynı anda kiralanıp diğer kanallar boş worker
            // bulamazdı.
            .Where(l => l.Running < maxPerChannel)
            .ToList();

        if (eligible.Count == 0)
        {
            return null;
        }

        return eligible
            // 1. ANLIK YÜK: şu an en az koşan. Bir kanalın aynı anda
            //    bütün worker'ları kaplamasını engelliyor.
            .OrderBy(l => l.Running)
            // 2. GEÇMİŞ PAY: yakın geçmişte en az sıra almış olan.
            //    Bu ölçüt olmadan, işler hızlı bittiğinde koşan sayısı
            //    hep sıfır kalıyor ve seçim tamamen kimlik sırasına
            //    düşüyor — en küçük kimlikli kanal her turu kazanıp
            //    diğerlerini aç bırakıyor.
            .ThenBy(l => l.RecentlyServed)
            // 3. En uzun bekleyen. Eşit paya sahip kanallar arasında
            //    seçim rastgele olsaydı biri şanssızlık yüzünden
            //    sürekli sona kalabilirdi.
            .ThenBy(l => l.OldestWaitingSince ?? DateTimeOffset.MaxValue)
            // Son çare kimlik: aynı anda gelen iki iş arasında karar
            // KARARLI olmalı, yoksa aynı sorgu iki kez farklı cevap
            // verir ve teşhis imkânsızlaşır.
            .ThenBy(l => l.ChannelId)
            .First()
            .ChannelId;
    }

    /// Bir kanal aç kalmış mı.
    ///
    /// Açlık, "iş yok" ile karıştırılmamalı: bekleyen işi OLAN ama
    /// hiç koşanı olmayan ve uzun süredir bekleyen bir kanal aç
    /// demektir. Bu ayrım ölçülebilir olmalı — panelde görünmeyen bir
    /// açlık, kimsenin fark etmediği bir açlıktır.
    public static bool IsStarving(ChannelLoad load, DateTimeOffset now, TimeSpan threshold)
    {
        ArgumentNullException.ThrowIfNull(load);

        return load is { Waiting: > 0, Running: 0 }
               && load.OldestWaitingSince is { } since
               && now - since > threshold;
    }

    /// Kanal başına eşzamanlılık tavanı.
    ///
    /// Kanal sayısına göre hesaplanıyor: tek kanal varken tavan
    /// koymak, sistemi boşuna yavaşlatmak olurdu. Çok kanalda ise
    /// tavan, en az bir worker'ın her kanala kalmasını sağlıyor.
    ///
    /// Tavan EN AZ 1: sıfır olsaydı hiçbir kanal iş alamazdı ve
    /// sistem sessizce dururdu.
    public static int CapFor(int workerCount, int activeChannels)
    {
        if (activeChannels <= 1)
        {
            return Math.Max(workerCount, 1);
        }

        return Math.Max(workerCount / activeChannels, 1);
    }

    public static string Describe(IReadOnlyList<ChannelLoad> loads, DateTimeOffset now, TimeSpan threshold)
    {
        ArgumentNullException.ThrowIfNull(loads);

        var starving = loads.Count(l => IsStarving(l, now, threshold));

        return string.Create(CultureInfo.InvariantCulture,
            $"{loads.Count} kanal, {loads.Sum(l => l.Running)} kosan, {loads.Sum(l => l.Waiting)} bekleyen, {starving} ac");
    }
}
