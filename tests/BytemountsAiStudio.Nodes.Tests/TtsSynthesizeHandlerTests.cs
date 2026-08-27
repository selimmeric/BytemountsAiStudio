using System.Text.Json;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Core.Time;
using BytemountsAiStudio.Nodes;
using BytemountsAiStudio.Providers.Fake;
using BytemountsAiStudio.Workflow.Engine;

namespace BytemountsAiStudio.Nodes.Tests;

/// Kelime zamanı KAYNAĞININ testleri (P1-15).
///
/// Asıl sınanan şey öncelik sırası ve düşüş davranışı:
///   1. Sağlayıcının kendi zamanlaması — en doğru, bedava
///   2. ASR hizalaması              — doğru, pahalı
///   3. Karakter bazlı dağıtım      — tahmin, bedava (P1-15a)
///
/// Üçüncüsüne düşmek bir kusur değil ama GÖRÜNMEK zorunda: bu depoda
/// gerçek videolar altyazısız çıktı çünkü eksik zamanlama hiçbir yerde
/// raporlanmıyordu.
public sealed class TtsSynthesizeHandlerTests
{
    /// Kelime zamanı VERMEYEN sağlayıcı — Windows TTS'in gerçek hâli.
    private sealed class SilentTtsProvider : ITtsProvider
    {
        public string Key => "silent-tts";

        public bool SupportsWordTimings => false;

        public Task<Result<ProviderResponse<TtsResponse>>> SynthesizeAsync(
            TtsRequest request, ProviderContext context, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            var inner = new FakeTtsProvider();
            var result = inner.SynthesizeAsync(request, context, cancellationToken).GetAwaiter().GetResult();

            return Task.FromResult(Result.Success(new ProviderResponse<TtsResponse>(
                result.Value.Value with { WordTimings = [] },
                result.Value.Usage)));
        }

        public Task<Result<IReadOnlyList<VoiceInfo>>> ListVoicesAsync(
            LanguageTag language, CancellationToken cancellationToken)
            => new FakeTtsProvider().ListVoicesAsync(language, cancellationToken);
    }

    /// Hep belirli bir hatayla düşen ASR.
    private sealed class FailingAsrProvider(Error error) : IAsrProvider
    {
        public string Key => "failing-asr";

        public int Calls { get; private set; }

        public Task<Result<ProviderResponse<AlignmentResult>>> AlignAsync(
            AlignRequest request, ProviderContext context, CancellationToken cancellationToken)
        {
            Calls++;

            return Task.FromResult(Result.Failure<ProviderResponse<AlignmentResult>>(error));
        }
    }

    /// Hizalama döndüren ama HİÇ KELİME vermeyen ASR.
    private sealed class EmptyAsrProvider : IAsrProvider
    {
        public string Key => "empty-asr";

        public Task<Result<ProviderResponse<AlignmentResult>>> AlignAsync(
            AlignRequest request, ProviderContext context, CancellationToken cancellationToken)
            => Task.FromResult(Result.Success(ProviderResponse<AlignmentResult>.Free(
                new AlignmentResult([], Ms.Zero))));
    }

    private static NodeContext Context(params string[] sentences)
    {
        var run = JsonSerializer.SerializeToElement(new
        {
            topic = new { topic = "Göbeklitepe", language = "tr-TR" },
            script = new { sentences },
        });

        return new NodeContext
        {
            RunId = Guid.CreateVersion7(),
            NodeId = "tts",
            NodeType = "tts.synthesize",
            Attempt = 1,
            Config = JsonSerializer.SerializeToElement(new { voice_id = "fake-tr-f1" }),
            RunContext = run,
            IdempotencyKey = "tts-test",
            CorrelationId = "tts-test",
        };
    }

    private static async Task<JsonElement> RunAsync(
        ITtsProvider tts, IAsrProvider? asr, params string[] sentences)
    {
        using var storage = new FakeStorageProvider();

        var handler = new TtsSynthesizeHandler(tts, storage, "ffprobe", asr);
        var result = await handler.ExecuteAsync(Context(sentences), CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);

        return result.Value;
    }

    private static List<string> Sources(JsonElement output)
        => [.. output.GetProperty("cues").EnumerateArray().Select(c => c.GetProperty("source").GetString()!)];

    /// Sağlayıcının kendi zamanlaması varsa ASR HİÇ çağrılmıyor:
    /// saniyeler süren bir işi, cevabı zaten elimizdeyken yapmak
    /// yalnızca gecikme olurdu.
    [Fact]
    public async Task SaglayiciZamanlamaVeriyorsa_AsrCagrilmaz()
    {
        var asr = new FakeAsrProvider();

        var output = await RunAsync(new FakeTtsProvider(), asr, "Bir cümle.");

        Assert.Equal(0, asr.AlignCount);
        Assert.All(Sources(output), source => Assert.Equal("provider", source));
        Assert.False(output.GetProperty("timings_estimated").GetBoolean());
    }

    /// Sağlayıcı zamanlama vermiyorsa ASR devreye giriyor — dağıtıma
    /// düşmeden önce.
    [Fact]
    public async Task SaglayiciZamanlamaVermiyorsa_AsrKullanilir()
    {
        var asr = new FakeAsrProvider();

        var output = await RunAsync(new SilentTtsProvider(), asr, "Bir cümle.");

        Assert.Equal(1, asr.AlignCount);
        Assert.All(Sources(output), source => Assert.Equal("asr", source));
        Assert.False(output.GetProperty("timings_estimated").GetBoolean());
    }

    /// ASR yoksa dağıtıma düşülüyor ve bu GÖRÜNÜR oluyor.
    [Fact]
    public async Task AsrYoksa_DagitimaDusulurVeIsaretlenir()
    {
        var output = await RunAsync(new SilentTtsProvider(), asr: null, "Bir cümle.");

        Assert.All(Sources(output), source => Assert.Equal("estimated", source));
        Assert.True(output.GetProperty("timings_estimated").GetBoolean());
    }

    /// KAYNAK hatası = yetenek yok (yan-servis kapalı ya da hizalama
    /// kurulu değil). Kalan cümleler için bir daha denenmiyor: kapalı
    /// bir servise cümle sayısı kadar bağlantı denemek, hepsi aynı
    /// cevabı verirken yalnızca gecikme olurdu.
    [Fact]
    public async Task KaynakHatasi_SonrakiCumlelerdeTekrarDenenmez()
    {
        var asr = new FailingAsrProvider(
            Error.Resource("tools.capability_missing", "hizalama kapalı", TimeSpan.FromMinutes(15)));

        var output = await RunAsync(new SilentTtsProvider(), asr, "Bir.", "İki.", "Üç.");

        Assert.Equal(1, asr.Calls);
        Assert.True(output.GetProperty("timings_estimated").GetBoolean());
    }

    /// GEÇİCİ hata başka: ağ bir cümlede kopup diğerinde düzelebilir,
    /// o yüzden denemeye devam ediliyor.
    [Fact]
    public async Task GeciciHata_HerCumledeYenidenDenenir()
    {
        var asr = new FailingAsrProvider(Error.Transient("tools.timeout", "zaman aşımı"));

        await RunAsync(new SilentTtsProvider(), asr, "Bir.", "İki.", "Üç.");

        Assert.Equal(3, asr.Calls);
    }

    /// Sıfır kelime bir hizalama DEĞİL: başarı sayılsaydı altyazı hiç
    /// üretilmez ve hiçbir şey kırılmazdı.
    [Fact]
    public async Task BosHizalama_DagitimaDusulur()
    {
        var output = await RunAsync(new SilentTtsProvider(), new EmptyAsrProvider(), "Bir cümle.");

        Assert.All(Sources(output), source => Assert.Equal("estimated", source));
        Assert.NotEmpty(output.GetProperty("cues").EnumerateArray());
    }

    /// NORMALİZASYON METNİ DEĞİŞTİRDİYSE ASR DE İŞE YARAMIYOR.
    ///
    /// Ses "bin dört yüz elli üç" diyor, altyazıda "1453" yazması
    /// gerekiyor: ASR beş kelime ölçüyor, ekranda bir kelime var.
    /// Zorla eşleştirmek altyazıyı kaydırırdı.
    [Fact]
    public async Task NormalizasyonMetniDegistirdiyse_AsrCagrilmaz()
    {
        var asr = new FakeAsrProvider();

        var output = await RunAsync(new SilentTtsProvider(), asr, "1453 yılında fetih gerçekleşti.");

        Assert.Equal(0, asr.AlignCount);
        Assert.All(Sources(output), source => Assert.Equal("estimated", source));
    }

    /// Altyazı EKRANDAKİ metni gösteriyor, okunanı değil.
    [Fact]
    public async Task Altyazi_EkrandakiMetniGosterir()
    {
        var output = await RunAsync(new SilentTtsProvider(), new FakeAsrProvider(), "1453 yılında fetih.");

        var words = output.GetProperty("cues").EnumerateArray()
            .Select(c => c.GetProperty("text").GetString())
            .ToList();

        Assert.Contains("1453", words);
        Assert.DoesNotContain("bin", words);
    }

    /// Bir koşuda bazı cümleler ölçülmüş bazıları tahmin edilmiş
    /// olabiliyor; tek bir bayrak bunu gizlerdi.
    [Fact]
    public async Task KarisikKoşu_KaynakCumleBasinaKaydedilir()
    {
        var output = await RunAsync(
            new SilentTtsProvider(), new FakeAsrProvider(), "Normal bir cümle.", "1453 yılı.");

        var sources = Sources(output).Distinct().ToList();

        Assert.Contains("asr", sources);
        Assert.Contains("estimated", sources);
        Assert.True(output.GetProperty("timings_estimated").GetBoolean());
    }
}
