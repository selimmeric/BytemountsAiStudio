using BytemountsAiStudio.Core.Time;

namespace BytemountsAiStudio.Core.Tests;

public sealed class MsTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1000, 30)]
    [InlineData(1016, 30)]   // 30.48 -> 30
    [InlineData(1017, 31)]   // 30.51 -> 31
    public void ToFrame_YuvarlamaKareyeEnYakinOlanaGider(int ms, int expectedFrame)
        => Assert.Equal(expectedFrame, new Ms(ms).ToFrame(30));

    [Fact]
    public void FromSeconds_YuvarlarKesmez()
    {
        Assert.Equal(4800, Ms.FromSeconds(4.8).Value);
        Assert.Equal(4801, Ms.FromSeconds(4.8006).Value);
    }

    [Fact]
    public void Karsilastirma_SayisalSiralamayiKorur()
    {
        Assert.True(new Ms(100) < new Ms(200));
        Assert.True(new Ms(200) >= new Ms(200));
        Assert.Equal(new Ms(300), new Ms(100) + new Ms(200));
    }
}

public sealed class TimeRangeTests
{
    [Fact]
    public void ArdisikAraliklar_Cakismaz()
    {
        // Yari acik aralik kurali: [0,4000) ve [4000,9000) uc uca eklenir.
        // Kapali olsaydi 4000. milisaniye iki sahnede birden cizilirdi.
        var first = new TimeRange(new Ms(0), new Ms(4000));
        var second = new TimeRange(new Ms(4000), new Ms(9000));

        Assert.False(first.Overlaps(second));
        Assert.False(second.Overlaps(first));
    }

    [Fact]
    public void CakisanAraliklar_Yakalanir()
    {
        var first = new TimeRange(new Ms(0), new Ms(4001));
        var second = new TimeRange(new Ms(4000), new Ms(9000));

        Assert.True(first.Overlaps(second));
    }

    [Fact]
    public void Contains_BitisiDisarida_BirakirBaslangiciIcerir()
    {
        var range = new TimeRange(new Ms(1000), new Ms(2000));

        Assert.True(range.Contains(new Ms(1000)));
        Assert.True(range.Contains(new Ms(1999)));
        Assert.False(range.Contains(new Ms(2000)));
    }

    [Fact]
    public void TersAralik_Reddedilir()
        => Assert.Throws<ArgumentException>(() => new TimeRange(new Ms(500), new Ms(100)));

    [Fact]
    public void FromDuration_SureyiKorur()
    {
        var range = TimeRange.FromDuration(new Ms(4820), new Ms(7210));

        Assert.Equal(4820, range.Start.Value);
        Assert.Equal(12030, range.End.Value);
        Assert.Equal(7210, range.Duration.Value);
    }
}
