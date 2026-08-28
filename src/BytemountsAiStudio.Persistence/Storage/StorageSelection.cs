using Amazon.Runtime;
using Amazon.S3;
using BytemountsAiStudio.Contracts.Providers;

namespace BytemountsAiStudio.Persistence.Storage;

/// Hangi varlık deposu kullanılacak (P4-02).
///
/// TEK KARAR NOKTASI. Worker, API ve CLI aynı seçimi yapmalı; üç
/// yerde ayrı `if` yazmak, birinin S3'e diğerinin dosya sistemine
/// bakması demekti — ve bu depoda tam olarak o hata (CLI ile Worker'ın
/// farklı kurulması) bir günü götürdü.
///
/// SEÇİM ORTAM DEĞİŞKENİNDEN: `BMAI_S3_ENDPOINT` doluysa nesne
/// deposu, boşsa dosya sistemi. Varsayılanın dosya sistemi olması
/// bilinçli — `dotnet run` yapan birinin çalışan bir MinIO'ya ihtiyacı
/// olmamalı.
public static class StorageSelection
{
    /// Nesne deposu yapılandırılmış mı.
    public static bool UsesObjectStore
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BMAI_S3_ENDPOINT"));

    public static string Bucket
        => Environment.GetEnvironmentVariable("BMAI_S3_BUCKET") ?? "bmai-varliklar";

    /// Depoyu kurar. `storageRoot` dosya sistemi kökü ya da nesne
    /// deposunun yerel önbelleği olarak kullanılıyor.
    ///
    /// İKİSİ DE AYNI KÖKÜ KULLANIYOR ve bu bilinçli: nesne deposuna
    /// geçen bir kurulumda eski dosyalar aynı yerde duruyor, yani
    /// geçiş sırasında yerelde bulunan bir varlık için ağa çıkmaya
    /// gerek kalmıyor.
    public static IStorageProvider Build(StudioDbContext db, string storageRoot)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (!UsesObjectStore)
        {
            return new FileSystemAssetStore(db, storageRoot);
        }

        return new S3AssetStore(CreateClient(), db, Bucket, storageRoot);
    }

    /// Depoyu kullanıma HAZIRLAR — açılışta bir kez.
    ///
    /// GERÇEK BİR KOŞUDA ÖĞRENİLDİ: `EnsureBucketAsync` yazılmıştı ama
    /// hiçbir yerden çağrılmıyordu. Sistem sorunsuz başladı, ilk
    /// seslendirme dosyasını yazmaya çalıştı ve "The specified bucket
    /// does not exist" ile düştü — üstelik geçici sayılıp üç kez.
    /// Bu depoda tekrar eden sınıf: yazıldı, bağlanmadı.
    ///
    /// AÇILIŞTA HAZIRLAMAK, hatayı ilk videodan önce ve tek bir
    /// yerde gösteriyor. İlk yazmaya bırakmak, aynı hatayı her
    /// kanalda ayrı ayrı ve üretimin ortasında görmek demekti.
    ///
    /// Dosya sistemi deposunda yapacak bir şey yok: dizinler
    /// yazarken oluşturuluyor.
    public static async Task<Core.Result> EnsureReadyAsync(
        IStorageProvider storage, CancellationToken cancellationToken)
        => storage is S3AssetStore s3
            ? await s3.EnsureBucketAsync(cancellationToken).ConfigureAwait(false)
            : Core.Result.Success();

    /// S3 istemcisi — MinIO, R2 ve gerçek S3 için aynı.
    public static IAmazonS3 CreateClient()
    {
        var endpoint = Environment.GetEnvironmentVariable("BMAI_S3_ENDPOINT")
            ?? throw new InvalidOperationException("BMAI_S3_ENDPOINT tanımlı değil.");

        var config = new AmazonS3Config
        {
            ServiceURL = endpoint,

            // YOL BİÇİMİ ZORUNLU: varsayılan `kova.host` biçimi MinIO
            // ve çoğu S3 uyumlu deponun yerel kurulumunda
            // çözümlenmiyor; istekler DNS hatasıyla düşerdi.
            ForcePathStyle = true,

            // Bölge adı S3 uyumlu depolarda anlamsız ama imza
            // hesabında kullanılıyor; boş bırakmak imzayı geçersiz
            // kılıyor.
            AuthenticationRegion = Environment.GetEnvironmentVariable("BMAI_S3_REGION") ?? "us-east-1",

            // SAGLAMA TOPLAMI YALNIZCA GEREKTIGINDE.
            //
            // AWS SDK v4 varsayilan olarak her istege ek bir saglama
            // toplami ekliyor ve S3 UYUMLU depolarin bir kismi bunu
            // anlamiyor: MinIO'nun CI surumu istekleri
            // "the provided 'x-amz-content-sha256' header does not
            // match what was computed" ile reddediyordu. Yerel MinIO
            // surumu kabul ediyordu -- yani hata yalnizca bir surumde
            // gorunuyordu.
            //
            // BUTUNLUK KAYBI DEGIL: yuklenen icerik zaten sha256 ile
            // adreslenmis ve indirdikten sonra ayni ozetle
            // dogrulanabiliyor. Kaldirilan sey ikinci, tasima
            // seviyesindeki bir kontrol.
            //
            // Bu ayni zamanda tasinabilirlik: R2 ve diger S3 uyumlu
            // depolar da yeni varsayilanla sorun cikariyor.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };

        var accessKey = Environment.GetEnvironmentVariable("BMAI_S3_ACCESS_KEY");
        var secretKey = Environment.GetEnvironmentVariable("BMAI_S3_SECRET_KEY");

        // ANAHTAR YOKSA VARSAYILAN ZİNCİR: bulut ortamında kimlik rol
        // üzerinden geliyor ve ortam değişkenine anahtar yazmak
        // gerekmiyor. Uydurma bir anahtarla devam etmek, hatayı ilk
        // yazma anına ertelemek olurdu.
        return string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey)
            ? new AmazonS3Client(config)
            : new AmazonS3Client(accessKey, secretKey, config);
    }
}
