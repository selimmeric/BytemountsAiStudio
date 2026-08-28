using System.Globalization;
using System.Text.Json;

namespace BytemountsAiStudio.Core.Execution;

/// Bir onay kapısının verdiği karar (P1-27).
public enum ApprovalOutcome
{
    /// İnsana sorulmadan geçildi.
    AutoApproved = 0,

    /// Run park edildi; insan kararı bekleniyor.
    Awaiting = 1,
}

/// Onay kapısının sonucu ve GEREKÇESİ.
///
/// Gerekçe zorunlu: "onay bekleniyor" tek başına, panelde bakan kişiye
/// neden bakması gerektiğini söylemiyor. Otomatik geçilen bir kapıda da
/// gerekçe lazım — "neden bu videoya kimse bakmadı" sorusunun cevabı.
public sealed record ApprovalDecision(ApprovalOutcome Outcome, string Reason)
{
    public bool Awaiting => Outcome == ApprovalOutcome.Awaiting;
}

/// Onay kapısının KARARI. Saf: veritabanı ve ağ yok.
///
/// Ayrı olmasının sebebi ADR-011'le aynı mantık — "insana sorulacak mı"
/// kararı, bir veritabanı kurulumu gerektirmeden sınanabilmeli. Yanlış
/// bir eşik değeri, gerçek bir koşuda kötü bir videonun yayına
/// girmesiyle öğrenilecek bir şey olmamalı.
public static class ApprovalGate
{
    /// §22'nin üç kipi.
    ///
    /// `Selective` en incelikli olanı: yalnızca QC skoru eşiğin altında
    /// kalanlar insana düşüyor. Bu, ölçeklenmenin tek yolu — her videoyu
    /// insana göstermek günde 50 video demek değil, günde 5 demek.
    public static ApprovalDecision Decide(ChannelMode mode, double? score, double threshold)
    {
        switch (mode)
        {
            case ChannelMode.Auto:
                return new ApprovalDecision(ApprovalOutcome.AutoApproved, "kanal otonom kipte");

            case ChannelMode.Selective:
                // SKOR YOKSA İNSANA SORULUYOR.
                //
                // "Ölçülmedi" ile "iyi" aynı şey değil. QC hiç koşmadıysa
                // ya da skoru okunamadıysa, otomatik geçirmek en kötü
                // ihtimali seçmek olurdu: kalitesi bilinmeyen bir videoyu
                // kimse görmeden yayına vermek.
                if (score is not { } value)
                {
                    return new ApprovalDecision(ApprovalOutcome.Awaiting,
                        "QC skoru yok — ölçülmemiş içerik otomatik geçmiyor");
                }

                return value >= threshold
                    ? new ApprovalDecision(ApprovalOutcome.AutoApproved,
                        Text($"QC {value:0.##} ≥ eşik {threshold:0.##}"))
                    : new ApprovalDecision(ApprovalOutcome.Awaiting,
                        Text($"QC {value:0.##} < eşik {threshold:0.##}"));

            default:
                return new ApprovalDecision(ApprovalOutcome.Awaiting, "kanal her aşamada onay istiyor");
        }
    }

    /// Kip adını okur. Tanınmayan bir değer ONAY kipine düşüyor.
    ///
    /// Güvenli taraf bu: yapılandırmadaki bir yazım hatası yüzünden
    /// kanalın sessizce tam otonom hâle gelmesi, tersinden çok daha
    /// pahalı bir hata.
    public static ChannelMode ParseMode(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "AUTO" => ChannelMode.Auto,
        "SELECTIVE" => ChannelMode.Selective,
        _ => ChannelMode.Approval,
    };

    /// Çıktıdaki bu alan MOTORLA SÖZLEŞME: motor bunu görürse run'ı
    /// park ediyor.
    public const string AwaitingField = "awaiting_approval";

    /// Bir node çıktısı onay bekliyor mu.
    ///
    /// Motor node TİPİNE bakmıyor, ÇIKTIYA bakıyor. Sebebi somut: aynı
    /// `human.approval` node'u bir koşuda insana sorup diğerinde
    /// sormuyor — karar QC skoruna ve kanal kipine bağlı. Tipe
    /// bakılsaydı otomatik geçen kapılar da run'ı park ederdi.
    ///
    /// Yalnızca GERÇEK `true` sayılıyor: `"true"` metni ya da 1 sayısı
    /// değil. Gevşek okumak, alanı yanlışlıkla dolduran bir node'un
    /// run'ı sessizce park etmesine yol açardı.
    public static bool Awaits(JsonElement output)
        => output.ValueKind == JsonValueKind.Object
           && output.TryGetProperty(AwaitingField, out var value)
           && value.ValueKind == JsonValueKind.True;

    private static string Text(FormattableString text)
        => text.ToString(CultureInfo.InvariantCulture);
}

/// Hedefli yeniden koşma isteği (P2-07).
///
/// Motor ile node arasındaki SÖZLEŞME — onay kapısındakiyle aynı
/// desen. Motor node tipine bakmıyor, ÇIKTIYA bakıyor: aynı QC
/// node'u bir koşuda yeniden koşma istiyor, diğerinde istemiyor ve
/// karar skora bağlı.
public sealed record RerunRequest(IReadOnlyList<string> Nodes, string Reason)
{
    /// Çıktıdaki bu alan sözleşme.
    public const string Field = "retry";

    /// Bir node çıktısı yeniden koşma istiyor mu.
    ///
    /// Boş bir node listesi istek SAYILMIYOR: "yeniden koş ama
    /// hiçbir şeyi koşma" anlamsız ve o hâlde run sessizce durur —
    /// kuyrukta iş kalmaz, kimse bir şeyin durduğunu fark etmez.
    public static RerunRequest? From(JsonElement output)
    {
        if (output.ValueKind != JsonValueKind.Object
            || !output.TryGetProperty(Field, out var retry)
            || retry.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!retry.TryGetProperty("decision", out var decision)
            || !string.Equals(decision.GetString(), "Rerun", StringComparison.Ordinal))
        {
            return null;
        }

        if (!retry.TryGetProperty("nodes", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var nodes = array.EnumerateArray()
            .Where(n => n.ValueKind == JsonValueKind.String)
            .Select(n => n.GetString()!)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();

        if (nodes.Count == 0)
        {
            return null;
        }

        var reason = retry.TryGetProperty("reason", out var text) ? text.GetString() : null;

        return new RerunRequest(nodes, reason ?? "QC yeniden koşma istedi");
    }
}
