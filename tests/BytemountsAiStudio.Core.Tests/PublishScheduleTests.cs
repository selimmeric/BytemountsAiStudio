using BytemountsAiStudio.Core.Execution;

namespace BytemountsAiStudio.Core.Tests;

/// Yayın zamanlamasının testleri (P2-02).
///
/// Kabul kriteri: **aynı anda toplu upload olmuyor; `publishAt` ile
/// yayın saati ayrılıyor.**
public sealed class PublishScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 9, 0, 0, TimeSpan.Zero);

    private static ChannelPacing Pacing(
        int target = 3, int gapHours = 3, params int[] windowHours)
        => new()
        {
            DailyTarget = target,
            MinimumGap = TimeSpan.FromHours(gapHours),
            PublishWindows = [.. windowHours.Select(h => new TimeOnly(h, 0))],
            TimeZoneId = "UTC",
        };

    private static QuotaReservation Ok()
        => new(QuotaOutcome.Reserved, QuotaLedger.UploadCost, 8_400, Now.AddHours(10));

    private static QuotaReservation Exhausted()
        => new(QuotaOutcome.Exhausted, QuotaLedger.UploadCost, 100, Now.AddHours(10));

    [Fact]
    public void TempoUygunsa_Basliyor()
    {
        var verdict = PublishSchedule.Decide(Pacing(), publishedToday: 0, null, Ok(), Now);

        Assert.True(verdict.ShouldStart);
    }

    /// KOTA ÖNCE bakılıyor: kota yoksa üretime hiç başlamamak
    /// gerekiyor. Videoyu üretip yükleyememek, harcanan her şeyi ertesi
    /// güne taşımak ve o gün yeniden ödemek demek.
    [Fact]
    public void KotaYoksa_UretimeBaslamiyor()
    {
        var verdict = PublishSchedule.Decide(Pacing(), publishedToday: 0, null, Exhausted(), Now);

        Assert.False(verdict.ShouldStart);
        Assert.Equal(ScheduleOutcome.QuotaExhausted, verdict.Outcome);
        Assert.True(verdict.RetryAfter > TimeSpan.Zero);
    }

    [Fact]
    public void GunlukHedefDoldu_Durmuyor()
    {
        var verdict = PublishSchedule.Decide(Pacing(target: 3), publishedToday: 3, null, Ok(), Now);

        Assert.Equal(ScheduleOutcome.TargetReached, verdict.Outcome);
        Assert.Contains("3/3", verdict.Reason, StringComparison.Ordinal);
    }

    /// KABUL KRİTERİ: toplu upload olmuyor.
    ///
    /// Beş videoyu arka arkaya yüklemek kanalı spam gibi gösteriyor ve
    /// platform hepsinin erişimini birden kısıyor.
    [Fact]
    public void TopluYukleme_Engelleniyor()
    {
        var verdict = PublishSchedule.Decide(
            Pacing(gapHours: 3), publishedToday: 1, lastPublishedAt: Now.AddMinutes(-30), Ok(), Now);

        Assert.False(verdict.ShouldStart);
        Assert.Equal(ScheduleOutcome.TooSoon, verdict.Outcome);

        // Bekleme, kalan süre kadar: sabit bir süre ya erken uyanır ya
        // da gereğinden uzun bekler.
        Assert.Equal(TimeSpan.FromHours(2.5), verdict.RetryAfter);
    }

    [Fact]
    public void AralikDoldu_Basliyor()
    {
        var verdict = PublishSchedule.Decide(
            Pacing(gapHours: 3), publishedToday: 1, lastPublishedAt: Now.AddHours(-4), Ok(), Now);

        Assert.True(verdict.ShouldStart);
    }

    /// KABUL KRİTERİ: `publishAt` ile yayın saati ayrılıyor.
    ///
    /// Kota gündüz harcanıyor, yayın istenen saatte oluyor — ikisini
    /// birbirine bağlamak, kotanın bittiği saatte yayın yapmak
    /// zorunda kalmaktı.
    [Fact]
    public void YayinSaati_UretimSaatindenAyri()
    {
        var verdict = PublishSchedule.Decide(
            Pacing(windowHours: [18, 21]), publishedToday: 0, null, Ok(), Now);

        Assert.True(verdict.ShouldStart);
        Assert.NotNull(verdict.PublishAt);

        // Üretim 09:00'da, yayın 18:00'de.
        Assert.Equal(18, verdict.PublishAt.Value.Hour);
    }

    /// Bugünün kalan penceresi yoksa YARININ ilki: yalnızca bugüne
    /// bakmak, akşam karar veren bir kanalın "pencere kalmadı" deyip
    /// durması demekti.
    [Fact]
    public void GununPencereleriBitti_YarininIlki()
    {
        var evening = new DateTimeOffset(2026, 8, 28, 22, 0, 0, TimeSpan.Zero);

        var next = PublishSchedule.NextWindow(Pacing(windowHours: [9, 18]), evening);

        Assert.NotNull(next);
        Assert.Equal(29, next.Value.Day);
        Assert.Equal(9, next.Value.Hour);
    }

    /// Pencere yoksa video hemen yayına giriyor. Boş listeyi "hiç
    /// yayınlama" diye okumak, pencere tanımlamayan bir kanalın
    /// sessizce durması demekti.
    [Fact]
    public void PencereYok_HemenYayin()
    {
        Assert.Null(PublishSchedule.NextWindow(Pacing(), Now));
    }

    /// Yapılandırmadaki bir yazım hatasının bedeli yanlış saat olmalı,
    /// hiç yayın olmaması değil.
    [Fact]
    public void BozukSaatDilimi_UtcyeDusuyor()
    {
        Assert.Equal(TimeZoneInfo.Utc, PublishSchedule.ResolveZone("Boyle/Bir_Yer_Yok"));
    }

    [Fact]
    public void GercekSaatDilimi_Cozuluyor()
    {
        var zone = PublishSchedule.ResolveZone(
            OperatingSystem.IsWindows() ? "Turkey Standard Time" : "Europe/Istanbul");

        Assert.NotEqual(TimeZoneInfo.Utc, zone);
    }

    /// Hedef pencere sayısından fazlaysa fazlalık ATILMIYOR: atmak,
    /// hedefi sessizce düşürmek olurdu.
    [Theory]
    [InlineData(5, 3, 2)]
    [InlineData(3, 3, 1)]
    [InlineData(1, 3, 1)]
    [InlineData(4, 0, 4)]
    public void HedefPencerelereDagitiliyor(int target, int windows, int expected)
    {
        Assert.Equal(expected, PublishSchedule.PerWindow(target, windows));
    }

    /// Sıfır hedefli kanal hiç başlamıyor: duraklatmanın bir yolu bu.
    [Fact]
    public void SifirHedef_HicBaslamiyor()
    {
        var verdict = PublishSchedule.Decide(Pacing(target: 0), publishedToday: 0, null, Ok(), Now);

        Assert.False(verdict.ShouldStart);
    }

    /// İlk video için "son yayın" yok; aralık kuralı devreye girmiyor.
    [Fact]
    public void IlkVideo_AralikKuraliYok()
    {
        Assert.True(PublishSchedule.Decide(Pacing(), 0, lastPublishedAt: null, Ok(), Now).ShouldStart);
    }
}
