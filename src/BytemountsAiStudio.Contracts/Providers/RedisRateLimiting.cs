using System.Globalization;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;
using StackExchange.Redis;

namespace BytemountsAiStudio.Contracts.Providers;

/// Token bucket — DAĞITIK (P4-03).
///
/// NEDEN GEREKLİ OLDU: P4-01 ile birden fazla worker gerçekten
/// koşuyor. Süreç içi bir sınır, iki worker'da iki kat istek demek —
/// sağlayıcı bizi keser ve sebep hiçbir logda görünmez, çünkü her
/// worker kendi sayacına göre kurallara uyuyor.
///
/// §8.3: sınır WORKER başına değil HESAP başına.
///
/// LUA SCRIPT ZORUNLU, KOLAYLIK DEĞİL. `GET` sonra `SET` yazsaydık iki
/// worker aynı anda okur, ikisi de "yer var" görür ve ikisi de geçerdi
/// — tam olarak engellemeye çalıştığımız şey. Redis Lua'yı tek parça
/// çalıştırıyor: okuma, hesap ve yazma arasında başka bir istemci
/// giremiyor.
public sealed class RedisRateLimiter : IRateLimiter
{
    /// Anahtar öneki.
    ///
    /// Aynı Redis birden fazla iş tarafından kullanılabiliyor;
    /// önek olmadan başka bir uygulamanın anahtarını ezmek mümkün.
    public const string KeyPrefix = "bmai:rl:";

    /// Pencere başına izin sayısı ve kalan süre TEK ÇAĞRIDA.
    ///
    /// `KEYS[1]` kova anahtarı, `ARGV`: izin sayısı, pencere (ms),
    /// istenen izin.
    ///
    /// Dönüş: `{izin_verildi, kalan_ms}`. İkincisi olmadan çağıran
    /// "ne kadar sonra tekrar dene" diyemezdi ve iş kuyruğu sabit bir
    /// süre uydururdu.
    private const string Script = """
        local sayac = redis.call('INCRBY', KEYS[1], ARGV[3])

        if sayac == tonumber(ARGV[3]) then
          redis.call('PEXPIRE', KEYS[1], ARGV[2])
        end

        local kalan = redis.call('PTTL', KEYS[1])

        if sayac <= tonumber(ARGV[1]) then
          return {1, kalan}
        end

        redis.call('DECRBY', KEYS[1], ARGV[3])
        return {0, kalan}
        """;

    private readonly IDatabase _database;
    private readonly Action<Exception>? _onDegraded;
    private readonly Dictionary<string, RateLimitPolicy> _policies = new(StringComparer.Ordinal);

    /// `onDegraded`: Redis'e ulaşılamadığında çağrılıyor.
    ///
    /// GEÇİCİ OLARAK SINIRSIZ ÇALIŞMAK SESSİZ OLMAMALI. İlk yazımda
    /// istisna tamamen yutuluyordu ve yorumu "çağıran logluyor"
    /// diyordu — çağırana hiçbir şey ulaşmıyordu, yani yorum kodun
    /// yapmadığı bir şeyi anlatıyordu.
    ///
    /// Bu geri çağırım olmadan, Redis günlerce kapalı kalabilir ve
    /// sistem sınırsız istek atarken herkes sınırın çalıştığını
    /// sanardı.
    public RedisRateLimiter(IConnectionMultiplexer connection, Action<Exception>? onDegraded = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _database = connection.GetDatabase();
        _onDegraded = onDegraded;
    }

    public RedisRateLimiter Configure(string providerKey, RateLimitPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _policies[providerKey] = policy;
        return this;
    }

    public async Task<Result> AcquireAsync(
        string providerKey, int permits, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Yapılandırılmamış sağlayıcı sınırsız sayılıyor —
        // `TokenBucketRateLimiter` ile aynı karar. Varsayılan olarak
        // kısıtlamak, her yeni sağlayıcının sessizce yavaşlaması
        // demekti.
        if (!_policies.TryGetValue(providerKey, out var policy))
        {
            return Result.Success();
        }

        RedisResult raw;

        try
        {
            raw = await _database.ScriptEvaluateAsync(
                Script,
                [KeyPrefix + providerKey],
                [policy.PermitsPerWindow, (long)policy.Window.TotalMilliseconds, permits])
                .ConfigureAwait(false);
        }
        catch (RedisException ex)
        {
            // REDIS DÜŞTÜĞÜNDE ÜRETİM DURMUYOR.
            //
            // Sınırı uygulayamamak ile hiç iş yapmamak arasında
            // seçim: ikincisi daha pahalı. Sağlayıcı kendi tarafında
            // da sınır uyguluyor ve o sınıra takılmak `Resource`
            // hatası veriyor, yani sistem yine doğru davranıyor —
            // sadece daha geç öğreniyor.
            //
            // VE SESSİZ DEĞİL: `onDegraded` çağrılıyor, host bunu
            // logluyor. Yoksa Redis günlerce kapalı kalır ve herkes
            // sınırın çalıştığını sanardı.
            _onDegraded?.Invoke(ex);

            return Result.Success();
        }

        var values = (RedisValue[])raw!;
        var allowed = (int)values[0] == 1;

        if (allowed)
        {
            return Result.Success();
        }

        var remaining = (long)values[1];

        // SINIR AŞILDI: HATA DEĞİL, ERTELEME (ADR-011). İş kuyruğu
        // bunu görünce işi pencerenin sonuna atıyor ve deneme sayacı
        // artmıyor.
        return Result.Failure(Error.Resource(
            "ratelimit.exceeded",
            string.Create(CultureInfo.InvariantCulture,
                $"'{providerKey}' için istek sınırı doldu ({policy.PermitsPerWindow}/{policy.Window})."),
            remaining > 0 ? TimeSpan.FromMilliseconds(remaining) : TimeSpan.FromSeconds(1)));
    }
}
