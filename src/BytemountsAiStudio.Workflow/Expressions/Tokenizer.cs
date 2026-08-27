using System.Globalization;
using System.Text;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;

namespace BytemountsAiStudio.Workflow.Expressions;

internal enum TokenKind
{
    Identifier,
    Number,
    String,
    Comparison,
    And,
    Or,
    Not,
    OpenParen,
    CloseParen,
}

internal sealed record Token(TokenKind Kind, string Text);

/// İfade sözcükleyicisi.
///
/// Tanımadığı her karakteri REDDEDER. Beyaz liste yaklaşımı kasıtlı: kara
/// liste yapsaydık, gözden kaçan bir karakter dilin kapsamını sessizce
/// genişletirdi. Burada eklenmeyen hiçbir şey ifade dilinin parçası olamaz.
internal static class Tokenizer
{
    public static Result<IReadOnlyList<Token>> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            switch (c)
            {
                case '(':
                    tokens.Add(new(TokenKind.OpenParen, "("));
                    i++;
                    continue;

                case ')':
                    tokens.Add(new(TokenKind.CloseParen, ")"));
                    i++;
                    continue;

                case '&' when Next(text, i) == '&':
                    tokens.Add(new(TokenKind.And, "&&"));
                    i += 2;
                    continue;

                case '|' when Next(text, i) == '|':
                    tokens.Add(new(TokenKind.Or, "||"));
                    i += 2;
                    continue;

                case '!' when Next(text, i) == '=':
                    tokens.Add(new(TokenKind.Comparison, "!="));
                    i += 2;
                    continue;

                case '!':
                    tokens.Add(new(TokenKind.Not, "!"));
                    i++;
                    continue;

                case '=' when Next(text, i) == '=':
                    tokens.Add(new(TokenKind.Comparison, "=="));
                    i += 2;
                    continue;

                case '<' or '>':
                    var withEquals = Next(text, i) == '=';
                    tokens.Add(new(TokenKind.Comparison, withEquals ? $"{c}=" : c.ToString()));
                    i += withEquals ? 2 : 1;
                    continue;

                case '\'' or '"':
                    var literal = ReadString(text, ref i, c);
                    if (literal is null)
                    {
                        return Error.Permanent("expr.unterminated_string",
                            "Kapanmayan metin sabiti.");
                    }

                    tokens.Add(new(TokenKind.String, literal));
                    continue;

                default:
                    break;
            }

            if (char.IsAsciiDigit(c))
            {
                var start = i;
                while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] == '.'))
                {
                    i++;
                }

                var number = text[start..i];

                if (!double.TryParse(number, CultureInfo.InvariantCulture, out _))
                {
                    return Error.Permanent("expr.bad_number", $"Geçersiz sayı: '{number}'");
                }

                tokens.Add(new(TokenKind.Number, number));
                continue;
            }

            if (char.IsAsciiLetter(c) || c == '_')
            {
                var start = i;
                while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] is '_' or '.'))
                {
                    i++;
                }

                tokens.Add(new(TokenKind.Identifier, text[start..i]));
                continue;
            }

            // Beyaz listede olmayan karakter. Örnek: '+', ';', '$', '`'.
            // Bunları sessizce yok saymak, ifadeyi yazan kişinin ne
            // kastettiğini tahmin etmek olurdu.
            return Error.Permanent("expr.illegal_character",
                $"İfade dilinde kullanılamayan karakter: '{c}' ({(int)c}). " +
                "Bu dil kasıtlı olarak sınırlıdır: alan erişimi, sabit, " +
                "karşılaştırma ve mantık işleçleri.");
        }

        return tokens;
    }

    private static char? Next(string text, int index)
        => index + 1 < text.Length ? text[index + 1] : null;

    private static string? ReadString(string text, ref int index, char quote)
    {
        var builder = new StringBuilder();
        index++;   // açılış tırnağı

        while (index < text.Length)
        {
            var c = text[index];

            if (c == quote)
            {
                index++;
                return builder.ToString();
            }

            builder.Append(c);
            index++;
        }

        return null;
    }
}
