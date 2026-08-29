using System.Text;
using BytemountsAiStudio.Core.Content;

namespace BytemountsAiStudio.Media.Timeline;

/// Yalnızca ses türevi — podcast (P6-05).
///
/// PODCAST "VİDEONUN SESİ" DEĞİL.
///
/// Videoda ekranda yazan ama seslendirilmeyen her şey podcast
/// dinleyicisi için YOK. "1453" diye bir metin katmanı varsa ve
/// anlatım o sayıyı söylemiyorsa, dinleyici o bilgiyi hiç almıyor —
/// ve bunu kimse fark etmiyor, çünkü ses dosyası kusursuz çalıyor.
///
/// Bu sınıf o boşluğu görünür kılıyor. Engellemiyor: kanal adı gibi
/// dekoratif katmanlar yüzünden üretimi durdurmak, olmayan bir sorun
/// için fabrikayı kapatmak olurdu. Ama kayda geçiyor ve istenirse
/// bloklayıcı hâle getirilebiliyor.
public static class PodcastRendition
{
    /// Seslendirilmeyen metin katmanları.
    ///
    /// Altyazılar sayılmıyor: onlar zaten konuşmadan türüyor, yeni
    /// bilgi taşımıyorlar.
    public static IReadOnlyList<string> VisualOnlyText(TimelineDocument timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);

        var spoken = Normalize(
            string.Join(
                ' ',
                timeline.Audio.VoiceSegments
                    .Select(s => s.SpeechText)
                    .Where(t => !string.IsNullOrWhiteSpace(t))),
            timeline.Language);

        var missing = new List<string>();

        foreach (var overlay in timeline.Scenes.SelectMany(s => s.Overlays))
        {
            var text = overlay.Text?.Trim();

            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            var normalized = Normalize(text, timeline.Language);

            if (normalized.Length == 0)
            {
                continue;
            }

            // SESLENDİRME METNİ YOKSA HİÇBİR KATMAN "KAPSANMIŞ"
            // SAYILMIYOR.
            //
            // Boş konuşma metnini "her şeyi içeriyor" gibi ele almak,
            // kontrolü sessizce kapatırdı — ve kontrolün kapalı olduğu
            // tek yer, en çok gerektiği yer olurdu.
            if (!spoken.Contains(normalized, StringComparison.Ordinal)
                && !missing.Contains(text, StringComparer.Ordinal))
            {
                missing.Add(text);
            }
        }

        return missing;
    }

    /// Karşılaştırma için metni sadeleştirir.
    ///
    /// KÜÇÜK HARFE ÇEVİRME DİLE DUYARLI: `ToLowerInvariant` Türkçe'de
    /// "İSTANBUL"u "i̇stanbul" yapıyor ve karşılaştırma tutmuyor.
    /// Kapak metninde ödenen dersin aynısı (P5-03).
    internal static string Normalize(string? text, LanguageTag language)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var space = true;

        foreach (var ch in text.ToLower(language.Culture))
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                space = false;
                continue;
            }

            // NOKTALAMA VE BOŞLUK TEK BOŞLUĞA İNİYOR: ekrandaki
            // "1453." ile söylenen "1453" aynı bilgi ve farklı
            // saymak, her videoda uydurma bir uyarı üretirdi.
            if (!space)
            {
                builder.Append(' ');
                space = true;
            }
        }

        return builder.ToString().Trim();
    }
}
