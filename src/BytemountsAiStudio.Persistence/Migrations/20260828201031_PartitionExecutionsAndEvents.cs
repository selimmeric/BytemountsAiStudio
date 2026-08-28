using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BytemountsAiStudio.Persistence.Migrations
{
    /// <summary>
    /// `run_events` aylık bölümlere ayrılıyor (P4-06).
    ///
    /// NEDEN BU TABLO: hacim üretimle doğrusal ve en hızlı büyüyen
    /// tablo. `runs` ve `channels` sabit kalıyor.
    ///
    /// ÖLÇÜLDÜ, VARSAYILMADI. 730.000 satırlık bir yıllık veriyle:
    ///   - eski bir ayı silmek: DELETE 175 ms, DROP PARTITION 3,5 ms
    ///   - DELETE'ten SONRA tablo hâlâ 165 MB ve 6.000 ölü satır
    ///     taşıyor; bölüm düşürüldüğünde yer HEMEN geri veriliyor
    ///   - "son 7 gün" sorgusu: düz tabloda 12.555 blok, bölümlüde
    ///     815 blok okuyor (bölüm budama)
    ///
    /// BEDELİ DE ÖLÇÜLDÜ: bölümlü tablo biraz daha büyük (170 MB'a
    /// karşı 165 MB), çünkü her bölümün kendi indeksi var.
    ///
    /// `node_executions` BÖLÜMLENMİYOR ve bu bilinçli bir geri adım.
    ///
    /// Önce o da bölümlenmişti. PostgreSQL bölüm anahtarının eşsizlik
    /// kısıtının parçası olmasını ŞART koşuyor, yani
    /// `(run_id, node_id, loop, attempt)` kısıtına `created_at`
    /// eklenmek zorundaydı — ve o an kısıt işini yapmayı bıraktı:
    /// aynı adımın iki kez yazılması artık ENGELLENMİYOR, yalnızca
    /// aynı mikrosaniyede yazılması engelleniyor.
    ///
    /// Var olan bir test bunu hemen yakaladı ve gerekçesi tam
    /// yerindeydi: "çift tetiklemeyi uygulama katmanında değil
    /// veritabanında durduruyoruz; uygulama katmanındaki kontrol
    /// yarış koşulunda kaçırırdı."
    ///
    /// Kısıt, saklama kolaylığından DEĞERLİ. `node_executions` günde
    /// ~1.500 satır büyüyor; `run_events` çok daha hızlı. Bölümlemenin
    /// kazandırdığı yer, kaybettirdiği garantiden azdı.
    /// </summary>
    public partial class PartitionExecutionsAndEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ESKİ TABLO YENİDEN ADLANDIRILIYOR, SİLİNMİYOR.
            //
            // Veri kopyalandıktan sonra siliniyor; kopyalama ortasında
            // bir hata olursa veri hâlâ duruyor.
            migrationBuilder.Sql(@"ALTER TABLE ""run_events"" RENAME TO ""run_events_eski"";");

            migrationBuilder.Sql(@"
                CREATE TABLE ""run_events"" (LIKE ""run_events_eski"" INCLUDING DEFAULTS)
                PARTITION BY RANGE (created_at);");

            // AYLIK BÖLÜMLER KOPYALAMADAN ÖNCE AÇILIYOR ve sıra
            // zorunlu: varsayılan bölüme düşmüş satırların olduğu bir
            // aralık için sonradan bölüm açılamıyor ("updated
            // partition constraint ... would be violated by some
            // row"). İlk yazımda sonra açıyordum ve migration tam bu
            // hatayla düştü.
            //
            // ARALIK MEVCUT VERİDEN TÜRÜYOR, `now()`'dan değil: depoda
            // bir yıl önceki satırlar olabilir ve yalnızca bu ayı
            // açmak hepsini varsayılan bölüme yığardı.
            migrationBuilder.Sql(@"
                DO $$
                DECLARE ay date; son date;
                BEGIN
                  SELECT COALESCE(date_trunc('month', min(created_at))::date,
                                  date_trunc('month', now())::date),
                         GREATEST(COALESCE(date_trunc('month', max(created_at))::date,
                                  date_trunc('month', now())::date),
                                  date_trunc('month', now())::date + interval '3 month')
                  INTO ay, son FROM ""run_events_eski"";

                  WHILE ay <= son LOOP
                    EXECUTE format(
                      'CREATE TABLE IF NOT EXISTS %I PARTITION OF %I FOR VALUES FROM (%L) TO (%L)',
                      'run_events_' || to_char(ay, 'YYYYMM'), 'run_events', ay, ay + interval '1 month');
                    ay := ay + interval '1 month';
                  END LOOP;
                END $$;");

            // VARSAYILAN BÖLÜM: kapsanmayan her satır buraya düşüyor ve
            // INSERT asla düşmüyor.
            //
            // Bu olmasaydı, bölüm bakımı bir ay geri kaldığında sistem
            // ayın birinde saat 00:00'da TAMAMEN dururdu: "no
            // partition of relation found for row". Otonom bir
            // sistemde bu, kimsenin ayakta olmadığı bir saatte
            // üretimin durması demek.
            //
            // AYLIK BÖLÜMLERDEN SONRA ekleniyor ki kopyalanan satırlar
            // doğru yere gitsin.
            migrationBuilder.Sql(
                @"CREATE TABLE ""run_events_varsayilan"" PARTITION OF ""run_events"" DEFAULT;");

            migrationBuilder.Sql(@"INSERT INTO ""run_events"" SELECT * FROM ""run_events_eski"";");

            // ESKİ TABLO İNDEKSLERDEN ÖNCE DÜŞÜYOR.
            //
            // Tabloyu yeniden adlandırmak İNDEKSLERİNİ yeniden
            // adlandırmıyor: `run_events_eski` hâlâ `pk_run_events`
            // adlı indeksi taşıyor. Yeni birincil anahtarı önce
            // kurmayı denemek "relation already exists" ile düştü —
            // ilk yazımda tam olarak bu oldu.
            migrationBuilder.Sql(@"DROP TABLE ""run_events_eski"";");

            // Birincil anahtar bölüm anahtarını İÇERMEK ZORUNDA.
            //
            // `run_events`'te bu bir kayıp değil: `id` yalnızca satırı
            // adreslemek için var, bir iş kuralı taşımıyor.
            // `node_executions`'ta ise taşıyordu — bu yüzden o tablo
            // bölümlenmedi.
            migrationBuilder.Sql(@"
                ALTER TABLE ""run_events""
                ADD CONSTRAINT ""pk_run_events"" PRIMARY KEY (id, created_at);");

            migrationBuilder.Sql(@"
                CREATE INDEX ""ix_run_events_run_id_created_at""
                ON ""run_events"" (run_id, created_at);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // GERİ ALMA VERİYİ KORUYOR: bölümlü tablo düz bir tabloya
            // kopyalanıyor. Yalnızca `DROP TABLE` yazmak, geri alma
            // sırasında bütün olay geçmişini silmek olurdu.
            migrationBuilder.Sql(@"ALTER TABLE ""run_events"" RENAME TO ""run_events_bolumlu"";");

            migrationBuilder.Sql(@"
                CREATE TABLE ""run_events"" (LIKE ""run_events_bolumlu"" INCLUDING DEFAULTS);");

            migrationBuilder.Sql(@"INSERT INTO ""run_events"" SELECT * FROM ""run_events_bolumlu"";");

            migrationBuilder.Sql(@"DROP TABLE ""run_events_bolumlu"" CASCADE;");

            migrationBuilder.Sql(
                @"ALTER TABLE ""run_events"" ADD CONSTRAINT ""pk_run_events"" PRIMARY KEY (id);");

            migrationBuilder.Sql(@"
                CREATE INDEX ""ix_run_events_run_id_created_at""
                ON ""run_events"" (run_id, created_at);");
        }
    }
}
