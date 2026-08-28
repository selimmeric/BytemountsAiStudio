using BytemountsAiStudio.Core.Execution;

namespace BytemountsAiStudio.Core.Tests;

/// Kanal kimliğinin ayar belgesinden okunması (P3-01).
///
/// ÇOKLU KANALIN EN TEMEL VAADİ: iki kanal AYNI grafla koşup farklı
/// sesle konuşabilmeli, farklı yazı tipi kullanabilmeli, farklı
/// tempoda üretebilmeli. Bunlar ayar belgesinde duruyordu ve
/// **hiçbiri okunmuyordu** — ses node ayarından geliyordu, yazı tipi
/// koda gömülüydü. Yani kanalı değiştirmek videoyu değiştirmiyordu.
public sealed class ChannelIdentityTests
{
    /// Tohumlanan kanalın gerçek ayar belgesi.
    private const string SeededSettings = """
        {
          "voice": { "voice_id": "fake-tr-f1", "speed": 1.0 },
          "font_stack": ["Inter", "Noto Sans", "Noto Color Emoji"],
          "model_tiers": { "cheap": "fake-llm", "standard": "fake-llm", "strong": "fake-llm" }
        }
        """;

    [Fact]
    public void TohumlananAyar_SesVeYaziTipiOkunuyor()
    {
        var settings = ChannelSettings.Parse(SeededSettings);

        Assert.Equal("fake-tr-f1", settings.VoiceId);
        Assert.Equal(["Inter", "Noto Sans", "Noto Color Emoji"], settings.FontStack);
    }

    /// İKİ YAZIM BİÇİMİ DE KABUL EDİLİYOR.
    ///
    /// Ayar belgesi hem `voice.voice_id` hem düz `voice_id` görüyor.
    /// Yalnız birini desteklemek, diğerini yazan kullanıcının ayarının
    /// sessizce yok sayılmasıydı — ve "sesi neden değişmiyor"
    /// sorusunun cevabı hiçbir yerde olmazdı.
    [Theory]
    [InlineData("""{"voice":{"voice_id":"a"}}""", "a")]
    [InlineData("""{"voice_id":"b"}""", "b")]
    [InlineData("""{"voice":{"voice_id":"a"},"voice_id":"b"}""", "a")]
    [InlineData("""{"voice":{}}""", null)]
    [InlineData("{}", null)]
    public void SesKimligi_IkiBicimdeDeOkunuyor(string json, string? expected)
        => Assert.Equal(expected, ChannelSettings.Parse(json).VoiceId);

    /// AYAR YOKSA `null`, boş liste değil.
    ///
    /// Boş liste "yazı tipi yok" diye okunursa hiçbir altyazı
    /// çizilemez. Yapılandırma hatasının bedeli varsayılan yazı tipi
    /// olmalı, altyazısız video değil.
    [Fact]
    public void YaziTipiYok_NullDonuyor()
        => Assert.Null(ChannelSettings.Parse("{}").FontStack);

    [Fact]
    public void BosYaziTipiListesi_NullVeUyari()
    {
        var settings = ChannelSettings.Parse("""{"font_stack":[]}""");

        Assert.Null(settings.FontStack);
        Assert.Contains(settings.Warnings, w => w.Contains("font_stack", StringComparison.Ordinal));
    }

    /// Metin olmayan girdiler atlanıyor, geçerli olanlar korunuyor.
    [Fact]
    public void KarisikYaziTipiListesi_GecerlileriAliyor()
    {
        var settings = ChannelSettings.Parse("""{"font_stack":["Inter", 42, "", "Arial"]}""");

        Assert.Equal(["Inter", "Arial"], settings.FontStack);
    }

    /// İKİ KANAL, İKİ KİMLİK: aynı ayrıştırıcı farklı belgelerden
    /// farklı kimlik üretiyor. Testin asıl söylediği bu — çoklu kanal
    /// tek bir belge biçimiyle ifade edilebiliyor.
    [Fact]
    public void IkiKanal_FarkliKimlik()
    {
        var turkish = ChannelSettings.Parse(
            """{"voice":{"voice_id":"tr-f1"},"font_stack":["Inter"],"daily_target":3}""");

        var arabic = ChannelSettings.Parse(
            """{"voice":{"voice_id":"ar-m1"},"font_stack":["Noto Naskh Arabic"],"daily_target":1}""");

        Assert.NotEqual(turkish.VoiceId, arabic.VoiceId);
        Assert.NotEqual(turkish.FontStack, arabic.FontStack);
        Assert.NotEqual(turkish.Pacing.DailyTarget, arabic.Pacing.DailyTarget);
    }
}
