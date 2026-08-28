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
            Warnings = warnings,
        };
    }

    private static ChannelPacing ReadPacing(JsonElement root, List<string> warnings)
    {
        var pacing = root.TryGetProperty("pacing", out var nested) && nested.ValueKind == JsonValueKind.Object
            ? nested
            : root;

        var target = Int(pacing, "daily_target");

        if (target is null)
        {
            warnings.Add("`daily_target` yok; günde 1 video varsayıldı");
        }
        else if (target < 0)
        {
            warnings.Add($"`daily_target` negatif ({target}); 0 sayıldı");
        }

        var zone = Text(pacing, "time_zone");

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
            PublishWindows = ReadWindows(pacing, warnings),
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
