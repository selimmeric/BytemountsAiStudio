using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;

namespace BytemountsAiStudio.Contracts.Providers;

/// Sağlayıcı yönlendirme ve yedeğe düşme (P1-03, §9.3).
///
/// Bir rol için sıralı bir sağlayıcı listesi tutuyor ve ilki
/// başarısız olduğunda sıradakini deniyor. Kanal ayarından katman
/// değiştiğinde ya da bir anahtar geldiğinde DEĞİŞEN TEK ŞEY bu liste
/// oluyor — çağıran taraf hangi sağlayıcıya gittiğini bilmiyor.
///
/// Asıl karar burada değil, `ShouldFallOver`'da: hangi hatada
/// sıradakine geçileceği. Yanlış yapılırsa ya para boşa gider ya da
/// çalışan bir yedek hiç denenmez.
public sealed class ProviderRouter<TProvider>(IReadOnlyList<TProvider> providers, Func<TProvider, string> keyOf)
    where TProvider : class
{
    private readonly IReadOnlyList<TProvider> _providers = providers.Count > 0
        ? providers
        : throw new ArgumentException("Yonlendirme listesi bos olamaz.", nameof(providers));

    public IReadOnlyList<string> Keys => [.. _providers.Select(keyOf)];

    /// Sırayla dener; ilk başarıyı döndürür.
    ///
    /// Hiçbiri tutmazsa SON hata değil, hataların TAMAMI bildiriliyor.
    /// Yalnızca sonuncuyu vermek en yaygın yanlış teşhis sebebi olurdu:
    /// asıl sorun genellikle ilk sağlayıcıdadır, sonuncusu zaten
    /// yedektir ve onun mesajı yanıltır.
    public async Task<Result<RoutedResult<T>>> InvokeAsync<T>(
        Func<TProvider, CancellationToken, Task<Result<T>>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var attempts = new List<RoutingAttempt>();

        for (var i = 0; i < _providers.Count; i++)
        {
            var provider = _providers[i];
            var key = keyOf(provider);

            var result = await operation(provider, cancellationToken).ConfigureAwait(false);

            if (result.IsSuccess)
            {
                return Result.Success(new RoutedResult<T>
                {
                    Value = result.Value,
                    ProviderKey = key,
                    // Sıfırdan büyükse yedeğe düşülmüş demek. Bunu
                    // kayda geçmek gerekiyor: birincil sağlayıcı sessizce
                    // ölürse hiçbir şey kırılmaz ve kimse fark etmez.
                    FellOverFrom = [.. attempts.Select(a => a.ProviderKey)],
                });
            }

            attempts.Add(new RoutingAttempt(key, result.Error));

            var isLast = i == _providers.Count - 1;

            if (isLast || !ShouldFallOver(result.Error))
            {
                return Result.Failure<RoutedResult<T>>(Combine(attempts));
            }
        }

        return Result.Failure<RoutedResult<T>>(Combine(attempts));
    }

    /// Bu hatada sıradaki sağlayıcıya geçilir mi?
    ///
    /// Geçilir:
    ///   Transient — sağlayıcı geçici olarak bozuk; başkası çalışabilir.
    ///               (Buraya geldiyse retry zaten tükenmiş demektir.)
    ///   Resource  — kota doldu ya da bütçe bitti. Beklemek yerine
    ///               ücretsiz/yerel yedeğe düşmek doğru cevap; ADR-015'in
    ///               işlevsel karşılığı tam olarak bu.
    ///
    /// GEÇİLMEZ:
    ///   Permanent — istek geçersiz. Aynı geçersiz isteği bir sağlayıcıya
    ///               daha göndermek yalnızca ikinci kez para harcamak
    ///               olurdu; cevap değişmez.
    ///   Poison    — girdi zaten zehirli; hiçbir sağlayıcı düzeltemez.
    ///   Cancelled — kullanıcı ya da kapanış istedi; devam etmek yanlış.
    ///
    /// Tek istisna Permanent içinde: KİMLİK hataları. "Bu anahtar
    /// geçersiz" isteğin değil, yapılandırmanın kusuru — başka bir
    /// sağlayıcı gayet çalışabilir.
    internal static bool ShouldFallOver(Error error)
        => error.Kind switch
        {
            ErrorKind.Transient => true,
            ErrorKind.Resource => true,
            ErrorKind.Permanent => IsCredentialProblem(error),
            _ => false,
        };

    private static bool IsCredentialProblem(Error error)
        => error.Code.Contains("credential", StringComparison.Ordinal)
           || error.Code.Contains("unauthorized", StringComparison.Ordinal)
           || error.Code.Contains("forbidden", StringComparison.Ordinal)
           || error.Code.EndsWith(".auth", StringComparison.Ordinal);

    /// Denemelerin hepsini tek bir hataya topluyor.
    ///
    /// Sınıf olarak İLK denemenin sınıfı korunuyor: iş kuyruğunun
    /// kararı (yeniden dene / ertele / düşür) birincil sağlayıcının
    /// durumuna göre verilmeli, en son yedeğinkine göre değil.
    private static Error Combine(IReadOnlyList<RoutingAttempt> attempts)
    {
        var first = attempts[0].Error;

        if (attempts.Count == 1)
        {
            return first;
        }

        var detail = string.Join(
            Environment.NewLine,
            attempts.Select(a => $"  {a.ProviderKey}: {a.Error}"));

        return first with
        {
            Code = "routing.all_failed",
            Message = $"{attempts.Count} saglayicinin hepsi basarisiz oldu.",
            Detail = detail,
        };
    }

    private sealed record RoutingAttempt(string ProviderKey, Error Error);
}

/// Sonuç + hangi sağlayıcıdan geldiği.
public sealed record RoutedResult<T>
{
    public required T Value { get; init; }

    public required string ProviderKey { get; init; }

    /// Denenip başarısız olan sağlayıcılar, sırayla. Boş değilse
    /// birincil sağlayıcı çalışmamış demek — kayda geçmesi gereken bilgi.
    public IReadOnlyList<string> FellOverFrom { get; init; } = [];

    public bool UsedFallback => FellOverFrom.Count > 0;
}
