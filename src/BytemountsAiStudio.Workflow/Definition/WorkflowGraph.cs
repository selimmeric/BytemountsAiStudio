using System.Text.Json;
using System.Text.Json.Serialization;

namespace BytemountsAiStudio.Workflow.Definition;

/// Workflow tanımı (mimari §6.2).
///
/// Graf JSONB olarak saklanır; ayrı `workflow_nodes` tablosu YOK. Node'lar
/// üzerinde sorgu ihtiyacı olmadığı için join maliyeti karşılıksız kalırdı.
public sealed record WorkflowGraph
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("key")]
    public required string Key { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("content_kind")]
    public string ContentKind { get; init; } = "Short";

    [JsonPropertyName("nodes")]
    public required IReadOnlyList<WorkflowNode> Nodes { get; init; }

    [JsonPropertyName("edges")]
    public required IReadOnlyList<WorkflowEdge> Edges { get; init; }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    public static WorkflowGraph? Parse(string json)
        => JsonSerializer.Deserialize<WorkflowGraph>(json, JsonOptions);

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public WorkflowNode? Node(string id)
        => Nodes.FirstOrDefault(n => string.Equals(n.Id, id, StringComparison.Ordinal));

    /// Bir node'un beslendiği node'lar. Tetikleme kararı buna bakar:
    /// tüm girdiler tamamlanmadan node çalışmaz.
    public IReadOnlyList<string> Predecessors(string nodeId)
        => Edges.Where(e => string.Equals(e.To, nodeId, StringComparison.Ordinal))
            .Select(e => e.From)
            .ToList();

    public IReadOnlyList<WorkflowEdge> OutgoingEdges(string nodeId)
        => Edges.Where(e => string.Equals(e.From, nodeId, StringComparison.Ordinal)).ToList();

    /// Girdisi olmayan node'lar: run buradan başlar.
    public IReadOnlyList<WorkflowNode> EntryNodes()
        => Nodes.Where(n => Predecessors(n.Id).Count == 0).ToList();
}

public sealed record WorkflowNode
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// Node kaydındaki işleyiciyi seçer: "script.generate", "media.render"…
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// İşleyiciye geçilen ayarlar. Şeması node tipine ait; engine bilmez.
    [JsonPropertyName("config")]
    public JsonElement Config { get; init; }
}

public sealed record WorkflowEdge
{
    [JsonPropertyName("from")]
    public required string From { get; init; }

    [JsonPropertyName("to")]
    public required string To { get; init; }

    /// Kısıtlı ifade. Boşsa kenar her zaman izlenir.
    [JsonPropertyName("when")]
    public string? When { get; init; }

    /// Geri dönen kenarlar (QC → render gibi) için tekrar sınırı.
    ///
    /// §6.2: bu olmadan QC→render döngüsü sonsuza kadar para harcar.
    /// Sınırsız bir döngüye izin vermek, otonom bir sistemde en pahalı hata.
    [JsonPropertyName("max_loops")]
    public int MaxLoops { get; init; } = 1;
}
