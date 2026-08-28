using System.Globalization;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;
using StackExchange.Redis;

namespace BytemountsAiStudio.Contracts.Providers;

/// Devre kesici — DAĞITIK (P4-03).
///
/// NEDEN PAYLAŞILMASI GEREKİYOR: sağlayıcı ölmüşse bunu ÖĞRENEN
/// worker bir tanedir, ama BİLMESİ gereken hepsidir. Süreç içi devre
/// kesiciyle her worker aynı dersi ayrı ayrı alıyor: beş worker,
/// eşikten beş kat fazla başarısız istek ve beş kat gecikme.
///
/// AÇIK DEVRE HATA DEĞİL, ERTELEME (ADR-011). İşler kuyrukta bekliyor,
/// run'lar düşmüyor — sağlayıcı toparlandığında kaldığı yerden devam
/// ediyor.
public sealed class RedisCircuitBreaker : ICircuitBreaker
{
    public const string KeyPrefix = "bmai:cb:";

    /// Hata sayacı ve devre durumu TEK ÇAĞRIDA.
    ///
    /// `INCR` sonra `GET` yazsaydık iki worker aynı anda eşiği
    /// geçebilir ve ikisi de "ben açtım" diye devreyi ayrı ayrı
    /// açardı — zararsız ama açılma zamanı ikinci yazımla kayardı,
    /// yani devre olması gerekenden uzun kapalı kalırdı.
    private const string FailureScript = """
        local sayac = redis.call('INCR', KEYS[1])
        redis.call('PEXPIRE', KEYS[1], ARGV[2])

        if sayac >= tonumber(ARGV[1]) then
          -- `NX`: devre zaten aciksa acilma zamani KORUNUYOR. Ust uste
          -- gelen hatalar devreyi surekli yeniden acsaydi, yarim acik
          -- deneme anina hic ulasilamazdi.
          redis.call('SET', KEYS[2], '1', 'PX', ARGV[3], 'NX')
        end

        return sayac
        """;

    private readonly IDatabase _database;
    private readonly Action<Exception>? _onDegraded;
    private readonly int _failureThreshold;
    private readonly TimeSpan _openDuration;
    private readonly TimeSpan _failureWindow;

    /// `openDuration`: devre bu süre boyunca açık kalıyor.
    /// `failureWindow`: hata sayacının yaşam süresi — bu kadar süre
    /// içinde eşiğe ulaşılmazsa sayaç sıfırlanıyor.
    ///
    /// SAYACIN SÜRESİ OLMALI: günde bir hata alan bir sağlayıcı, beş
    /// gün sonra "art arda beş hata" sayılırdı. "Art arda" ancak bir
    /// zaman penceresi içinde anlamlı.
    public RedisCircuitBreaker(
        IConnectionMultiplexer connection,
        int failureThreshold = 5,
        TimeSpan? openDuration = null,
        TimeSpan? failureWindow = null,
        Action<Exception>? onDegraded = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        _database = connection.GetDatabase();
        _failureThreshold = failureThreshold;
        _openDuration = openDuration ?? TimeSpan.FromMinutes(5);
        _failureWindow = failureWindow ?? TimeSpan.FromMinutes(5);
        _onDegraded = onDegraded;
    }

    public async Task<Result> CheckAsync(string providerKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var remaining = await _database.KeyTimeToLiveAsync(OpenKey(providerKey)).ConfigureAwait(false);

            if (remaining is null)
            {
                // Anahtar yok ya da süresi dolmuş: devre kapalı.
                //
                // SÜRENİN DOLMASI YARI AÇIK DURUMUN KENDİSİ: bir
                // sonraki istek geçiyor. Başarılıysa sayaç
                // sıfırlanıyor, başarısızsa devre yeniden açılıyor.
                // Ayrı bir "yarı açık" durumu tutmak, üç durumu iki
                // anahtarla senkron tutmak demekti.
                return Result.Success();
            }

            return Result.Failure(Error.Resource(
                "circuit.open",
                string.Create(CultureInfo.InvariantCulture,
                    $"'{providerKey}' devresi açık; art arda {_failureThreshold} geçici hata alındı."),
                remaining.Value));
        }
        catch (RedisException ex)
        {
            // REDIS DÜŞTÜĞÜNDE DEVRE KAPALI SAYILIYOR.
            //
            // Açık saymak, Redis kesintisinde bütün üretimi
            // durdurmak olurdu — devre kesicinin amacı sağlayıcıyı
            // korumak, sistemi kilitlemek değil. Sağlayıcı gerçekten
            // ölüyse istekler yine düşecek, sadece daha pahalıya.
            _onDegraded?.Invoke(ex);
            return Result.Success();
        }
    }

    public async Task RecordSuccessAsync(string providerKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            // İKİSİ BİRDEN SİLİNİYOR: yalnızca sayacı silmek, açık bir
            // devreyi açık bırakırdı ve başarılı bir istek hiçbir şeyi
            // değiştirmezdi.
            await _database.KeyDeleteAsync([FailureKey(providerKey), OpenKey(providerKey)])
                .ConfigureAwait(false);
        }
        catch (RedisException ex)
        {
            _onDegraded?.Invoke(ex);
        }
    }

    public async Task RecordFailureAsync(string providerKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await _database.ScriptEvaluateAsync(
                FailureScript,
                [FailureKey(providerKey), OpenKey(providerKey)],
                [
                    _failureThreshold,
                    (long)_failureWindow.TotalMilliseconds,
                    (long)_openDuration.TotalMilliseconds,
                ])
                .ConfigureAwait(false);
        }
        catch (RedisException ex)
        {
            // Hata KAYDEDİLEMEDİ: devre açılmayacak ve sistem ölü bir
            // sağlayıcıya istek atmaya devam edecek. Sessiz kalmak,
            // bu ikinci kaybı da görünmez yapardı.
            _onDegraded?.Invoke(ex);
        }
    }

    private static RedisKey FailureKey(string providerKey) => KeyPrefix + providerKey + ":hata";

    private static RedisKey OpenKey(string providerKey) => KeyPrefix + providerKey + ":acik";
}
