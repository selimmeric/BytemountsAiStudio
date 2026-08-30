using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Execution;
using Pgvector;

namespace BytemountsAiStudio.Persistence.Entities;

/// Kalıcılık modeli.
///
/// Neden Core'da değil: Core hiçbir şeye bağımlı olmamalı (AssemblyMarker
/// kuralı), oysa gömme vektörü için Pgvector, JSONB için Npgsql gerekiyor.
/// Ayrıca depolama modeli ile domain modeli aynı şey değil — `runs.context`
/// JSONB'si bir domain kavramı değil, bir erişim kolaylığı.
///
/// Kimlikler UUIDv7: zaman sıralı olduğu için birincil anahtar indeksinde
/// sayfa parçalanması olmaz. Rastgele UUID'de her ekleme indeksin farklı bir
/// yerine düşer ve tablo büyüdükçe yazma yavaşlar.
public abstract class EntityBase
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Channel : EntityBase
{
    public required string Name { get; set; }

    public required string Language { get; set; }

    public ChannelMode Mode { get; set; } = ChannelMode.Approval;

    public bool IsPaused { get; set; }

    /// Ses, görsel stil, model katmanları, yayın takvimi — hepsi tek belge.
    /// Ayrı tablolara bölmek her ayar eklendiğinde şema göçü gerektirirdi;
    /// bunlar üzerinde sorgu yazmıyoruz, okuyup uyguluyoruz.
    public string SettingsJson { get; set; } = "{}";

    public decimal? DailyBudget { get; set; }

    public decimal? MaxCostPerVideo { get; set; }
}

public sealed class Topic : EntityBase
{
    public Guid? ChannelId { get; set; }

    public Channel? Channel { get; set; }

    public required string Title { get; set; }

    public required string Language { get; set; }

    public string? Angle { get; set; }

    /// Altı boyutlu skor. Tek tek kolon yapmak yerine belge: skorlama
    /// boyutları değişecek ve her değişiklik şema göçü olmamalı.
    public string ScoresJson { get; set; } = "{}";

    /// Sıralama için ayrı kolon — JSONB içinden sıralamak indekslenemezdi.
    public double OverallScore { get; set; }

    /// ADR-003 ve §20.5: tekillik embedding benzerliğiyle çözülür.
    ///
    /// 768 boyut bilinçli: yerel modeller (nomic-embed-text, multilingual-e5)
    /// bu boyutta çalışıyor ve OpenAI'nin modelleri `dimensions` parametresiyle
    /// 768'e indirilebiliyor. 1536 seçseydik yerel model kullanılamazdı — bu da
    /// ADR-015'in (ücretsiz/yerel önce) altını oyardı.
    public Vector? Embedding { get; set; }

    public TopicState State { get; set; } = TopicState.New;

    public string? RejectedReason { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum TopicState
{
    New = 0,
    Queued = 1,
    InProgress = 2,
    Published = 3,
    Failed = 4,
    Rejected = 5,
}

public sealed class Workflow : EntityBase
{
    public required string Key { get; set; }

    public required string Name { get; set; }

    public ContentKind ContentKind { get; set; }

    public Guid? ChannelId { get; set; }

    public int CurrentVersion { get; set; }

    public ICollection<WorkflowVersion> Versions { get; set; } = [];
}

public sealed class WorkflowVersion : EntityBase
{
    public Guid WorkflowId { get; set; }

    public Workflow? Workflow { get; set; }

    public int Version { get; set; }

    /// Graf JSONB olarak. §10.1: ayrı `workflow_nodes` tablosu yok — node'lar
    /// üzerinde sorgu ihtiyacı yok, join maliyeti karşılıksız kalırdı.
    public required string GraphJson { get; set; }

    public ICollection<Run> Runs { get; set; } = [];
}

public sealed class Run : EntityBase
{
    public Guid WorkflowVersionId { get; set; }

    public WorkflowVersion? WorkflowVersion { get; set; }

    public Guid? ChannelId { get; set; }

    public Guid? TopicId { get; set; }

    /// Bu koşu hangi koşudan türetildi (P6-06).
    ///
    /// Çok dilli türevde iki koşu aynı araştırmayı paylaşıyor ama ayrı
    /// videolar üretiyor. Bağ olmadan "bu konunun hangi dillerde
    /// sürümü var" sorusu cevaplanamıyor ve iki koşu birbirinden
    /// bağımsız görünüyor.
    public Guid? DerivedFromRunId { get; set; }

    public RunState State { get; set; } = RunState.Pending;

    public int Priority { get; set; }

    /// Node çıktılarının hızlı erişim görünümü. Kanonik kayıt
    /// `node_executions`; burası ondan türetilir.
    public string ContextJson { get; set; } = "{}";

    /// Kaçıncı düzeltme turundayız (P2-07).
    ///
    /// QC düşen bir videoyu hedefli olarak yeniden koşturuyor ve o
    /// koşuda AYNI node'lar bir kez daha çalışıyor. Tur numarası
    /// olmadan ikinci çalıştırma, `node_executions`'daki
    /// (run, node, attempt) eşsiz kısıtını ihlal ediyor ve run
    /// çöküyor — yani hedefli retry, tur numarası olmadan hiç
    /// çalışamazdı.
    public int RetryLoop { get; set; }

    /// ***BU KOLON NE YAZILIYOR NE OKUNUYOR ve bu YAZILI duruyor.***
    ///
    /// Amaci "kosu baslamadan once tahmin edilen maliyet"ti; tahmin
    /// hicbir zaman uretilmedi. Butce kapisi da bugun sifir tahminle
    /// calisiyor ve sebebi `PipelineDecorators` icinde yazili: yanlis
    /// bir tahmin, tahmin olmamasindan kotu.
    ///
    /// KALDIRILMADI cunku kaldirmak bir goc dosyasi gerektiriyor ve
    /// kolonun gelecekte bir karsiligi olabilir. Ama "doluyor" diye
    /// okunmasin diye burada soyleniyor -- `ActualCost` DOLUYOR,
    /// bu DOLMUYOR.
    public decimal EstimatedCost { get; set; }

    public decimal ActualCost { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public string? ErrorJson { get; set; }

    public ICollection<NodeExecution> NodeExecutions { get; set; } = [];
}

public sealed class NodeExecution : EntityBase
{
    /// Hangi düzeltme turunda çalıştı (P2-07).
    ///
    /// Eşsizlik (run, node, TUR, deneme) üzerinden: "senaryoyu ikinci
    /// kez ürettik" ile "senaryoyu üretmeyi ikinci kez denedik"
    /// gerçekten farklı şeyler ve ikisini aynı sayıya sıkıştırmak,
    /// hedefli retry'ı imkânsız kılardı.
    public int Loop { get; set; }

    public Guid RunId { get; set; }

    public Run? Run { get; set; }

    public required string NodeId { get; set; }

    public required string NodeType { get; set; }

    public int Attempt { get; set; }

    public NodeState State { get; set; } = NodeState.Pending;

    /// ADR-010: aynı anahtarla ikinci kez çalışma API'ye gitmez.
    public required string IdempotencyKey { get; set; }

    /// Bu adımı HANGİ WORKER çalıştırdı (P4-01).
    ///
    /// Tek makineli kurulumda gereksiz görünüyordu; render worker'ları
    /// ayrı makineye çıkınca "bu videoyu hangi makine üretti" gerçek
    /// bir soru oldu ve cevabı hiçbir yerde yoktu. `jobs.leased_by`
    /// var ama iş bitince temizleniyor, yani tam da soruyu sorduğunuz
    /// anda kayıp.
    ///
    /// Bir makine bozuk çıktı üretmeye başladığında (eski ffmpeg,
    /// eksik yazı tipi, dolu disk) ayıran tek şey bu alan olabiliyor.
    public string? WorkerId { get; set; }

    public string? OutputJson { get; set; }

    public decimal Cost { get; set; }

    public int DurationMs { get; set; }

    public string? ErrorJson { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }
}

public sealed class RunEvent : EntityBase
{
    public Guid RunId { get; set; }

    public string? NodeId { get; set; }

    public required string Level { get; set; }

    public required string Message { get; set; }

    /// ***BU KOLON NE YAZILIYOR NE OKUNUYOR (30 Ağu 2026).***
    ///
    /// Olay kaydına yapılandırılmış ek veri taşıması için açılmıştı;
    /// hiçbir yerden doldurulmadı. Bugün olayın taşıdığı her şey
    /// `Message` içinde düz metin olarak duruyor.
    ///
    /// KALDIRILMADI çünkü kaldırmak bir göç dosyası gerektiriyor ve
    /// `run_events` **bölümlenmiş** bir tablo — kolon düşürmek her
    /// bölümü dolaşıyor. Ama "doluyor" sanılmasın diye burada
    /// söyleniyor: bu alana bakan bir sorgu her satırda `null`
    /// bulur.
    public string? DataJson { get; set; }
}

public sealed class Job : EntityBase
{
    public QueueClass Queue { get; set; }

    public Guid? RunId { get; set; }

    public string? NodeId { get; set; }

    public Guid? ChannelId { get; set; }

    public int Priority { get; set; }

    /// Kanal başına adil dağıtım anahtarı. Tek kuyrukta bir kanalın
    /// diğerlerini aç bırakmasını engeller (§8.2).
    public string? FairKey { get; set; }

    public string PayloadJson { get; set; } = "{}";

    public JobState State { get; set; } = JobState.Pending;

    public int Attempt { get; set; }

    public int MaxAttempts { get; set; } = 3;

    /// Bu andan önce alınamaz. Backoff ve kaynak ertelemesi bununla yapılır —
    /// ertelenen iş "başarısız" değil, ileri tarihli.
    public DateTimeOffset RunAfter { get; set; } = DateTimeOffset.UtcNow;

    public string? LeasedBy { get; set; }

    /// Kiralamanın son geçerlilik anı. Worker çökerse bu an geçer ve iş
    /// yeniden dağıtılır — kurtarma mekanizmasının tamamı bu (§8.2).
    public DateTimeOffset? LeaseExpiresAt { get; set; }

    public string? LastError { get; set; }

    /// İş ne zaman bitti (başarıyla ya da başarısızlıkla).
    ///
    /// Kanal adaleti (P2-05) buna bakıyor: "yakın geçmişte kaç iş
    /// aldı" ölçütü, biten işleri saymadan hesaplanamıyor ve o ölçüt
    /// olmadan işler hızlı bittiğinde adalet tamamen bozuluyor —
    /// koşan sayısı hep sıfır kalıyor ve seçim kimlik sırasına
    /// düşüyor.
    ///
    /// `lease_expires_at` bu iş için kullanılamazdı: tamamlanınca
    /// temizleniyor ve zaten kiralamanın bitişini değil son
    /// geçerlilik anını taşıyor.
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class Asset
{
    /// İçerik-adresli: birincil anahtar sha256'nın kendisi.
    /// Aynı görsel kırk videoda kullanılsa tek satır (§10.1).
    public required string Sha256 { get; set; }

    public required string Kind { get; set; }

    public required string MimeType { get; set; }

    public long Bytes { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    public int? DurationMs { get; set; }

    public required string StoragePath { get; set; }

    public string? SourceProvider { get; set; }

    public string? SourceUrl { get; set; }

    /// §2.3/14: lisans bir metadata değil, uyum kaydı. Alındığı andaki hâliyle
    /// saklanır — kurallar sonradan değişir, kanıt değişmemeli.
    public string? LicenseJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ProviderCall : EntityBase
{
    public Guid? RunId { get; set; }

    public string? NodeId { get; set; }

    public required string ProviderKey { get; set; }

    public required string Operation { get; set; }

    /// Tüketilen birimler (token/karakter/görsel/saniye). Maliyet buradan
    /// TÜRETİLİR, burada saklanmaz: fiyat zamanla değişir, birim değişmez.
    public string UnitsJson { get; set; } = "{}";

    public decimal Cost { get; set; }

    public int LatencyMs { get; set; }

    public int? HttpStatus { get; set; }

    public bool Succeeded { get; set; }
}

/// Şifrelenmiş API anahtarı (§16, P1-01).
///
/// Gizli değer burada ŞİFRELİ duruyor — `CipherText`. Düz metin saklamak,
/// veritabanı yedeğini alan herkese bütün hesapları vermek demekti; yedekler
/// de çoğu zaman kod deposundan daha az korunuyor.
///
/// Şifreleme anahtarı veritabanında değil, ASP.NET Data Protection'ın anahtar
/// halkasında. İkisi ayrı yerde durmadıkça şifrelemenin bir anlamı olmazdı.
public sealed class Credential : EntityBase
{
    /// null = genel kayıt; bütün kanallar için geçerli.
    public Guid? ChannelId { get; set; }

    public Channel? Channel { get; set; }

    /// `config/providers.json` içindeki `key` ile aynı değer.
    public required string ProviderKey { get; set; }

    /// Havuzdaki hesabın adı (P4-04).
    ///
    /// AYNI SAĞLAYICI İÇİN BİRDEN FAZLA HESAP: YouTube günlük 10.000
    /// birim veriyor ve bir yükleme 1.600 — proje başına günde altı
    /// video. Günde 100 video hedefi on yedi proje istiyor ve tek
    /// kayıtlı bir kimlik bunu ifade edemiyordu.
    ///
    /// Varsayılan `default`: tek hesaplı kurulumda hiçbir şey
    /// değişmiyor.
    public string Account { get; set; } = "default";

    public required string CipherText { get; set; }

    /// Son dört karakter, maskeli. Arayüzde göstermek için — bu alanı
    /// okumak anahtarın çözülmesini gerektirmiyor.
    public required string Masked { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// Kullanılmayan anahtarı görmek için: dönmesi gerekirken unutulmuş
    /// ya da hiç devreye girmemiş kayıtlar buradan ayırt ediliyor.
    public DateTimeOffset? LastUsedAt { get; set; }
}

/// Araştırmada kullanılan bir kaynak (P1-11, §2.3).
///
/// İÇERİK ÖZETİYLE tekilleştiriliyor, adresle değil: aynı sayfa iki
/// farklı adresten gelebiliyor (yönlendirme, izleme parametreleri) ve
/// aynı içeriği iki kez saklamak "bu videonun kaç kaynağı var"
/// sorusunun cevabını bozardı.
///
/// Metin BURADA DEĞİL: bir Wikipedia makalesi 50 KB ve kaynak tablosu
/// hızlı sorgulanacak. Tam metin gerekirse varlık deposunda.
public sealed class Source : EntityBase
{
    public required string Url { get; set; }

    public required string Title { get; set; }

    /// Encyclopedia / Official / Academic / News / Community / Blog.
    /// Güven skoru ve QC kuralları buna bakıyor.
    public required string SourceType { get; set; }

    /// İçeriğin sha256'sı. Tekillik anahtarı ve "kaynak değişmiş mi"
    /// sorusunun cevabı.
    public required string ContentHash { get; set; }

    /// Modele giden özet. Tam metin değil.
    public string? Excerpt { get; set; }

    public int ContentLength { get; set; }

    /// Kaynağın güvenilirliği (0–1). Şimdilik türden türetiliyor;
    /// P5'te gerçek performansla kalibre edilecek.
    /// ***YAZILIYOR AMA HİÇBİR YERDE OKUNMUYOR (30 Ağu 2026).***
    ///
    /// `KnowledgeBase` kaynağın türüne göre bir güven puanı hesaplayıp
    /// yazıyor (resmî API > ansiklopedi > serbest web) ve **hiçbir
    /// karar buna bakmıyor**: QC'nin böyle bir girdisi yok, iddia
    /// doğrulama kaynak türünü ayırmıyor.
    ///
    /// Amacı belli ve hâlâ geçerli: düşük güvenli bir kaynağa dayanan
    /// iddia, yüksek güvenliye dayanandan farklı ağırlık taşımalı.
    /// Ama o kural yazılmadan puanın kendisi bir karara dönüşmüyor —
    /// ve "hesaplanıyor" ile "kullanılıyor" farkı tam da bu depoda
    /// defalarca karıştırıldı.
    public double TrustScore { get; set; }

    public DateTimeOffset FetchedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// Senaryodan çıkarılmış ve kaynağa karşı doğrulanmış bir iddia
/// (P1-10/11, §2.2/8).
///
/// Run'a bağlı, kanala değil: aynı iddia farklı run'larda farklı
/// kaynaklarla doğrulanabilir ve hangi videonun neye dayandığı
/// sorusunun cevabı run düzeyinde.
public sealed class ClaimRecord : EntityBase
{
    public Guid RunId { get; set; }

    public Run? Run { get; set; }

    public required string Text { get; set; }

    /// Senaryodaki cümle sırası. Hedefli düzeltme (P2-07) buna bakıyor.
    public int SentenceIndex { get; set; }

    /// Supported / Unsupported / Contradicted.
    public required string Verdict { get; set; }

    /// Doğrulamada kullanılan kaynak. Null = eşleştirilemedi.
    public Guid? SourceId { get; set; }

    public Source? Source { get; set; }

    /// Modelin gerekçesi. İnsan onayı ekranında gösteriliyor.
    public string? Reason { get; set; }

    /// Doğrulama, çıkarımla AYNI modelden mi geldi. Aynıysa sonuç
    /// iyimser olma eğiliminde; bu bilgi olmadan skora fazla
    /// güvenilirdi.
    public bool SameModel { get; set; }
}

/// Bir onay isteğinin durumu (P1-27).
public enum ApprovalState
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
}

/// İnsan onayı isteği (P1-27, §22).
///
/// Ayrı bir tablo, `runs` üzerinde bir bayrak DEĞİL. Sebebi onay
/// kuyruğunun kendisi: "bekleyen onaylar" bir liste ekranı ve o listenin
/// sorgusu run tablosunu taramamalı. Ayrıca karar KAYIT: kim, ne zaman,
/// hangi gerekçeyle onayladı — bir video sorun çıkardığında ilk
/// sorulacak şey bu.
public sealed class Approval : EntityBase
{
    public Guid RunId { get; set; }

    public Run? Run { get; set; }

    /// Hangi node park etti. Onay verilince buradan sonrası kuyruğa
    /// giriyor; bilinmezse run nerede devam edeceğini bilemezdi.
    public required string NodeId { get; set; }

    public ApprovalState State { get; set; } = ApprovalState.Pending;

    /// Neden insana soruldu. Panelde bakan kişinin göreceği ilk şey.
    public required string Reason { get; set; }

    /// Kararı veren. Otomatik geçilen kapılarda bu kayıt hiç oluşmuyor —
    /// yalnızca gerçekten bir insanın baktığı kararlar burada.
    public string? DecidedBy { get; set; }

    /// Reddetme gerekçesi ya da onay notu. Öğrenen sistemin (Faz 5)
    /// besleneceği yer: "neden reddedildi" verisi olmadan model
    /// iyileştirilemez.
    /// ***UZUN SÜRE YAZILIP GERİ OKUNMUYORDU (30 Ağu 2026'da kapatıldı).***
    ///
    /// Gerekçe buraya giriyordu ve hiçbir ekran, rapor ya da sorgu onu
    /// geri göstermiyordu: bir insan "bu videoyu şu yüzden reddettim"
    /// yazıyor ve o cümle bir daha kimsenin karşısına çıkmıyordu.
    ///
    /// Kaydın kendisi doğruydu; eksik olan GÖSTERİMDİ. Artık koşu
    /// detayı (`GET /runs/{id}` → `approvals`) kararı, karar vereni ve
    /// bu notu döndürüyor. Bekleyen onay listesi DEĞİL: bekleyen bir
    /// onayın henüz notu yok.
    public string? Note { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }
}

/// Sistem geneli ayar (P2-04).
///
/// Anahtar/değer, çünkü ilk kullanıcısı tek bir bayrak (acil
/// durdurma) ve onun için ayrı bir tablo açmak, ikinci bayrak
/// geldiğinde ikinci bir şema göçü demekti.
///
/// VERİTABANINDA, bellekte DEĞİL. Acil durdurma statik bir alanken
/// yalnızca o süreci durduruyordu: filodaki diğer worker'lar hiçbir
/// şey görmüyor, yeniden başlatmada bayrak kayboluyordu — "tek tıkla
/// her şey dursun" sözünün karşılığı yoktu.
public sealed class Setting
{
    public required string Key { get; set; }

    public required string Value { get; set; }

    /// Kim değiştirdi ve neden. Acil durdurma gibi bir düğmede "kim
    /// bastı" sorusu ilk sorulacak şey.
    public string? UpdatedBy { get; set; }

    public string? Reason { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// Bir deney (P5-02).
///
/// TEK DEĞİŞKEN: `Dimension` neyin değiştiğini söylüyor (kapak,
/// başlık, istem sürümü). Aynı anda iki şey değiştiren bir deney
/// kazandığında hangisinin kazandırdığı bilinemez — ve bir sonraki
/// videoda yanlış olanı taşımak mümkün.
public sealed class Experiment : EntityBase
{
    public Guid? ChannelId { get; set; }

    public Channel? Channel { get; set; }

    /// Neyin değiştiği: `thumbnail`, `title`, `prompt`.
    public required string Dimension { get; set; }

    public required string Name { get; set; }

    /// Görmek istediğimiz MUTLAK fark (0,02 = iki puan).
    ///
    /// Deney BAŞLARKEN yazılıyor, sonuca bakılırken değil. Sonradan
    /// gevşetmek, "anlamlı olana kadar eşiği indir" demek olurdu.
    public double MinimumDetectableEffect { get; set; } = 0.02;

    /// Varyant başına gereken deneme — başlangıçta hesaplanıp
    /// SAKLANIYOR.
    ///
    /// Her bakışta yeniden hesaplamak, taban oran değiştikçe hedefin
    /// de kaymasi demekti: hedefi kaydırarak "yeterli veri"ye
    /// ulaşmak, kapıyı olduğu yere taşımak.
    public int RequiredPerVariant { get; set; }

    public string State { get; set; } = "Running";

    public string? Outcome { get; set; }

    public string? Reason { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }
}

/// Kota defterinin bir satırı: bir hesabın bir günü (P4-04).
///
/// ADI `QuotaLedgerEntry`, `QuotaReservation` DEĞİL: `Core.Execution`
/// içinde aynı adla saf bir KARAR kaydı var ve ikisini aynı adla
/// anmak, "rezervasyon isteği" ile "defterdeki satır" arasındaki farkı
/// silerdi.
///
/// REZERVASYON, HARCAMA DEĞİL. İkisi ayrı çünkü arada iş var: rezerve
/// edilen kota yükleme BAŞLAMADAN önce düşülüyor, gerçek harcama sonra
/// oluyor. Yalnızca gerçekleşeni saymak, aynı anda başlayan iki
/// yüklemenin ikisinin de "yer var" görmesi demekti.
///
/// SATIR BAŞINA BİR GÜN: gün anahtarı PASİFİK tarihi, çünkü YouTube
/// kotayı Pasifik gece yarısında sıfırlıyor.
public sealed class QuotaLedgerEntry : EntityBase
{
    public required string ProviderKey { get; set; }

    /// Havuzdaki hesap adı.
    public required string Account { get; set; }

    /// `yyyy-MM-dd`, Pasifik tarihi.
    public required string DayKey { get; set; }

    /// Bugüne kadar rezerve edilen birim.
    public int ReservedUnits { get; set; }
}

/// Bir deneyin varyantı.
public sealed class ExperimentVariant : EntityBase
{
    public Guid ExperimentId { get; set; }

    public Experiment? Experiment { get; set; }

    public required string Name { get; set; }

    /// Kontrol varyantı — karşılaştırmanın temeli.
    public bool IsControl { get; set; }

    /// Bu varyantın node ayarlarına kattığı fark.
    public string ConfigJson { get; set; } = "{}";
}

/// Hangi run hangi varyantı aldı.
///
/// AYRI BİR TABLO, `runs` üzerinde bir kolon değil: bir run birden
/// fazla deneye katılabiliyor (kapak deneyi ve başlık deneyi aynı
/// anda koşabilir, çünkü farklı boyutlar).
public sealed class ExperimentAssignment : EntityBase
{
    public Guid ExperimentId { get; set; }

    public Experiment? Experiment { get; set; }

    public Guid VariantId { get; set; }

    public ExperimentVariant? Variant { get; set; }

    public Guid RunId { get; set; }

    public Run? Run { get; set; }
}

/// Yayınlanmış bir videonun günlük ölçümü (P5-01/P5-02).
///
/// ZAMAN SERİSİ, tek satır değil: bir videonun ilk gün ile yedinci
/// gün performansı farklı ve "hangi kapak daha iyi" sorusu ancak aynı
/// yaştaki videoları karşılaştırarak cevaplanıyor. Tek bir toplam
/// tutmak, bir haftalık videoyla bir aylık videoyu yan yana koymak
/// olurdu.
public sealed class PublicationMetric : EntityBase
{
    public Guid RunId { get; set; }

    public Run? Run { get; set; }

    /// Yayından sonraki kaçıncı gün (0 = yayın günü).
    public int DayOffset { get; set; }

    public int Impressions { get; set; }

    public int Clicks { get; set; }

    public int Views { get; set; }

    public long WatchSeconds { get; set; }

    /// Ölçümün alındığı an — aynı günün iki kez çekilmesini ayırt
    /// etmek için.
    public DateTimeOffset MeasuredAt { get; set; } = DateTimeOffset.UtcNow;
}
