using System.Text.Json;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Core.Time;
using BytemountsAiStudio.Media.Timeline;
using BytemountsAiStudio.Nodes;
using BytemountsAiStudio.Providers.Fake;
using BytemountsAiStudio.Workflow.Engine;

namespace BytemountsAiStudio.Nodes.Tests;

/// Arka plan müziğinin seçilip indirilmesi (P2-09).
///
/// Buradaki asıl konu lisans: görselde eksik atıf düzeltilebilir bir
/// kusur, müzikte düzeltilemez bir hasar — Content ID müziği otomatik
/// tanıyor ve bir talep kanalın o videodan gelen gelirinin tamamını
/// götürüyor.
public sealed class MusicSelectHandlerTests
{
    private static NodeContext Context(string runContext = "{}", string config = "{}")
        => new()
        {
            RunId = Guid.CreateVersion7(),
            NodeId = "muzik",
            NodeType = "music.select",
            Attempt = 1,
            Config = JsonDocument.Parse(config).RootElement.Clone(),
            RunContext = JsonDocument.Parse(runContext).RootElement.Clone(),
            IdempotencyKey = "test",
            CorrelationId = "test",
        };

    private static Func<Uri, CancellationToken, Task<Result<DownloadedAudio>>> Ok(int bytes = 1024)
        => (_, _) => Task.FromResult(Result.Success(new DownloadedAudio(new byte[bytes], "audio/wav")));

    private static Func<Uri, CancellationToken, Task<Result<DownloadedAudio>>> Fails(string code)
        => (_, _) => Task.FromResult(Result.Failure<DownloadedAudio>(Error.Transient(code, "olmadi")));

    private sealed class StubMusic(MusicTrack? track, Error? error = null) : IMusicProvider
    {
        public string Key => "stub-music";

        public Task<Result<ProviderResponse<MusicTrack>>> SelectAsync(
            MusicQuery query, ProviderContext context, CancellationToken cancellationToken)
            => Task.FromResult(track is null
                ? Result.Failure<ProviderResponse<MusicTrack>>(error ?? Error.Transient("stub", "yok"))
                : Result.Success(new ProviderResponse<MusicTrack>(track, UsageUnits.OfRequests())));
    }

    private static MusicTrack Track(
        string license = "cc0", string? author = "besteci", bool requiresAttribution = false)
        => new()
        {
            Url = new Uri("https://ornek.invalid/parca.mp3"),
            Duration = new Ms(120_000),
            Title = "Bir parca",
            License = new LicenseInfo
            {
                Name = license,
                Author = author,
                RequiresAttribution = requiresAttribution,
                CapturedAt = DateTimeOffset.UnixEpoch,
            },
        };

    private static async Task<JsonElement> RunAsync(
        MusicTrack? track,
        Func<Uri, CancellationToken, Task<Result<DownloadedAudio>>>? download = null,
        string runContext = "{}")
    {
        var handler = new MusicSelectHandler(
            new StubMusic(track), new FakeStorageProvider(), download ?? Ok());

        var result = await handler.ExecuteAsync(Context(runContext), CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);

        return result.Value;
    }

    [Fact]
    public async Task ParcaBulundu_VarlikVeLisansYaziliyor()
    {
        var output = await RunAsync(Track());

        Assert.NotNull(output.GetProperty("asset").GetString());
        Assert.Equal("cc0", output.GetProperty("license").GetProperty("name").GetString());
        Assert.Equal(120_000, output.GetProperty("duration_ms").GetInt32());
    }

    /// LİSANS KANITI OLMAYAN PARÇA HİÇ İNDİRİLMİYOR.
    ///
    /// İndirip QC'nin yakalamasını beklemek, çalışan bir kontrol
    /// varken riski üretim hattının içine sokmak olurdu.
    [Fact]
    public async Task LisansAdiYok_Atlaniyor()
    {
        var indirildi = false;

        var output = await RunAsync(
            Track(license: "  "),
            (_, _) =>
            {
                indirildi = true;
                return Task.FromResult(Result.Success(new DownloadedAudio([], "audio/wav")));
            });

        Assert.True(output.GetProperty("skipped").GetBoolean());
        Assert.False(indirildi);
    }

    /// ATIF GEREKİYORSA YAZAR ADI ŞART: "CC BY" deyip yazarı bilmemek,
    /// atfı yapılamaz kılıyor ve lisansı ihlal ediyor.
    [Fact]
    public async Task AtifGerekliAmaYazarYok_Atlaniyor()
    {
        var output = await RunAsync(Track(license: "by", author: null, requiresAttribution: true));

        Assert.True(output.GetProperty("skipped").GetBoolean());
        Assert.Contains("lisans", output.GetProperty("reason").GetString()!, StringComparison.Ordinal);
    }

    /// Atıf gerekmiyorsa yazar da gerekmiyor: CC0 tam da bu.
    [Fact]
    public async Task AtifGerekmiyor_YazarsizGeciyor()
    {
        var output = await RunAsync(Track(license: "cc0", author: null, requiresAttribution: false));

        Assert.False(output.TryGetProperty("skipped", out _));
    }

    /// MÜZİK BULUNAMAZSA KOŞU DÜŞMÜYOR: müziksiz video tamamen
    /// geçerli, müzik yüzünden koşuyu düşürmek videoyu tamamen
    /// kaybetmek olurdu. Ama sebep yazılıyor.
    [Fact]
    public async Task ParcaBulunamadi_SebepleAtlaniyor()
    {
        var output = await RunAsync(track: null);

        Assert.True(output.GetProperty("skipped").GetBoolean());
        Assert.NotNull(output.GetProperty("reason").GetString());
        Assert.Null(output.GetProperty("asset").GetString());
    }

    /// İNDİRME HATASI DA KOŞUYU DÜŞÜRMÜYOR.
    [Fact]
    public async Task IndirmeDustu_SebepleAtlaniyor()
    {
        var output = await RunAsync(Track(), Fails("music.download_failed"));

        Assert.True(output.GetProperty("skipped").GetBoolean());
    }

    /// SÜRE BAĞLAMDAN OKUNUYOR: kısa bir parça döngüye alınabiliyor
    /// ama her döngü duyulur bir dikiş bırakıyor.
    [Theory]
    [InlineData("""{"tts":{"total_ms":42000}}""", 42_000)]
    [InlineData("""{"tts":{"total_ms":0}}""", 60_000)]
    [InlineData("""{"tts":{}}""", 60_000)]
    [InlineData("{}", 60_000)]
    public void EnAzSure_BaglamdanOkunuyor(string runContext, int expected)
    {
        using var document = JsonDocument.Parse(runContext);

        Assert.Equal(expected, MusicSelectHandler.MinimumDurationFrom(document.RootElement).Value);
    }

    /// SINIRI AŞAN AKIŞ DURDURULUYOR.
    ///
    /// `Content-Length` olmayan ya da yalan söyleyen bir cevap sınırı
    /// aşabiliyor; yanlış etiketlenmiş bir podcast bölümü (saatlerce,
    /// yüzlerce MB) diski ve zamanı yerdi — ve bunu ancak disk
    /// dolduğunda fark ederdik.
    [Fact]
    public async Task BuyukAkis_SinirdaDuruyor()
    {
        using var source = new EndlessStream();
        using var destination = new MemoryStream();

        var ok = await MusicSelectHandler.CopyBoundedAsync(source, destination, CancellationToken.None);

        Assert.False(ok);

        // Sınırın hemen üstünde durdu: sonsuza kadar okumadı.
        Assert.True(destination.Length <= MusicSelectHandler.MaxBytes + 81920);
    }

    [Fact]
    public async Task KucukAkis_TamamKopyalaniyor()
    {
        using var source = new MemoryStream(new byte[5000]);
        using var destination = new MemoryStream();

        Assert.True(await MusicSelectHandler.CopyBoundedAsync(source, destination, CancellationToken.None));
        Assert.Equal(5000, destination.Length);
    }

    /// Sonsuz akış: `Content-Length` yalan söyleyen bir sunucuyu
    /// taklit ediyor.
    private sealed class EndlessStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => count;

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(buffer.Length);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// Müziğin timeline'a bağlanması (P2-09).
public sealed class TimelineMusicTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private const string ValidMusic = """
        {"music":{"asset":"sha256:0000000000000000000000000000000000000000000000000000000000000001",
        "license":{"name":"cc0","requires_attribution":false,"captured_at":"2026-08-28T00:00:00Z"}}}
        """;

    [Fact]
    public void GecerliMuzik_YataginaBaglaniyor()
    {
        var bed = TimelineBuilder.MusicFrom(Json(ValidMusic));

        Assert.NotNull(bed);
        Assert.NotNull(bed.License);
        Assert.True(bed.License.IsComplete);

        // DUCKING VARSAYILAN OLARAK AÇIK: kapalı olsaydı müzik
        // konuşmanın üstüne biner ve bunu ancak videoyu dinleyen biri
        // fark ederdi.
        Assert.NotNull(bed.Ducking);
    }

    /// LİSANS KANITI TAŞINMAZSA MÜZİK DE TAŞINMIYOR.
    [Theory]
    [InlineData("""{"music":{"asset":"sha256:0000000000000000000000000000000000000000000000000000000000000001"}}""")]
    [InlineData("""{"music":{"asset":"sha256:0000000000000000000000000000000000000000000000000000000000000001","license":{"name":""}}}""")]
    [InlineData("""{"music":{"asset":"sha256:0000000000000000000000000000000000000000000000000000000000000001","license":{"name":"by","requires_attribution":true}}}""")]
    [InlineData("""{"music":{"skipped":true,"reason":"bulunamadi"}}""")]
    [InlineData("""{"music":{"asset":"bozuk-referans","license":{"name":"cc0"}}}""")]
    [InlineData("{}")]
    public void EksikVeyaBozuk_MuzikYok(string runContext)
        => Assert.Null(TimelineBuilder.MusicFrom(Json(runContext)));
}
