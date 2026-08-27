using System.Globalization;

namespace BytemountsAiStudio.Media.Ir;

public enum MediaKind
{
    Video = 0,
    Audio = 1,
}

/// Grafikteki bir akışa isimli referans.
///
/// §12.1/L2 — bu tasarımın ana kazancı: akışlar İSİMLE anılır, konumla değil.
/// Studio'da girdi indeksleri elle korunan bir teamüle bağlıydı ve yeni bir
/// girdi tipi eklemek tüm indeksleri kaydırıyordu. Burada indeksleri emitter
/// en son atar; sıralamayı değiştirmek grafiği bozmaz.
public readonly record struct StreamRef(string Id, MediaKind Kind)
{
    public override string ToString() => $"{Id}:{(Kind == MediaKind.Video ? "v" : "a")}";
}

public enum InputKind
{
    /// Tek kare görsel. Süre `-loop 1` + `-t` ile verilir.
    Image = 0,
    Audio = 1,
    Video = 2,
}

/// FFmpeg'e verilecek bir girdi dosyası.
public sealed record InputDecl
{
    public required string Id { get; init; }

    public required string Path { get; init; }

    public required InputKind Kind { get; init; }

    /// Görsellerde zorunlu: tek kare, istenen süre boyunca döndürülür.
    public bool Loop { get; init; }

    /// Girdiden okunacak süre (saniye). `-t` olarak verilir.
    public double? DurationSeconds { get; init; }

    public double? FrameRate { get; init; }
}

/// Bir filtre argümanı. İsimli ya da konumsal olabilir.
public readonly record struct FilterArg(string? Key, string Value)
{
    public static FilterArg Named(string key, string value) => new(key, value);

    public static FilterArg Named(string key, int value)
        => new(key, value.ToString(CultureInfo.InvariantCulture));

    public static FilterArg Named(string key, double value)
        => new(key, value.ToString("0.######", CultureInfo.InvariantCulture));

    public static FilterArg Positional(string value) => new(null, value);
}

/// Filtre grafiğindeki tek bir düğüm.
///
/// Tasarım notu: her filtre için ayrı bir C# tipi yazmak yerine, filtre adı +
/// doğrulanmış argümanlar taşıyan tek bir kayıt kullanıyoruz; tip güvenliği
/// aşağıdaki fabrika metotlarında sağlanıyor. Mimarideki kazanımların tamamı
/// korunuyor — graf modeli, doğrulama, dot dökümü ve TEK escape noktası —
/// ama yirmi tip yazma maliyeti ödenmiyor. Bir filtrenin argümanları
/// karmaşıklaşırsa o filtreye özel bir fabrika eklenir.
public sealed record FilterNode
{
    public required string Filter { get; init; }

    public required IReadOnlyList<StreamRef> Inputs { get; init; }

    public required IReadOnlyList<StreamRef> Outputs { get; init; }

    public IReadOnlyList<FilterArg> Args { get; init; } = [];

    /// Grafik dökümünde okunabilirlik için; render'ı etkilemez.
    public string? Comment { get; init; }

    // ---- fabrikalar: tip güvenliği burada ----

    /// Görseli kadraja sığdır: orana göre büyüt, taşanı kes.
    public static FilterNode ScaleCover(StreamRef input, StreamRef output, int width, int height, double overscan = 1.0)
    {
        var w = (int)Math.Round(width * overscan);
        var h = (int)Math.Round(height * overscan);

        return new FilterNode
        {
            Filter = "scale",
            Inputs = [input],
            Outputs = [output],
            Args =
            [
                FilterArg.Named("w", w),
                FilterArg.Named("h", h),
                FilterArg.Named("force_original_aspect_ratio", "increase"),
            ],
            Comment = $"kadraja sigdir ({w}x{h}, overscan {overscan:0.##})",
        };
    }

    public static FilterNode Crop(StreamRef input, StreamRef output, int width, int height)
        => new()
        {
            Filter = "crop",
            Inputs = [input],
            Outputs = [output],
            Args = [FilterArg.Positional(width.ToString(CultureInfo.InvariantCulture)),
                    FilterArg.Positional(height.ToString(CultureInfo.InvariantCulture))],
            Comment = "tasan kismi kes",
        };

    /// Ken Burns. `zoom`, `x`, `y` ifadeleri kare numarası (`on`) üzerinden
    /// yazılır; §12.1'de korunmasına karar verilen "düz ifade" yaklaşımı.
    public static FilterNode Zoompan(
        StreamRef input, StreamRef output, Expr zoom, Expr x, Expr y,
        int frames, int width, int height, int fps)
        => new()
        {
            Filter = "zoompan",
            Inputs = [input],
            Outputs = [output],
            Args =
            [
                FilterArg.Named("z", zoom.Text),
                FilterArg.Named("x", x.Text),
                FilterArg.Named("y", y.Text),
                FilterArg.Named("d", frames),
                FilterArg.Named("s", $"{width}x{height}"),
                FilterArg.Named("fps", fps),
            ],
            Comment = $"ken burns, {frames} kare",
        };

    public static FilterNode SetSar(StreamRef input, StreamRef output)
        => new()
        {
            Filter = "setsar",
            Inputs = [input],
            Outputs = [output],
            Args = [FilterArg.Positional("1")],
            Comment = "piksel oranini normalize et",
        };

    public static FilterNode Format(StreamRef input, StreamRef output, string pixelFormat)
        => new()
        {
            Filter = "format",
            Inputs = [input],
            Outputs = [output],
            Args = [FilterArg.Positional(pixelFormat)],
        };

    /// Sahne sonu kararması. `st` saniye cinsinden sahne içi konumdur.
    public static FilterNode FadeOut(StreamRef input, StreamRef output, double startSeconds, double durationSeconds)
        => new()
        {
            Filter = "fade",
            Inputs = [input],
            Outputs = [output],
            Args =
            [
                FilterArg.Named("t", "out"),
                FilterArg.Named("st", startSeconds),
                FilterArg.Named("d", durationSeconds),
            ],
            Comment = "sahne sonu kararma",
        };

    public static FilterNode ConcatVideo(IReadOnlyList<StreamRef> inputs, StreamRef output)
        => new()
        {
            Filter = "concat",
            Inputs = inputs,
            Outputs = [output],
            Args =
            [
                FilterArg.Named("n", inputs.Count),
                FilterArg.Named("v", 1),
                FilterArg.Named("a", 0),
            ],
            Comment = $"{inputs.Count} sahneyi birlestir",
        };

    /// Ses parçasını zaman çizelgesindeki yerine kaydır.
    public static FilterNode ADelay(StreamRef input, StreamRef output, int milliseconds)
        => new()
        {
            Filter = "adelay",
            Inputs = [input],
            Outputs = [output],
            Args =
            [
                FilterArg.Named("delays", milliseconds.ToString(CultureInfo.InvariantCulture)),
                FilterArg.Named("all", 1),
            ],
            Comment = $"{milliseconds} ms geciktir",
        };

    /// Parçaları tek ses akışında topla.
    ///
    /// `normalize=0` kritik: varsayılan normalize, girdi sayısına bölerek
    /// sesi kısar. Beş parçalı bir videoda konuşma duyulmaz hâle gelirdi.
    public static FilterNode AMix(IReadOnlyList<StreamRef> inputs, StreamRef output)
        => new()
        {
            Filter = "amix",
            Inputs = inputs,
            Outputs = [output],
            Args =
            [
                FilterArg.Named("inputs", inputs.Count),
                FilterArg.Named("normalize", 0),
                FilterArg.Named("dropout_transition", 0),
            ],
            Comment = $"{inputs.Count} ses parcasini karistir",
        };

    public static FilterNode Volume(StreamRef input, StreamRef output, double gainDb)
        => new()
        {
            Filter = "volume",
            Inputs = [input],
            Outputs = [output],
            Args = [FilterArg.Named("volume", $"{gainDb.ToString("0.##", CultureInfo.InvariantCulture)}dB")],
        };

    /// Sesi tam video süresine oturt: kısaysa sessizlikle uzat, uzunsa kes.
    /// İkisi birlikte olmazsa çıktı süresi videodan sapar ve QC'de düşer.
    public static FilterNode APadTrim(StreamRef input, StreamRef output, double durationSeconds)
        => new()
        {
            Filter = "apad",
            Inputs = [input],
            Outputs = [output],
            Args = [FilterArg.Named("whole_dur", durationSeconds)],
            Comment = "sesi video suresine tamamla",
        };

    public static FilterNode ATrim(StreamRef input, StreamRef output, double durationSeconds)
        => new()
        {
            Filter = "atrim",
            Inputs = [input],
            Outputs = [output],
            Args = [FilterArg.Named("duration", durationSeconds)],
        };

    public static FilterNode ALoudNorm(StreamRef input, StreamRef output, double targetLufs)
        => new()
        {
            Filter = "loudnorm",
            Inputs = [input],
            Outputs = [output],
            Args = [FilterArg.Named("I", targetLufs), FilterArg.Named("TP", -1.5), FilterArg.Named("LRA", 11)],
            Comment = "ses seviyesini yayin standardina getir",
        };
}

/// Tam filtre grafiği. Emitter'ın girdisi.
public sealed record FilterGraph
{
    public required IReadOnlyList<InputDecl> Inputs { get; init; }

    public required IReadOnlyList<FilterNode> Nodes { get; init; }

    public required StreamRef VideoOut { get; init; }

    public required StreamRef AudioOut { get; init; }
}
