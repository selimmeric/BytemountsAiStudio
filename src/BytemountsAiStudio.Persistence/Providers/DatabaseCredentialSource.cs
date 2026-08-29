using BytemountsAiStudio.Contracts.Providers;

namespace BytemountsAiStudio.Persistence.Providers;

/// Şifreli kimlik deposunu sağlayıcılara BAĞLAYAN köprü.
///
/// ***BU SINIF YOKTU VE YOKLUĞU `bmai credential set` KOMUTUNU
/// ANLAMSIZ KILIYORDU.***
///
/// `ICredentialSource` arayüzünün **hiçbir üretim gerçeklemesi
/// yoktu** — yalnızca test sahteleri. Her sağlayıcı şöyle okuyor:
///
/// ```
/// credentials?.Get(name) ?? Environment.GetEnvironmentVariable(name)
/// ```
///
/// `credentials` her zaman `null` olduğu için **her anahtar ortam
/// değişkeninden geliyordu**. Yani şifreli depo yaz-bir-daha-okunmaz
/// bir kutuydu: `bmai credential set youtube` çalışıyor, satırı
/// şifreliyor, kaydediyor — ve hiçbir yayın o değeri görmüyordu.
///
/// ***KATALOG KÖPRÜ:*** depo sağlayıcı ANAHTARINA göre saklıyor
/// (`youtube`), sağlayıcılar ise ORTAM DEĞİŞKENİ ADINA göre okuyor
/// (`YOUTUBE_REFRESH_TOKEN`). İkisini bağlayan şey katalogdaki
/// `key_env` alanı — ki o alan da bugüne kadar hiçbir davranışa
/// dönüşmüyordu.
///
/// ***ANLIK GÖRÜNTÜ, CANLI SORGU DEĞİL.*** `ICredentialSource.Get`
/// **senkron** ve her sağlayıcı çağrısında çalışıyor; oradan
/// veritabanına gitmek her istek için bir sorgu demekti.
///
/// ***OKUMA DA SENKRON — VE BU `.Result` DEĞİL.*** Kayıt bir DI
/// fabrikasından kuruluyor ve o fabrika senkron. Asenkron bir çağrıyı
/// `.Result` ile beklemek worker iş parçacığını bloke eder ve klasik
/// kilitlenmeyi üretir; EF'in **gerçek senkron sorgusu** (`ToList`)
/// öyle bir risk taşımıyor — bekleyen bir `Task` yok. Bu yol koşu
/// başına bir kez, indeksli ve birkaç satırlık.
///
/// Bedeli: koşu sırasında değiştirilen bir anahtar o koşuda
/// görülmüyor. Kabul edilebilir — kayıt her koşu için yeniden
/// kuruluyor (scoped) ve anahtar dönüşü zaten yeniden başlatma
/// gerektiren bir işlem.
public sealed class DatabaseCredentialSource : ICredentialSource
{
    private readonly Dictionary<string, string> _values;

    private DatabaseCredentialSource(Dictionary<string, string> values) => _values = values;

    /// Hiçbir şey bilmeyen kaynak — depo okunamadığında.
    ///
    /// `null` DÖNMEK YERİNE BOŞ KAYNAK: çağıran taraf `null` ile boş
    /// arasında ayrım yapmak zorunda kalmasın. İkisi de aynı sonucu
    /// veriyor (ortam değişkenine düşülüyor) ama boş nesne o kararı
    /// tek yerde tutuyor.
    public static DatabaseCredentialSource Empty { get; } = new([]);

    public string? Get(string name)
        => _values.GetValueOrDefault(name);

    /// Kaç anahtar yüklendi. Açılışta loglanıyor: sıfır görmek,
    /// "anahtarı kaydettim ama çalışmıyor" sorusunun ilk cevabı.
    public int Count => _values.Count;

    /// Depodaki bütün kimlikleri ortam değişkeni adlarına eşleyerek
    /// yükler.
    ///
    /// KANAL KAPSAMI: kanala özel kayıt genel kaydı EZİYOR — deponun
    /// kendi önceliğiyle aynı (`CredentialStore.GetAsync`). İki ayrı
    /// öncelik kuralı olsaydı, aynı anahtar iki yoldan farklı değer
    /// verirdi.
    public static DatabaseCredentialSource Load(
        CredentialStore store,
        ProviderCatalog? catalog,
        Guid? channelId,
        Action<string>? onWarning = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        if (catalog is null)
        {
            // KATALOG YOKSA EŞLEME DE YOK: hangi sağlayıcının hangi
            // değişkene karşılık geldiğini söyleyen tek yer o.
            return Empty;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var infos = store.List(channelId);

        // GENEL KAYITLAR ÖNCE, KANALA ÖZEL SONRA: sonra yazılan
        // kazanıyor, yani kanala özel olan geneli eziyor.
        foreach (var info in infos.OrderBy(i => i.ChannelId is null ? 0 : 1))
        {
            var descriptor = catalog.Providers.FirstOrDefault(p => p.Key == info.ProviderKey);

            if (descriptor?.KeyEnv is not { Length: > 0 } variable)
            {
                // Katalogda `key_env` yoksa bu kimliğin nereye
                // gideceği bilinmiyor. Sessiz geçmek, kaydedilmiş bir
                // anahtarın hiç kullanılmaması demekti.
                onWarning?.Invoke(
                    $"'{info.ProviderKey}' için kimlik kayıtlı ama katalogda `key_env` yok; "
                    + "bu anahtar hiçbir sağlayıcıya ulaşmayacak.");

                continue;
            }

            var secret = store.Get(info.ProviderKey, info.ChannelId, info.Account);

            if (secret.IsFailure)
            {
                // ÇÖZÜLEMEYEN KAYIT KOŞUYU DÜŞÜRMÜYOR: diğer
                // anahtarlar çalışmalı. Ama sebep söyleniyor —
                // anahtar değişmiş bir keyring'de bütün kimlikler
                // çözülemez olur ve sessizce ortam değişkenine
                // düşmek bunu görünmez kılardı.
                onWarning?.Invoke(
                    $"'{info.ProviderKey}' kimliği çözülemedi: {secret.Error.Message}");

                continue;
            }

            // ***HESABA ÖZEL AD DA YAZILIYOR (P4-04).***
            //
            // Kota havuzu hesap seçiyor ve yayıncı
            // `YOUTUBE_REFRESH_TOKEN_PROJE_02` gibi bir ad arıyor.
            // Yalnızca temel adı yazmak, havuzdaki ikinci hesabın
            // kimliğinin hiç bulunamaması demekti.
            values[Credentials.VariableFor(variable, info.Account)] = secret.Value;
        }

        return new DatabaseCredentialSource(values);
    }
}
