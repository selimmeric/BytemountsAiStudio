using BytemountsAiStudio.Workflow.Expressions;

namespace BytemountsAiStudio.Workflow.Definition;

public sealed record WorkflowIssue(string Code, string Message)
{
    public override string ToString() => $"{Code}: {Message}";
}

/// Workflow tanımını kaydetmeden ÖNCE doğrular.
///
/// Bozuk bir grafı kaydetmek, hatayı çalışma zamanına — yani gerçek para
/// harcanmaya başladıktan sonrasına — ertelemek demek. Buradaki kontroller
/// saniyenin binde birinde koşuyor.
public static class WorkflowValidator
{
    public static IReadOnlyList<WorkflowIssue> Validate(
        WorkflowGraph graph, IReadOnlySet<string>? knownNodeTypes = null)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var issues = new List<WorkflowIssue>();

        if (graph.SchemaVersion != 1)
        {
            issues.Add(new("workflow.schema_version",
                $"Desteklenmeyen şema sürümü: {graph.SchemaVersion}"));
        }

        if (graph.Nodes.Count == 0)
        {
            issues.Add(new("workflow.no_nodes", "Workflow en az bir node içermeli."));
            return issues;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in graph.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id))
            {
                issues.Add(new("node.empty_id", "Node kimliği boş olamaz."));
                continue;
            }

            if (!ids.Add(node.Id))
            {
                issues.Add(new("node.duplicate_id", $"Node kimliği tekrarlanıyor: {node.Id}"));
            }

            // Bilinmeyen tip, run'ın ortasında "bu node nasıl çalıştırılır"
            // sorusuyla karşılaşmak demek. Kayıt anında yakalanmalı.
            if (knownNodeTypes is not null && !knownNodeTypes.Contains(node.Type))
            {
                issues.Add(new("node.unknown_type",
                    $"'{node.Id}' node'unun tipi tanınmıyor: '{node.Type}'. " +
                    $"Bilinen tipler: {string.Join(", ", knownNodeTypes.Order(StringComparer.Ordinal))}"));
            }
        }

        foreach (var edge in graph.Edges)
        {
            if (!ids.Contains(edge.From))
            {
                issues.Add(new("edge.unknown_source", $"Kenar olmayan bir node'dan çıkıyor: {edge.From}"));
            }

            if (!ids.Contains(edge.To))
            {
                issues.Add(new("edge.unknown_target", $"Kenar olmayan bir node'a gidiyor: {edge.To}"));
            }

            if (string.Equals(edge.From, edge.To, StringComparison.Ordinal))
            {
                issues.Add(new("edge.self_loop", $"'{edge.From}' kendine bağlanıyor."));
            }

            if (edge.MaxLoops < 1)
            {
                issues.Add(new("edge.max_loops", $"{edge.From}→{edge.To} için max_loops en az 1 olmalı."));
            }

            if (edge.When is { } expression)
            {
                var parsed = ExpressionParser.TryParse(expression);
                if (parsed.IsFailure)
                {
                    issues.Add(new("edge.bad_expression",
                        $"{edge.From}→{edge.To} koşulu geçersiz: {parsed.Error.Message}"));
                }
            }
        }

        if (graph.EntryNodes().Count == 0)
        {
            // Her node'un girdisi varsa graf bir döngüdür; başlangıç yoktur.
            issues.Add(new("workflow.no_entry", "Giriş node'u yok — graf tamamen döngüsel."));
        }

        issues.AddRange(FindUnreachable(graph, ids));
        issues.AddRange(FindCycles(graph));

        return issues;
    }

    /// Erişilemeyen node'lar sessizce hiç çalışmaz; bu neredeyse her zaman
    /// bir kenar hatasıdır ve fark edilmesi zordur.
    private static List<WorkflowIssue> FindUnreachable(WorkflowGraph graph, HashSet<string> ids)
    {
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(graph.EntryNodes().Select(n => n.Id));

        while (queue.TryDequeue(out var current))
        {
            if (!reachable.Add(current))
            {
                continue;
            }

            foreach (var edge in graph.OutgoingEdges(current))
            {
                queue.Enqueue(edge.To);
            }
        }

        return ids.Where(id => !reachable.Contains(id))
            .Select(id => new WorkflowIssue("node.unreachable", $"'{id}' node'una hiç ulaşılamıyor."))
            .ToList();
    }

    /// Döngü tespiti.
    ///
    /// Döngüler tamamen yasak DEĞİL — QC→render geri dönüşü meşru bir
    /// desendir. Ama `max_loops` olmadan sonsuza kadar para harcar, o yüzden
    /// döngüdeki her kenarın sınırı olması şart.
    private static List<WorkflowIssue> FindCycles(WorkflowGraph graph)
    {
        var issues = new List<WorkflowIssue>();
        var state = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var node in graph.Nodes)
        {
            state[node.Id] = 0;
        }

        foreach (var node in graph.Nodes.Where(n => state[n.Id] == 0))
        {
            Visit(node.Id, graph, state, issues);
        }

        return issues;
    }

    private static void Visit(
        string nodeId, WorkflowGraph graph, Dictionary<string, int> state, List<WorkflowIssue> issues)
    {
        state[nodeId] = 1;

        foreach (var edge in graph.OutgoingEdges(nodeId))
        {
            if (!state.TryGetValue(edge.To, out var status))
            {
                continue;
            }

            if (status == 1)
            {
                // Geri kenar bulundu. Sınırsız olması kabul edilemez.
                if (edge.MaxLoops > 10)
                {
                    issues.Add(new("edge.loop_too_large",
                        $"{edge.From}→{edge.To} döngüsünün sınırı çok yüksek ({edge.MaxLoops}); " +
                        "otonom sistemde bu kontrolsüz maliyet demek."));
                }
            }
            else if (status == 0)
            {
                Visit(edge.To, graph, state, issues);
            }
        }

        state[nodeId] = 2;
    }
}
