using System.Text.Json;
using BytemountsAiStudio.Persistence;
using BytemountsAiStudio.Persistence.Entities;
using BytemountsAiStudio.Workflow.Definition;
using BytemountsAiStudio.Workflow.Engine;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Api;

/// İş akışı editörünün sunucu tarafı (P3-05).
///
/// EDİTÖRÜN KENDİ DOĞRULAMA KURALLARI YOK ve bu en önemli karar.
/// Ekran, motorun kullandığı `WorkflowValidator`'ı ve `NodeRegistry`'yi
/// çağırıyor. Kuralları tarayıcıda tekrar yazmak, iki kural setinin
/// zamanla ayrışması demekti: editör "geçerli" der, motor reddeder ve
/// aradaki farkı ancak kaydetmeye çalışan kişi görürdü.
///
/// KAYDETMEK HER ZAMAN YENİ SÜRÜM ÜRETİYOR, mevcut sürümü
/// değiştirmiyor. Koşan run'lar başladıkları grafa bağlı (§6.2) ve
/// yerinde düzenleme, yarısı eski yarısı yeni kurallarla üretilmiş bir
/// video demekti.
public static class WorkflowEditor
{
    /// Grafı doğrular — KAYDETMEDEN.
    ///
    /// Bozuk bir grafı kaydetmek, hatayı çalışma zamanına, yani gerçek
    /// para harcanmaya başladıktan sonrasına ertelemek demek.
    public static ValidationResult Validate(string graphJson, NodeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var graph = WorkflowGraph.Parse(graphJson);

        if (graph is null)
        {
            // OKUNAMAYAN GRAF, "sorunsuz graf" değil. Ayrı bir kod
            // taşıyor çünkü yapılacak şey de ayrı: JSON'u düzeltmek,
            // node eklemek değil.
            return new ValidationResult(false, [new IssueView("workflow.parse", "Graf okunamadı: geçersiz JSON.")]);
        }

        var issues = WorkflowValidator.Validate(graph, registry.KnownTypes);

        return new ValidationResult(
            issues.Count == 0,
            [.. issues.Select(i => new IssueView(i.Code, i.Message))]);
    }

    /// Grafı YENİ SÜRÜM olarak kaydeder.
    ///
    /// Doğrulama burada TEKRAR koşuyor. İstemci zaten doğrulamış
    /// olabilir ama istemciye güvenmek, tarayıcıyı atlayan herhangi
    /// bir çağrının bozuk graf kaydedebilmesi demekti — ve o graf
    /// bir sonraki run'da patlardı.
    public static async Task<Result> SaveAsync(
        StudioDbContext db,
        NodeRegistry registry,
        string key,
        string graphJson,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        var validation = Validate(graphJson, registry);

        if (!validation.Valid)
        {
            return new Result(false, 0, validation.Issues);
        }

        var workflow = await db.Workflows
            .FirstOrDefaultAsync(w => w.Key == key, cancellationToken)
            .ConfigureAwait(false);

        if (workflow is null)
        {
            return new Result(false, 0,
                [new IssueView("workflow.unknown", $"'{key}' adında bir iş akışı yok.")]);
        }

        // SONRAKİ SÜRÜM NUMARASI KAYITLI EN YÜKSEKTEN TÜRÜYOR,
        // `CurrentVersion`'dan değil.
        //
        // İkisi ayrışabiliyor: bir sürüme geri dönülmüşse
        // `CurrentVersion` daha küçük olur ve ondan +1 almak, VAR OLAN
        // bir sürümün numarasını ikinci kez kullanmak demekti —
        // yani bir grafın üzerine başka bir graf yazmak.
        var highest = await db.WorkflowVersions
            .Where(v => v.WorkflowId == workflow.Id)
            .MaxAsync(v => (int?)v.Version, cancellationToken)
            .ConfigureAwait(false) ?? 0;

        var version = new WorkflowVersion
        {
            WorkflowId = workflow.Id,
            Version = highest + 1,
            GraphJson = graphJson,
        };

        db.WorkflowVersions.Add(version);
        workflow.CurrentVersion = version.Version;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new Result(true, version.Version, []);
    }

    /// Editörün node paletine koyacağı tipler.
    ///
    /// KAYITLI TİPLERDEN GELİYOR, elle yazılmış bir listeden değil:
    /// liste olsaydı yeni bir handler eklendiğinde palete eklemeyi
    /// unutmak mümkün olurdu ve o node hiç kullanılamazdı — ya da
    /// daha kötüsü, paletteki bir tip kayıtlı olmaz ve graf
    /// kaydedilemezdi.
    public static IReadOnlyList<string> Palette(NodeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        return [.. registry.KnownTypes.Order(StringComparer.Ordinal)];
    }

    public sealed record IssueView(string Code, string Message);

    public sealed record ValidationResult(bool Valid, IReadOnlyList<IssueView> Issues);

    public sealed record Result(bool Saved, int Version, IReadOnlyList<IssueView> Issues);
}
