namespace BytemountsAiStudio.TestSupport;

/// Test için ilerletilebilir saat.
///
/// TestSupport'a bağlı projelerin paylaştığı tek kopya. Zaman
/// testlerin en sinsi girdisi — "bugün" tanımı kayarsa test dün geçip
/// bugün düşer; o davranışın tek bir yerde tanımlı olması gerekiyor.
/// (Contracts.Tests kendi kopyasını taşımaya devam ediyor: yalnız bu
/// on satır için oraya Persistence ve Npgsql bağımlılığı çekmek,
/// tekrarın maliyetinden büyük.)
public sealed class FakeTimeProvider(DateTimeOffset? start = null) : TimeProvider
{
    private DateTimeOffset _now = start ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}
