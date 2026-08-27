using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;

namespace BytemountsAiStudio.Contracts.Tests;

/// Model katmanlama testleri (P1-03).
///
/// Kabul kriteri şu: kanal ayarından katman değişince KOD DEĞİŞMİYOR.
/// Bunu sınamanın yolu, aynı isteğin farklı yapılandırmalarda farklı
/// sağlayıcıya gitmesini göstermek.
public sealed class TieredLlmProviderTests
{
    private sealed class StubLlm(string key, Error? error = null) : ILlmProvider
    {
        public string Key => key;

        public int Calls { get; private set; }

        public ModelTier? LastTier { get; private set; }

        public LlmCapabilities Capabilities => new()
        {
            SupportsToolUse = true,
            SupportsVision = false,
            ContextWindowTokens = 8192,
            SupportsEmbeddings = true,
        };

        public Task<Result<ProviderResponse<LlmResponse>>> CompleteAsync(
            LlmRequest request, ProviderContext context, CancellationToken cancellationToken)
        {
            Calls++;
            LastTier = request.Tier;

            return Task.FromResult(error is not null
                ? Result.Failure<ProviderResponse<LlmResponse>>(error)
                : Result.Success(new ProviderResponse<LlmResponse>(
                    new LlmResponse { Text = key, ModelId = key }, new UsageUnits())));
        }

        public Task<Result<ProviderResponse<IReadOnlyList<float>>>> EmbedAsync(
            string text, ProviderContext context, CancellationToken cancellationToken)
        {
            Calls++;

            return Task.FromResult(error is not null
                ? Result.Failure<ProviderResponse<IReadOnlyList<float>>>(error)
                : Result.Success(new ProviderResponse<IReadOnlyList<float>>([1f, 2f], new UsageUnits())));
        }
    }

    private static LlmRequest Request(ModelTier tier) => new()
    {
        Messages = [new(ChatRole.User, "merhaba")],
        Tier = tier,
    };

    private static Task<Result<ProviderResponse<LlmResponse>>> Complete(
        TieredLlmProvider provider, ModelTier tier)
        => provider.CompleteAsync(Request(tier), ProviderContext.ForTest(), CancellationToken.None);

    [Fact]
    public async Task IstenenKatman_DogruSaglayiciyaGider()
    {
        var provider = new TieredLlmProvider(new Dictionary<ModelTier, IReadOnlyList<ILlmProvider>>
        {
            [ModelTier.Cheap] = [new StubLlm("ollama")],
            [ModelTier.Strong] = [new StubLlm("openai")],
        });

        Assert.Equal("ollama", (await Complete(provider, ModelTier.Cheap)).Value.Value.Text);
        Assert.Equal("openai", (await Complete(provider, ModelTier.Strong)).Value.Value.Text);
    }

    /// Anahtar yokken Strong katmanı boş olacak. Sistemin bu yüzden hiç
    /// çalışmaması saçma olurdu (ADR-015) — bir alt katmana düşülüyor.
    [Fact]
    public async Task TanimsizKatman_BirAltaDuser()
    {
        var provider = new TieredLlmProvider(new Dictionary<ModelTier, IReadOnlyList<ILlmProvider>>
        {
            [ModelTier.Cheap] = [new StubLlm("ollama")],
        });

        var result = await Complete(provider, ModelTier.Strong);

        Assert.True(result.IsSuccess);
        Assert.Equal("ollama", result.Value.Value.Text);
    }

    /// Katman düştüyse istek de düşürülüyor: sağlayıcı kendi içinde
    /// katmana göre model seçiyor olabilir ve ona "Strong istedim"
    /// demek yanlış olurdu.
    [Fact]
    public async Task KatmanDuserse_IstektekiKatmanDaDuser()
    {
        var stub = new StubLlm("ollama");
        var provider = new TieredLlmProvider(new Dictionary<ModelTier, IReadOnlyList<ILlmProvider>>
        {
            [ModelTier.Cheap] = [stub],
        });

        await Complete(provider, ModelTier.Strong);

        Assert.Equal(ModelTier.Cheap, stub.LastTier);
    }

    [Fact]
    public async Task KatmanIcindeYedegeDusulur()
    {
        var provider = new TieredLlmProvider(new Dictionary<ModelTier, IReadOnlyList<ILlmProvider>>
        {
            [ModelTier.Strong] =
            [
                new StubLlm("openai", Error.Transient("openai.down", "coktu")),
                new StubLlm("gemini"),
            ],
        });

        var result = await Complete(provider, ModelTier.Strong);

        Assert.Equal("gemini", result.Value.Value.Text);
        Assert.True(provider.LastRoute!.UsedFallback);
        Assert.Equal(["openai"], provider.LastRoute.FellOverFrom);
    }

    /// Birincil sağlayıcı sessizce ölürse hiçbir şey kırılmaz ve kimse
    /// fark etmezdi. Yol bilgisi çıktıya yazılabilsin diye tutuluyor.
    [Fact]
    public async Task YedegeDusulmediginde_YolTemiz()
    {
        var provider = TieredLlmProvider.Single(new StubLlm("ollama"));

        await Complete(provider, ModelTier.Cheap);

        Assert.Equal("ollama", provider.LastRoute!.ProviderKey);
        Assert.False(provider.LastRoute.UsedFallback);
    }

    /// Gömme hacimli bir iş; kalite farkı maliyet farkını karşılamıyor
    /// (ADR-003). Her zaman en ucuz katmandan.
    [Fact]
    public async Task Gomme_EnUcuzKatmandanIstenir()
    {
        var cheap = new StubLlm("ollama");
        var strong = new StubLlm("openai");

        var provider = new TieredLlmProvider(new Dictionary<ModelTier, IReadOnlyList<ILlmProvider>>
        {
            [ModelTier.Cheap] = [cheap],
            [ModelTier.Strong] = [strong],
        });

        await provider.EmbedAsync("metin", ProviderContext.ForTest(), CancellationToken.None);

        Assert.Equal(1, cheap.Calls);
        Assert.Equal(0, strong.Calls);
    }

    /// Yalnızca yüksek katmanlar tanımlıysa: pahalıya çıkabilir ama
    /// çalışmamaktan iyidir ve yapılandırma hatası olarak görünür.
    [Fact]
    public async Task YalnizcaYuksekKatmanVarsa_OKullanilir()
    {
        var provider = new TieredLlmProvider(new Dictionary<ModelTier, IReadOnlyList<ILlmProvider>>
        {
            [ModelTier.Strong] = [new StubLlm("openai")],
        });

        var result = await Complete(provider, ModelTier.Cheap);

        Assert.True(result.IsSuccess);
        Assert.Equal("openai", result.Value.Value.Text);
    }

    [Fact]
    public void HicSaglayiciYok_Reddedilir()
    {
        Assert.Throws<ArgumentException>(() =>
            new TieredLlmProvider(new Dictionary<ModelTier, IReadOnlyList<ILlmProvider>>
            {
                [ModelTier.Cheap] = [],
            }));
    }

    /// Yapılandırma tanı sırasında okunabilmeli.
    [Fact]
    public void Yerlesim_KatmanBasinaSaglayicilariGosterir()
    {
        var provider = new TieredLlmProvider(new Dictionary<ModelTier, IReadOnlyList<ILlmProvider>>
        {
            [ModelTier.Cheap] = [new StubLlm("ollama")],
            [ModelTier.Strong] = [new StubLlm("openai"), new StubLlm("gemini")],
        });

        Assert.Equal(["ollama"], provider.Layout[ModelTier.Cheap]);
        Assert.Equal(["openai", "gemini"], provider.Layout[ModelTier.Strong]);
    }

    /// P1-03'ün kabul kriteri: yapılandırma değişince kod değişmiyor.
    /// Aynı çağrı, iki farklı sözlükle, iki farklı sağlayıcıya gidiyor.
    [Fact]
    public async Task AyniCagri_FarkliYapilandirmadaFarkliSaglayiciya()
    {
        var ucretsiz = new TieredLlmProvider(new Dictionary<ModelTier, IReadOnlyList<ILlmProvider>>
        {
            [ModelTier.Cheap] = [new StubLlm("ollama")],
        });

        var ucretli = new TieredLlmProvider(new Dictionary<ModelTier, IReadOnlyList<ILlmProvider>>
        {
            [ModelTier.Cheap] = [new StubLlm("ollama")],
            [ModelTier.Strong] = [new StubLlm("openai")],
        });

        Assert.Equal("ollama", (await Complete(ucretsiz, ModelTier.Strong)).Value.Value.Text);
        Assert.Equal("openai", (await Complete(ucretli, ModelTier.Strong)).Value.Value.Text);
    }
}
