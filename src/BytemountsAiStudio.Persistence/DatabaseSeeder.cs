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
            { "id": "muzik",    "type": "music.select",     "config": { "mood": "ambient" } },
            { "id": "render",   "type": "media.render",     "config": { "preset": "shorts-1080x1920" } },
            { "id": "claims",   "type": "claim.check",      "config": {} },
            { "id": "seo",      "type": "seo.generate",     "config": {} },
            { "id": "qc",       "type": "qc.mechanical",    "config": {} },
            { "id": "onay",     "type": "human.approval",   "config": { "min_score": 0.75 } }
          ],
          "edges": [
            { "from": "topic",    "to": "research" },
            { "from": "research", "to": "script" },
            { "from": "script",   "to": "claims" },
            { "from": "claims",   "to": "tts" },
            { "from": "tts",      "to": "visuals" },
            { "from": "tts",      "to": "muzik" },
            { "from": "visuals",  "to": "timeline" },
            { "from": "muzik",    "to": "timeline" },
            { "from": "timeline", "to": "render" },
            { "from": "render",   "to": "seo" },
            { "from": "seo",      "to": "qc" },
            { "from": "qc",       "to": "onay" }
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
    private static async Task<int> EnsureFakeWorkflowAsync(StudioDbContext db, CancellationToken cancellationToken)
    {
        var workflow = await db.Workflows
            .Include(w => w.Versions)
            .FirstOrDefaultAsync(w => w.Key == FakeWorkflowKey, cancellationToken)
            .ConfigureAwait(false);

        if (workflow is null)
        {
            var created = new Workflow
            {
                Key = FakeWorkflowKey,
                Name = "Sahte Shorts (Faz 0 iskeleti)",
                ContentKind = ContentKind.Short,
                CurrentVersion = 1,
            };

            created.Versions.Add(new WorkflowVersion { Version = 1, GraphJson = FakeGraphJson });
            db.Workflows.Add(created);

            return 1;
        }

        var current = workflow.Versions.MaxBy(v => v.Version);

        if (!NeedsNewVersion(current?.GraphJson, FakeGraphJson))
        {
            return 0;
        }

        var next = (current?.Version ?? 0) + 1;

        workflow.Versions.Add(new WorkflowVersion { Version = next, GraphJson = FakeGraphJson });
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

    private static string Normalize(string json)
        => json.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
}
