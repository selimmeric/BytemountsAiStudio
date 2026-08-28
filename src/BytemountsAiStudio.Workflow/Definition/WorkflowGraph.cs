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

    /// Graf JSON'unu okur; okunamıyorsa `null`.
    ///
    /// BOZUK JSON İSTİSNA ATMIYOR ve bu, imzanın zaten verdiği sözdü.
    /// `Deserialize` yalnızca metin literal `null` olduğunda `null`
    /// döndürüyor, bozuk metinde `JsonException` ATIYOR — yani bu
    /// metodun `WorkflowGraph?` dönüş tipi yanlış bir söz veriyordu.
    ///
    /// Bütün çağıranlar (`WorkflowEngine`, `ApprovalService`,
    /// `DeadLetterTriage`, panel sorguları) `is null` diye
    /// bakıyordu: o kontrollerin hiçbiri bozuk bir kayıtta
    /// çalışmıyordu. Depoda bozuk tek bir graf satırı, "graf
    /// okunamadı" yerine motorda işlenmemiş bir istisna demekti —
    /// ve editörde HTTP cevabına düşmüş bir yığın izi olarak
    /// görüldü.
    public static WorkflowGraph? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<WorkflowGraph>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (ArgumentNullException)
        {
            // `Deserialize(null)` da istisna atıyor; çağıran için
            // "okunamadı" ile aynı şey.
            return null;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public WorkflowNode? Node(string id)
        => Nodes.FirstOrDefault(n => string.Equals(n.Id, id, StringComparison.Ordinal));

    /// Bir yeniden koşma hedefini graftaki node kimliklerine çevirir.
    ///
    /// HEM KİMLİK HEM TİP eşleşiyor ve bu bir kolaylık değil,
    /// zorunluluk: QC planlayıcısı kullanıcının node'lara verdiği
    /// keyfi kimlikleri (`yaz`, `gorsel`) bilemez, yalnızca boru
    /// hattı AŞAMALARINI (`media.render`) bilir. Motor grafı biliyor,
    /// çeviri buraya ait.
    ///
    /// Bir tip BİRDEN ÇOK node eşleştirebilir — iki görsel node'u olan
    /// bir grafta "görselden itibaren yeniden koş" ikisini de
    /// kapsamalı; yalnızca ilkini almak, ikinci görseli eski hâliyle
    /// bırakırdı.
    public IReadOnlyList<string> ResolveTargets(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        var resolved = new List<string>();

        foreach (var name in names)
        {
            // KİMLİK ÖNCE: grafta bu adda bir node varsa kastedilen
            // odur. Tersi sırada, kimliği başka bir node'un tipiyle
            // aynı olan bir node yanlış hedefi koştururdu.
            var matches = Nodes.Where(n => string.Equals(n.Id, name, StringComparison.Ordinal)).ToList();

            if (matches.Count == 0)
            {
                matches = Nodes.Where(n => string.Equals(n.Type, name, StringComparison.Ordinal)).ToList();
            }

            foreach (var match in matches.Where(m => !resolved.Contains(m.Id, StringComparer.Ordinal)))
            {
                resolved.Add(match.Id);
            }
        }

        return resolved;
    }

    /// Bir hedef kümesinin GİRİŞ node'ları: kümedeki başka bir
    /// node'dan beslenmeyenler.
    ///
    /// Hedefli retry planı hedefi VE sonrasındaki her şeyi
    /// listeliyor. Hepsini birden kuyruğa atmak, sırayı yok saymak
    /// olurdu: yeni görseller daha üretilmeden timeline derlenir,
    /// derlenmemiş timeline render edilirdi. Yalnızca girişler
    /// kuyruğa giriyor; gerisini kenar takibi zaten sırayla
    /// tetikliyor — ve aynı node'un iki kez kuyruğa girmesi de böyle
    /// engelleniyor.
    public IReadOnlyList<string> EntryPointsOf(IReadOnlyCollection<string> nodeIds)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);

        var roots = nodeIds
            .Where(id => !Predecessors(id).Any(p => nodeIds.Contains(p, StringComparer.Ordinal)))
            .ToList();

        // KÜMENİN TAMAMI BİR DÖNGÜNÜN İÇİNDEYSE hiçbir node giriş
        // sayılmaz ve retry sessizce hiçbir şey koşmazdı. O hâlde
        // kümeyi olduğu gibi kuyruğa vermek, hiç koşmamaktan iyi.
        return roots.Count > 0 ? roots : nodeIds.ToList();
    }

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
