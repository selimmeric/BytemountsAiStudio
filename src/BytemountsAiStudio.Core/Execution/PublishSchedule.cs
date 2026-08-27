using System.Globalization;

namespace BytemountsAiStudio.Core.Execution;

/// Bir kanalın yayın temposu (P2-02).
public sealed record ChannelPacing
{
    /// Günde kaç video hedefleniyor.
    public required int DailyTarget { get; init; }

    /// Yayın saatleri, kanalın kendi saat diliminde. Boşsa gün boyu.
    ///
    /// Pencere bir TERCİH değil bir gereklilik: izleyicinin uyanık
    /// olduğu saate denk gelmeyen video, algoritmanın ilk saatteki
    /// etkileşim sinyalini alamıyor ve o video bir daha
    /// toparlanmıyor.
    public IReadOnlyList<TimeOnly> PublishWindows { get; init; } = [];

    /// İki yayın arasındaki en az süre.
    ///
    /// TOPLU YÜKLEMEYİ ENGELLEYEN ŞEY BU. Beş videoyu arka arkaya
    /// yüklemek, kanalı spam gibi gösteriyor ve platform hepsinin
    /// erişimini birden kısıyor — üstelik izleyici de aynı anda beş
    /// bildirim alıyor.
    public TimeSpan MinimumGap { get; init; } = TimeSpan.FromHours(3);

    public string TimeZoneId { get; init; } = "Europe/Istanbul";
}

public enum ScheduleOutcome
{
    /// Şimdi başlat.
    StartNow = 0,

    /// Günlük hedef doldu.
    TargetReached = 1,

    /// Son yayının üstünden yeterli süre geçmedi.
    TooSoon = 2,

    /// Kota yetmiyor.
    QuotaExhausted = 3,
}

public sealed record ScheduleVerdict(
    ScheduleOutcome Outcome, DateTimeOffset? PublishAt, TimeSpan RetryAfter, string Reason)
{
    public bool ShouldStart => Outcome == ScheduleOutcome.StartNow;

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{Outcome}: {Reason}");
}

/// Yayın zamanlaması (P2-02, §15.3).
///
/// SAF: veritabanı yok. "Şimdi yeni bir video başlatmalı mıyız"
/// kararı, gerçek bir gün boyu sistem koşturularak öğrenilecek bir şey
/// olmamalı.
///
/// KOTA İLE YAYIN TEMPOSU AYRI ŞEYLER ve bu ayrım §15.3'ün özü.
///
/// Kota gündüz harcanıyor (üretim ve yükleme), yayın istenen saatte
/// oluyor: video gizli yükleniyor ve `publishAt` ile o saatte
/// kendiliğinden açılıyor. Böylece "kota bitmeden önce yükle" ile
/// "izleyicinin uyanık olduğu saatte yayınla" aynı anda sağlanıyor —
/// ikisini birbirine bağlamak, kotanın bittiği saatte yayın yapmak
/// zorunda kalmak demekti.
public static class PublishSchedule
{
    public static ScheduleVerdict Decide(
        ChannelPacing pacing,
        int publishedToday,
        DateTimeOffset? lastPublishedAt,
        QuotaReservation quota,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(pacing);
        ArgumentNullException.ThrowIfNull(quota);

        // KOTA ÖNCE: kota yoksa üretime hiç başlamamak gerekiyor.
        // Videoyu üretip yükleyememek, harcanan her şeyi ertesi güne
        // taşımak ve o gün yeniden ödemek demek.
        if (!quota.Granted)
        {
            return new ScheduleVerdict(ScheduleOutcome.QuotaExhausted, null,
                quota.RetryAfter(now), "günlük kota yetmiyor");
        }

        if (publishedToday >= Math.Max(pacing.DailyTarget, 0))
        {
            return new ScheduleVerdict(ScheduleOutcome.TargetReached, null,
                BudgetPolicy.UntilTomorrow(now),
                string.Create(CultureInfo.InvariantCulture,
                    $"günlük hedef doldu ({publishedToday}/{pacing.DailyTarget})"));
        }

        // TOPLU YÜKLEME ENGELİ.
        if (lastPublishedAt is { } last && now - last < pacing.MinimumGap)
        {
            var wait = pacing.MinimumGap - (now - last);

            return new ScheduleVerdict(ScheduleOutcome.TooSoon, null, wait,
                string.Create(CultureInfo.InvariantCulture,
                    $"son yayının üstünden {(now - last).TotalMinutes:0} dk geçti, en az {pacing.MinimumGap.TotalMinutes:0} dk gerekiyor"));
        }

        return new ScheduleVerdict(ScheduleOutcome.StartNow, NextWindow(pacing, now), TimeSpan.Zero,
            "tempo uygun");
    }

    /// Sıradaki yayın penceresi.
    ///
    /// Pencere yoksa `null` — video hemen yayına giriyor. Boş bir
    /// listeyi "hiç yayınlama" diye okumak, pencere tanımlamayan bir
    /// kanalın sessizce durması demekti.
    public static DateTimeOffset? NextWindow(ChannelPacing pacing, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(pacing);

        if (pacing.PublishWindows.Count == 0)
        {
            return null;
        }

        var zone = ResolveZone(pacing.TimeZoneId);
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var today = TimeOnly.FromDateTime(local.DateTime);

        // BUGÜNÜN kalan pencereleri, sonra yarının ilki. Yalnızca
        // bugüne bakmak, akşam saatinde karar veren bir kanalın
        // "pencere kalmadı" deyip durması demekti.
        var upcoming = pacing.PublishWindows.Where(w => w > today).OrderBy(w => w).ToList();

        var (date, window) = upcoming.Count > 0
            ? (local.Date, upcoming[0])
            : (local.Date.AddDays(1), pacing.PublishWindows.Min());

        var target = date.Add(window.ToTimeSpan());

        // Ofset HEDEF TARİHTEN alınıyor, bugünden değil: yaz saati
        // geçişinde iki tarih farklı ofsette olabiliyor ve bugünün
        // ofsetini kullanmak yayını bir saat kaydırırdı.
        var offset = zone.GetUtcOffset(target);

        return new DateTimeOffset(target, offset);
    }

    /// Saat dilimi kimliği platforma göre değişiyor ve yanlış kimlik
    /// istisna atıyor; bulunamazsa UTC'ye düşülüyor.
    ///
    /// UTC'ye düşmek yayın saatini kaydırıyor ama kanalı tamamen
    /// durdurmuyor — yapılandırmadaki bir yazım hatasının bedeli
    /// yanlış saat olmalı, hiç yayın olmaması değil.
    internal static TimeZoneInfo ResolveZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    /// Günlük hedefi pencerelere dağıtır.
    ///
    /// Hedef pencere sayısından fazlaysa fazlalık ATILMIYOR, pencereler
    /// arasında paylaştırılıyor: üç pencereli bir kanalda günde beş
    /// video hedefi, iki pencerede iki video demek. Atmak, hedefi
    /// sessizce düşürmek olurdu.
    public static int PerWindow(int dailyTarget, int windowCount)
    {
        if (windowCount <= 0)
        {
            return Math.Max(dailyTarget, 0);
        }

        return (int)Math.Ceiling(Math.Max(dailyTarget, 0) / (double)windowCount);
    }
}
