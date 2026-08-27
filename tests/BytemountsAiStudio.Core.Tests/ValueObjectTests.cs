using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Assets;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Errors;

namespace BytemountsAiStudio.Core.Tests;

public sealed class LanguageTagTests
{
    [Theory]
    [InlineData("tr-TR", "tr")]
    [InlineData("en-US", "en")]
    [InlineData("de-DE", "de")]
    public void GecerliEtiket_AnaDiliCikarir(string tag, string primary)
    {
        var result = LanguageTag.TryCreate(tag);

        Assert.True(result.IsSuccess);
        Assert.Equal(primary, result.Value.Primary);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void BosEtiket_Reddedilir(string? tag)
    {
        var result = LanguageTag.TryCreate(tag);

        Assert.True(result.IsFailure);
        Assert.Equal("language.empty", result.Error.Code);
    }

    [Fact]
    public void TaninmayanEtiket_Reddedilir()
    {
        var result = LanguageTag.TryCreate("zz-ZZ-nonsense");

        Assert.True(result.IsFailure);
        Assert.Equal("language.unknown", result.Error.Code);
    }

    [Fact]
    public void TurkceKultur_IHarfiniDogruBuyutur()
    {
        // InvariantGlobalization acik olsaydi ya da Invariant kultur
        // kullansaydik "i".ToUpper() = "I" olurdu; Turkce'de "İ" olmali.
        // Bu test o ayarin kapali kaldiginin bekcisi.
        var tr = LanguageTag.Create("tr-TR");

        Assert.Equal("İSTANBUL", "istanbul".ToUpper(tr.Culture));
        Assert.Equal("ISTANBUL", "istanbul".ToUpper(LanguageTag.Create("en-US").Culture));
    }

    [Fact]
    public void ArapcaEtiket_SagdanSolaIsaretlenir()
        => Assert.True(LanguageTag.Create("ar-SA").IsRightToLeft);
}

public sealed class AssetRefTests
{
    private const string ValidHash = "9f8e7d6c5b4a39281706f5e4d3c2b1a09f8e7d6c5b4a39281706f5e4d3c2b1a0";

    [Fact]
    public void OnekliVeOneksiz_AyniReferansiUretir()
    {
        var withPrefix = AssetRef.Create("sha256:" + ValidHash);
        var without = AssetRef.Create(ValidHash);

        Assert.Equal(without, withPrefix);
    }

    [Fact]
    public void BuyukHarf_KucukHarfeNormallestirilir()
        => Assert.Equal(ValidHash, AssetRef.Create(ValidHash.ToUpperInvariant()).Sha256);

    [Theory]
    [InlineData("abc")]
    [InlineData("9f8e7d6c5b4a39281706f5e4d3c2b1a09f8e7d6c5b4a39281706f5e4d3c2b1")]   // 63
    public void YanlisUzunluk_Reddedilir(string hash)
    {
        var result = AssetRef.TryCreate(hash);

        Assert.True(result.IsFailure);
        Assert.Equal("asset.ref.length", result.Error.Code);
    }

    [Fact]
    public void OnaltilikOlmayanKarakter_Reddedilir()
    {
        var result = AssetRef.TryCreate(ValidHash[..63] + "z");

        Assert.True(result.IsFailure);
        Assert.Equal("asset.ref.format", result.Error.Code);
    }

    [Fact]
    public void RelativePath_IkiSeviyeShardUretir()
        => Assert.Equal($"9f/8e/{ValidHash}.png", AssetRef.Create(ValidHash).RelativePath(".png"));
}

public sealed class CanvasTests
{
    [Fact]
    public void TekSayiliBoyut_Reddedilir()
    {
        // yuv420p kroma alt ornekleme cift boyut ister. Bunu render sirasinda
        // FFmpeg hatasi olarak gormek yerine burada durduruyoruz.
        Assert.Throws<ArgumentException>(() => new Canvas(1081, 1920, 30));
        Assert.Throws<ArgumentException>(() => new Canvas(1080, 1921, 30));
    }

    [Fact]
    public void ShortsTuvali_DikeyDir()
    {
        Assert.True(Canvas.Shorts1080.IsPortrait);
        Assert.False(Canvas.Landscape1080.IsPortrait);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(121)]
    public void MakulOlmayanKareHizi_Reddedilir(int fps)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new Canvas(1080, 1920, fps));
}

public sealed class ResultTests
{
    [Fact]
    public void BasarisizSonuctanDegerOkumak_Patlar()
    {
        Result<int> result = Error.Permanent("test", "olmadi");

        Assert.True(result.IsFailure);
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void Bind_IlkHatadaZinciriKirar()
    {
        var calls = 0;

        var result = Result.Success(5)
            .Bind<int>(_ => Error.Permanent("bozuk", "dur"))
            .Bind(x => { calls++; return Result.Success(x * 2); });

        Assert.True(result.IsFailure);
        Assert.Equal("bozuk", result.Error.Code);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void KaynakHatasi_RetryEdilebilirSayilmaz()
    {
        // Kaynak hatasi tekrar denenmez, ERTELENIR. Ikisini karistirmak
        // kota dolu bir hesaba ustuste istek attirir.
        var resource = Error.Resource("quota.youtube", "Gunluk kota doldu.", TimeSpan.FromHours(6));

        Assert.False(resource.IsRetryable);
        Assert.Equal(ErrorKind.Resource, resource.Kind);
        Assert.Equal(TimeSpan.FromHours(6), resource.RetryAfter);
    }

    [Fact]
    public void GeciciHata_RetryEdilebilir()
        => Assert.True(Error.Transient("http.429", "Rate limit").IsRetryable);
}
