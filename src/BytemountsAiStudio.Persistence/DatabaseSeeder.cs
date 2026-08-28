using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Persistence;

/// Geliştirme ve test için başlangıç verisi.
///
/// Idempotent: iki kez çalıştırılırsa ikinci seferde hiçbir şey eklemez.
/// Aksi hâlde her uygulama açılışında kopya kanal ve workflow birikirdi.
public static class DatabaseSeeder
{
    public const string FakeWorkflowKey = "shorts-fake";

    /// Faz 0'ın yürüyen iskeleti: tüm node'lar sahte sağlayıcılarla çalışır.
    /// Gerçek grafın yapısını birebir taşır — sonradan sağlayıcıları
    /// değiştirmek graf değişikliği değil, konfigürasyon değişikliği olsun.
    ///
    /// PUBLIC: graftaki node tiplerinin kayıtlı olup olmadığı
    /// veritabanı gerektirmeden sınanabilsin. Kayıtlı olmayan bir tip
    /// run'ı çalışma ortasında düşürürdü (§6.2) ve bunu yakalamak için
    /// Postgres ayağa kaldırmak gereksiz.
    ///
    /// NODE KİMLİKLERİ BİR ARAYÜZ, keyfi ad değil.
    ///
    /// Run bağlamı node KİMLİĞİNE göre anahtarlanıyor
    /// (`context["render"]`) ve tüketiciler sabit anahtarlar okuyor:
    /// timeline `music`, QC `thumbnail`, render `timeline` arıyor.
    /// Kimliği değiştirmek, o çıktıyı GÖRÜNMEZ kılıyor.
    ///
    /// Bu gerçekten oldu: node'lar `muzik` ve `kapak` adlandırılmıştı.
    /// İkisi de doğru çalışıp doğru çıktı üretti ve HİÇBİRİ
    /// okunmadı — müzik hiçbir videoya girmedi (üstelik "müziksiz
    /// video geçerli" olduğu için QC de sessiz kaldı) ve kapak
    /// "ölçülmedi" diye bloklayıcı bir kontrolü düşürdü.
    ///
    /// `SeedGraphTests` bu sözleşmeyi sınıyor.
    public const string FakeGraphJson = """
        {
          "schema_version": 1,
          "key": "shorts-fake",
          "name": "Sahte Shorts (Faz 0 iskeleti)",
          "content_kind": "Short",
          "nodes": [
            { "id": "topic",    "type": "topic.select",     "config": { "min_score": 0 } },
            { "id": "research", "type": "research.deep",    "config": { "max_sources": 3 } },
            { "id": "script",   "type": "script.generate",  "config": { "target_seconds": 30 } },
            { "id": "tts",      "type": "tts.synthesize",   "config": { "voice_id": "fake-tr-f1" } },
            { "id": "timeline", "type": "timeline.compile", "config": { "aspect": "9:16" } },
            { "id": "visuals",  "type": "visual.resolve",   "config": { "order": ["fake-stock", "fake-imagegen"] } },
            { "id": "music",    "type": "music.select",     "config": { "mood": "ambient" } },
            { "id": "render",   "type": "media.render",     "config": { "preset": "shorts-1080x1920" } },
            { "id": "claims",   "type": "claim.check",      "config": {} },
            { "id": "seo",      "type": "seo.generate",     "config": {} },
            { "id": "thumbnail","type": "thumbnail.render", "config": {} },
            { "id": "qc",       "type": "qc.mechanical",    "config": {} },
            { "id": "qcs",      "type": "qc.semantic",      "config": {} },
            { "id": "onay",     "type": "human.approval",   "config": { "min_score": 0.75 } }
          ],
          "edges": [
            { "from": "topic",    "to": "research" },
            { "from": "research", "to": "script" },
            { "from": "script",   "to": "claims" },
            { "from": "claims",   "to": "tts" },
            { "from": "tts",      "to": "visuals" },
            { "from": "tts",      "to": "music" },
            { "from": "visuals",  "to": "timeline" },
            { "from": "music",    "to": "timeline" },
            { "from": "timeline", "to": "render" },
            { "from": "render",   "to": "seo" },
            { "from": "seo",      "to": "thumbnail" },
            { "from": "thumbnail","to": "qc" },
            { "from": "qc",       "to": "qcs" },
            { "from": "qcs",      "to": "onay" }
          ]
        }
        """;

    public const string LongWorkflowKey = "video-uzun";

    /// Uzun video iş akışı (P3-02).
    ///
    /// KISA VİDEODAN FARKI İKİ NODE: `script.generate` yerine
    /// `chapter.plan` + `script.long`. Gerisi aynı — seslendirme,
    /// görsel, müzik, timeline, render, QC, onay hepsi ortak.
    ///
    /// Bu, sağlayıcı ve node soyutlamasının asıl sınavı: yeni bir
    /// içerik türü yeni bir boru hattı değil, farklı bir GRAF
    /// olmalıydı (§34). Öyle çıktı.
    public const string LongGraphJson = """
        {
          "schema_version": 1,
          "key": "video-uzun",
          "name": "Uzun video (8-15 dk)",
          "content_kind": "Video",
          "nodes": [
            { "id": "topic",     "type": "topic.select",     "config": { "min_score": 0 } },
            { "id": "research",  "type": "research.deep",    "config": { "max_sources": 8 } },
            { "id": "chapters",  "type": "chapter.plan",     "config": { "target_minutes": 10 } },
            { "id": "script",    "type": "script.long",      "config": {} },
            { "id": "claims",    "type": "claim.check",      "config": {} },
            { "id": "tts",       "type": "tts.synthesize",   "config": {} },
            { "id": "visuals",   "type": "visual.resolve",   "config": {} },
            { "id": "music",     "type": "music.select",     "config": { "mood": "documentary" } },
            { "id": "timeline",  "type": "timeline.compile", "config": { "aspect": "16:9" } },
            { "id": "render",    "type": "media.render",     "config": { "preset": "video-1920x1080", "segmented": true } },
            { "id": "seo",       "type": "seo.generate",     "config": {} },
            { "id": "thumbnail", "type": "thumbnail.render", "config": {} },
            { "id": "qc",        "type": "qc.mechanical",    "config": {} },
            { "id": "qcs",       "type": "qc.semantic",      "config": {} },
            { "id": "onay",      "type": "human.approval",   "config": { "min_score": 0.8 } }
          ],
          "edges": [
            { "from": "topic",     "to": "research" },
            { "from": "research",  "to": "chapters" },
            { "from": "chapters",  "to": "script" },
            { "from": "script",    "to": "claims" },
            { "from": "claims",    "to": "tts" },
            { "from": "tts",       "to": "visuals" },
            { "from": "tts",       "to": "music" },
            { "from": "visuals",   "to": "timeline" },
            { "from": "music",     "to": "timeline" },
            { "from": "timeline",  "to": "render" },
            { "from": "render",    "to": "seo" },
            { "from": "seo",       "to": "thumbnail" },
            { "from": "thumbnail", "to": "qc" },
            { "from": "qc",        "to": "qcs" },
            { "from": "qcs",       "to": "onay" }
          ]
        }
        """;

    public static async Task<int> SeedAsync(StudioDbContext db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var added = 0;

        added += await EnsureChannelAsync(db, "Sahte Kanal (TR)", "tr-TR", cancellationToken).ConfigureAwait(false);
        added += await EnsureChannelAsync(db, "Fake Channel (EN)", "en-US", cancellationToken).ConfigureAwait(false);
        added += await EnsureFakeWorkflowAsync(db, cancellationToken).ConfigureAwait(false);
        added += await EnsureWorkflowAsync(
            db, LongWorkflowKey, "Uzun video (8-15 dk)", ContentKind.Video, LongGraphJson,
            cancellationToken).ConfigureAwait(false);

        if (added > 0)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return added;
    }

    private static async Task<int> EnsureChannelAsync(
        StudioDbContext db, string name, string language, CancellationToken cancellationToken)
    {
        if (await db.Channels.AnyAsync(c => c.Name == name, cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }

        db.Channels.Add(new Channel
        {
            Name = name,
            Language = language,
            Mode = ChannelMode.Approval,
            SettingsJson = $$"""
                {
                  "voice": { "voice_id": "fake-{{language[..2]}}-f1", "speed": 1.0 },
                  "font_stack": ["Inter", "Noto Sans", "Noto Color Emoji"],
                  "model_tiers": { "cheap": "fake-llm", "standard": "fake-llm", "strong": "fake-llm" }
                }
                """,
            DailyBudget = 5.00m,
            MaxCostPerVideo = 0.50m,
        });

        return 1;
    }

    /// Grafı İÇERİĞE göre tohumlar.
    ///
    /// Önceden yalnızca anahtarın varlığına bakılıyordu ve bu bir tuzaktı:
    /// koddaki grafı değiştirmek MEVCUT bir veritabanında hiçbir şey
    /// yapmıyordu. Yeni bir node eklendiğinde CI (boş veritabanı) yeşil
    /// yanıyor, geliştirme makinesi (tohumlanmış veritabanı) eski grafla
    /// koşmaya devam ediyor ve fark hiçbir yerde görünmüyordu.
    ///
    /// Artık graf değiştiğinde YENİ BİR SÜRÜM ekleniyor. Eski sürüm
    /// SİLİNMİYOR: hâlihazırda koşan run'lar ona bağlı ve "bu video hangi
    /// grafla üretildi" sorusunun cevabı o kayıt (§6.2).
    private static Task<int> EnsureFakeWorkflowAsync(StudioDbContext db, CancellationToken cancellationToken)
        => EnsureWorkflowAsync(
            db, FakeWorkflowKey, "Sahte Shorts (Faz 0 iskeleti)", ContentKind.Short,
            FakeGraphJson, cancellationToken);

    private static async Task<int> EnsureWorkflowAsync(
        StudioDbContext db,
        string key,
        string name,
        ContentKind kind,
        string graphJson,
        CancellationToken cancellationToken)
    {
        var workflow = await db.Workflows
            .Include(w => w.Versions)
            .FirstOrDefaultAsync(w => w.Key == key, cancellationToken)
            .ConfigureAwait(false);

        if (workflow is null)
        {
            var created = new Workflow
            {
                Key = key,
                Name = name,
                ContentKind = kind,
                CurrentVersion = 1,
            };

            created.Versions.Add(new WorkflowVersion { Version = 1, GraphJson = graphJson });
            db.Workflows.Add(created);

            return 1;
        }

        var current = workflow.Versions.MaxBy(v => v.Version);

        if (!NeedsNewVersion(current?.GraphJson, graphJson))
        {
            return 0;
        }

        var next = (current?.Version ?? 0) + 1;

        workflow.Versions.Add(new WorkflowVersion { Version = next, GraphJson = graphJson });
        workflow.CurrentVersion = next;

        return 1;
    }

    /// Depodaki graf ile koddaki graf ayrıştı mı.
    ///
    /// Ayrı ve SAF: kararın kendisi veritabanı gerektirmiyor. İlk
    /// denemede bu davranışı veritabanına bağlı bir testle sınamaya
    /// çalıştım ve test, PAYLAŞILAN tohum verisini bozarak komşu testi
    /// düşürdü — CI'da görüldü. Kararı ayırmak hem testi izole ediyor
    /// hem veritabanı olmayan makinede de koşturuyor.
    ///
    /// Karşılaştırma satır sonu normalize edilerek: aynı graf Windows
    /// ve Linux'ta farklı sayılmamalı, yoksa her makinede bir sürüm
    /// daha eklenir ve sürüm numarası anlamını yitirirdi.
    internal static bool NeedsNewVersion(string? storedGraph, string currentGraph)
        => storedGraph is null || Normalize(storedGraph) != Normalize(currentGraph);

    /// KARŞILAŞTIRMA METİNSEL DEĞİL, ANLAMSAL — VE ANAHTAR SIRASINDAN
    /// BAĞIMSIZ.
    ///
    /// Graf `jsonb` kolonunda duruyor ve PostgreSQL jsonb'yi kendi
    /// biçiminde saklıyor: boşlukları atıyor ve **anahtarları yeniden
    /// sıralıyor** (önce uzunluğa, sonra bayta göre). Depodan okunan
    /// metin, koddaki metinle asla birebir aynı olmuyor — `{"key":...,
    /// "name":...}` sırası bile korunmuyor.
    ///
    /// Satır sonu normalizasyonu bunu yakalamıyordu ve sonuç sessiz bir
    /// hataydı: her `db seed` çağrısı "graf değişmiş" deyip yeni bir
    /// sürüm ekliyordu. Tablo sonsuza kadar büyüyor, `current_version`
    /// her dağıtımda artıyor ve "bu video hangi grafla üretildi"
    /// sorusunun cevabı anlamsızlaşıyordu.
    ///
    /// Bunu ancak GERÇEK bir veritabanında koşturmak gösterdi: testler
    /// dizeyi doğrudan karşılaştırıyordu ve jsonb hiç devreye
    /// girmiyordu. Yalnızca ayrıştırıp yeniden yazmak da yetmedi —
    /// belge sırası korunduğu için iki metin yine farklı çıkıyordu.
    /// Anahtarların SIRALANMASI şart.
    private static string Normalize(string json)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);

            var builder = new System.Text.StringBuilder();
            WriteCanonical(document.RootElement, builder);

            return builder.ToString();
        }
        catch (System.Text.Json.JsonException)
        {
            // Okunamayan bir graf METİN olarak karşılaştırılıyor:
            // ayrıştırılamayanı "değişmemiş" saymak, bozuk bir grafın
            // sonsuza kadar depoda kalması olurdu.
            return json.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        }
    }

    /// Anahtarları sıralanmış, boşluksuz JSON.
    ///
    /// DİZİ SIRASI KORUNUYOR: nesne anahtarlarının sırası anlam
    /// taşımıyor ama dizininki taşıyor — kenarların sırası grafın
    /// kendisi.
    private static void WriteCanonical(
        System.Text.Json.JsonElement element, System.Text.StringBuilder builder)
    {
        switch (element.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                builder.Append('{');
                var first = true;

                foreach (var property in element.EnumerateObject()
                             .OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    first = false;
                    builder.Append(System.Text.Json.JsonSerializer.Serialize(property.Name)).Append(':');
                    WriteCanonical(property.Value, builder);
                }

                builder.Append('}');
                break;

            case System.Text.Json.JsonValueKind.Array:
                builder.Append('[');
                var firstItem = true;

                foreach (var item in element.EnumerateArray())
                {
                    if (!firstItem)
                    {
                        builder.Append(',');
                    }

                    firstItem = false;
                    WriteCanonical(item, builder);
                }

                builder.Append(']');
                break;

            default:
                builder.Append(element.GetRawText());
                break;
        }
    }
}
