using System.Globalization;
using System.Text.Json;

namespace BytemountsAiStudio.Core.Execution;

/// Bir kanalın ayar belgesinden okunan çalışma kuralları (P2-01/02/03/12).
///
/// SAF: veritabanı yok. Ayar okuma, gerçek bir kanal kurulup gece
/// beklenerek sınanacak bir şey olmamalı.
///
/// TEK BELGE, ÇOK POLİTİKA: tempo, bütçe ve tür karışımı aynı JSON'dan
/// geliyor çünkü hepsi "bu kanal nasıl çalışsın" sorusunun parçası ve
/// ayrı tablolara bölmek her yeni ayarda şema göçü demekti.
public sealed record ChannelSettings
{
    public required ChannelPacing Pacing { get; init; }

    public BudgetAction BudgetAction { get; init; } = BudgetAction.FinishInFlight;

    /// Tür karışımı (P2-12). Boşsa sürekli mod tür seçmiyor.
    public IReadOnlyList<ContentGenre> Genres { get; init; } = [];

    /// Hangi iş akışıyla üretiliyor. Boşsa çağıran varsayılanı seçiyor.
    public string? WorkflowKey { get; init; }

    /// Kanalın sesi (P3-01).
    ///
    /// KANALIN KİMLİĞİNİN PARÇASI: iki kanal aynı grafla koşup farklı
    /// sesle konuşabilmeli. Ayar kaydediliyordu ama hiçbir yerde
    /// okunmuyordu — ses yalnızca node ayarından geliyordu, yani
    /// kanalı değiştirmek sesi değiştirmiyordu.
    public string? VoiceId { get; init; }

    /// Yazı tipi zinciri (P3-01).
    ///
    /// Aynı hikâye: kanal ayarında duruyordu, timeline sabit bir liste
    /// kullanıyordu. Bir kanalın altyazı karakterini değiştirmek
    /// imkânsızdı — üstelik dile göre farklı yazı tipi gerekebiliyor
    /// (Arapça, Japonca).
    ///
    /// Boşsa `null`: çağıran kendi varsayılanını seçsin. Boş liste
    /// dönmek "yazı tipi yok" demekti ve o hâlde hiçbir altyazı
    /// çizilemezdi.
    public IReadOnlyList<string>? FontStack { get; init; }

    /// Onay rejimi (P2-08).
    public ChannelMode? Mode { get; init; }

    /// AYARDA ANLAŞILMAYAN NE VARSA BURADA.
    ///
    /// Varsayılana düşmek doğru davranış ama SESSİZCE düşmek değil:
    /// `daily_target` yerine `dailyTarget` yazan biri, kanalının günde
    /// bir video ürettiğini aylar sonra fark ederdi. Uyarılar
    /// kaydediliyor ve panelde görünüyor.
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public static ChannelSettings Defaults { get; } = new()
    {
        Pacing = new ChannelPacing { DailyTarget = 1 },
    };

    public static ChannelSettings Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Defaults;
        }

        JsonElement root;

        try
        {
            using var document = JsonDocument.Parse(json);
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            // BOZUK BELGE KANALI DURDURMUYOR.
            //
            // Durdurmak, bir virgül hatasının bütün üretimi kesmesi
            // demekti. Varsayılanla devam ediyor ve uyarı görünür
            // kalıyor — yapılandırma hatasının bedeli yanlış tempo
            // olmalı, hiç video olmaması değil.
            return Defaults with { Warnings = [$"ayar belgesi okunamadı: {ex.Message}"] };
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return Defaults with { Warnings = ["ayar belgesi bir nesne değil"] };
        }

        var warnings = new List<string>();

        return new ChannelSettings
        {
            Pacing = ReadPacing(root, warnings),
            BudgetAction = BudgetPolicy.ParseAction(Text(root, "action_on_exceed")),
            Genres = ReadGenres(root, warnings),
            WorkflowKey = Text(root, "workflow_key"),
            VoiceId = ReadVoiceId(root),
            FontStack = ReadFontStack(root, warnings),
            Warnings = warnings,
        };
    }

    private static ChannelPacing ReadPacing(JsonElement root, List<string> warnings)
    {
        // TEMPO AYARLARI HEM `pacing` ICINDE HEM KOKTE ARANIYOR.
        //
        // Yalnizca birine bakmak, yarisini ic ice yarisini kokte yazan
        // birinin ayarlarinin sessizce yok sayilmasi demekti — ve bu
        // gercekten oldu: `daily_target` kokte, `time_zone` icerideyken
        // hedef gorulmuyordu. Ses kimliginde de ayni esneklik var
        // (`voice.voice_id` ve duz `voice_id`).
        var nested = root.TryGetProperty("pacing", out var block) && block.ValueKind == JsonValueKind.Object
            ? block
            : default;

        var pacing = nested.ValueKind == JsonValueKind.Object ? nested : root;

        var target = Int(pacing, "daily_target") ?? Int(root, "daily_target");

        if (target is null)
        {
            warnings.Add("`daily_target` yok; günde 1 video varsayıldı");
        }
        else if (target < 0)
        {
            warnings.Add($"`daily_target` negatif ({target}); 0 sayıldı");
        }

        var zone = Text(pacing, "time_zone") ?? Text(root, "time_zone");

        if (zone is not null && !TimeZoneExists(zone))
        {
            // BİLİNMEYEN SAAT DİLİMİ UTC'YE DÜŞÜYOR ve bunu söylüyor:
            // pencereler sessizce kayarsa yayın hep yanlış saatte olur
            // ve sebebi hiçbir yerde yazmaz.
            warnings.Add($"saat dilimi tanınmadı ({zone}); UTC kullanılıyor");
            zone = "UTC";
        }

        return new ChannelPacing
        {
            DailyTarget = Math.Max(target ?? 1, 0),
            PublishWindows = ReadWindows(pacing, warnings) is { Count: > 0 } windows
                ? windows
                : ReadWindows(root, warnings),
            MinimumGap = ReadGap(pacing, warnings),
            TimeZoneId = zone ?? "Europe/Istanbul",
        };
    }

    private static List<TimeOnly> ReadWindows(JsonElement pacing, List<string> warnings)
    {
        if (!pacing.TryGetProperty("publish_windows", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var windows = new List<TimeOnly>();

        foreach (var item in array.EnumerateArray())
        {
            var text = item.ValueKind == JsonValueKind.String ? item.GetString() : null;

            if (TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed))
            {
                windows.Add(parsed);
                continue;
            }

            // OKUNAMAYAN PENCERE SESSİZCE ATLANMIYOR: atlamak,
            // "09:00, 13:0, 18:00" yazan bir kanalın günde üç yerine
            // iki video yayınlaması ve kimsenin bunu görmemesiydi.
            warnings.Add($"yayın penceresi okunamadı: '{text}' (HH:mm bekleniyor)");
        }

        windows.Sort();

        return windows;
    }

    private static TimeSpan ReadGap(JsonElement pacing, List<string> warnings)
    {
        var minutes = Int(pacing, "minimum_gap_minutes");

        if (minutes is null)
        {
            return TimeSpan.FromHours(3);
        }

        if (minutes < 0)
        {
            warnings.Add($"`minimum_gap_minutes` negatif ({minutes}); 0 sayıldı");
        }

        return TimeSpan.FromMinutes(Math.Max(minutes.Value, 0));
    }

    private static IReadOnlyList<ContentGenre> ReadGenres(JsonElement root, List<string> warnings)
    {
        if (!root.TryGetProperty("genres", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var genres = new List<ContentGenre>();

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = Text(item, "name");

            if (string.IsNullOrWhiteSpace(name))
            {
                warnings.Add("adı olmayan bir tür atlandı");
                continue;
            }

            var share = item.TryGetProperty("share", out var value)
                        && value.ValueKind == JsonValueKind.Number
                        && value.TryGetDouble(out var parsed)
                ? parsed
                : 0;

            if (share <= 0)
            {
                // PAYSIZ TÜR HİÇ ÜRETİLMEZ. Listede olup hiç
                // seçilmemesi, "neden bu türden video gelmiyor"
                // sorusunun sessiz cevabı olurdu.
                warnings.Add($"'{name}' türünün payı yok; hiç üretilmeyecek");
                continue;
            }

            genres.Add(new ContentGenre(name, share));
        }

        return genres.Count > 0 ? ContinuousStrategy.Normalize(genres) : [];
    }

    private static bool TimeZoneExists(string id)
    {
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }

    /// Ses kimliği: `voice.voice_id` ya da düz `voice_id`.
    ///
    /// İkisi de kabul ediliyor çünkü ayar belgesi ikisini de görüyor —
    /// yalnız birini desteklemek, diğerini yazan kullanıcının ayarının
    /// sessizce yok sayılması olurdu.
    private static string? ReadVoiceId(JsonElement root)
    {
        if (root.TryGetProperty("voice", out var voice) && voice.ValueKind == JsonValueKind.Object)
        {
            var nested = Text(voice, "voice_id");

            if (!string.IsNullOrWhiteSpace(nested))
            {
                return nested;
            }
        }

        var flat = Text(root, "voice_id");

        return string.IsNullOrWhiteSpace(flat) ? null : flat;
    }

    private static List<string>? ReadFontStack(JsonElement root, List<string> warnings)
    {
        if (!root.TryGetProperty("font_stack", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var fonts = array.EnumerateArray()
            .Where(f => f.ValueKind == JsonValueKind.String)
            .Select(f => f.GetString()!)
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .ToList();

        if (fonts.Count == 0)
        {
            // BOŞ LİSTE `null` OLUYOR: "yazı tipi yok" diye okunursa
            // hiçbir altyazı çizilemez. Yapılandırma hatasının bedeli
            // varsayılan yazı tipi olmalı, altyazısız video değil.
            warnings.Add("`font_stack` boş; varsayılan yazı tipleri kullanılıyor");
            return null;
        }

        return fonts;
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Int(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var parsed)
            ? parsed
            : null;
}
