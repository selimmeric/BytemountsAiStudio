using BytemountsAiStudio.Media.Timeline;

namespace BytemountsAiStudio.Media.Tests;

/// Çıktı konteynerinin GERÇEKTEN uygulandığının sınanması.
///
/// ***BU DOSYANIN VAR OLMA SEBEBİ:*** `OutputSpec.Container` timeline'da
/// okunuyor, doğrulanıyor ve **hiçbir yere taşınmıyordu**. Timeline'da
/// `container: "webm"` yazan biri hiçbir hata almıyor, render yine mp4
/// üretiyordu — "ayarımı neden uygulamıyor" sorusunun cevabı hiçbir
/// yerde yoktu. Bu, deponun tekrar eden hata sınıfı: kaydediliyor,
/// okunmuyor.
public sealed class ContainerTests
{
    /// VARSAYILAN mp4.
    [Fact]
    public void Varsayilan_Mp4()
        => Assert.Equal("mp4", new OutputSpec { Preset = "shorts-1080x1920" }.Container);

    /// ***BİLİNMEYEN KONTEYNER DOĞRULAMADA DÜŞÜYOR.***
    ///
    /// Uzantı doğrudan dosya adına gidiyor: serbest bırakmak
    /// `container: "../../etc"` yazan bir timeline'ın çıktı yolunu
    /// dizin dışına taşıması demekti. Yazım hatası (`"mp"`) de ffmpeg
    /// tarafında anlaşılmaz bir hata üretirdi.
    [Theory]
    [InlineData("mp")]
    [InlineData("../../etc")]
    [InlineData("")]
    public void BilinmeyenKonteyner_Reddediliyor(string container)
    {
        var timeline = TimelineFactory.Valid() with
        {
            Output = new OutputSpec { Preset = "shorts-1080x1920", Container = container },
        };

        var issues = TimelineValidator.Validate(timeline);

        Assert.Contains(issues, i => i.Code == "timeline.unknown_container");
    }

    /// BİLİNEN KONTEYNERLERİN HEPSİ KABUL EDİLİYOR.
    ///
    /// Liste ile doğrulama aynı yerden geliyor; ayrışsalardı belgede
    /// yazan bir değer kodda reddedilirdi.
    [Fact]
    public void BilinenKonteynerler_KabulEdiliyor()
    {
        foreach (var container in OutputSpec.KnownContainers)
        {
            var timeline = TimelineFactory.Valid() with
            {
                Output = new OutputSpec { Preset = "shorts-1080x1920", Container = container },
            };

            Assert.DoesNotContain(
                TimelineValidator.Validate(timeline),
                i => i.Code == "timeline.unknown_container");
        }
    }
}
