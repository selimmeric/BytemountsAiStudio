using System.Text.Json;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Workflow.Engine;

namespace BytemountsAiStudio.Nodes;

/// İnsan onayı kapısı (P1-27, §22).
///
/// Bu node hiçbir iş YAPMIYOR: yalnızca "buradan sonrası insana mı
/// sorulacak" sorusunu cevaplıyor. Kararı motor okuyor ve gerekirse
/// run'ı park ediyor.
///
/// Kararın burada, motorda değil verilmesinin sebebi: karar İÇERİĞE
/// bağlı (QC skoru) ve kanala bağlı (kip). Motora gömülseydi her kanal
/// için motoru değiştirmek gerekirdi.
///
/// Onay bekleyen run WORKER KAYNAĞI TÜKETMİYOR: kuyrukta iş kalmıyor,
/// çünkü bu node başarıyla bitiyor ve sonraki node'lar kuyruğa
/// GİRMİYOR. Beklemeyi bir işin içinde yapmak — uyuyup tekrar bakmak —
/// bir worker'ı saatlerce tutmak demekti.
public sealed class ApprovalGateHandler : INodeHandler
{
    public string NodeType => "human.approval";

    /// Kuyruk sınıfı önemsiz: bu node ağa çıkmıyor, model çağırmıyor,
    /// ölçülebilir bir kaynak tüketmiyor. En hafif sınıf seçildi.
    public QueueClass Queue => QueueClass.Search;

    public Task<Result<JsonElement>> ExecuteAsync(NodeContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var mode = ApprovalGate.ParseMode(NodeJson.Text(context.Config, "mode"));
        var threshold = ConfigDouble(context.Config, "min_score", 0.75);
        var score = ScoreFrom(context.RunContext);

        var decision = ApprovalGate.Decide(mode, score, threshold);

        return Task.FromResult(Result.Success(NodeJson.From(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [ApprovalGate.AwaitingField] = decision.Awaiting,
            ["reason"] = decision.Reason,
            ["mode"] = mode.ToString(),
            ["threshold"] = threshold,
            // Skor da yazılıyor: karardan sonra eşiği tartışan biri,
            // o koşudaki gerçek skoru aramak zorunda kalmasın.
            ["score"] = score,
        })));
    }

    /// QC skorunu run bağlamından okur.
    ///
    /// Ayrı ve `internal`: skorun BULUNAMAMASI da bir sonuç ve o durumda
    /// insana soruluyor (bkz. `ApprovalGate`). Sessizce 0 döndürmek
    /// "çok kötü video" demek olurdu, sessizce 1 döndürmek "mükemmel";
    /// ikisi de yanlış — doğrusu "bilinmiyor".
    internal static double? ScoreFrom(JsonElement runContext)
    {
        foreach (var node in new[] { "qc", "quality", "qc.mechanical" })
        {
            if (!runContext.TryGetProperty(node, out var output)
                || output.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var field in new[] { "score", "total_score" })
            {
                if (output.TryGetProperty(field, out var value)
                    && value.ValueKind == JsonValueKind.Number
                    && value.TryGetDouble(out var parsed))
                {
                    return parsed;
                }
            }
        }

        return null;
    }

    private static double ConfigDouble(JsonElement config, string name, double fallback)
        => config.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetDouble(out var parsed)
            ? Math.Clamp(parsed, 0, 1)
            : fallback;
}
