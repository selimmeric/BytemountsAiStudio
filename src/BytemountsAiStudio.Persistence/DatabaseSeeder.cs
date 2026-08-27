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
    private const string FakeGraphJson = """
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
            { "id": "render",   "type": "media.render",     "config": { "preset": "shorts-1080x1920" } }
          ],
          "edges": [
            { "from": "topic",    "to": "research" },
            { "from": "research", "to": "script" },
            { "from": "script",   "to": "tts" },
            { "from": "tts",      "to": "visuals" },
            { "from": "visuals",  "to": "timeline" },
            { "from": "timeline", "to": "render" }
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

    private static async Task<int> EnsureFakeWorkflowAsync(StudioDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Workflows.AnyAsync(w => w.Key == FakeWorkflowKey, cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }

        var workflow = new Workflow
        {
            Key = FakeWorkflowKey,
            Name = "Sahte Shorts (Faz 0 iskeleti)",
            ContentKind = ContentKind.Short,
            CurrentVersion = 1,
        };

        workflow.Versions.Add(new WorkflowVersion
        {
            Version = 1,
            GraphJson = FakeGraphJson,
        });

        db.Workflows.Add(workflow);
        return 1;
    }
}
