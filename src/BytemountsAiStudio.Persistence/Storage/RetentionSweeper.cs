using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core.Assets;
using BytemountsAiStudio.Core.Content;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Persistence.Storage;

/// Saklama kuralını UYGULAYAN taraf (P4-02).
///
/// ***`RetentionPolicy` YAZILMIŞ, TESTLENMİŞ VE HİÇBİR YERDEN
/// ÇAĞRILMIYORDU.***
///
/// Dosyanın kendi yorumunun tarif ettiği sorun aynen duruyordu: hiçbir
/// ara varlık silinmiyor, depo sınırsız büyüyor ve maliyet ÜRETİMLE
/// değil GEÇMİŞLE orantılı hâle geliyordu. 30 günlük kural,
/// yayınlanmış videoyu koruma kuralı ve lisanslı varlık istisnası
/// kodda vardı ve hiçbir zaman uygulanmıyordu.
///
/// ***NİHAİ VİDEO BU SÜPÜRÜCÜNÜN HİÇ GÖRMEDİĞİ BİR ŞEY ve bu yazılı
/// duruyor:*** render çıktısı varlık deposuna değil `outputDirectory`
/// altına yazılıyor, yani `assets` tablosunda hiç satırı yok. Bu
/// yüzden `RetentionPolicy`'nin "nihai video silinmiyor" kuralı
/// bugün hiçbir satıra denk gelmiyor — kural yanlış değil,
/// ULAŞILAMAZ. Kaldırılmadı çünkü çıktı bir gün depoya taşınırsa
/// ilk gereken şey o olur; ama "uygulanıyor" sanılmasın diye burada
/// söyleniyor.
///
/// ***KARAR SAF, UYGULAMA BURADA.*** `RetentionPolicy.Decide` tek bir
/// varlığa bakıyor ve veritabanı istemiyor; burası o kararı gerçek
/// satırlara uyguluyor. Ayrılmalarının sebebi kararın sınanabilirliği:
/// "yayınlanmış video silinmiyor" kuralını doğrulamak için depo
/// kurmak gerekmemeli.
public sealed class RetentionSweeper(
    StudioDbContext db, IStorageProvider storage, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// Tek turda en fazla kaç varlık siliniyor.
    ///
    /// SINIR VAR ÇÜNKÜ İLK TUR EN BÜYÜĞÜ: kural aylardır
    /// uygulanmadıysa ilk koşu on binlerce varlık bulabilir. Hepsini
    /// tek turda silmek, saatlerce süren ve yarıda kesilebilen bir
    /// işlem demek. Sınırlı turlar günde bir koşuyor ve birikimi
    /// birkaç günde eritiyor.
    public const int DefaultBatchSize = 500;

    /// Saklama süresi — `BMAI_RETENTION_DAYS` ile ayarlanabiliyor.
    ///
    /// Sabit otuz gün her kuruluma uymuyor: disk bol olan bir yerde
    /// daha uzun tutmak, dar bir yerde daha kısa tutmak isteniyor.
    public const string DaysVariable = "BMAI_RETENTION_DAYS";

    public static TimeSpan Age()
        => int.TryParse(Environment.GetEnvironmentVariable(DaysVariable),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var days) && days > 0
                ? TimeSpan.FromDays(days)
                : RetentionPolicy.IntermediateAge;

    public sealed record SweepResult(int Examined, int Deleted, long BytesFreed, int Failed);

    /// Süresi dolmuş ara ürünleri siler.
    ///
    /// ***KURU KOŞU VARSAYILAN DEĞİL AMA VAR:*** `dryRun` ile kaç
    /// varlığın silineceği ölçülebiliyor. Silme kararı geri alınabilir
    /// (içerik-adresli varlık yeniden üretilince aynı sha256'ya
    /// düşüyor) ama yine de ilk koşuyu görmeden yapmak istemezsiniz.
    public async Task<SweepResult> SweepAsync(
        CancellationToken cancellationToken, bool dryRun = false, int? batchSize = null)
    {
        var age = Age();
        var cutoff = _time.GetUtcNow() - age;
        var limit = batchSize ?? DefaultBatchSize;

        // ***YAYINLANMIŞ KOŞULARIN VARLIKLARI ÖNCE TOPLANIYOR.***
        //
        // Karar "bu varlık yayınlanmış bir içeriğe mi ait" sorusunu
        // soruyor ve cevap varlık satırında YOK: bağ `node_executions`
        // çıktılarından geçiyor. Her varlık için ayrı sorgu atmak
        // beş yüz varlık için beş yüz sorgu demekti.
        var publishedAssets = await PublishedAssetsAsync(cancellationToken).ConfigureAwait(false);

        // ESKİ OLANLAR ÖNCE: en çok yer kazandıran ve en az riskli
        // olanlar. Sıralamasız bir sorgu her turda aynı varlıkları
        // getirip hiç ilerlemeyebilirdi.
        var candidates = await db.Assets.AsNoTracking()
            .Where(a => a.CreatedAt < cutoff)
            .OrderBy(a => a.CreatedAt)
            .Take(limit * 2)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var deleted = 0;
        var failed = 0;
        long freed = 0;

        foreach (var asset in candidates)
        {
            if (deleted >= limit)
            {
                break;
            }

            // ***TANINMAYAN TÜR SİLİNMİYOR.***
            //
            // Bir varsayılan türe düşürmek (örneğin "resim") en pahalı
            // hata türünü açardı: yeni bir varlık türü eklendiğinde,
            // onu tanımayan bu satır yüzünden sessizce silinebilir
            // sayılırdı. Bilinmeyen bir şeyi silmemek, bilmediğini
            // silmekten her zaman ucuz.
            if (!Enum.TryParse<AssetKind>(asset.Kind, ignoreCase: true, out var kind))
            {
                continue;
            }

            var decision = RetentionPolicy.Decide(
                kind,
                _time.GetUtcNow() - asset.CreatedAt,
                published: publishedAssets.Contains(asset.Sha256),
                // LİSANS KAYDI OLAN VARLIK DIŞ KAYNAKLI SAYILIYOR.
                //
                // Alan doluysa bu varlığın lisansını ispatlayan tek
                // şey dosyanın kendisi (§2.3/14): kayıt, kanıtı
                // olmayan bir beyana dönüşürdü.
                externallyLicensed: !string.IsNullOrWhiteSpace(asset.LicenseJson));

            if (!decision.CanDelete)
            {
                continue;
            }

            if (dryRun)
            {
                deleted++;
                freed += asset.Bytes;
                continue;
            }

            var reference = AssetRef.TryCreate("sha256:" + asset.Sha256);

            if (reference.IsFailure)
            {
                failed++;
                continue;
            }

            var removed = await storage.DeleteAsync(reference.Value, cancellationToken)
                .ConfigureAwait(false);

            if (removed.IsFailure)
            {
                // ***DEPODAN SİLİNEMEDİYSE SATIR DA SİLİNMİYOR.***
                //
                // Sırası bu: satır önce silinseydi ve depo düşseydi,
                // dosya sonsuza kadar sahipsiz kalırdı — hiçbir kayıt
                // onu göstermediği için bir daha da bulunamazdı. Ters
                // sırada en kötü ihtimal, bir sonraki turda yeniden
                // denenmesi.
                failed++;
                continue;
            }

            // SATIR SHA256 ILE SILINIYOR: birincil anahtar o
            // (içerik-adresli, §10.1). `ExecuteDelete` tek ifadede
            // gidiyor ve değişiklik izleyicisine dokunmuyor —
            // süpürücü beş yüz varlığı izlemek zorunda kalmasın.
            await db.Assets
                .Where(a => a.Sha256 == asset.Sha256)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);

            deleted++;
            freed += asset.Bytes;
        }

        return new SweepResult(candidates.Count, deleted, freed, failed);
    }

    /// Yayınlanmış koşuların ürettiği varlıkların sha256 kümesi.
    ///
    /// KOŞU DURUMUNDAN GİDİLİYOR, `publications` tablosundan değil:
    /// yayın kaydı yalnızca nihai videoyu gösteriyor, oysa yayınlanmış
    /// bir koşunun ara ürünleri de o videonun yeniden üretilebilmesi
    /// için gerekiyor.
    private async Task<HashSet<string>> PublishedAssetsAsync(CancellationToken cancellationToken)
    {
        // ***YAYIN KAYDI AYRI BİR TABLODA DEĞİL:*** yayın, başarıyla
        // koşmuş bir `publish.upload` node'u. Ayrı bir tablo olsaydı
        // yayın anında İKİ YERE yazmak gerekirdi ve biri unutulduğunda
        // yayınlanmış bir videonun varlıkları silinebilir sayılırdı —
        // silme kararında en pahalı hata türü.
        var runIds = db.NodeExecutions.AsNoTracking()
            .Where(e => e.NodeType == "publish.upload" && e.State == Core.Execution.NodeState.Succeeded)
            .Select(e => e.RunId);

        var outputs = await db.NodeExecutions.AsNoTracking()
            .Where(e => runIds.Contains(e.RunId) && e.OutputJson != null)
            .Select(e => e.OutputJson!)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var json in outputs)
        {
            foreach (var hash in AssetHashes(json))
            {
                hashes.Add(hash);
            }
        }

        return hashes;
    }

    /// Bir JSON belgesindeki bütün `sha256:...` referanslarını bulur.
    ///
    /// ***METİN TARAMASI, ALAN ADIYLA DEĞİL.*** Node çıktılarında
    /// varlık referansı onlarca farklı alan adı altında duruyor
    /// (`timeline_asset`, `asset`, `thumbnail`, `images[].asset`…) ve
    /// bir alan adı listesi tutmak, yeni bir node eklendiğinde onun
    /// varlıklarının sessizce silinebilir sayılması demekti — silme
    /// kararında en pahalı hata türü.
    internal static IEnumerable<string> AssetHashes(string json)
    {
        const string prefix = "sha256:";
        var index = json.IndexOf(prefix, StringComparison.Ordinal);

        while (index >= 0)
        {
            var start = index + prefix.Length;
            var end = start;

            while (end < json.Length && Uri.IsHexDigit(json[end]))
            {
                end++;
            }

            // TAM 64 HANE: kısa bir eşleşme bir varlık referansı değil
            // ve onu saymak, alakasız bir varlığı "yayınlanmış"
            // sayarak sonsuza kadar saklamak olurdu.
            if (end - start == 64)
            {
                yield return json[start..end];
            }

            index = json.IndexOf(prefix, end, StringComparison.Ordinal);
        }
    }
}
