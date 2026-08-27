using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;

namespace BytemountsAiStudio.Contracts.Tests;

/// "Önce stok, bulunamazsa üret" yönlendirmesinin testleri (P1-18).
///
/// Asıl mesele stok BULAMAMANIN bir hata olmaması: soyut bir cümlenin
/// stok karşılığı yok ve bu normal. Hata saymak, her soyut sahnede
/// run'ı düşürürdü.
public sealed class StockFirstImageProviderTests
{
    private sealed class StubImage(ImageProviderKind kind) : IImageProvider
    {
        public string Key => kind == ImageProviderKind.Stock ? "stok" : "uretim";

        public ImageProviderKind Kind => kind;

        public IReadOnlyList<ImageCandidate> Candidates { get; set; } = [];

        public Error? FindError { get; set; }

        public Error? GenerateError { get; set; }

        public int FindCalls { get; private set; }

        public int GenerateCalls { get; private set; }

        public string? LastTerms { get; private set; }

        public Task<Result<ProviderResponse<IReadOnlyList<ImageCandidate>>>> FindAsync(
            ImageQuery query, ProviderContext context, CancellationToken cancellationToken)
        {
            FindCalls++;
            LastTerms = query.Terms;

            return Task.FromResult(FindError is not null
                ? Result.Failure<ProviderResponse<IReadOnlyList<ImageCandidate>>>(FindError)
                : Result.Success(new ProviderResponse<IReadOnlyList<ImageCandidate>>(
                    Candidates, new UsageUnits())));
        }

        public Task<Result<ProviderResponse<GeneratedImage>>> GenerateAsync(
            ImagePrompt prompt, ProviderContext context, CancellationToken cancellationToken)
        {
            GenerateCalls++;

            return Task.FromResult(GenerateError is not null
                ? Result.Failure<ProviderResponse<GeneratedImage>>(GenerateError)
                : Result.Success(new ProviderResponse<GeneratedImage>(
                    Image("uretilen"), new UsageUnits { Images = 1 })));
        }
    }

    private static GeneratedImage Image(string marker) => new()
    {
        Data = System.Text.Encoding.UTF8.GetBytes(marker + new string('x', 2000)),
        MimeType = "image/jpeg",
        Width = 1080,
        Height = 1920,
        License = new LicenseInfo
        {
            Name = marker,
            RequiresAttribution = false,
            CapturedAt = DateTimeOffset.UtcNow,
        },
    };

    private static ImageCandidate Candidate(int width = 1600, int height = 2400, string license = "CC0")
        => new()
        {
            Url = new Uri("https://ornek.com/gorsel.jpg"),
            Width = width,
            Height = height,
            License = new LicenseInfo
            {
                Name = license,
                Author = "Fotografci",
                RequiresAttribution = true,
                CapturedAt = DateTimeOffset.UtcNow,
            },
        };

    private static ImagePrompt Prompt(string text = "Göbeklitepe: tapınak, dikilitaş. cinematic. no text")
        => new() { Text = text, Width = 1080, Height = 1920, Seed = 3 };

    private static (StockFirstImageProvider Provider, StubImage Stock, StubImage Generative, List<Uri> Downloads)
        Build(bool downloadWorks = true)
    {
        var stock = new StubImage(ImageProviderKind.Stock);
        var generative = new StubImage(ImageProviderKind.Generative);
        var downloads = new List<Uri>();

        var provider = new StockFirstImageProvider(stock, generative, (url, _) =>
        {
            downloads.Add(url);

            return Task.FromResult(downloadWorks
                ? Result.Success(Image("indirilen"))
                : Result.Failure<GeneratedImage>(Error.Transient("x", "indirilemedi")));
        });

        return (provider, stock, generative, downloads);
    }

    private static Task<Result<ProviderResponse<GeneratedImage>>> Run(StockFirstImageProvider provider)
        => provider.GenerateAsync(Prompt(), ProviderContext.ForTest(), CancellationToken.None);

    // ---- Stok tuttuğunda ----

    [Fact]
    public async Task StokBulunca_UretimeHicGidilmez()
    {
        var (provider, stock, generative, _) = Build();
        stock.Candidates = [Candidate()];

        var result = await Run(provider);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);
        Assert.Equal(0, generative.GenerateCalls);
        Assert.Equal("stock", provider.LastRoute);
    }

    /// LİSANS ADAYDAN geliyor, indirilen bayttan değil: indiren taraf
    /// lisansı bilmiyor, aday biliyor ve §14 uyum kaydının kaynağı o.
    [Fact]
    public async Task StokGorseli_LisansiAdaydanAlir()
    {
        var (provider, stock, _, _) = Build();
        stock.Candidates = [Candidate(license: "CC BY 4.0")];

        var result = await Run(provider);

        Assert.Equal("CC BY 4.0", result.Value.Value.License.Name);
        Assert.Equal("Fotografci", result.Value.Value.License.Author);
        Assert.True(result.Value.Value.License.RequiresAttribution);
    }

    // ---- Stok tutmadığında ----

    /// Stok BULAMAMAK bir hata değil: soyut bir cümlenin stok karşılığı
    /// yok ve bu normal. Hata saymak her soyut sahnede run'ı düşürürdü.
    [Fact]
    public async Task StokBulamayinca_SessizceUretimeDuser()
    {
        var (provider, stock, generative, _) = Build();
        stock.Candidates = [];

        var result = await Run(provider);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, generative.GenerateCalls);
        Assert.Equal("generative:no_match", provider.LastRoute);
    }

    /// 1080×1920 tuvale küçük bir görseli büyütmek bulanık kare üretiyor
    /// ve bu videoda ilk göze çarpan şey.
    [Fact]
    public async Task KucukStokGorseli_Reddedilir()
    {
        var (provider, stock, generative, downloads) = Build();
        stock.Candidates = [Candidate(width: 400, height: 300)];

        var result = await Run(provider);

        Assert.True(result.IsSuccess);
        Assert.Empty(downloads);
        Assert.Equal(1, generative.GenerateCalls);
    }

    /// Stok sağlayıcısının HATA vermesi, sonuç bulamamasından farklı bir
    /// durum ve kayda geçiyor.
    [Fact]
    public async Task StokHataVerince_UretimeDuserVeKaydaGecer()
    {
        var (provider, stock, generative, _) = Build();
        stock.FindError = Error.Transient("stok.down", "servis kapali");

        var result = await Run(provider);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, generative.GenerateCalls);
        Assert.Equal("generative:stock_error", provider.LastRoute);
    }

    /// Stok servisleri sık sık ölü bağlantı döndürüyor; tek bir adayın
    /// indirilememesi zinciri düşürmemeli.
    [Fact]
    public async Task IndirmeBasarisiz_SonrakiAdayDenenir()
    {
        var stock = new StubImage(ImageProviderKind.Stock)
        {
            Candidates =
            [
                Candidate() with { Url = new Uri("https://ornek.com/olu.jpg") },
                Candidate() with { Url = new Uri("https://ornek.com/saglam.jpg") },
            ],
        };

        var generative = new StubImage(ImageProviderKind.Generative);
        var attempts = new List<Uri>();

        var provider = new StockFirstImageProvider(stock, generative, (url, _) =>
        {
            attempts.Add(url);

            return Task.FromResult(url.AbsolutePath.Contains("olu", StringComparison.Ordinal)
                ? Result.Failure<GeneratedImage>(Error.Transient("x", "olu baglanti"))
                : Result.Success(Image("indirilen")));
        });

        var result = await Run(provider);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, attempts.Count);
        Assert.Equal("stock", provider.LastRoute);
        Assert.Equal(0, generative.GenerateCalls);
    }

    [Fact]
    public async Task TumIndirmelerDuserse_UretimeDuser()
    {
        var (provider, stock, generative, _) = Build(downloadWorks: false);
        stock.Candidates = [Candidate(), Candidate()];

        var result = await Run(provider);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, generative.GenerateCalls);
    }

    /// İKİSİ DE düşerse yalnızca sonuncuyu vermek yanlış teşhise yol
    /// açardı.
    [Fact]
    public async Task IkisiDeDuserse_HerIkiHataDaBildirilir()
    {
        var (provider, stock, generative, _) = Build();
        stock.FindError = Error.Transient("stok.down", "stok coktu");
        generative.GenerateError = Error.Transient("uretim.down", "uretim coktu");

        var result = await Run(provider);

        Assert.True(result.IsFailure);
        Assert.Contains("uretim coktu", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("stok coktu", result.Error.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    // ---- Terim kısaltma ----

    /// İstem "Konu: terim, terim. üslup… no text" gibi geliyor; stok
    /// aramasına bunun tamamını vermek sıfır sonuç demek.
    [Theory]
    [InlineData("Göbeklitepe: tapınak, dikilitaş. cinematic documentary. no text", "tapınak, dikilitaş")]
    [InlineData("Antikythera: gears. digital illustration", "gears")]
    [InlineData("yalnızca konu", "yalnızca konu")]
    [InlineData("Konu: terimler", "terimler")]
    public void UzunIstem_KisaTerimeIndirgenir(string prompt, string expected)
    {
        Assert.Equal(expected, StockFirstImageProvider.ShortTerms(prompt));
    }

    [Fact]
    public async Task StokAramasi_KisaTerimlerleYapilir()
    {
        var (provider, stock, _, _) = Build();
        stock.Candidates = [Candidate()];

        await Run(provider);

        Assert.Equal("tapınak, dikilitaş", stock.LastTerms);
    }

    /// Aynı görsel farklı dillerde kullanılacağı için üzerindeki yazı
    /// sorun olur (§20.7).
    [Fact]
    public async Task StokAramasi_YaziliGorselleriEler()
    {
        var stock = new StubImage(ImageProviderKind.Stock);
        ImageQuery? captured = null;

        var provider = new StockFirstImageProvider(
            new CapturingStock(stock, q => captured = q),
            new StubImage(ImageProviderKind.Generative),
            (_, _) => Task.FromResult(Result.Success(Image("x"))));

        await Run(provider);

        Assert.NotNull(captured);
        Assert.True(captured.ExcludeTextInImage);
    }

    private sealed class CapturingStock(StubImage inner, Action<ImageQuery> capture) : IImageProvider
    {
        public string Key => inner.Key;

        public ImageProviderKind Kind => inner.Kind;

        public Task<Result<ProviderResponse<IReadOnlyList<ImageCandidate>>>> FindAsync(
            ImageQuery query, ProviderContext context, CancellationToken cancellationToken)
        {
            capture(query);

            return inner.FindAsync(query, context, cancellationToken);
        }

        public Task<Result<ProviderResponse<GeneratedImage>>> GenerateAsync(
            ImagePrompt prompt, ProviderContext context, CancellationToken cancellationToken)
            => inner.GenerateAsync(prompt, context, cancellationToken);
    }

    /// Zincirin türü STOK: çağıran "önce stok denenecek" bilgisini
    /// buradan okuyor.
    [Fact]
    public void Zincir_StokTurundeGorunur()
    {
        var (provider, _, _, _) = Build();

        Assert.Equal(ImageProviderKind.Stock, provider.Kind);
    }
}
