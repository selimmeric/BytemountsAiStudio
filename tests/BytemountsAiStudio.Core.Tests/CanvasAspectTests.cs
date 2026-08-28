using BytemountsAiStudio.Core.Content;

namespace BytemountsAiStudio.Core.Tests;

/// Tuval oranının ayardan okunması (P3-03).
///
/// Bu ayar graftaki timeline node'unda duruyordu ve HİÇBİR ŞEY
/// okumuyordu: uzun video grafı `16:9` diyordu ama her video 1080×1920
/// çıkıyordu. Bu depoda tekrar eden sınıf — kaydediliyor, okunmuyor.
public sealed class CanvasAspectTests
{
    [Theory]
    [InlineData("9:16", 1080, 1920)]
    [InlineData("dikey", 1080, 1920)]
    [InlineData("portrait", 1080, 1920)]
    [InlineData("shorts", 1080, 1920)]
    [InlineData("16:9", 1920, 1080)]
    [InlineData("yatay", 1920, 1080)]
    [InlineData("landscape", 1920, 1080)]
    [InlineData("video", 1920, 1080)]
    public void TaninanOranlar_DogruTuval(string aspect, int width, int height)
    {
        var canvas = Canvas.ForAspect(aspect);

        Assert.Equal(width, canvas.Width);
        Assert.Equal(height, canvas.Height);
    }

    /// Boşluklu yazım da kabul: ayar dosyasına elle yazan biri boşluk
    /// bırakabiliyor ve o boşluk yüzünden videonun oranının değişmesi
    /// teşhis edilmesi çok zor bir hata olurdu.
    [Theory]
    [InlineData(" 16:9 ")]
    [InlineData("16:9")]
    public void BosluklarKirpiliyor(string aspect)
        => Assert.Equal(1920, Canvas.ForAspect(aspect).Width);

    /// TANINMAYAN DEĞER DİKEY'E DÜŞÜYOR ama bu bilgi KAYBOLMUYOR.
    ///
    /// Sessizce dikeye düşen bir yatay video ancak render bittikten
    /// sonra fark edilirdi ve o noktada on beş dakikalık bir render
    /// harcanmış olurdu. `TryParseAspect` çağırana "tanıdım mı"
    /// sorusunu ayrıca cevaplıyor.
    [Theory]
    [InlineData("4:3")]
    [InlineData("kare")]
    [InlineData("")]
    [InlineData(null)]
    public void TaninmayanOran_DikeyeDusuyorAmaBildiriliyor(string? aspect)
    {
        Assert.Equal(1080, Canvas.ForAspect(aspect).Width);
        Assert.Null(Canvas.TryParseAspect(aspect));
    }

    /// Dikey ve yatay gerçekten farklı: testin asıl söylediği,
    /// aynı kodun iki farklı içerik türünü taşıyabildiği.
    [Fact]
    public void DikeyVeYatay_Farkli()
    {
        var portrait = Canvas.ForAspect("9:16");
        var landscape = Canvas.ForAspect("16:9");

        Assert.True(portrait.IsPortrait);
        Assert.False(landscape.IsPortrait);
        Assert.True(landscape.AspectRatio > 1);
        Assert.True(portrait.AspectRatio < 1);
    }
}
