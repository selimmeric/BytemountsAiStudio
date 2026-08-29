using System.Globalization;
using System.Text.Json;

namespace BytemountsAiStudio.Core.Content;

/// Kanalın altyazı görünümü ve müzik seviyeleri.
///
/// ***BU DOSYA, "AYAR VAR AMA ETKİSİ YOK" HATA SINIFININ EN GÖRÜNÜR
/// ÖRNEĞİNİ KAPATIYOR.***
///
/// `TextStyle`'ın kendi yorumu şunu söz veriyordu: "bir kanalın altyazı
/// stilini değiştirmek tek satır olsun (§11.3)". Gerçekte stilin tamamı
/// `TimelineBuilder` içinde SABİTTİ: iki kanal aynı graftan koşunca
/// altyazılar piksel piksel aynı çıkıyordu — kanal kimliğinin en görünür
/// parçası değiştirilemiyordu.
///
/// `TextStyle.FontFamily` daha kötüsüydü: yazılıyor ve HİÇBİR YERDE
/// OKUNMUYORDU. Çizim `timeline.FontStack` kullanıyor. Yani birisi
/// timeline JSON'unda ya da kodda yazı tipi adını değiştirdiğinde hiçbir
/// şey olmuyordu — müzik anahtarı ve en-boy ayarıyla aynı hikâye.
///
/// VARSAYILANLAR ESKİ SABİT DEĞERLERİN AYNISI: bu iş bir davranış
/// değişikliği değil, bir yapılandırma açması. Ayar yazmayan kanal
/// dünkü videoyla aynı videoyu üretiyor.
public sealed record CaptionStyle
{
    /// Altyazının kendi yazı tipi.
    ///
    /// `null` ise kanalın `font_stack`'i kullanılıyor. Ayrı bir alan
    /// olması gerçek bir ihtiyaç: kapak ve altyazı aynı kanalda farklı
    /// yazı tipi isteyebiliyor — kapakta ağır bir başlık yüzü, altyazıda
    /// okunaklı bir metin yüzü.
    public string? FontFamily { get; init; }

    /// Tuval yüksekliğinin yüzdesi. Piksel değil: 9:16 ve 16:9 arasında
    /// aynı stil bambaşka görünürdü.
    public double SizePercent { get; init; } = 5.5;

    public string Color { get; init; } = "#FFFFFF";

    public string? HighlightColor { get; init; } = "#FFD400";

    public string? StrokeColor { get; init; } = "#000000";

    public int StrokeWidth { get; init; } = 8;

    public string? BoxColor { get; init; } = "#000000";

    public double BoxOpacity { get; init; } = 0.35;

    /// `top_left`, `top_right`, `bottom_left`, `bottom_right`, `center`,
    /// `bottom_center`. Çizim katmanındaki `Anchor` ile aynı küme ama
    /// AYRI: `Core` katmanı `Media`'ya bakmıyor ve bakmamalı.
    public string Position { get; init; } = "bottom_center";

    public double OffsetPercent { get; init; } = 22;

    public int MaxLines { get; init; } = 2;

    public bool Bold { get; init; } = true;

    public static CaptionStyle Default { get; } = new();

    /// Bilinen konum adları. Doğrulama BURADA yapılıyor ki yazım hatası
    /// sessizce varsayılana düşmesin.
    public static IReadOnlyList<string> Positions { get; } =
    [
        "top_left", "top_right", "bottom_left", "bottom_right", "center", "bottom_center",
    ];

    /// `caption_style` bloğunu okur.
    ///
    /// BLOK YOKSA VARSAYILAN, hata değil: kanalların çoğu altyazı stiline
    /// dokunmayacak.
    public static CaptionStyle Read(JsonElement root, List<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(warnings);

        if (!root.TryGetProperty("caption_style", out var block)
            || block.ValueKind != JsonValueKind.Object)
        {
            return Default;
        }

        var position = SettingsJson.Text(block, "position") ?? Default.Position;

        if (!Positions.Contains(position, StringComparer.Ordinal))
        {
            // SESSİZCE VARSAYILANA DÜŞMÜYOR: "bottom-center" yazan biri
            // (alt çizgi yerine tire) altyazısının neden yer
            // değiştirmediğini asla anlayamazdı.
            warnings.Add(
                $"`caption_style.position` tanınmadı: '{position}'. "
                + $"Geçerli değerler: {string.Join(", ", Positions)}");

            position = Default.Position;
        }

        return new CaptionStyle
        {
            FontFamily = SettingsJson.Text(block, "font_family"),
            SizePercent = SettingsJson.Double(block, "size_percent", warnings,
                Default.SizePercent, min: 1, max: 25),
            Color = SettingsJson.Color(block, "color", warnings) ?? Default.Color,
            HighlightColor = block.TryGetProperty("highlight_color", out _)
                ? SettingsJson.Color(block, "highlight_color", warnings)
                : Default.HighlightColor,
            StrokeColor = block.TryGetProperty("stroke_color", out _)
                ? SettingsJson.Color(block, "stroke_color", warnings)
                : Default.StrokeColor,
            StrokeWidth = (int)SettingsJson.Double(block, "stroke_width", warnings,
                Default.StrokeWidth, min: 0, max: 40),
            BoxColor = block.TryGetProperty("box_color", out _)
                ? SettingsJson.Color(block, "box_color", warnings)
                : Default.BoxColor,
            BoxOpacity = SettingsJson.Double(block, "box_opacity", warnings,
                Default.BoxOpacity, min: 0, max: 1),
            Position = position,
            OffsetPercent = SettingsJson.Double(block, "offset_percent", warnings,
                Default.OffsetPercent, min: 0, max: 90),
            MaxLines = (int)SettingsJson.Double(block, "max_lines", warnings,
                Default.MaxLines, min: 1, max: 4),
            Bold = SettingsJson.Bool(block, "bold") ?? Default.Bold,
        };
    }
}

/// Kanalın müzik seviyeleri.
///
/// ***ÖNCEDEN YALNIZCA KAYIT VARSAYILANIYDI:*** müziği biraz öne
/// çıkarmak isteyen bir kanalın tek seçeneği müziği tamamen kapatmaktı.
/// Platform farkı da ifade edilemiyordu.
public sealed record MusicLevels
{
    /// Müziğin konuşma altındaki seviyesi (§13: müzik %8–15).
    public double GainDb { get; init; } = -22.0;

    /// Konuşma sırasında müziğin indiği seviye.
    public double DuckingDb { get; init; } = -30.0;

    /// Ducking açık mı.
    ///
    /// VARSAYILAN AÇIK: kapalı olsaydı müzik konuşmanın üstüne biner ve
    /// bunu ancak videoyu DİNLEYEN biri fark ederdi — mekanik QC ses
    /// seviyesine bakıyor ama "hangi ses" sorusuna cevap veremiyor.
    public bool Ducking { get; init; } = true;

    public int FadeInMs { get; init; } = 1200;

    public int FadeOutMs { get; init; } = 2000;

    public static MusicLevels Default { get; } = new();

    public static MusicLevels Read(JsonElement root, List<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(warnings);

        if (!root.TryGetProperty("music", out var block) || block.ValueKind != JsonValueKind.Object)
        {
            return Default;
        }

        return new MusicLevels
        {
            // SINIRLAR SESSİZ DEĞİL: `gain_db: 6` yazan biri müziği
            // konuşmanın ÜSTÜNE çıkarırdı ve videoyu dinlemeden bunu
            // kimse fark etmezdi. Üst sınır sıfır: müzik hiçbir zaman
            // referans seviyenin üstüne çıkmıyor.
            GainDb = SettingsJson.Double(block, "gain_db", warnings,
                Default.GainDb, min: -60, max: 0),
            DuckingDb = SettingsJson.Double(block, "ducking_db", warnings,
                Default.DuckingDb, min: -60, max: 0),
            Ducking = SettingsJson.Bool(block, "ducking") ?? Default.Ducking,
            FadeInMs = (int)SettingsJson.Double(block, "fade_in_ms", warnings,
                Default.FadeInMs, min: 0, max: 10_000),
            FadeOutMs = (int)SettingsJson.Double(block, "fade_out_ms", warnings,
                Default.FadeOutMs, min: 0, max: 10_000),
        };
    }
}

/// Ayar belgesinden tip okuyan ortak yardımcılar.
///
/// Tek yerde olmasının sebebi sınır kontrolü: her okuma yerinde ayrı
/// yazılsaydı biri sınırı unuturdu ve o alan sessizce saçma bir değer
/// kabul ederdi.
public static partial class SettingsJson
{
    public static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static bool? Bool(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
           && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : null;

    /// Sayı okur ve SINIRLARA UYUYOR MU diye bakar.
    ///
    /// Sınır dışı değer varsayılana düşüyor VE uyarıya yazılıyor: sessiz
    /// düşüş, "ayarımı neden uygulamıyor" sorusunu cevapsız bırakırdı.
    public static double Double(
        JsonElement element, string name, List<string> warnings,
        double fallback, double min, double max)
    {
        ArgumentNullException.ThrowIfNull(warnings);

        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return fallback;
        }

        if (!value.TryGetDouble(out var number))
        {
            return fallback;
        }

        if (number < min || number > max)
        {
            warnings.Add(string.Create(CultureInfo.GetCultureInfo("tr-TR"),
                $"`{name}` aralık dışı ({number}); {min}–{max} bekleniyor, varsayılan kullanılıyor"));

            return fallback;
        }

        return number;
    }

    /// Renk okur — `#RRGGBB` biçiminde olmak zorunda.
    ///
    /// Doğrulanmasaydı `"kirmizi"` yazan biri hiçbir uyarı almadan
    /// varsayılan beyaz altyazı görürdü.
    public static string? Color(JsonElement element, string name, List<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(warnings);

        var text = Text(element, name);

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!HexColor.IsMatch(text))
        {
            warnings.Add($"`{name}` renk biçimi tanınmadı: '{text}'. `#RRGGBB` bekleniyor");
            return null;
        }

        return text;
    }

    private static readonly System.Text.RegularExpressions.Regex HexColor = HexColorRegex();

    [System.Text.RegularExpressions.GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial System.Text.RegularExpressions.Regex HexColorRegex();
}
