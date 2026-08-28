using BytemountsAiStudio.Core.Execution;

namespace BytemountsAiStudio.Core.Tests;

/// Kanal ayar belgesinin okunması (P2-01/02/03/12).
///
/// Buradaki asıl sınav "doğru okuyor mu" değil, **yanlış yazılmış bir
/// ayarın görünür olup olmadığı**. Sessizce varsayılana düşmek, bir
/// kanalın aylarca yanlış tempoda çalışması ve kimsenin sebebini
/// bilmemesi demek.
public sealed class ChannelSettingsTests
{
    [Fact]
    public void BosBelge_VarsayilanaDusuyor()
    {
        var settings = ChannelSettings.Parse(null);

        Assert.Equal(1, settings.Pacing.DailyTarget);
        Assert.Equal(TimeSpan.FromHours(3), settings.Pacing.MinimumGap);
        Assert.Empty(settings.Genres);
    }

    [Fact]
    public void TamBelge_TumAlanlariOkuyor()
    {
        var settings = ChannelSettings.Parse(
            """
            {
              "workflow_key": "shorts-tr",
              "action_on_exceed": "stop",
              "pacing": {
                "daily_target": 4,
                "minimum_gap_minutes": 90,
                "time_zone": "Europe/Istanbul",
                "publish_windows": ["18:00", "09:00", "13:30"]
              },
              "genres": [
                { "name": "tarih", "share": 6 },
                { "name": "bilim", "share": 3 },
                { "name": "kultur", "share": 1 }
              ]
            }
            """);

        Assert.Equal("shorts-tr", settings.WorkflowKey);
        Assert.Equal(BudgetAction.StopEverything, settings.BudgetAction);
        Assert.Equal(4, settings.Pacing.DailyTarget);
        Assert.Equal(TimeSpan.FromMinutes(90), settings.Pacing.MinimumGap);

        // Pencereler SIRALI: "18:00, 09:00" yazan biri günün ilk
        // penceresini 18:00 sanmamalı.
        Assert.Equal([new TimeOnly(9, 0), new TimeOnly(13, 30), new TimeOnly(18, 0)],
            settings.Pacing.PublishWindows);

        // Paylar normalleştiriliyor: "6, 3, 1" ile "%60, %30, %10"
        // aynı anlama gelmeli.
        Assert.Equal(0.6, settings.Genres[0].Share, 3);
        Assert.Empty(settings.Warnings);
    }

    /// Ayarlar KÖKTE de olabiliyor: `pacing` sarmalayıcısı zorunlu
    /// değil. Zorunlu olsaydı en yalın ayar belgesi bile bir seviye
    /// iç içe yazılmak zorunda kalırdı.
    [Fact]
    public void PacingSarmalayicisiOlmadan_KoktenOkuyor()
        => Assert.Equal(7, ChannelSettings.Parse("""{"daily_target":7}""").Pacing.DailyTarget);

    /// BOZUK BELGE KANALI DURDURMUYOR ama sessiz de kalmıyor.
    [Fact]
    public void BozukJson_VarsayilanVeUyari()
    {
        var settings = ChannelSettings.Parse("{ bu json degil");

        Assert.Equal(1, settings.Pacing.DailyTarget);
        Assert.Single(settings.Warnings);
        Assert.Contains("okunamadı", settings.Warnings[0], StringComparison.Ordinal);
    }

    /// EN SİNSİ HATA BU: alan adı yanlış yazılmış.
    ///
    /// `dailyTarget` yazan biri günde 5 video beklerken 1 alır ve
    /// hiçbir hata görmez. Uyarı olmasaydı bunu ancak aylar sonra,
    /// üretim sayısını elle sayarak fark ederdi.
    [Fact]
    public void YanlisYazilmisAlan_UyariUretiyor()
    {
        var settings = ChannelSettings.Parse("""{"dailyTarget":5}""");

        Assert.Equal(1, settings.Pacing.DailyTarget);
        Assert.Contains(settings.Warnings, w => w.Contains("daily_target", StringComparison.Ordinal));
    }

    /// Okunamayan pencere ATLANMIYOR, duyuruluyor: "13:0" yazan bir
    /// kanal günde üç yerine iki video yayınlar ve kimse görmez.
    [Fact]
    public void BozukPencere_DigerleriniKorurVeUyarir()
    {
        var settings = ChannelSettings.Parse(
            """{"publish_windows":["09:00","13:0","18:00"]}""");

        Assert.Equal(2, settings.Pacing.PublishWindows.Count);
        Assert.Contains(settings.Warnings, w => w.Contains("13:0", StringComparison.Ordinal));
    }

    /// Bilinmeyen saat dilimi UTC'ye düşüyor: yapılandırma hatasının
    /// bedeli yanlış saat olmalı, hiç yayın olmaması değil.
    [Fact]
    public void BilinmeyenSaatDilimi_UtcVeUyari()
    {
        var settings = ChannelSettings.Parse("""{"time_zone":"Mars/Olympus"}""");

        Assert.Equal("UTC", settings.Pacing.TimeZoneId);
        Assert.Contains(settings.Warnings, w => w.Contains("Mars/Olympus", StringComparison.Ordinal));
    }

    /// PAYSIZ TÜR listede durup hiç üretilmezdi; artık bunu söylüyor.
    [Fact]
    public void PaysizTur_AtlaniyorVeUyariyor()
    {
        var settings = ChannelSettings.Parse(
            """{"genres":[{"name":"tarih","share":1},{"name":"bilim"}]}""");

        Assert.Single(settings.Genres);
        Assert.Contains(settings.Warnings, w => w.Contains("bilim", StringComparison.Ordinal));
    }

    /// Negatif hedef sıfıra çekiliyor: negatif bir hedef "hedef
    /// dolmadı" diye sonsuza kadar üretim tetiklerdi.
    [Fact]
    public void NegatifHedef_SifiraCekiliyor()
    {
        var settings = ChannelSettings.Parse("""{"daily_target":-3}""");

        Assert.Equal(0, settings.Pacing.DailyTarget);
        Assert.NotEmpty(settings.Warnings);
    }

    /// Tanınmayan `action_on_exceed` VARSAYILANA düşüyor,
    /// `StopEverything`'e değil: bir yazım hatası yüzünden yarım
    /// videoların çöpe gitmesi, bütçenin biraz aşılmasından pahalı.
    [Fact]
    public void TaninmayanButceEylemi_VarsayilanaDusuyor()
        => Assert.Equal(BudgetAction.FinishInFlight,
            ChannelSettings.Parse("""{"action_on_exceed":"dur bakalim"}""").BudgetAction);

    /// Nesne olmayan bir belge (dizi, sayı) de kanalı durdurmuyor.
    [Theory]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"metin\"")]
    public void NesneOlmayanBelge_VarsayilanVeUyari(string json)
    {
        var settings = ChannelSettings.Parse(json);

        Assert.Equal(1, settings.Pacing.DailyTarget);
        Assert.NotEmpty(settings.Warnings);
    }

    /// Sıfır hedef geçerli: kanal duraklatılmadan üretimi
    /// durdurulabilmeli.
    [Fact]
    public void SifirHedef_GecerliVeUyarisiz()
    {
        var settings = ChannelSettings.Parse("""{"daily_target":0}""");

        Assert.Equal(0, settings.Pacing.DailyTarget);
        Assert.Empty(settings.Warnings);
    }
}
