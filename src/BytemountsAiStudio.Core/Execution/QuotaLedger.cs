using System.Globalization;

namespace BytemountsAiStudio.Core.Execution;

/// Bir kota rezervasyonunun sonucu (P1-24).
public enum QuotaOutcome
{
    /// Rezerve edildi; iş çalışabilir.
    Reserved = 0,

    /// Bugünlük kota yetmiyor. HATA DEĞİL — iş ertesi güne kayıyor.
    Exhausted = 1,
}

public sealed record QuotaReservation(QuotaOutcome Outcome, int Cost, int RemainingAfter, DateTimeOffset ResetsAt)
{
    public bool Granted => Outcome == QuotaOutcome.Reserved;

    /// Kotanın sıfırlanmasına kalan süre.
    ///
    /// Kaynak hatasının `retryAfter` değeri bu: iş tam sıfırlanma
    /// anında uyanıyor, daha önce değil. Sabit bir süre (örneğin bir
    /// saat) vermek, kota gece yarısı sıfırlanırken sabaha kadar
    /// boşuna uyanmak demekti.
    public TimeSpan RetryAfter(DateTimeOffset now)
    {
        var wait = ResetsAt - now;

        return wait > TimeSpan.Zero ? wait : TimeSpan.FromMinutes(1);
    }

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture,
            $"{Outcome}: {Cost} birim, kalan {RemainingAfter}, sifirlanma {ResetsAt:u}");
}

/// Günlük kota muhasebesi (P1-24, §15.1).
///
/// KOTA BU SİSTEMDE BÜTÇE KADAR BİRİNCİ SINIF BİR KAYNAK (ADR-011).
/// YouTube'da bir yükleme 1.600 birim ve günlük havuz 10.000 birim —
/// yani günde altı video. Yedincisi bir HATA değil, ertesi güne kalmış
/// bir iş.
///
/// SAF: veritabanı yok, ağ yok. "Bu iş bugün çalışabilir mi" kararı,
/// gerçek bir kota tüketilerek öğrenilecek bir şey olmamalı.
///
/// REZERVASYON, HARCAMA DEĞİL. İkisi ayrı çünkü arada iş var: rezerve
/// edilen kota yükleme başlamadan önce düşülüyor, gerçek harcama
/// sonra bildiriliyor. Yalnızca gerçekleşeni saymak, aynı anda başlayan
/// iki yüklemenin ikisinin de "yer var" görmesi demekti.
public static class QuotaLedger
{
    /// YouTube'un günlük varsayılan havuzu.
    public const int DailyUnits = 10_000;

    /// Tek yüklemenin maliyeti (video ekleme çağrısı).
    public const int UploadCost = 1_600;

    /// Kapak görseli ayrı bir çağrı ve ayrı ücretli.
    public const int ThumbnailCost = 50;

    /// Kotanın sıfırlandığı an.
    ///
    /// YouTube kotayı **Pasifik saatiyle gece yarısı** sıfırlıyor,
    /// UTC'yle değil. UTC varsayılsaydı yaz saatine göre 7–8 saat
    /// sapma olurdu ve iş ya erken uyanıp boşuna denerdi ya da geç
    /// uyanıp bir günü kaybederdi.
    public static DateTimeOffset NextReset(DateTimeOffset now)
    {
        var pacific = TimeZoneInfo.FindSystemTimeZoneById(PacificId());
        var local = TimeZoneInfo.ConvertTime(now, pacific);
        var midnight = new DateTimeOffset(local.Date.AddDays(1), local.Offset);

        // Sıfırlama anı yaz saati geçişinde farklı bir ofsete
        // düşebiliyor; dönüşüm yeniden yapılıyor.
        return TimeZoneInfo.ConvertTime(midnight, TimeZoneInfo.Utc);
    }

    /// Kimlik farkı: Windows ve Linux aynı saat dilimine farklı ad
    /// veriyor ve yanlış ad `TimeZoneNotFoundException` atıyor.
    private static string PacificId()
        => OperatingSystem.IsWindows() ? "Pacific Standard Time" : "America/Los_Angeles";

    /// Bugün harcanmış birime bakarak rezervasyon kararı verir.
    public static QuotaReservation Reserve(int spentToday, int cost, DateTimeOffset now, int dailyLimit = DailyUnits)
    {
        var limit = Math.Max(dailyLimit, 0);
        var spent = Math.Clamp(spentToday, 0, int.MaxValue);
        var needed = Math.Max(cost, 0);
        var remaining = limit - spent;
        var resets = NextReset(now);

        // TAM SIĞMASI gerekiyor: kısmi yükleme diye bir şey yok.
        // Yarım kotayla başlanan bir yükleme ortasında reddedilir ve
        // harcanan kısım da geri gelmez.
        if (needed > remaining)
        {
            return new QuotaReservation(QuotaOutcome.Exhausted, needed, remaining, resets);
        }

        return new QuotaReservation(QuotaOutcome.Reserved, needed, remaining - needed, resets);
    }

    /// Bir yayının toplam kota maliyeti.
    ///
    /// Kapak AYRI sayılıyor: kapaksız bir yayın 1.600, kapaklı 1.650
    /// birim. Hep kapaklı varsaymak, günde bir videoluk kotayı boşuna
    /// rezerve etmek demekti.
    public static int CostOf(bool withThumbnail, bool withPlaylist)
        => UploadCost
           + (withThumbnail ? ThumbnailCost : 0)
           + (withPlaylist ? PlaylistCost : 0);

    /// Oynatma listesine ekleme de ayrı bir yazma çağrısı.
    public const int PlaylistCost = 50;
}
