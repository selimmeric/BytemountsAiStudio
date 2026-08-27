using System.Globalization;
using System.Text;

namespace BytemountsAiStudio.Core.Content;

/// Yayın platformlarının metadata sınırları (P1-22, §15).
///
/// Sınırlar KODDA uygulanıyor, modele "100 karakteri geçme" demekle
/// yetinilmiyor. Sebep basit: model bazen geçiyor, ve o zaman upload
/// REDDEDİLİYOR — hem de videonun kalan her adımı yapıldıktan sonra.
/// Bir üretim hattında en pahalı hata, son adımda ortaya çıkan hatadır.
///
/// Kırpma "kes ve bırak" değil: kelime sınırında kesiliyor ve gerekirse
/// üç nokta ekleniyor. Ortadan kesilmiş bir başlık ("Dünyanın En Tehli")
/// hem okunmuyor hem tıklanmıyor; kırpmanın amacı reddi önlemek, kaliteyi
/// büsbütün bitirmek değil.
public static class PlatformLimits
{
    /// YouTube başlık sınırı. Aşan başlıkla upload reddediliyor.
    public const int TitleMaxLength = 100;

    public const int DescriptionMaxLength = 5000;

    /// Etiketlerin TOPLAM uzunluğu (ayraçlar dahil).
    ///
    /// Ayraçları saymak şart: platform da öyle sayıyor ve unutmak,
    /// sınırın hemen altındaki bir etiket kümesini reddettirir.
    public const int TagsTotalMaxLength = 500;

    /// Tek bir etiketin sınırı.
    public const int TagMaxLength = 100;

    /// Başlığı sınıra sığdırır.
    ///
    /// Sığıyorsa DOKUNULMUYOR — gereksiz normalizasyon, modelin
    /// kasıtlı boşluk ya da noktalamasını bozardı.
    public static string TrimTitle(string title)
    {
        ArgumentNullException.ThrowIfNull(title);

        var collapsed = CollapseWhitespace(title);

        return collapsed.Length <= TitleMaxLength
            ? collapsed
            : TrimAtWord(collapsed, TitleMaxLength);
    }

    public static string TrimDescription(string description)
    {
        ArgumentNullException.ThrowIfNull(description);

        return description.Length <= DescriptionMaxLength
            ? description
            : TrimAtWord(description, DescriptionMaxLength);
    }

    /// Etiketleri sınıra sığdırır.
    ///
    /// SONDAN atılıyor, baştan değil: model en alakalı etiketi başa
    /// yazıyor ve baştan atmak en değerlisini atmak olurdu.
    ///
    /// Tekrarlanan etiketler de eleniyor — aynı etiketi iki kez
    /// göndermek sınırı boşa harcıyor.
    public static IReadOnlyList<string> TrimTags(IEnumerable<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var accepted = new List<string>();
        var total = 0;

        foreach (var raw in tags)
        {
            var tag = CollapseWhitespace(raw);

            if (tag.Length == 0 || tag.Length > TagMaxLength || !seen.Add(tag))
            {
                continue;
            }

            // Ayraç maliyeti: ilk etiket hariç her etiket bir karakter
            // daha yer kaplıyor.
            var cost = tag.Length + (accepted.Count > 0 ? 1 : 0);

            if (total + cost > TagsTotalMaxLength)
            {
                // Sığmayan etiketi atlayıp devam ediyoruz: sonraki
                // daha kısa bir etiket sığabilir ve sınırı boş
                // bırakmanın anlamı yok.
                continue;
            }

            accepted.Add(tag);
            total += cost;
        }

        return accepted;
    }

    /// Etiket kümesinin platform tarafından sayılan uzunluğu.
    public static int TagsLength(IReadOnlyList<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        return tags.Count == 0 ? 0 : tags.Sum(t => t.Length) + tags.Count - 1;
    }

    /// Kelime sınırında kırpar.
    ///
    /// Üç nokta SINIRA DAHİL: eklendikten sonra taşan bir metin, kırpma
    /// yapmamışız gibi reddedilirdi.
    private static string TrimAtWord(string text, int maxLength)
    {
        const string ellipsis = "…";

        var budget = maxLength - ellipsis.Length;
        var slice = text[..budget];
        var lastSpace = slice.LastIndexOf(' ');

        // Kelime sınırı çok geride kalıyorsa (tek uzun kelime) sert
        // kesiliyor: yarım kelime, boş bir başlıktan iyidir.
        var cut = lastSpace > budget / 2 ? slice[..lastSpace] : slice;

        return cut.TrimEnd(' ', ',', ';', ':', '-', '–', '—') + ellipsis;
    }

    private static string CollapseWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        var lastWasSpace = false;

        foreach (var ch in text.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                lastWasSpace = true;
                continue;
            }

            if (lastWasSpace && builder.Length > 0)
            {
                builder.Append(' ');
            }

            lastWasSpace = false;
            builder.Append(ch);
        }

        return builder.ToString();
    }

    /// Kırpma sonrası hâlâ sınır dışında bir şey var mı.
    ///
    /// Kırpmanın kendisini denetliyor: bir hata yüzünden sınırı aşan
    /// bir metin üretirsek bunu upload sırasında değil burada görelim.
    public static IReadOnlyList<string> Violations(
        string title, string description, IReadOnlyList<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(title))
        {
            problems.Add("baslik bos");
        }
        else if (title.Length > TitleMaxLength)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture,
                $"baslik {title.Length} karakter (sinir {TitleMaxLength})"));
        }

        if (description?.Length > DescriptionMaxLength)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture,
                $"aciklama {description.Length} karakter (sinir {DescriptionMaxLength})"));
        }

        var tagsLength = TagsLength(tags);

        if (tagsLength > TagsTotalMaxLength)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture,
                $"etiketler {tagsLength} karakter (sinir {TagsTotalMaxLength})"));
        }

        foreach (var tag in tags.Where(t => t.Length > TagMaxLength))
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture,
                $"etiket cok uzun: {tag.Length} karakter"));
        }

        return problems;
    }
}
