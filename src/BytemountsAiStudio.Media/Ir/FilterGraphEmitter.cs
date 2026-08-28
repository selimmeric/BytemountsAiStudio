using System.Buffers;
using System.Globalization;
using System.Text;

namespace BytemountsAiStudio.Media.Ir;

public sealed record EmittedCommand(IReadOnlyList<string> Arguments, string FilterComplex);

/// IR → FFmpeg metni.
///
/// §12.1/L1 ve L2: kaçış (escape) ve girdi indeksi ataması YALNIZCA burada
/// yapılır. Studio'da bu iş grafiğin her yerine dağılmıştı; bir yerde
/// unutulan tırnak, hata mesajı olmayan bir render çökmesi demekti.
public static class FilterGraphEmitter
{
    /// `filter_complex` metnini üretir.
    ///
    /// Girdi kimlikleri burada sayıya çevrilir; graf hangi sırayla kurulmuş
    /// olursa olsun sonuç aynı — L2'nin çözdüğü hata sınıfı bu.
    public static string EmitFilterComplex(FilterGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var inputIndex = graph.Inputs
            .Select((input, index) => (input.Id, index))
            .ToDictionary(x => x.Id, x => x.index, StringComparer.Ordinal);

        var builder = new StringBuilder();

        for (var i = 0; i < graph.Nodes.Count; i++)
        {
            var node = graph.Nodes[i];

            foreach (var input in node.Inputs)
            {
                builder.Append('[').Append(Label(input, inputIndex)).Append(']');
            }

            builder.Append(node.Filter);

            if (node.Args.Count > 0)
            {
                builder.Append('=').Append(EmitArgs(node.Args));
            }

            foreach (var output in node.Outputs)
            {
                builder.Append('[').Append(Label(output, inputIndex)).Append(']');
            }

            if (i < graph.Nodes.Count - 1)
            {
                builder.Append(';').Append('\n');
            }
        }

        return builder.ToString();
    }

    /// Tam ffmpeg argüman listesi.
    ///
    /// Filtre grafiği komut satırına değil DOSYAYA yazılır
    /// (`-filter_complex_script`): Studio'da öğrenilen ders — karmaşık
    /// grafikler Windows'un komut satırı uzunluk sınırını aşıyor.
    public static EmittedCommand Emit(
        FilterGraph graph,
        string filterScriptPath,
        string outputPath,
        OutputOptions options)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(options);

        var args = new List<string>
        {
            "-hide_banner",
            "-nostdin",
            "-y",
            "-loglevel", "error",
            "-progress", "pipe:1",
        };

        foreach (var input in graph.Inputs)
        {
            if (input.Loop)
            {
                args.Add("-loop");
                args.Add("1");
            }

            if (input.FrameRate is { } fps)
            {
                args.Add("-framerate");
                args.Add(fps.ToString("0.####", CultureInfo.InvariantCulture));
            }

            if (input.DurationSeconds is { } duration)
            {
                // `-t` girdiden ÖNCE gelmeli; sonra gelirse çıktıya uygulanır
                // ve tüm videoyu kırpar.
                args.Add("-t");
                args.Add(duration.ToString("0.###", CultureInfo.InvariantCulture));
            }

            // `-itsoffset` GIRDIDEN ONCE ve `-t`'den SONRA: `-t` girdiden
            // okunacak sureyi, `-itsoffset` o surenin zaman ekseninde
            // nereye oturacagini belirliyor. Sirasi degisirse katman
            // yanlis anda gorunur.
            if (input.OffsetSeconds is { } offset && offset > 0)
            {
                args.Add("-itsoffset");
                args.Add(offset.ToString("0.###", CultureInfo.InvariantCulture));
            }

            args.Add("-i");
            args.Add(input.Path);
        }

        args.Add("-filter_complex_script");
        args.Add(filterScriptPath);

        args.Add("-map");
        args.Add($"[{graph.VideoOut.Id}]");
        if (graph.AudioOut is { } audioOut)
        {
            args.Add("-map");
            args.Add($"[{audioOut.Id}]");
        }

        args.Add("-c:v");
        args.Add(options.VideoCodec);
        args.Add("-crf");
        args.Add(options.Crf.ToString(CultureInfo.InvariantCulture));
        args.Add("-preset");
        args.Add(options.PresetSpeed);
        args.Add("-pix_fmt");
        args.Add(options.PixelFormat);
        args.Add("-r");
        args.Add(options.FrameRate.ToString(CultureInfo.InvariantCulture));

        // ANAHTAR KARE ARALIĞI (P3-02): oynatıcı yalnızca anahtar
        // kareye atlayabiliyor. Sınır yokken x264 anahtar kareleri
        // sahne değişimine göre seçiyor ve "3. bölüme atla"
        // saniyelerce sapabiliyor.
        //
        // SINIR, HEDEF DEĞİL: sahne değişimi daha sık anahtar kare
        // ekleyebilir ve bu iyi — atlama daha da isabetli olur.
        if (options.KeyframeInterval is { } keyframeInterval && keyframeInterval > 0)
        {
            args.Add("-g");
            args.Add(keyframeInterval.ToString(CultureInfo.InvariantCulture));
        }

        args.Add("-c:a");
        args.Add(options.AudioCodec);
        args.Add("-b:a");
        args.Add(options.AudioBitrate);

        // Süreyi açıkça sabitliyoruz: ses ve video akışlarından biri bir kare
        // uzun bittiğinde çıktı süresi sapar ve QC'de düşer.
        args.Add("-t");
        args.Add(options.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture));

        args.Add("-movflags");
        args.Add("+faststart");

        args.Add(outputPath);

        return new EmittedCommand(args, EmitFilterComplex(graph));
    }

    private static string Label(StreamRef streamRef, Dictionary<string, int> inputIndex)
        => inputIndex.TryGetValue(streamRef.Id, out var index)
            ? $"{index.ToString(CultureInfo.InvariantCulture)}:{(streamRef.Kind == MediaKind.Video ? "v" : "a")}"
            : streamRef.Id;

    private static string EmitArgs(IReadOnlyList<FilterArg> args)
        => string.Join(':', args.Select(a =>
            a.Key is null ? Escape(a.Value) : $"{a.Key}={Escape(a.Value)}"));

    /// Filtre argümanı kaçışı — TEK NOKTA.
    ///
    /// Filtre sözdiziminde `:` argümanları, `,` filtreleri, `;` zincirleri
    /// ayırır; `'` ve `\` da özeldir. İfadeler bu karakterleri sıkça içerir
    /// (`min(1,on/29)` gibi), o yüzden tırnak içine alıp içeriği kaçırıyoruz.
    private static readonly SearchValues<char> SpecialCharacters =
        SearchValues.Create(":,;[]' \\");

    private static string Escape(string value)
    {
        var needsQuoting = value.AsSpan().IndexOfAny(SpecialCharacters) >= 0;

        if (!needsQuoting)
        {
            return value;
        }

        var escaped = value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);

        return $"'{escaped}'";
    }
}

public sealed record OutputOptions
{
    public string VideoCodec { get; init; } = "libx264";

    public int Crf { get; init; } = 20;

    public string PresetSpeed { get; init; } = "medium";

    /// Anahtar kareler arası EN ÇOK kaç KARE. `null` = kodlayıcı
    /// kendi bilir.
    ///
    /// Saniye değil kare, çünkü ffmpeg'in `-g` argümanı kare
    /// istiyor; saniyeden kareye çeviri kare hızını bilen yerde
    /// (planlayıcı) yapılıyor.
    public int? KeyframeInterval { get; init; }

    public string PixelFormat { get; init; } = "yuv420p";

    public string AudioCodec { get; init; } = "aac";

    public string AudioBitrate { get; init; } = "192k";

    public required int FrameRate { get; init; }

    public required double DurationSeconds { get; init; }
}
