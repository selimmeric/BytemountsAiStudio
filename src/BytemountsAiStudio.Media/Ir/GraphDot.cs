using System.Globalization;
using System.Text;

namespace BytemountsAiStudio.Media.Ir;

/// Filtre grafiğini Graphviz `dot` biçiminde döker.
///
/// §12.3: render patladığında 12 KB'lık bir metne değil, bir resme bakmak
/// istiyoruz. FFmpeg'in kendi `graph2dot` aracının yaptığı işin aynısı —
/// farkı, bunun FFmpeg çalıştırılmadan ÖNCE yapılabilmesi.
public static class GraphDot
{
    public static string Render(FilterGraph graph, string name = "filtergraph")
    {
        ArgumentNullException.ThrowIfNull(graph);

        var builder = new StringBuilder();
        builder.Append("digraph ").Append(Identifier(name)).AppendLine(" {");
        builder.AppendLine("  rankdir=LR;");
        builder.AppendLine("  node [shape=box, style=rounded, fontname=\"IBM Plex Mono\"];");
        builder.AppendLine();

        // Girdiler kaynak; farklı biçimle çiziliyor ki grafiğin nereden
        // başladığı bir bakışta görünsün.
        foreach (var input in graph.Inputs)
        {
            builder.Append("  ").Append(Identifier(input.Id))
                .Append(" [shape=folder, label=\"")
                .Append(Escape(input.Id)).Append("\\n")
                .Append(Escape(Path.GetFileName(input.Path)))
                .AppendLine("\"];");
        }

        builder.AppendLine();

        for (var i = 0; i < graph.Nodes.Count; i++)
        {
            var node = graph.Nodes[i];
            var id = $"n{i.ToString(CultureInfo.InvariantCulture)}";

            var label = node.Comment is null
                ? node.Filter
                : $"{node.Filter}\\n{Escape(node.Comment)}";

            builder.Append("  ").Append(id).Append(" [label=\"").Append(label).AppendLine("\"];");

            foreach (var input in node.Inputs)
            {
                builder.Append("  ").Append(SourceOf(input, graph))
                    .Append(" -> ").Append(id)
                    .Append(" [label=\"").Append(Escape(input.ToString())).AppendLine("\"];");
            }
        }

        builder.AppendLine();

        // GÖRÜNTÜSÜZ GRAFİKTE VİDEO DÜĞÜMÜ ÇİZİLMİYOR (P6-05) —
        // sessiz grafikte ses düğümünün çizilmemesiyle aynı sebep.
        if (graph.VideoOut is { } videoOut)
        {
            builder.Append("  out_video [shape=doublecircle, label=\"")
                .Append(Escape(videoOut.ToString())).AppendLine("\"];");
            builder.Append("  ").Append(SourceOf(videoOut, graph)).AppendLine(" -> out_video;");
        }

        // SESSİZ GRAFİKTE SES DÜĞÜMÜ HİÇ ÇİZİLMİYOR.
        //
        // Boş bir düğüm çizmek, diyagrama bakan birine "ses var ama
        // bağlanmamış" dedirtirdi — oysa bölüm bazlı render'da (P2-11)
        // sessizlik kasıtlı.
        if (graph.AudioOut is { } audioOut)
        {
            builder.Append("  out_audio [shape=doublecircle, label=\"")
                .Append(Escape(audioOut.ToString())).AppendLine("\"];");
            builder.Append("  ").Append(SourceOf(audioOut, graph)).AppendLine(" -> out_audio;");
        }

        builder.AppendLine("}");
        return builder.ToString();
    }

    private static string SourceOf(StreamRef streamRef, FilterGraph graph)
    {
        for (var i = 0; i < graph.Nodes.Count; i++)
        {
            if (graph.Nodes[i].Outputs.Contains(streamRef))
            {
                return $"n{i.ToString(CultureInfo.InvariantCulture)}";
            }
        }

        return Identifier(streamRef.Id);
    }

    /// Graphviz tanımlayıcıları sınırlı bir karakter kümesi kabul ediyor.
    private static string Identifier(string value)
    {
        var builder = new StringBuilder(value.Length + 1);
        builder.Append('_');

        foreach (var c in value)
        {
            builder.Append(char.IsLetterOrDigit(c) ? c : '_');
        }

        return builder.ToString();
    }

    private static string Escape(string value)
        => value.Replace("\"", "\\\"", StringComparison.Ordinal);
}
