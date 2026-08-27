using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace BytemountsAiStudio.Providers.Open;

/// HTML'den ana metin çıkarma (P1-06).
///
/// Neden bir HTML kütüphanesi getirmedik: bu sınıfın işi tam DOM
/// dolaşmak değil, "menü ve altbilgi olmayan okunabilir metin" üretmek.
/// AngleSharp doğru DOM verirdi ama asıl zor kısım olan ANA İÇERİĞİ
/// SEÇMEYİ yine çözmezdi; onun için ayrıca bir okunabilirlik algoritması
/// gerekiyor.
///
/// JS ile yüklenen sayfalar bu yoldan alınamıyor — orası tools-sidecar'ın
/// işi (P1-04, Playwright). Buradaki uygulama düz HTML sayfalar için;
/// ansiklopedik ve kurumsal kaynakların büyük kısmı öyle.
public static class HtmlTextExtractor
{
    /// İçeriği hiç olmayan, tamamen atılan öğeler.
    ///
    /// `head` de burada: içindeki `<title>` ve `<meta>` metni gövdeye
    /// karışıyordu. `<article>`/`<main>` olan sayfalarda daraltma bunu
    /// gizliyor, olmayanlarda özetin ilk cümlesi hep başlık çıkıyordu
    /// ve model aynı bilgiyi iki kez okuyordu. Python yan-servisinde
    /// de aynı kural (tools/bmai_tools/extract.py).
    private static readonly string[] Dropped =
        ["head", "script", "style", "noscript", "svg", "iframe", "template", "form", "button", "select"];

    /// Metni olan ama ana içerik olmayan öğeler. Menü ve altbilgi
    /// metni iddia çıkarımına girerse kaynak güvenilirliği çöker:
    /// "Gizlilik Politikası" bir olgu değil.
    private static readonly string[] Chrome =
        ["nav", "header", "footer", "aside"];

    /// Blok sınırı işareti.
    ///
    /// Ham satır sonu KULLANILAMAZ: kaynak HTML'de bir paragrafın
    /// içindeki satır sonu, HTML kurallarına göre yalnızca boşluktur ve
    /// boşluğa indirgenmeli. Ama BİZİM koyduğumuz blok sınırlarının
    /// satır sonu olarak kalması gerekiyor. İkisini ayırt edebilmek için
    /// metinde geçemeyecek ayrı bir işaret kullanılıyor.
    private const char BlockMark = '\u0001';

    /// Blok öğeleri — metin sınırında satır sonu üretiyorlar ki
    /// birbirinden bağımsız iki cümle yapışmasın.
    private static readonly HashSet<string> Blocks = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "br", "li", "tr", "h1", "h2", "h3", "h4", "h5", "h6",
        "section", "article", "blockquote", "pre", "td", "th", "figcaption",
    };

    public static string ExtractTitle(string html)
    {
        ArgumentNullException.ThrowIfNull(html);

        var open = html.IndexOf("<title", StringComparison.OrdinalIgnoreCase);

        if (open < 0)
        {
            return string.Empty;
        }

        var start = FindTagEnd(html, open);
        var close = start < 0 ? -1 : html.IndexOf("</title", start, StringComparison.OrdinalIgnoreCase);

        return start < 0 || close < 0
            ? string.Empty
            : Collapse(WebUtility.HtmlDecode(html[(start + 1)..close]));
    }

    public static string ExtractMainText(string html)
    {
        ArgumentNullException.ThrowIfNull(html);

        var working = RemoveComments(html);

        foreach (var tag in Dropped)
        {
            working = RemoveElement(working, tag, keepText: false);
        }

        // `<article>` ya da `<main>` varsa yalnızca onu alıyoruz: sayfanın
        // kendi işaretlediği ana içerik, bizim tahminimizden iyi.
        var narrowed = Narrow(working, "article") ?? Narrow(working, "main") ?? working;

        foreach (var tag in Chrome)
        {
            narrowed = RemoveElement(narrowed, tag, keepText: false);
        }

        return Collapse(StripTags(narrowed));
    }

    /// Ödeme duvarı işaretleri.
    ///
    /// Kesin bir tespit değil, olamaz da. Ama yanlış pozitifin bedeli
    /// bir kaynağı atlamak; yanlış negatifin bedeli yarım bir metinden
    /// iddia çıkarmak. İkincisi çok daha pahalı, o yüzden geniş
    /// davranıyoruz.
    public static bool LooksPaywalled(string html, string mainText)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(mainText);

        string[] markers =
        [
            "paywall", "subscribe to continue", "abonelere özel", "aboneler için",
            "subscription required", "premium içerik", "üye girişi yapın",
        ];

        if (markers.Any(m => html.Contains(m, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Uzun bir HTML'den çok kısa bir metin çıkması, içeriğin
        // gizlendiğinin klasik işareti.
        return html.Length > 40_000 && mainText.Length < 500;
    }

    private static string RemoveComments(string html)
    {
        var builder = new StringBuilder(html.Length);
        var index = 0;

        while (index < html.Length)
        {
            var open = html.IndexOf("<!--", index, StringComparison.Ordinal);

            if (open < 0)
            {
                builder.Append(html, index, html.Length - index);
                break;
            }

            builder.Append(html, index, open - index);

            var close = html.IndexOf("-->", open + 4, StringComparison.Ordinal);

            if (close < 0)
            {
                break;
            }

            index = close + 3;
        }

        return builder.ToString();
    }

    /// `<tag ...>...</tag>` bloklarını atar. İç içe aynı etiketleri
    /// sayarak takip ediyor — saymasaydı iç içe `<div>`lerde ilk
    /// kapanışta durur ve sayfanın yarısını yerdi.
    private static string RemoveElement(string html, string tag, bool keepText)
    {
        var builder = new StringBuilder(html.Length);
        var index = 0;

        while (index < html.Length)
        {
            var open = FindTag(html, tag, index, closing: false);

            if (open < 0)
            {
                builder.Append(html, index, html.Length - index);
                break;
            }

            builder.Append(html, index, open - index);

            var afterOpen = FindTagEnd(html, open);

            if (afterOpen < 0)
            {
                break;
            }

            // Kendi kendine kapanan etiket: içerik yok.
            if (html[afterOpen - 1] == '/')
            {
                index = afterOpen + 1;
                continue;
            }

            var depth = 1;
            var cursor = afterOpen + 1;
            var contentStart = cursor;

            while (depth > 0 && cursor < html.Length)
            {
                var nextOpen = FindTag(html, tag, cursor, closing: false);
                var nextClose = FindTag(html, tag, cursor, closing: true);

                if (nextClose < 0)
                {
                    cursor = html.Length;
                    break;
                }

                if (nextOpen >= 0 && nextOpen < nextClose)
                {
                    depth++;
                    cursor = nextOpen + 1;
                    continue;
                }

                depth--;

                if (depth == 0)
                {
                    if (keepText)
                    {
                        builder.Append(html, contentStart, nextClose - contentStart);
                    }

                    var end = FindTagEnd(html, nextClose);
                    cursor = end < 0 ? html.Length : end + 1;
                    break;
                }

                cursor = nextClose + 1;
            }

            // Blok sınırı: silinen öğenin öncesi ve sonrası yapışmasın.
            builder.Append(BlockMark);
            index = cursor;
        }

        return builder.ToString();
    }

    /// Verilen etiketin İÇİNİ döndürür; yoksa null.
    private static string? Narrow(string html, string tag)
    {
        var open = FindTag(html, tag, 0, closing: false);

        if (open < 0)
        {
            return null;
        }

        var start = FindTagEnd(html, open);
        var close = start < 0 ? -1 : html.LastIndexOf($"</{tag}", StringComparison.OrdinalIgnoreCase);

        return start < 0 || close <= start ? null : html[(start + 1)..close];
    }

    /// `<div` bulur ama `<divx` bulmaz: etiket adından sonra gelen
    /// karakterin sınır olması gerekiyor.
    private static int FindTag(string html, string tag, int from, bool closing)
    {
        var needle = closing ? $"</{tag}" : $"<{tag}";
        var index = from;

        while (index < html.Length)
        {
            index = html.IndexOf(needle, index, StringComparison.OrdinalIgnoreCase);

            if (index < 0)
            {
                return -1;
            }

            var after = index + needle.Length;

            if (after >= html.Length
                || html[after] is '>' or ' ' or '\t' or '\n' or '\r' or '/')
            {
                return index;
            }

            index = after;
        }

        return -1;
    }

    private static string StripTags(string html)
    {
        var builder = new StringBuilder(html.Length);
        var index = 0;

        while (index < html.Length)
        {
            var open = html.IndexOf('<', index);

            if (open < 0)
            {
                AppendFlowed(builder, html, index, html.Length - index);
                break;
            }

            AppendFlowed(builder, html, index, open - index);

            var close = FindTagEnd(html, open);

            if (close < 0)
            {
                break;
            }

            var name = TagName(html, open, close);

            if (Blocks.Contains(name))
            {
                builder.Append(BlockMark);
            }

            index = close + 1;
        }

        return WebUtility.HtmlDecode(builder.ToString());
    }

    /// `open` konumundaki etiketin kapanış `>` işaretini bulur.
    ///
    /// Tırnak içindeki `>` ATLANMALI. Wikipedia gibi kaynaklar öznitelik
    /// değerlerinde JSON taşıyor (`data-mw='{"parts":[...]}'`) ve o JSON
    /// içinde `>` geçiyor. Basit bir `IndexOf('>')` etiketi orada
    /// kapatıyor, kalan öznitelik metni de gövde metniymiş gibi çıktıya
    /// sızıyordu — gerçek bir sayfada denenince görüldü.
    private static int FindTagEnd(string html, int open)
    {
        var quote = '\0';

        for (var i = open + 1; i < html.Length; i++)
        {
            var ch = html[i];

            if (quote != '\0')
            {
                if (ch == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (ch is '"' or '\'')
            {
                quote = ch;
                continue;
            }

            if (ch == '>')
            {
                return i;
            }
        }

        return -1;
    }

    /// Metin parçasını ekler; kaynak satır sonlarını boşluğa çevirir.
    /// Bizim koyduğumuz blok işaretine dokunmuyor — ayrımın tamamı bu.
    private static void AppendFlowed(StringBuilder builder, string html, int start, int length)
    {
        for (var i = start; i < start + length; i++)
        {
            builder.Append(html[i] is '\n' or '\r' ? ' ' : html[i]);
        }
    }

    private static string TagName(string html, int open, int close)
    {
        var start = open + 1;

        if (start < html.Length && html[start] == '/')
        {
            start++;
        }

        var end = start;

        while (end < close && char.IsLetterOrDigit(html[end]))
        {
            end++;
        }

        return html[start..end];
    }

    /// Boşlukları toparlar: satır içi boşluklar teke iner, boş satırlar
    /// tek satır sonuna iner. Modele giden metnin yarısı boşluk olmasın.
    private static string Collapse(string text)
    {
        var builder = new StringBuilder(text.Length);
        var lastWasSpace = false;
        var newlines = 0;

        foreach (var ch in text)
        {
            if (ch == BlockMark || ch is '\n' or '\r')
            {
                newlines++;
                lastWasSpace = true;
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                lastWasSpace = true;
                continue;
            }

            if (builder.Length > 0 && lastWasSpace)
            {
                builder.Append(newlines > 0 ? '\n' : ' ');
            }

            newlines = 0;
            lastWasSpace = false;
            builder.Append(ch);
        }

        return builder.ToString();
    }

    /// Icerigin sha256'si.
    ///
    /// TEK yerde: bilgi tabani tekillestirmeyi icerik ozetine gore
    /// yapiyor (P1-11). Iki ayri tanim sessizce ayrisirsa ayni sayfa
    /// iki kez kaydedilir ve kimse fark etmez.
    internal static string Sha256(string text)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text ?? string.Empty)));
}
