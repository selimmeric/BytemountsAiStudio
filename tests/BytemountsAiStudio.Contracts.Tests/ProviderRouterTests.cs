using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;

namespace BytemountsAiStudio.Contracts.Tests;

/// Yönlendirme ve yedeğe düşme testleri (P1-03).
///
/// Asıl mesele HANGİ HATADA sıradakine geçileceği. Fazla geçersek aynı
/// geçersiz isteği ikinci kez ücretlendiririz; az geçersek çalışan bir
/// yedek hiç denenmez.
public sealed class ProviderRouterTests
{
    private sealed record Stub(string Key, Result<string> Result)
    {
        public int Calls { get; set; }
    }

    private static ProviderRouter<Stub> Router(params Stub[] stubs)
        => new(stubs, s => s.Key);

    private static Task<Result<string>> Call(Stub stub, CancellationToken _)
    {
        stub.Calls++;
        return Task.FromResult(stub.Result);
    }

    private static Stub Ok(string key, string value = "sonuc")
        => new(key, Result.Success(value));

    private static Stub Fail(string key, Error error)
        => new(key, Result.Failure<string>(error));

    [Fact]
    public async Task IlkSaglayiciCalisirsa_DigerineHicGidilmez()
    {
        var second = Ok("yedek");
        var router = Router(Ok("birincil"), second);

        var result = await router.InvokeAsync(Call, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("birincil", result.Value.ProviderKey);
        Assert.False(result.Value.UsedFallback);
        Assert.Equal(0, second.Calls);
    }

    /// Geçici hata: sağlayıcı geçici olarak bozuk, başkası çalışabilir.
    [Fact]
    public async Task GeciciHata_YedegeDusulur()
    {
        var router = Router(
            Fail("birincil", Error.Transient("x.down", "cokmus")),
            Ok("yedek"));

        var result = await router.InvokeAsync(Call, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("yedek", result.Value.ProviderKey);
        Assert.True(result.Value.UsedFallback);
        Assert.Equal(["birincil"], result.Value.FellOverFrom);
    }

    /// Kota doldu: beklemek yerine ücretsiz/yerel yedeğe düşmek doğru
    /// cevap — ADR-015'in işlevsel karşılığı.
    [Fact]
    public async Task KaynakHatasi_YedegeDusulur()
    {
        var router = Router(
            Fail("ucretli", Error.Resource("x.quota", "kota doldu", TimeSpan.FromHours(6))),
            Ok("yerel"));

        var result = await router.InvokeAsync(Call, CancellationToken.None);

        Assert.Equal("yerel", result.Value.ProviderKey);
    }

    /// KALICI hata: istek geçersiz. Aynı geçersiz isteği bir sağlayıcıya
    /// daha göndermek yalnızca ikinci kez para harcamak olurdu.
    [Fact]
    public async Task KaliciHata_YedegeDUSULMEZ()
    {
        var second = Ok("yedek");
        var router = Router(Fail("birincil", Error.Permanent("x.bad_request", "gecersiz istek")), second);

        var result = await router.InvokeAsync(Call, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("x.bad_request", result.Error.Code);
        Assert.Equal(0, second.Calls);
    }

    /// Tek istisna: kimlik hatası. "Bu anahtar geçersiz" isteğin değil,
    /// yapılandırmanın kusuru — başka bir sağlayıcı gayet çalışabilir.
    [Theory]
    [InlineData("credential.missing")]
    [InlineData("openai.unauthorized")]
    [InlineData("x.forbidden")]
    [InlineData("gemini.auth")]
    public async Task KimlikHatasi_KaliciOlsaBile_YedegeDusulur(string code)
    {
        var router = Router(Fail("birincil", Error.Permanent(code, "anahtar yok")), Ok("yedek"));

        var result = await router.InvokeAsync(Call, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("yedek", result.Value.ProviderKey);
    }

    [Fact]
    public async Task ZehirliGirdi_YedegeDusulmez()
    {
        var second = Ok("yedek");
        var router = Router(new Stub("birincil",
            Result.Failure<string>(new Error("x.poison", "bozuk girdi", ErrorKind.Poison))), second);

        Assert.True((await router.InvokeAsync(Call, CancellationToken.None)).IsFailure);
        Assert.Equal(0, second.Calls);
    }

    [Fact]
    public async Task IptalEdilmis_YedegeDusulmez()
    {
        var second = Ok("yedek");
        var router = Router(Fail("birincil", Error.Cancelled()), second);

        Assert.True((await router.InvokeAsync(Call, CancellationToken.None)).IsFailure);
        Assert.Equal(0, second.Calls);
    }

    /// Yalnızca son hatayı vermek en yaygın yanlış teşhis sebebi olurdu:
    /// asıl sorun genellikle İLK sağlayıcıdadır.
    [Fact]
    public async Task HepsiDuserse_TumHatalarBildirilir()
    {
        var router = Router(
            Fail("bir", Error.Transient("bir.down", "birinci coktu")),
            Fail("iki", Error.Transient("iki.down", "ikinci coktu")),
            Fail("uc", Error.Transient("uc.down", "ucuncu coktu")));

        var result = await router.InvokeAsync(Call, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("routing.all_failed", result.Error.Code);
        Assert.Contains("birinci coktu", result.Error.Detail!, StringComparison.Ordinal);
        Assert.Contains("ikinci coktu", result.Error.Detail!, StringComparison.Ordinal);
        Assert.Contains("ucuncu coktu", result.Error.Detail!, StringComparison.Ordinal);
    }

    /// İş kuyruğunun kararı (yeniden dene / ertele / düşür) BİRİNCİL
    /// sağlayıcının durumuna göre verilmeli, en son yedeğinkine göre değil.
    [Fact]
    public async Task HepsiDuserse_HataSinifiIlkinkiniKorur()
    {
        var router = Router(
            Fail("bir", Error.Resource("bir.quota", "kota", TimeSpan.FromMinutes(30))),
            Fail("iki", Error.Transient("iki.down", "coktu")));

        var result = await router.InvokeAsync(Call, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Resource, result.Error.Kind);
        Assert.Equal(TimeSpan.FromMinutes(30), result.Error.RetryAfter);
    }

    [Fact]
    public async Task TekSaglayiciDuserse_HatasiOlduguGibiDoner()
    {
        var router = Router(Fail("tek", Error.Transient("tek.down", "coktu")));

        var result = await router.InvokeAsync(Call, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("tek.down", result.Error.Code);
    }

    [Fact]
    public void BosListe_Reddedilir()
    {
        Assert.Throws<ArgumentException>(() => new ProviderRouter<Stub>([], s => s.Key));
    }

    [Fact]
    public async Task ZincirdekiTumSaglayicilar_SirayilaDenenir()
    {
        var bir = Fail("bir", Error.Transient("a", "a"));
        var iki = Fail("iki", Error.Transient("b", "b"));
        var uc = Ok("uc");
        var router = Router(bir, iki, uc);

        var result = await router.InvokeAsync(Call, CancellationToken.None);

        Assert.Equal("uc", result.Value.ProviderKey);
        Assert.Equal(1, bir.Calls);
        Assert.Equal(1, iki.Calls);
        Assert.Equal(1, uc.Calls);
    }
}
