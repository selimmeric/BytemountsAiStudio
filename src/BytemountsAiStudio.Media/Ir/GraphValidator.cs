using BytemountsAiStudio.Media.Timeline;

namespace BytemountsAiStudio.Media.Ir;

/// Grafiği FFmpeg ÇALIŞTIRMADAN doğrular.
///
/// Studio'da bu kontroller mümkün değildi: graf tek bir metin olduğu için
/// "bu pad iki kez tüketiliyor" gibi bir soru sorulamıyordu — hata ancak
/// FFmpeg "Invalid argument" dediğinde, dakikalar sonra ve nerede olduğunu
/// söylemeden ortaya çıkıyordu.
public static class GraphValidator
{
    public static IReadOnlyList<ValidationIssue> Validate(FilterGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var issues = new List<ValidationIssue>();

        var produced = new Dictionary<string, StreamRef>(StringComparer.Ordinal);
        var consumedBy = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        // Girdiler grafiğin kaynakları; hepsi baştan üretilmiş sayılır.
        foreach (var input in graph.Inputs)
        {
            var kinds = input.Kind switch
            {
                InputKind.Image => new[] { MediaKind.Video },
                InputKind.Audio => [MediaKind.Audio],
                _ => [MediaKind.Video, MediaKind.Audio],
            };

            foreach (var kind in kinds)
            {
                var streamRef = new StreamRef(input.Id, kind);
                if (!produced.TryAdd(streamRef.ToString(), streamRef))
                {
                    issues.Add(new("graph.duplicate_input", $"Girdi kimliği tekrarlanıyor: {input.Id}"));
                }
            }

            if (string.IsNullOrWhiteSpace(input.Path))
            {
                issues.Add(new("graph.input_no_path", $"'{input.Id}' girdisinin yolu boş."));
            }
        }

        foreach (var node in graph.Nodes)
        {
            foreach (var output in node.Outputs)
            {
                if (!produced.TryAdd(output.ToString(), output))
                {
                    issues.Add(new("graph.duplicate_stream",
                        $"'{output}' akışı birden fazla düğüm tarafından üretiliyor " +
                        $"(son üreten: {node.Filter})."));
                }
            }
        }

        // Tüketim: her akış tam bir kez tüketilmeli. Filter graph'ta bir pad'i
        // iki yerde kullanmak `split` gerektirir; sessizce yapılırsa FFmpeg
        // anlaşılmaz bir hata verir.
        foreach (var node in graph.Nodes)
        {
            foreach (var input in node.Inputs)
            {
                var key = input.ToString();

                if (!produced.ContainsKey(key))
                {
                    issues.Add(new("graph.unknown_stream",
                        $"'{node.Filter}' düğümü üretilmemiş bir akışa başvuruyor: {key}"));
                }

                if (!consumedBy.TryGetValue(key, out var consumers))
                {
                    consumers = [];
                    consumedBy[key] = consumers;
                }

                consumers.Add(node.Filter);
            }
        }

        foreach (var (key, consumers) in consumedBy.Where(kv => kv.Value.Count > 1))
        {
            issues.Add(new("graph.multiple_consumers",
                $"'{key}' akışı {consumers.Count} kez tüketiliyor ({string.Join(", ", consumers)}); " +
                "split gerekir."));
        }

        // Çıkışlar üretilmiş ve tüketilmemiş olmalı.
        var outputs = new List<(string Name, StreamRef Stream)>();

        if (graph.VideoOut is { } video)
        {
            outputs.Add(("video", video));
        }

        if (graph.AudioOut is { } audio)
        {
            outputs.Add(("ses", audio));
        }

        // HİÇ ÇIKIŞI OLMAYAN GRAFİK BİR HATA.
        //
        // Video ve ses birlikte nullable olunca "ikisi de yok" hâli
        // derleyiciye geçerli görünüyor — ffmpeg ise böyle bir komutta
        // hiçbir şey üretmeden başarıyla çıkabiliyor. Sessiz başarı
        // burada yakalanıyor.
        if (outputs.Count == 0)
        {
            issues.Add(new("graph.no_output", "Grafiğin ne video ne ses çıkışı var."));
        }

        foreach (var (name, output) in outputs)
        {
            var key = output.ToString();

            if (!produced.ContainsKey(key))
            {
                issues.Add(new("graph.output_missing", $"Grafiğin {name} çıkışı üretilmiyor: {key}"));
            }
            else if (consumedBy.ContainsKey(key))
            {
                issues.Add(new("graph.output_consumed",
                    $"Grafiğin {name} çıkışı ({key}) başka bir düğüm tarafından tüketiliyor."));
            }
        }

        if (graph.VideoOut is { } videoOut && videoOut.Kind != MediaKind.Video)
        {
            issues.Add(new("graph.output_kind", "Video çıkışı video akışı olmalı."));
        }

        if (graph.AudioOut is { } audioOut && audioOut.Kind != MediaKind.Audio)
        {
            issues.Add(new("graph.output_kind", "Ses çıkışı ses akışı olmalı."));
        }

        // Kullanılmayan ara akışlar: FFmpeg bunlara "Output pad not connected"
        // der ve render hiç başlamaz.
        var dangling = produced.Keys
            .Where(k => !consumedBy.ContainsKey(k))
            .Where(k => k != graph.VideoOut?.ToString() && k != graph.AudioOut?.ToString())
            .Where(k => !graph.Inputs.Any(i => k.StartsWith(i.Id + ":", StringComparison.Ordinal)))
            .ToList();

        foreach (var key in dangling)
        {
            issues.Add(new("graph.dangling_stream", $"'{key}' akışı üretiliyor ama kullanılmıyor."));
        }

        issues.AddRange(DetectCycle(graph));

        return issues;
    }

    /// Döngü tespiti. Filter graph yönlü ve döngüsüz olmak zorunda;
    /// döngü olursa FFmpeg kilitlenir ya da anlamsız bir hata verir.
    private static List<ValidationIssue> DetectCycle(FilterGraph graph)
    {
        var producer = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i < graph.Nodes.Count; i++)
        {
            foreach (var output in graph.Nodes[i].Outputs)
            {
                producer[output.ToString()] = i;
            }
        }

        var state = new int[graph.Nodes.Count];   // 0 = ziyaret edilmedi, 1 = yolda, 2 = bitti
        var issues = new List<ValidationIssue>();

        for (var i = 0; i < graph.Nodes.Count; i++)
        {
            if (Visit(i, graph, producer, state))
            {
                issues.Add(new("graph.cycle",
                    $"Filtre grafiğinde döngü var ('{graph.Nodes[i].Filter}' düğümünden erişilebiliyor)."));
                break;
            }
        }

        return issues;
    }

    private static bool Visit(int index, FilterGraph graph, Dictionary<string, int> producer, int[] state)
    {
        if (state[index] == 1)
        {
            return true;
        }

        if (state[index] == 2)
        {
            return false;
        }

        state[index] = 1;

        foreach (var input in graph.Nodes[index].Inputs)
        {
            if (producer.TryGetValue(input.ToString(), out var upstream) && Visit(upstream, graph, producer, state))
            {
                return true;
            }
        }

        state[index] = 2;
        return false;
    }
}
