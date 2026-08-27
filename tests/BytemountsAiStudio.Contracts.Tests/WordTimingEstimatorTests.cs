using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core.Time;

namespace BytemountsAiStudio.Contracts.Tests;

/// Kelime zamanı dağıtımının testleri (P1-15 ara çözüm).
///
/// Bu sınıf, ücretsiz hatta kullandığımız Windows konuşma sentezinin
/// kelime zamanlaması vermemesi yüzünden var. Zamanlama olmayınca
/// altyazı ipucu üretilemiyordu ve GERÇEK videolar altyazısız çıkıyordu —
/// sahte hatta altyazı vardı, gerçek hatta yoktu, ve fark görünmüyordu.
public sealed class WordTimingEstimatorTests
{
    private static IReadOnlyList<WordTiming> Distribute(string text, int ms)
        => WordTimingEstimator.Distribute(text, new Ms(ms));

    [Fact]
    public void HerKelime_BirIpucuAlir()
    {
        var timings = Distribute("Bir iki üç dört", 4000);

        Assert.Equal(4, timings.Count);
        Assert.Equal("Bir", timings[0].Text);
        Assert.Equal("dört", timings[3].Text);
    }

    /// Son kelimenin sonu TAM olarak segment sonu. Kayan nokta birikimi
    /// yüzünden birkaç milisaniye eksik kalırsa fark uzun videolarda
    /// büyür.
    [Fact]
    public void SonKelime_SegmentSonundaBiter()
    {
        var timings = Distribute("Bir iki üç dört beş altı yedi", 7777);

        Assert.Equal(7777, timings[^1].End.Value);
    }

    [Fact]
    public void IlkKelime_SifirdaBaslar()
    {
        Assert.Equal(0, Distribute("Merhaba dünya", 2000)[0].Start.Value);
    }

    [Fact]
    public void Ipuclari_BosluksuzArdArda()
    {
        var timings = Distribute("Bir iki üç dört beş", 5000);

        for (var i = 1; i < timings.Count; i++)
        {
            Assert.Equal(timings[i - 1].End.Value, timings[i].Start.Value);
        }
    }

    /// Eşit paylaştırmak "bir" ile "arkeologların" kelimesine aynı süreyi
    /// verirdi ve uzun kelimelerde altyazı sesin gerisinde kalırdı.
    [Fact]
    public void UzunKelime_DahaFazlaSureAlir()
    {
        var timings = Distribute("bir arkeologların", 4000);

        var first = timings[0].End.Value - timings[0].Start.Value;
        var second = timings[1].End.Value - timings[1].Start.Value;

        Assert.True(second > first, $"uzun kelime daha kisa surdu: {second} <= {first}");
    }

    /// Cümle ve virgül sonrasında gerçek bir es var. Saymazsak sondaki
    /// kelimeler erken biter ve altyazı sesin ÖNÜNE geçer — önüne geçen
    /// altyazı arkada kalandan daha rahatsız edici.
    [Fact]
    public void NoktalamaSonrasi_DahaUzunSurer()
    {
        var withPause = Distribute("kelime. kelime", 4000);
        var withoutPause = Distribute("kelime kelime", 4000);

        var pausedFirst = withPause[0].End.Value - withPause[0].Start.Value;
        var plainFirst = withoutPause[0].End.Value - withoutPause[0].Start.Value;

        Assert.True(pausedFirst > plainFirst,
            $"noktalamali kelime daha kisa surdu: {pausedFirst} <= {plainFirst}");
    }

    [Fact]
    public void TekKelime_TumSureyiAlir()
    {
        var timings = Distribute("Göbeklitepe", 3000);

        Assert.Single(timings);
        Assert.Equal(0, timings[0].Start.Value);
        Assert.Equal(3000, timings[0].End.Value);
    }

    [Theory]
    [InlineData("", 3000)]
    [InlineData("   ", 3000)]
    [InlineData("kelime", 0)]
    [InlineData("kelime", -100)]
    public void GecersizGirdi_BosDoner(string text, int ms)
    {
        Assert.Empty(Distribute(text, ms));
    }

    /// Aynı girdi aynı çıktıyı vermeli: altyazı katmanı render
    /// önbelleğinin parçası.
    [Fact]
    public void AyniGirdi_AyniCikti()
    {
        Assert.Equal(
            Distribute("Bir iki üç", 3000),
            Distribute("Bir iki üç", 3000));
    }

    [Fact]
    public void FazlaBosluk_KelimeUretmez()
    {
        Assert.Equal(2, Distribute("  bir    iki  ", 2000).Count);
    }

    /// Hiçbir ipucu sıfır süreli olmamalı; ekranda görünmeyen bir
    /// altyazı üretmenin anlamı yok.
    [Fact]
    public void HicbirIpucu_SifirSureliDegil()
    {
        var timings = Distribute("a b c d e f g h i j k l m n o p", 2000);

        Assert.All(timings, t => Assert.True(t.End.Value > t.Start.Value));
    }
}
