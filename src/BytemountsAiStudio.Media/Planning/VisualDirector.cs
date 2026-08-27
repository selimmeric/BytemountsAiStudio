using System.Globalization;
using System.Text;
using BytemountsAiStudio.Core.Content;

namespace BytemountsAiStudio.Media.Planning;

/// Bir cümleden görsel yönergesi üretir (P1-16, §10).
///
/// İki çıktı veriyor:
///   - `SearchQuery`  — stok görsel araması için (Openverse, Pexels)
///   - `ImagePrompt`  — AI üretimi için (Pollinations, Flux)
///
/// İkisi AYRI, çünkü ihtiyaçları zıt. Stok araması KISA ve somut terim
/// istiyor; uzun bir sorgu hiçbir şey bulmuyor. AI üretimi ise tam
/// tersine bağlam, üslup ve olumsuz yönerge istiyor; kısa prompt genel
/// ve tekdüze görsel veriyor. Tek bir metni ikisine birden vermek her
/// ikisini de kötüleştirirdi — ilk hâlde öyleydi ve görseller konudan
/// bağımsız çıkıyordu.
///
/// KURAL TABANLI, model çağırmıyor. Gerekçe: sahne başına bir LLM
/// çağrısı, video başına üç-beş çağrı daha demek ve asıl kazancı
/// belirsiz. Ayrıca aynı senaryo her koşuda aynı görseli üretmeli —
/// render önbelleğini ve determinizmi anlamlı kılan şey bu (ADR-006).
/// Model tabanlı bir yönetmen ileride bunun ÜSTÜNE eklenebilir; imzası
/// aynı kalıyor.
public static class VisualDirector
{
    /// Sorguya girmesi anlamsız kelimeler.
    ///
    /// Bağlaç, zamir ve yardımcı fiiller görsel aramada gürültü:
    /// "bir" araması hiçbir şey ifade etmiyor. Liste iki dilli, çünkü
    /// dil birinci sınıf bir boyut (ADR-013) ve tek dilli bir liste
    /// İngilizce kanalda hiçbir şey elemezdi.
    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        // Türkçe
        "bir", "bu", "şu", "o", "ve", "ile", "için", "ama", "fakat", "ancak",
        "daha", "çok", "az", "en", "gibi", "kadar", "sonra", "önce", "her",
        "hiç", "de", "da", "ki", "mi", "mı", "mu", "mü", "ise", "veya",
        "olarak", "olan", "olduğu", "oldu", "var", "yok", "değil", "ne",
        "hangi", "nasıl", "neden", "çünkü", "yani", "işte", "böyle", "şöyle",
        "kişi", "şey", "biri", "bazı", "tüm", "bütün", "birçok", "yine",
        "hâlâ", "hala", "bile", "sadece", "yalnızca", "artık", "şimdi",
        "bunun", "onun", "bunlar", "onlar", "kendi", "aynı", "başka", "diğer",

        // İngilizce
        "the", "a", "an", "and", "or", "but", "if", "then", "than", "that",
        "this", "these", "those", "is", "are", "was", "were", "be", "been",
        "of", "in", "on", "at", "to", "for", "with", "by", "from", "as",
        "it", "its", "they", "them", "their", "there", "here", "what",
        "which", "who", "how", "why", "when", "where", "all", "some", "many",
        "more", "most", "very", "just", "only", "also", "still", "even",
        "one", "two", "not", "no", "yes", "has", "have", "had", "can", "will",
    };

    /// Sorguya girecek en fazla terim.
    ///
    /// Dört bilinçli: daha fazlası stok aramasında sonuç sayısını sıfıra
    /// indiriyor, daha azı sahneler arasında ayrım bırakmıyor.
    private const int MaxQueryTerms = 4;

    /// Bu uzunluğun altındaki kelimeler atlanıyor. Türkçede iki harfli
    /// kelimelerin neredeyse tamamı ek ya da bağlaç.
    private const int MinTermLength = 3;

    public static VisualDirection Direct(
        string sentence, string topic, LanguageTag language, VisualStyle style, int sceneIndex)
    {
        ArgumentNullException.ThrowIfNull(sentence);
        ArgumentNullException.ThrowIfNull(topic);
        ArgumentNullException.ThrowIfNull(style);

        var terms = Keywords(sentence, language);

        // Cümleden hiç anlamlı terim çıkmazsa konuya düşülüyor. Boş bir
        // sorgu göndermek, sağlayıcıdan rastgele bir görsel almak demek
        // olurdu — konuyla ilgisiz bir kare, hiç görsel olmamasından
        // daha kötü.
        var query = terms.Count > 0
            ? string.Join(' ', terms)
            : topic;

        return new VisualDirection
        {
            SceneIndex = sceneIndex,
            SearchQuery = query,
            ImagePrompt = BuildPrompt(topic, terms, style),
            StyleHint = style.Name,
            // Tohum sahne indeksinden: aynı senaryo her koşuda aynı
            // görseli veriyor, ama sahneler birbirinin aynısı olmuyor.
            Seed = sceneIndex,
        };
    }

    /// Cümledeki taşıyıcı kelimeler, cümledeki sıralarıyla.
    ///
    /// Sıra korunuyor çünkü Türkçede de İngilizcede de cümlenin başındaki
    /// öge genellikle konudur; alfabetik ya da uzunluğa göre sıralamak
    /// anlamı dağıtırdı.
    internal static List<string> Keywords(string sentence, LanguageTag language)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var terms = new List<string>();
        var culture = CultureFor(language);

        foreach (var raw in sentence.Split(
            [' ', '\t', '\n', '\r', ',', '.', ';', ':', '!', '?', '"', '\'', '(', ')', '—', '–', '-'],
            StringSplitOptions.RemoveEmptyEntries))
        {
            var word = raw.Trim();

            if (word.Length < MinTermLength
                || Stopwords.Contains(word)
                || !word.Any(char.IsLetter))
            {
                continue;
            }

            // Büyük harfle başlayan kelimeler (özel adlar) önceliklendirilmiyor
            // ama küçültülüp tekilleştiriliyor: "Göbeklitepe" ve
            // "göbeklitepe" aynı terim.
            var normalized = word.ToLower(culture);

            if (seen.Add(normalized))
            {
                terms.Add(normalized);
            }

            if (terms.Count == MaxQueryTerms)
            {
                break;
            }
        }

        return terms;
    }

    /// AI görsel istemi.
    ///
    /// Olumsuz yönergeler (`metin yok, filigran yok`) şart: üretilen
    /// görsellerde uydurma yazı çıkması en sık görülen kusur ve o yazı
    /// videoda okunuyor. Altyazıyı biz yakıyoruz; görselin içinde ikinci
    /// bir yazı olması hem çirkin hem yanıltıcı.
    private static string BuildPrompt(string topic, List<string> terms, VisualStyle style)
    {
        var builder = new StringBuilder();

        builder.Append(topic);

        if (terms.Count > 0)
        {
            builder.Append(": ").AppendJoin(", ", terms);
        }

        builder.Append(". ").Append(style.PromptSuffix);
        builder.Append(". no text, no watermark, no letters");

        return builder.ToString();
    }

    /// Küçültme dile duyarlı olmak zorunda.
    ///
    /// Türkçede `I` → `ı`, İngilizcede `I` → `i`. Değişmez kültürle
    /// küçültmek "İSTANBUL" kelimesini bozar ve arama sonucu değişir.
    private static CultureInfo CultureFor(LanguageTag language)
        => language.Value.StartsWith("tr", StringComparison.OrdinalIgnoreCase)
            ? CultureInfo.GetCultureInfo("tr-TR")
            : CultureInfo.InvariantCulture;
}

/// Bir sahnenin görsel yönergesi.
public sealed record VisualDirection
{
    public required int SceneIndex { get; init; }

    /// Stok görsel araması için KISA terim.
    public required string SearchQuery { get; init; }

    /// AI üretimi için bağlamlı istem.
    public required string ImagePrompt { get; init; }

    public required string StyleHint { get; init; }

    /// Aynı tohum + aynı istem = aynı görsel (destekleyen sağlayıcılarda).
    public int? Seed { get; init; }
}

/// Kanalın görsel üslubu.
///
/// Kanal ayarından geliyor (§3): aynı kanalın videoları birbirine
/// benzemeli, farklı kanallarınki benzememeli. Sabit bir üslup
/// gömseydik çok kanallılık ilk günden kırılırdı.
public sealed record VisualStyle
{
    public required string Name { get; init; }

    /// İsteme eklenen üslup betimlemesi.
    public required string PromptSuffix { get; init; }

    /// Belgesel anlatı için varsayılan: gerçekçi, sinematik, insan yüzü
    /// içermeyen. Yüz istememek bilinçli — AI üretimi yüzler hâlâ
    /// güvenilmez ve tek bir bozuk yüz videoyu izlenemez kılıyor.
    public static VisualStyle Documentary { get; } = new()
    {
        Name = "documentary",
        PromptSuffix = "cinematic documentary photography, natural lighting, "
                       + "high detail, realistic, no people",
    };

    public static VisualStyle Illustration { get; } = new()
    {
        Name = "illustration",
        PromptSuffix = "digital illustration, bold shapes, limited palette, clean composition",
    };

    public static VisualStyle Get(string? name)
        => name?.ToLowerInvariant() switch
        {
            "illustration" => Illustration,
            _ => Documentary,
        };
}
