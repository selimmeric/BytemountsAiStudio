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

    public RunState State { get; set; } = RunState.Pending;

    public int Priority { get; set; }

    /// Node çıktılarının hızlı erişim görünümü. Kanonik kayıt
    /// `node_executions`; burası ondan türetilir.
    public string ContextJson { get; set; } = "{}";

    public decimal EstimatedCost { get; set; }

    public decimal ActualCost { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public string? ErrorJson { get; set; }

    public ICollection<NodeExecution> NodeExecutions { get; set; } = [];
}

public sealed class NodeExecution : EntityBase
{
    public Guid RunId { get; set; }

    public Run? Run { get; set; }

    public required string NodeId { get; set; }

    public required string NodeType { get; set; }

    public int Attempt { get; set; }

    public NodeState State { get; set; } = NodeState.Pending;

    /// ADR-010: aynı anahtarla ikinci kez çalışma API'ye gitmez.
    public required string IdempotencyKey { get; set; }

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
