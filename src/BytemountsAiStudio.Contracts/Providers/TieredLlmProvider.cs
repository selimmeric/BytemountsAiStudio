using BytemountsAiStudio.Core;

namespace BytemountsAiStudio.Contracts.Providers;

/// Katman + yönlendirme uygulayan LLM sağlayıcısı (P1-03, §9.3).
///
/// Kendisi `ILlmProvider` — çağıran taraf tek bir sağlayıcıyla mı yoksa
/// beş sağlayıcılık bir zincirle mi konuştuğunu bilmiyor. Bu, işleyici
/// kodunun yönlendirme politikasından haberdar olmamasını sağlıyor;
/// haberdar olsaydı politika değişikliği her işleyiciye dokunurdu.
///
/// Katman kavramı burada gerçek oluyor:
///   Cheap    — hacimli işler; ücretsiz/yerel model yeter
///   Standard — plan ve özet
///   Strong   — yalnızca senaryo; video başına 1-2 çağrı, para burada
///
/// Bir katman için sağlayıcı tanımlı değilse BİR ALT katmana düşülüyor.
/// Boş dönmek yerine düşmek bilinçli: anahtar yokken Strong katmanı boş
/// olacak ve sistemin o yüzden hiç çalışmaması saçma olurdu (ADR-015).
/// Düşüş kayda geçiyor, sessiz değil.
public sealed class TieredLlmProvider : ILlmProvider
{
    private readonly IReadOnlyDictionary<ModelTier, ProviderRouter<ILlmProvider>> _routers;
    private readonly ILlmProvider _representative;

    public TieredLlmProvider(IReadOnlyDictionary<ModelTier, IReadOnlyList<ILlmProvider>> byTier)
    {
        ArgumentNullException.ThrowIfNull(byTier);

        var routers = new Dictionary<ModelTier, ProviderRouter<ILlmProvider>>();

        foreach (var (tier, providers) in byTier)
        {
            if (providers.Count > 0)
            {
                routers[tier] = new ProviderRouter<ILlmProvider>(providers, p => p.Key);
            }
        }

        if (routers.Count == 0)
        {
            throw new ArgumentException("En az bir katmanda saglayici olmali.", nameof(byTier));
        }

        _routers = routers;

        // Yetenekler ve anahtar için bir temsilci gerekiyor. En düşük
        // katmanınki seçiliyor: her zaman tanımlı olan katman o.
        _representative = byTier[routers.Keys.Min()][0];
    }

    /// Tek sağlayıcıyla kurma kısayolu — testler ve anahtarsız hat için.
    public static TieredLlmProvider Single(ILlmProvider provider)
        => new(new Dictionary<ModelTier, IReadOnlyList<ILlmProvider>>
        {
            [ModelTier.Cheap] = [provider],
        });

    public string Key => "tiered";

    public LlmCapabilities Capabilities => _representative.Capabilities;

    /// Son çağrının hangi sağlayıcıya gittiği ve yedeğe düşülüp
    /// düşülmediği. Node işleyicisi bunu çıktıya yazıyor — birincil
    /// sağlayıcı sessizce ölürse hiçbir şey kırılmaz ve kimse fark
    /// etmezdi.
    public RoutedResult<LlmResponse>? LastRoute { get; private set; }

    public async Task<Result<ProviderResponse<LlmResponse>>> CompleteAsync(
        LlmRequest request, ProviderContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (router, effective) = Resolve(request.Tier);

        // Katman düştüyse istek de düşürülüyor: sağlayıcı kendi
        // içinde katmana göre model seçiyor olabilir, ve ona "Strong
        // istedim" demek yanlış olurdu.
        var effectiveRequest = effective == request.Tier ? request : request with { Tier = effective };

        var routed = await router
            .InvokeAsync<ProviderResponse<LlmResponse>>(
                (provider, ct) => provider.CompleteAsync(effectiveRequest, context, ct),
                cancellationToken)
            .ConfigureAwait(false);

        if (routed.IsFailure)
        {
            return Result.Failure<ProviderResponse<LlmResponse>>(routed.Error);
        }

        LastRoute = new RoutedResult<LlmResponse>
        {
            Value = routed.Value.Value.Value,
            ProviderKey = routed.Value.ProviderKey,
            FellOverFrom = routed.Value.FellOverFrom,
        };

        return Result.Success(routed.Value.Value);
    }

    public async Task<Result<ProviderResponse<IReadOnlyList<float>>>> EmbedAsync(
        string text, ProviderContext context, CancellationToken cancellationToken)
    {
        // Gömme her zaman EN UCUZ katmandan: hacimli bir iş ve kalite
        // farkı, maliyet farkını karşılamıyor (ADR-003).
        var (router, _) = Resolve(ModelTier.Cheap);

        var routed = await router
            .InvokeAsync<ProviderResponse<IReadOnlyList<float>>>(
                (provider, ct) => provider.EmbedAsync(text, context, ct),
                cancellationToken)
            .ConfigureAwait(false);

        return routed.IsFailure
            ? Result.Failure<ProviderResponse<IReadOnlyList<float>>>(routed.Error)
            : Result.Success(routed.Value.Value);
    }

    /// İstenen katman yoksa bir alta düşer; en alt da yoksa en yakın
    /// tanımlı katmanı kullanır.
    internal (ProviderRouter<ILlmProvider> Router, ModelTier Effective) Resolve(ModelTier requested)
    {
        for (var tier = requested; tier >= ModelTier.Cheap; tier--)
        {
            if (_routers.TryGetValue(tier, out var router))
            {
                return (router, tier);
            }
        }

        // Yalnızca istenenden YÜKSEK katmanlar tanımlıysa buraya
        // düşülüyor. Pahalıya çıkabilir ama çalışmamaktan iyidir ve
        // yapılandırma hatası olarak zaten görünür.
        var fallback = _routers.Keys.Min();

        return (_routers[fallback], fallback);
    }

    /// Hangi katmanda hangi sağlayıcılar var — `bmai providers`
    /// çıktısında ve tanı sırasında okunuyor.
    public IReadOnlyDictionary<ModelTier, IReadOnlyList<string>> Layout
        => _routers.ToDictionary(p => p.Key, p => p.Value.Keys);
}
