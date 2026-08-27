using BytemountsAiStudio.Api;
using BytemountsAiStudio.Core.Execution;

namespace BytemountsAiStudio.Api.Tests;

/// Canlı akışın kapanma kararı (P1-28).
///
/// Küçük bir fonksiyon ama yanlış olması pahalı: bir durum yanlışlıkla
/// "bitmiş" sayılırsa pano o run'ı canlı izlemeyi bırakır ve
/// kullanıcı, ilerlemeyi görmek için sayfayı yenilemek zorunda kalır —
/// yani P1-28'in tek kabul kriteri düşer.
public sealed class ProgressStreamTests
{
    [Theory]
    [InlineData(RunState.Completed)]
    [InlineData(RunState.Failed)]
    [InlineData(RunState.Cancelled)]
    public void BitmisDurumlar_AkisiKapatir(RunState state)
    {
        Assert.True(ProgressStream.IsTerminal(state));
    }

    /// BEKLEYEN DURUMLAR BİTMİŞ SAYILMIYOR.
    ///
    /// `WaitingApproval` en kritik olanı: run devam edecek, yalnızca
    /// bir insan kararı bekliyor. Akış kapatılsaydı, onay verildiği
    /// anda pano bunu göremezdi — oysa panonun asıl işi tam olarak o.
    [Theory]
    [InlineData(RunState.Pending)]
    [InlineData(RunState.Running)]
    [InlineData(RunState.WaitingApproval)]
    [InlineData(RunState.WaitingResource)]
    public void BekleyenDurumlar_AkisiKapatmaz(RunState state)
    {
        Assert.False(ProgressStream.IsTerminal(state));
    }

    /// Yeni bir durum eklendiğinde bu test kırılıyor ve ekleyen kişi
    /// "bu durum akışı kapatmalı mı" sorusunu cevaplamak zorunda
    /// kalıyor. Sessizce "kapatmaz" tarafına düşmek, bitmiş bir run'ın
    /// bağlantısını sonsuza kadar açık bırakırdı.
    [Fact]
    public void TumDurumlar_KararaBaglanmis()
    {
        var known = new[]
        {
            RunState.Pending, RunState.Running, RunState.WaitingApproval,
            RunState.WaitingResource, RunState.Completed, RunState.Failed, RunState.Cancelled,
        };

        Assert.Equal(known.Length, Enum.GetValues<RunState>().Length);
    }
}

/// Hata belgesi okumanın testleri.
///
/// Bir run zaten hatalıysa, hatanın KAYDININ da bozuk olma ihtimali
/// düşük değil. Panelin o yüzden çökmesi, arızayı incelemeyi tam da
/// gerektiği anda imkânsız kılardı.
public sealed class ErrorFieldTests
{
    [Fact]
    public void GecerliHata_OkunUyor()
    {
        const string json = """{"code":"tts.no_voice","message":"ses yok","kind":"Resource"}""";

        Assert.Equal("tts.no_voice", RunQueries.ErrorCodeOf(json));
        Assert.Equal("ses yok", RunQueries.ErrorMessageOf(json));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ bozuk json")]
    [InlineData("[]")]
    [InlineData("\"sadece metin\"")]
    [InlineData("""{"baska":"alan"}""")]
    public void BozukVeyaEksikHata_CokmuYor(string? json)
    {
        Assert.Null(RunQueries.ErrorCodeOf(json));
        Assert.Null(RunQueries.ErrorMessageOf(json));
    }
}
