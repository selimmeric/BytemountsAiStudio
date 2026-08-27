using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Core.Time;
using BytemountsAiStudio.Providers.Open;

namespace BytemountsAiStudio.Providers.Open.Tests;

/// Sıralı TTS yedeklemesinin testleri (P1-26).
///
/// Asıl sınanan şey HANGİ HATADA yedeğe düşüldüğü. Bu makinede
/// Windows'un yalnızca Türkçe sesi kurulu; İngilizce içerik ancak
/// Kaynak hatasında Piper'a geçilirse üretilebiliyor.
public sealed class FallbackTtsProviderTests
{
    private sealed class StubTts(string key, Result<ProviderResponse<TtsResponse>> result, bool timings = false)
        : ITtsProvider
    {
        public string Key => key;

        public bool SupportsWordTimings => timings;

        public int Calls { get; private set; }

        public Task<Result<ProviderResponse<TtsResponse>>> SynthesizeAsync(
            TtsRequest request, ProviderContext context, CancellationToken cancellationToken)
        {
            Calls++;

            return Task.FromResult(result);
        }

        public Task<Result<IReadOnlyList<VoiceInfo>>> ListVoicesAsync(
            LanguageTag language, CancellationToken cancellationToken)
            => Task.FromResult(Result.Success<IReadOnlyList<VoiceInfo>>([]));
    }

    private static Result<ProviderResponse<TtsResponse>> Ok(string voice)
        => Result.Success(ProviderResponse<TtsResponse>.Free(new TtsResponse
        {
            Audio = new byte[2048],
            MimeType = "audio/wav",
            ReportedDuration = new Ms(1000),
            VoiceUsed = voice,
        }));

    private static Result<ProviderResponse<TtsResponse>> Fail(Error error)
        => Result.Failure<ProviderResponse<TtsResponse>>(error);

    private static Task<Result<ProviderResponse<TtsResponse>>> RunAsync(FallbackTtsProvider provider)
        => provider.SynthesizeAsync(
            new TtsRequest
            {
                SpeechText = "metin",
                VoiceId = string.Empty,
                Language = LanguageTag.Create("en-US"),
            },
            ProviderContext.ForTest("fallback"),
            CancellationToken.None);

    [Fact]
    public async Task IlkSaglayiciCalisiyorsa_IkinciyeGidilmez()
    {
        var second = new StubTts("piper", Ok("piper"));
        var provider = new FallbackTtsProvider([new StubTts("windows", Ok("Tolga")), second]);

        var result = await RunAsync(provider);

        Assert.True(result.IsSuccess);
        Assert.Equal("Tolga", result.Value.Value.VoiceUsed);
        Assert.Equal(0, second.Calls);
    }

    /// KAYNAK hatası = dil paketi yok. Yedeğe geçmenin ASIL sebebi bu:
    /// Windows'ta İngilizce ses kurulu değil.
    [Fact]
    public async Task KaynakHatasinda_YedegeGecilir()
    {
        var provider = new FallbackTtsProvider(
        [
            new StubTts("windows", Fail(Error.Resource("windows_speech.no_voice", "ses yok", TimeSpan.FromHours(1)))),
            new StubTts("piper", Ok("en_US-amy-medium")),
        ]);

        var result = await RunAsync(provider);

        Assert.True(result.IsSuccess);
        Assert.Equal("en_US-amy-medium", result.Value.Value.VoiceUsed);
    }

    [Fact]
    public async Task GeciciHatada_YedegeGecilir()
    {
        var provider = new FallbackTtsProvider(
        [
            new StubTts("windows", Fail(Error.Transient("windows_speech.no_output", "üretilmedi"))),
            new StubTts("piper", Ok("piper")),
        ]);

        Assert.True((await RunAsync(provider)).IsSuccess);
    }

    /// KALICI hatada geçilmiyor: aynı geçersiz isteği ikinci bir
    /// sağlayıcıya göndermek yalnızca ikinci kez başarısız olmaktı.
    [Fact]
    public async Task KaliciHatada_YedegeGecilmez()
    {
        var second = new StubTts("piper", Ok("piper"));
        var provider = new FallbackTtsProvider(
        [
            new StubTts("windows", Fail(Error.Permanent("windows_speech.empty", "metin boş"))),
            second,
        ]);

        var result = await RunAsync(provider);

        Assert.True(result.IsFailure);
        Assert.Equal(0, second.Calls);
        Assert.Equal(ErrorKind.Permanent, result.Error.Kind);
    }

    /// Hepsi düşerse HATALARIN TAMAMI bildiriliyor: yalnızca sonuncuyu
    /// vermek en yaygın yanlış teşhis sebebi olurdu.
    [Fact]
    public async Task HepsiDuserse_TumHatalarBildirilir()
    {
        var provider = new FallbackTtsProvider(
        [
            new StubTts("windows", Fail(Error.Resource("windows_speech.no_voice", "dil paketi yok", TimeSpan.FromHours(1)))),
            new StubTts("piper", Fail(Error.Transient("tools.unreachable", "yan-servis kapalı"))),
        ]);

        var result = await RunAsync(provider);

        Assert.True(result.IsFailure);
        Assert.Contains("dil paketi yok", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("yan-servis kapalı", result.Error.Message, StringComparison.Ordinal);
    }

    /// Sınıf İLKİNİN sınıfı kalıyor: kuyruğun kararı birincile göre
    /// verilmeli.
    [Fact]
    public async Task HataSinifi_IlkininSinifi()
    {
        var provider = new FallbackTtsProvider(
        [
            new StubTts("windows", Fail(Error.Resource("a", "x", TimeSpan.FromHours(1)))),
            new StubTts("piper", Fail(Error.Transient("b", "y"))),
        ]);

        var result = await RunAsync(provider);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Resource, result.Error.Kind);
    }

    /// Herhangi biri kelime zamanı veriyorsa hat ASR'ye gitmeden önce
    /// deneme şansı bulmalı.
    [Fact]
    public void KelimeZamani_HerhangiBiriVeriyorsaBildirilir()
    {
        Assert.True(new FallbackTtsProvider(
            [new StubTts("a", Ok("a")), new StubTts("b", Ok("b"), timings: true)]).SupportsWordTimings);

        Assert.False(new FallbackTtsProvider([new StubTts("a", Ok("a"))]).SupportsWordTimings);
    }

    [Fact]
    public void BosSaglayiciListesi_Reddedilir()
    {
        Assert.Throws<ArgumentException>(() => new FallbackTtsProvider([]));
    }

    [Theory]
    [InlineData("tr_TR-dfki-medium", "tr-TR")]
    [InlineData("en_US-amy-medium", "en-US")]
    [InlineData("en_GB-alba-low", "en-GB")]
    public void PiperSesAdindan_DilOkunur(string voice, string expected)
    {
        Assert.Equal(expected, SidecarTtsProvider.ParseLanguage(voice)?.Value);
    }

    [Theory]
    [InlineData("gecersiz")]
    [InlineData("")]
    [InlineData("-bastan-tire")]
    public void BozukSesAdi_NullDoner(string voice)
    {
        Assert.Null(SidecarTtsProvider.ParseLanguage(voice));
    }
}
