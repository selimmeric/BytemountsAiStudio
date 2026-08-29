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

    /// Girdinin zaman ekseninde kaydırılacağı saniye (`-itsoffset`).
    ///
    /// NEDEN VAR (P4-09): altyazı katmanları eskiden VİDEONUN TAMAMI
    /// boyunca döngüye alınıyor ve ne zaman görüneceğini yalnızca
    /// `enable` belirliyordu. Sonuç: 48 saniyelik bir videoda 97
    /// altyazı için 97 × 1.440 = 140.000 kare üretiliyordu — her biri
    /// bir saniyeden kısa görünen katmanlar için.
    ///
    /// Ölçüldü: tek render 31,5 GB bellek ve 280 saniye. Üç render
    /// aynı makinede koşunca 64 GB RAM tükendi ve sistem takasa
    /// girdi.
    ///
    /// Kaydırma ile her katman YALNIZCA kendi penceresi kadar
    /// üretiliyor ve doğru ana yerleşiyor. Eski yorum "girdiyi kendi
    /// aralığına kırpmak overlay'in zaman eksenini kaydırırdı"
    /// diyordu — doğruydu, eksik olan şey kaydırmanın kendisiydi.
    public double? OffsetSeconds { get; init; }
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

    /// Şeffaf bir katmanı ana görüntünün üzerine bindirir.
    ///
    /// `enable` aralığı verildiğinde katman yalnızca o pencerede görünür —
    /// altyazı için kare dizisi üretmek yerine kullandığımız mekanizma bu
    /// (§12.4). Görüntü tüm video boyunca girdi olarak durur, ama yalnızca
    /// kendi zaman aralığında çizilir.
    public static FilterNode Overlay(
        StreamRef main, StreamRef layer, StreamRef output,
        string x = "0", string y = "0",
        (double StartSeconds, double EndSeconds)? enable = null)
    {
        var args = new List<FilterArg>
        {
            FilterArg.Named("x", x),
            FilterArg.Named("y", y),
        };

        if (enable is { } window)
        {
            args.Add(FilterArg.Named("enable",
                $"between(t,{window.StartSeconds.ToString("0.###", CultureInfo.InvariantCulture)}," +
                $"{window.EndSeconds.ToString("0.###", CultureInfo.InvariantCulture)})"));
        }

        return new FilterNode
        {
            Filter = "overlay",
            Inputs = [main, layer],
            Outputs = [output],
            Args = args,
            Comment = enable is null ? "kalici katman" : "zamanli katman",
        };
    }

    /// Katmanın saydamlığını ayarlar.
    ///
    /// `format=rgba` ÖNCE gerekiyor: alfa kanalı olmayan bir görselde
    /// `colorchannelmixer` saydamlık üretemiyor ve filigran tam opak
    /// çıkıyor. PNG'de alfa zaten var ama JPEG'de yok, ve filigranın
    /// hangi biçimde geleceğini önceden bilmiyoruz.
    public static FilterNode Opacity(StreamRef input, StreamRef output, double opacity)
        => new()
        {
            Filter = "colorchannelmixer",
            Inputs = [input],
            Outputs = [output],
            Args = [FilterArg.Named("aa", Math.Clamp(opacity, 0.0, 1.0))],
            Comment = $"saydamlik {opacity.ToString("0.##", CultureInfo.InvariantCulture)}",
        };

    /// Piksel biçimini zorlar. Alfa gerektiren işlemlerden önce.
    public static FilterNode FormatRgba(StreamRef input, StreamRef output)
        => new()
        {
            Filter = "format",
            Inputs = [input],
            Outputs = [output],
            Args = [FilterArg.Named("pix_fmts", "rgba")],
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

    /// Sahne başı açılması: siyahtan görüntüye.
    ///
    /// `st` her zaman 0 — sahnenin BAŞI. Parametre almıyor, çünkü
    /// "sahnenin ortasında açılma" diye bir şey yok ve serbest
    /// bırakmak sessizce yanlış yazılabilecek bir sayı eklerdi.
    public static FilterNode FadeIn(StreamRef input, StreamRef output, double durationSeconds)
        => new()
        {
            Filter = "fade",
            Inputs = [input],
            Outputs = [output],
            Args =
            [
                FilterArg.Named("t", "in"),
                FilterArg.Named("st", 0),
                FilterArg.Named("d", durationSeconds),
            ],
            Comment = "sahne başı açılma",
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

    /// Bir ses akışını iki kopyaya ayırır.
    ///
    /// Ducking için ŞART: konuşma hem karışıma hem sidechain tetiğine
    /// gidiyor ve FFmpeg'de bir FİLTRE ÇIKIŞI yalnızca BİR KEZ
    /// tüketilebiliyor.
    ///
    /// Ayrım önemli ve canlı denemede öğrenildi: ham girdi pad'lerini
    /// (`[1:a]`) FFmpeg kendisi çoğaltıyor, onlarda `asplit` gerekmiyor.
    /// Bizim konuşma akışımız ham girdi DEĞİL — `amix`/`apad`
    /// zincirinden çıkan bir filtre çıktısı, dolayısıyla kural bize
    /// uyguluyor. Ayırmadan bağlamak "filtre grafiği geçersiz" hatası
    /// veriyor ve o mesaj sorunun nerede olduğunu hiç söylemiyor.
    /// İkisi de `DuckingFfmpegTests` içinde gerçek FFmpeg'e karşı
    /// sabitlendi.
    public static FilterNode ASplit(StreamRef input, IReadOnlyList<StreamRef> outputs)
        => new()
        {
            Filter = "asplit",
            Inputs = [input],
            Outputs = outputs,
            Args = [FilterArg.Positional(outputs.Count.ToString(CultureInfo.InvariantCulture))],
            Comment = $"ses akisini {outputs.Count} kopyaya ayir",
        };

    /// Müziği video süresince tekrarlar.
    ///
    /// `-1` sonsuz döngü demek; süreye oturtmayı sonraki `atrim`
    /// yapıyor. Döngü olmadan kısa bir müzik parçası videonun
    /// ortasında bitip geri kalanı sessiz bırakırdı.
    public static FilterNode ALoop(StreamRef input, StreamRef output)
        => new()
        {
            Filter = "aloop",
            Inputs = [input],
            Outputs = [output],
            Args =
            [
                FilterArg.Named("loop", -1),
                // Ornek sayisi: 2^31-1, pratikte "tamamini tekrarla".
                FilterArg.Named("size", 2147483647),
            ],
            Comment = "muzigi video suresince tekrarla",
        };

    public static FilterNode AFadeIn(StreamRef input, StreamRef output, double durationSeconds)
        => new()
        {
            Filter = "afade",
            Inputs = [input],
            Outputs = [output],
            Args =
            [
                FilterArg.Named("t", "in"),
                FilterArg.Named("st", 0),
                FilterArg.Named("d", durationSeconds),
            ],
        };

    public static FilterNode AFadeOut(
        StreamRef input, StreamRef output, double startSeconds, double durationSeconds)
        => new()
        {
            Filter = "afade",
            Inputs = [input],
            Outputs = [output],
            Args =
            [
                FilterArg.Named("t", "out"),
                FilterArg.Named("st", startSeconds),
                FilterArg.Named("d", durationSeconds),
            ],
        };

    /// Ducking: konuşma varken müziği kıs.
    ///
    /// İlk girdi KISILAN (müzik), ikinci girdi TETİK (konuşma). Sıra
    /// ters olursa müzik konuşmayı kısar — teknik olarak geçerli bir
    /// grafik, ama tam tersi bir video.
    ///
    /// `attack` ve `release` doğrudan duyulan şeyi belirliyor: attack
    /// çok yüksekse müzik konuşmanın ilk hecesini yutuyor, release çok
    /// düşükse cümle aralarında müzik pompalıyor.
    public static FilterNode SidechainCompress(
        StreamRef music, StreamRef trigger, StreamRef output,
        double reductionDb, int attackMs, int releaseMs)
        => new()
        {
            Filter = "sidechaincompress",
            Inputs = [music, trigger],
            Outputs = [output],
            Args =
            [
                FilterArg.Named("threshold", 0.03),
                // Oran, istenen kisma miktarindan turetiliyor: kullanici
                // "ne kadar kisilsin" diye dusunuyor, "orani kac olsun"
                // diye degil.
                FilterArg.Named("ratio", Math.Clamp(Math.Abs(reductionDb) / 2.0, 2.0, 20.0)),
                FilterArg.Named("attack", attackMs),
                FilterArg.Named("release", releaseMs),
                FilterArg.Named("makeup", 1),
            ],
            Comment = $"konusma varken muzigi {Math.Abs(reductionDb):0.#} dB kis",
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

    /// Video çıkışı. `null` ise plan SESSİZ DEĞİL, GÖRÜNTÜSÜZ:
    /// podcast rendition'ı (P6-05) yalnızca ses üretiyor.
    ///
    /// `AudioOut` ilk günden nullable'dı; video da olmalıydı. Zorunlu
    /// kalması, "yalnızca ses" diye bir çıktının var olamayacağını
    /// söylüyordu.
    public StreamRef? VideoOut { get; init; }

    /// Ses çıkışı. `null` = SESSİZ video ve bu geçerli bir durum
    /// (P2-11): bölüm bazlı render'da segmentler sessiz üretiliyor,
    /// ses birleştirmeden sonra tek seferde biniyor. Sesi de bölmek,
    /// cümlelerin segment sınırlarında kesilmesi demekti.
    public StreamRef? AudioOut { get; init; }
}
