using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Persistence;

public class StudioDbContext : DbContext
{
    public StudioDbContext(DbContextOptions<StudioDbContext> options)
        : base(options)
    {
    }

    /// Türetilmiş bağlamlar için (P4-06).
    ///
    /// `ReadOnlyDbContext` bu sınıftan türüyor ki panel sorguları
    /// DEĞİŞMEDEN replikaya yönlendirilebilsin. Ayrı bir DbSet
    /// arayüzü çıkarmak, aynı sorguların iki biçimde yazılması
    /// demekti — ve ikisi zamanla ayrışırdı.
    ///
    /// `base(options)`, `this(...)` DEĞİL. İlk yazımda birincil
    /// kurucuya zincirlemek zorunda kaldım ve `DbContextOptions`'ı
    /// `DbContextOptions<StudioDbContext>`'e cast ettim — DERLENİYOR
    /// ama çalışma zamanında `DbContextOptions<ReadOnlyDbContext>`
    /// geldiğinde atardı. Derlenen ama olamayacak bir dönüşüm, en
    /// geç anda patlayan hata türü. Birincil kurucudan vazgeçmek
    /// bunun bedeli.
    ///
    /// SINIF ARTIK `sealed` DEĞİL: tek türetilmiş tip depoda ve onun
    /// tek işi bağlantıyı değiştirmek.
    protected StudioDbContext(DbContextOptions options)
        : base(options)
    {
    }

    public DbSet<Channel> Channels => Set<Channel>();

    public DbSet<Topic> Topics => Set<Topic>();

    public DbSet<Workflow> Workflows => Set<Workflow>();

    public DbSet<WorkflowVersion> WorkflowVersions => Set<WorkflowVersion>();

    public DbSet<Run> Runs => Set<Run>();

    public DbSet<NodeExecution> NodeExecutions => Set<NodeExecution>();

    public DbSet<RunEvent> RunEvents => Set<RunEvent>();

    public DbSet<Job> Jobs => Set<Job>();

    public DbSet<Asset> Assets => Set<Asset>();

    public DbSet<ProviderCall> ProviderCalls => Set<ProviderCall>();

    public DbSet<Credential> Credentials => Set<Credential>();

    public DbSet<Source> Sources => Set<Source>();

    public DbSet<ClaimRecord> Claims => Set<ClaimRecord>();

    public DbSet<Approval> Approvals => Set<Approval>();

    public DbSet<Setting> Settings => Set<Setting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => ApplyModel(modelBuilder);

    /// Model tanımı AYRI BİR METOTTA (P4-06).
    ///
    /// Okuma replikası bağlamı (`ReadOnlyDbContext`) aynı modeli
    /// kullanıyor. Kopyalamak, replika şemasının birincilden
    /// ayrışması demekti — oysa replika birincilin bayt kopyası;
    /// ayrışması mümkün bile değil. Kopyalanmış bir tanım yalnızca
    /// güncellenmeyi unutulacak ikinci bir yer olurdu.
    internal static void ApplyModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var b = modelBuilder;

        b.HasPostgresExtension("vector");
        b.HasPostgresExtension("pg_trgm");

        // ---- KİMLİĞİ UYGULAMA ÜRETİYOR, VERİTABANI DEĞİL ----
        //
        // `EntityBase.Id` C#'ta UUIDv7 ile doluyor ve tablolarda hiçbir
        // varsayılan yok. EF ise Guid anahtarları varsayılan olarak
        // "depo üretir" (`ValueGeneratedOnAdd`) sayıyor ve bu, sessiz
        // bir hataya yol açıyordu:
        //
        // Bir varlık İZLENEN bir gezinme koleksiyonuna eklendiğinde
        // (`workflow.Versions.Add(...)`) EF, Added mı Modified mı
        // olduğuna anahtarın dolu olup olmamasına bakarak karar
        // veriyor. Anahtar zaten dolu olduğu için "bu kayıt var" deyip
        // INSERT yerine UPDATE üretiyordu — ve o UPDATE hiçbir satırı
        // etkilemediği için `DbUpdateConcurrencyException` fırlatıyordu.
        //
        // `db.Set<T>().Add(...)` ile eklenen her yerde sorun yoktu
        // (durum açıkça veriliyor), bu yüzden bütün testler geçiyordu.
        // Tohumlama gerçek bir veritabanında ilk kez koşturulduğunda
        // ortaya çıktı.
        foreach (var entity in b.Model.GetEntityTypes()
                     .Where(e => typeof(EntityBase).IsAssignableFrom(e.ClrType)))
        {
            entity.FindProperty(nameof(EntityBase.Id))?.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
        }

        // Enum'lar metin olarak saklanır. Sayı olsaydı enum sırasını değiştiren
        // bir refactor veritabanındaki anlamı sessizce kaydırırdı; ayrıca
        // psql'den bakan insan ne olduğunu göremezdi.
        b.Entity<Channel>(e =>
        {
            e.Property(x => x.Mode).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Language).HasMaxLength(16);
            e.Property(x => x.SettingsJson).HasColumnType("jsonb");
            e.Property(x => x.DailyBudget).HasPrecision(12, 4);
            e.Property(x => x.MaxCostPerVideo).HasPrecision(12, 4);
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<Topic>(e =>
        {
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Title).HasMaxLength(500);
            e.Property(x => x.Language).HasMaxLength(16);
            e.Property(x => x.ScoresJson).HasColumnType("jsonb");
            e.Property(x => x.Embedding).HasColumnType("vector(768)");

            // Sıradaki konuyu seçen sorgu: durum + skor. Kısmi indeks, çünkü
            // yayınlanmış konular bu sorguya hiç girmiyor ve indeksi şişirirdi.
            e.HasIndex(x => new { x.State, x.OverallScore })
                .HasFilter("state IN ('New', 'Queued')")
                .IsDescending(false, true);

            // Tekillik kontrolü kanal + dil kapsamında (§20.5): TR'de yayınlanan
            // bir konu EN kanalında tekrar sayılmaz.
            e.HasIndex(x => new { x.ChannelId, x.Language });

            e.HasOne(x => x.Channel).WithMany().HasForeignKey(x => x.ChannelId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Workflow>(e =>
        {
            e.Property(x => x.Key).HasMaxLength(100);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.ContentKind).HasConversion<string>().HasMaxLength(32);
            e.HasIndex(x => x.Key).IsUnique();
        });

        b.Entity<WorkflowVersion>(e =>
        {
            e.Property(x => x.GraphJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.WorkflowId, x.Version }).IsUnique();
            e.HasOne(x => x.Workflow).WithMany(x => x.Versions)
                .HasForeignKey(x => x.WorkflowId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Run>(e =>
        {
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.ContextJson).HasColumnType("jsonb");
            e.Property(x => x.ErrorJson).HasColumnType("jsonb");
            e.Property(x => x.EstimatedCost).HasPrecision(12, 4);
            e.Property(x => x.ActualCost).HasPrecision(12, 4);

            // Panoda "çalışan run'lar" sorgusu; bitmişler indekste yer kaplamasın.
            e.HasIndex(x => new { x.State, x.CreatedAt })
                .HasFilter("state IN ('Pending', 'Running', 'WaitingApproval', 'WaitingResource')");

            e.HasOne(x => x.WorkflowVersion).WithMany(x => x.Runs)
                .HasForeignKey(x => x.WorkflowVersionId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<NodeExecution>(e =>
        {
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.NodeId).HasMaxLength(100);
            e.Property(x => x.NodeType).HasMaxLength(100);
            e.Property(x => x.IdempotencyKey).HasMaxLength(128);
            e.Property(x => x.OutputJson).HasColumnType("jsonb");
            e.Property(x => x.ErrorJson).HasColumnType("jsonb");
            e.Property(x => x.Cost).HasPrecision(12, 4);

            // Aynı node'un aynı denemesi iki kez yazılamaz. Bu, çift tetiklemeyi
            // uygulama katmanında değil veritabanında durdurur.
            // TUR de esssizlige dahil (P2-07): hedefli retry ayni
            // node'u ikinci bir turda calistiriyor ve deneme sayaci
            // yeni bir isle 1'den basliyor. Tur olmadan bu, kisit
            // ihlali ve cokme demekti.
            e.HasIndex(x => new { x.RunId, x.NodeId, x.Loop, x.Attempt }).IsUnique();
            e.HasIndex(x => x.IdempotencyKey);

            e.HasOne(x => x.Run).WithMany(x => x.NodeExecutions)
                .HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RunEvent>(e =>
        {
            e.Property(x => x.Level).HasMaxLength(16);
            e.Property(x => x.Message).HasMaxLength(2000);
            e.Property(x => x.NodeId).HasMaxLength(100);
            e.Property(x => x.DataJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.RunId, x.CreatedAt });
        });

        b.Entity<Job>(e =>
        {
            e.Property(x => x.Queue).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.PayloadJson).HasColumnType("jsonb");
            e.Property(x => x.NodeId).HasMaxLength(100);
            e.Property(x => x.FairKey).HasMaxLength(100);
            e.Property(x => x.LeasedBy).HasMaxLength(100);
            e.Property(x => x.LastError).HasMaxLength(4000);

            // Kuyruğun sıcak sorgusu: sınıfa göre bekleyen, zamanı gelmiş işler.
            // Kısmi indeks kritik — tablo milyonlarca bitmiş iş biriktirecek ama
            // bu indeks yalnızca bekleyenleri tutar.
            e.HasIndex(x => new { x.Queue, x.RunAfter, x.Priority })
                .HasFilter("state = 'Pending'");

            // Süpürücünün sorgusu: süresi dolmuş kiralamalar.
            e.HasIndex(x => x.LeaseExpiresAt).HasFilter("state = 'Leased'");
        });

        b.Entity<Asset>(e =>
        {
            e.HasKey(x => x.Sha256);
            e.Property(x => x.Sha256).HasMaxLength(64).IsFixedLength();
            e.Property(x => x.Kind).HasMaxLength(32);
            e.Property(x => x.MimeType).HasMaxLength(128);
            e.Property(x => x.StoragePath).HasMaxLength(500);
            e.Property(x => x.SourceProvider).HasMaxLength(64);
            e.Property(x => x.SourceUrl).HasMaxLength(2000);
            e.Property(x => x.LicenseJson).HasColumnType("jsonb");
            e.HasIndex(x => x.Kind);
        });

        b.Entity<Source>(e =>
        {
            e.Property(x => x.Url).HasMaxLength(2000);
            e.Property(x => x.Title).HasMaxLength(500);
            e.Property(x => x.SourceType).HasMaxLength(32);
            e.Property(x => x.ContentHash).HasMaxLength(64).IsFixedLength();
            e.Property(x => x.Excerpt).HasMaxLength(4000);

            // Tekillik ICERIK ozetinde, adreste degil: ayni sayfa iki
            // farkli adresten gelebiliyor (yonlendirme, izleme
            // parametreleri) ve ayni icerigi iki kez saklamak "bu
            // videonun kac kaynagi var" sorusunun cevabini bozardi.
            e.HasIndex(x => x.ContentHash).IsUnique();
            e.HasIndex(x => x.Url);
        });

        b.Entity<ClaimRecord>(e =>
        {
            e.Property(x => x.Text).HasMaxLength(1000);
            e.Property(x => x.Verdict).HasMaxLength(32);
            e.Property(x => x.Reason).HasMaxLength(2000);

            // "Bir videonun tum kaynaklari tek sorguyla" - P1-11'in
            // kabul kriteri bu indeksten besleniyor.
            e.HasIndex(x => new { x.RunId, x.Verdict });

            e.HasOne(x => x.Run).WithMany().HasForeignKey(x => x.RunId)
                .OnDelete(DeleteBehavior.Cascade);

            // Kaynak silinirse iddia KALIYOR, kaynagi bosaliyor.
            // Silmek, "bu iddia neye dayaniyordu" sorusunun cevabini
            // tamamen kaybettirirdi.
            e.HasOne(x => x.Source).WithMany().HasForeignKey(x => x.SourceId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Setting>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(64);
            e.Property(x => x.Value).HasMaxLength(256);
            e.Property(x => x.UpdatedBy).HasMaxLength(128);
            e.Property(x => x.Reason).HasMaxLength(500);
        });

        b.Entity<Approval>(e =>
        {
            e.Property(x => x.NodeId).HasMaxLength(128);
            e.Property(x => x.Reason).HasMaxLength(1000);
            e.Property(x => x.DecidedBy).HasMaxLength(128);
            e.Property(x => x.Note).HasMaxLength(2000);

            // KISMI INDEKS: onay kuyrugu ekrani yalnizca BEKLEYENLERI
            // listeliyor ve karara baglanmis satirlar zamanla birikip
            // buyuyor. Tam indeks, her gecen gun daha yavas cevap
            // verirdi - oysa sorgu her zaman ayni buyuklukte bir
            // kumeye bakiyor.
            e.HasIndex(x => x.CreatedAt)
                .HasFilter("state = 0")
                .HasDatabaseName("ix_approvals_pending");

            // Bir run'in ayni node'unda ikinci bir bekleyen onay
            // OLAMAZ: motor yeniden denerse ya da iki worker ayni isi
            // alirsa, panelde ayni video iki kez gorunurdu.
            e.HasIndex(x => new { x.RunId, x.NodeId })
                .IsUnique()
                .HasFilter("state = 0")
                .HasDatabaseName("ux_approvals_pending_node");

            e.HasOne(x => x.Run).WithMany().HasForeignKey(x => x.RunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Credential>(e =>
        {
            e.Property(x => x.ProviderKey).HasMaxLength(64);
            e.Property(x => x.Masked).HasMaxLength(16);

            // Sifreli metin uzun olabiliyor (Data Protection basliklariyla
            // birlikte); sinir koymak ileride sessiz bir kesilme olurdu.
            e.Property(x => x.CipherText).HasColumnType("text");

            // Bir saglayici + bir kapsam icin TEK kayit. Iki kayit olsaydi
            // hangisinin kullanildigi belirsizlesirdi.
            //
            // NULL kanal (genel kayit) icin ayri kismi indeks gerekiyor:
            // Postgres'te NULL != NULL oldugu icin bilesik tekil indeks
            // genel kayitlarin coklanmasini engellemiyor.
            e.HasIndex(x => new { x.ChannelId, x.ProviderKey })
                .IsUnique()
                .HasFilter("channel_id IS NOT NULL");

            e.HasIndex(x => x.ProviderKey)
                .IsUnique()
                .HasFilter("channel_id IS NULL");

            e.HasOne(x => x.Channel).WithMany().HasForeignKey(x => x.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ProviderCall>(e =>
        {
            e.Property(x => x.ProviderKey).HasMaxLength(64);
            e.Property(x => x.Operation).HasMaxLength(64);
            e.Property(x => x.NodeId).HasMaxLength(100);
            e.Property(x => x.UnitsJson).HasColumnType("jsonb");
            e.Property(x => x.Cost).HasPrecision(12, 6);

            // Maliyet raporlarının tamamı bu iki indeksten besleniyor:
            // "bugün ne harcadık" ve "bu run'a ne harcadık".
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => new { x.RunId, x.CreatedAt });
        });
    }
}
