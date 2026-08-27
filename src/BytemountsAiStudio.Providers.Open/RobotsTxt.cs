using System.Globalization;

namespace BytemountsAiStudio.Providers.Open;

/// robots.txt ayrıştırıcısı ve eşleştiricisi (P1-06).
///
/// Neden kendimiz yazdık: .NET'te yerleşik yok ve mevcut paketler ya
/// bakımsız ya da bizim ihtiyacımızın çok ötesinde. Kural kümesi küçük
/// ve iyi tanımlı; testle kapatılabiliyor.
///
/// Neden hiç atlanamaz olması gerekiyor: robots.txt'ye uymamak, tek bir
/// sitenin bizi engellemesiyle bitmiyor — otonom ve sürekli çalışan bir
/// sistem için IP itibarı ve yasal zemin meselesi. Kontrol sağlayıcının
/// İÇİNDE (çağıranın elinde değil), çünkü çağırana bırakılan bir kural
/// er geç bir yerde atlanır.
public sealed class RobotsTxt
{
    private readonly List<Rule> _rules;

    private RobotsTxt(List<Rule> rules) => _rules = rules;

    /// Hiçbir kısıt yok. robots.txt 404 dönerse bu kullanılıyor:
    /// dosyanın olmaması "her şey serbest" demek (RFC 9309).
    public static RobotsTxt AllowAll { get; } = new([]);

    /// Her şey yasak. robots.txt okunamadığında değil, açıkça
    /// `Disallow: /` yazdığında oluşuyor.
    public static RobotsTxt DenyAll { get; } = new([new Rule("/", Allow: false)]);

    public int RuleCount => _rules.Count;

    /// Verilen kullanıcı aracı için kuralları ayrıştırır.
    ///
    /// En özgül grup kazanıyor: adımızla eşleşen bir grup varsa `*`
    /// grubu hiç okunmuyor — standart böyle diyor ve tersi, bize özel
    /// konmuş bir yasağı sessizce yok saymak olurdu.
    public static RobotsTxt Parse(string content, string userAgent)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(userAgent);

        var groups = new Dictionary<string, List<Rule>>(StringComparer.OrdinalIgnoreCase);
        var currentAgents = new List<string>();
        var expectingAgent = true;

        foreach (var raw in content.Split('\n'))
        {
            var line = Strip(raw);

            if (line.Length == 0)
            {
                continue;
            }

            var separator = line.IndexOf(':', StringComparison.Ordinal);

            if (separator <= 0)
            {
                continue;
            }

            var field = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();

            if (field.Equals("user-agent", StringComparison.OrdinalIgnoreCase))
            {
                // Art arda gelen User-agent satırları TEK grup oluşturuyor.
                // Araya bir kural girdiğinde yeni grup başlıyor.
                if (!expectingAgent)
                {
                    currentAgents.Clear();
                    expectingAgent = true;
                }

                currentAgents.Add(value);

                if (!groups.ContainsKey(value))
                {
                    groups[value] = [];
                }

                continue;
            }

            var isAllow = field.Equals("allow", StringComparison.OrdinalIgnoreCase);
            var isDisallow = field.Equals("disallow", StringComparison.OrdinalIgnoreCase);

            if (!isAllow && !isDisallow)
            {
                continue;
            }

            expectingAgent = false;

            // Boş `Disallow:` "hiçbir şey yasak değil" demek — kural
            // olarak eklenirse her yolu yasaklardı.
            if (isDisallow && value.Length == 0)
            {
                continue;
            }

            foreach (var agent in currentAgents)
            {
                groups[agent].Add(new Rule(value, isAllow));
            }
        }

        // En özgül eşleşme: tam ad, sonra ön ek, sonra `*`.
        var chosen = groups.Keys
            .Where(a => !a.Equals("*", StringComparison.Ordinal)
                        && userAgent.StartsWith(a, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(a => a.Length)
            .FirstOrDefault()
            ?? (groups.ContainsKey("*") ? "*" : null);

        return chosen is null ? AllowAll : new RobotsTxt(groups[chosen]);
    }

    /// Bu yol çekilebilir mi.
    ///
    /// En UZUN eşleşen kural kazanıyor; eşitlikte Allow kazanıyor.
    /// Standart bunu böyle tanımlıyor ve sırası önemli: kısa bir
    /// `Disallow: /` ile uzun bir `Allow: /wiki/` yan yana geldiğinde
    /// doğru cevap "çekilebilir".
    public bool IsAllowed(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (_rules.Count == 0)
        {
            return true;
        }

        Rule? best = null;
        var bestLength = -1;

        foreach (var rule in _rules)
        {
            if (!Matches(rule.Pattern, path))
            {
                continue;
            }

            var length = rule.Pattern.Length;

            if (length > bestLength || (length == bestLength && rule.Allow))
            {
                best = rule;
                bestLength = length;
            }
        }

        return best?.Allow ?? true;
    }

    /// `*` herhangi bir dizi, `$` yol sonu demek.
    ///
    /// Genel bir düzenli ifadeye çevirmedik: robots.txt'deki bir
    /// desende geçen `(`, `+`, `?` gibi karakterler düzenli ifade
    /// olarak yorumlanınca ya patlar ya da yanlış eşleşir. Elle
    /// tarama iki joker karakteri de doğru ele alıyor.
    internal static bool Matches(string pattern, string path)
    {
        var anchored = pattern.EndsWith('$');
        var effective = anchored ? pattern[..^1] : pattern;

        return Walk(effective, 0, path, 0, anchored);
    }

    private static bool Walk(string pattern, int p, string path, int t, bool anchored)
    {
        while (p < pattern.Length)
        {
            if (pattern[p] == '*')
            {
                // Sondaki `*` geri kalan her şeyi yutuyor — `$` ile
                // birlikte gelse bile, çünkü "her şey" sona kadar demek.
                if (p == pattern.Length - 1)
                {
                    return true;
                }

                for (var skip = t; skip <= path.Length; skip++)
                {
                    if (Walk(pattern, p + 1, path, skip, anchored))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (t >= path.Length || path[t] != pattern[p])
            {
                return false;
            }

            p++;
            t++;
        }

        return !anchored || t == path.Length;
    }

    /// Yorumları ve BOM'u atar.
    private static string Strip(string line)
    {
        var text = line.TrimEnd('\r').TrimStart('﻿');
        var comment = text.IndexOf('#', StringComparison.Ordinal);

        return (comment >= 0 ? text[..comment] : text).Trim();
    }

    private sealed record Rule(string Pattern, bool Allow);

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"robots.txt ({_rules.Count} kural)");
}
