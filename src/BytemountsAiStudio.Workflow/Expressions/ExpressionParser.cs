using System.Globalization;
using System.Text.Json;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;

namespace BytemountsAiStudio.Workflow.Expressions;

/// Kısıtlı koşul ifadesi (mimari §6.2).
///
/// KASITLI OLARAK ZAYIF bir dil. Desteklenen her şey:
///   - alan erişimi: `qc.passed`, `channel.mode`
///   - sabitler: sayı, tırnaklı metin, true/false, null
///   - karşılaştırma: == != &lt; &lt;= &gt; &gt;=
///   - mantık: &amp;&amp; || ! ve parantez
///
/// Desteklenmeyen her şey de kasıtlı: fonksiyon çağrısı yok, atama yok,
/// döngü yok, kod çalıştırma yok. §19.1/R7'nin savunması bu — kendi engine'ini
/// yazan projelerin çoğu farkında olmadan bir programlama dili yazmaya başlar.
/// Bu dil büyümeye izin vermiyor.
public static class ExpressionParser
{
    public static Result<Expression> TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Error.Permanent("expr.empty", "İfade boş.");
        }

        var tokens = Tokenizer.Tokenize(text);
        if (tokens.IsFailure)
        {
            return Result.Failure<Expression>(tokens.Error);
        }

        var parser = new Parser(tokens.Value);
        var expression = parser.ParseOr();

        if (expression.IsFailure)
        {
            return expression;
        }

        return parser.AtEnd
            ? expression
            : Error.Permanent("expr.trailing",
                $"İfadenin sonunda beklenmeyen içerik: '{parser.Remaining}'");
    }

    private sealed class Parser(IReadOnlyList<Token> tokens)
    {
        private int _position;

        public bool AtEnd => _position >= tokens.Count;

        public string Remaining => string.Join(' ', tokens.Skip(_position).Select(t => t.Text));

        public Result<Expression> ParseOr()
        {
            var left = ParseAnd();
            if (left.IsFailure)
            {
                return left;
            }

            while (Match(TokenKind.Or))
            {
                var right = ParseAnd();
                if (right.IsFailure)
                {
                    return right;
                }

                left = Result.Success<Expression>(new OrExpression(left.Value, right.Value));
            }

            return left;
        }

        private Result<Expression> ParseAnd()
        {
            var left = ParseComparison();
            if (left.IsFailure)
            {
                return left;
            }

            while (Match(TokenKind.And))
            {
                var right = ParseComparison();
                if (right.IsFailure)
                {
                    return right;
                }

                left = Result.Success<Expression>(new AndExpression(left.Value, right.Value));
            }

            return left;
        }

        private Result<Expression> ParseComparison()
        {
            var left = ParseUnary();
            if (left.IsFailure)
            {
                return left;
            }

            if (Peek() is { Kind: TokenKind.Comparison } op)
            {
                _position++;
                var right = ParseUnary();

                return right.IsFailure
                    ? right
                    : Result.Success<Expression>(new ComparisonExpression(left.Value, op.Text, right.Value));
            }

            return left;
        }

        private Result<Expression> ParseUnary()
        {
            if (Match(TokenKind.Not))
            {
                var inner = ParseUnary();
                return inner.IsFailure ? inner : Result.Success<Expression>(new NotExpression(inner.Value));
            }

            return ParsePrimary();
        }

        private Result<Expression> ParsePrimary()
        {
            var token = Peek();

            if (token is null)
            {
                return Error.Permanent("expr.unexpected_end", "İfade beklenmedik şekilde bitti.");
            }

            _position++;

            switch (token.Kind)
            {
                case TokenKind.OpenParen:
                    var inner = ParseOr();
                    if (inner.IsFailure)
                    {
                        return inner;
                    }

                    return Match(TokenKind.CloseParen)
                        ? inner
                        : Error.Permanent("expr.unbalanced", "Kapanmayan parantez.");

                case TokenKind.Number:
                    return Result.Success<Expression>(
                        new LiteralExpression(double.Parse(token.Text, CultureInfo.InvariantCulture)));

                case TokenKind.String:
                    return Result.Success<Expression>(new LiteralExpression(token.Text));

                case TokenKind.Identifier when token.Text is "true" or "false":
                    return Result.Success<Expression>(
                        new LiteralExpression(string.Equals(token.Text, "true", StringComparison.Ordinal)));

                case TokenKind.Identifier when token.Text == "null":
                    return Result.Success<Expression>(new LiteralExpression(null));

                case TokenKind.Identifier:
                    return Result.Success<Expression>(new PathExpression(token.Text));

                default:
                    return Error.Permanent("expr.unexpected_token",
                        $"Beklenmeyen simge: '{token.Text}'");
            }
        }

        private Token? Peek() => _position < tokens.Count ? tokens[_position] : null;

        private bool Match(TokenKind kind)
        {
            if (Peek()?.Kind != kind)
            {
                return false;
            }

            _position++;
            return true;
        }
    }
}

public abstract record Expression
{
    /// İfadeyi bağlam üzerinde değerlendirir.
    ///
    /// Bağlam node çıktılarının JSON belgesi: `qc.passed` → `context["qc"]["passed"]`.
    public abstract object? Evaluate(JsonElement context);

    public bool EvaluateAsBoolean(JsonElement context) => Truthy(Evaluate(context));

    /// Hangi değerlerin "doğru" sayıldığı açıkça tanımlı.
    ///
    /// JavaScript'in örtük dönüşüm kuralları burada YOK: boş metin ya da
    /// sıfır, `false` DEĞİL — çünkü `when: "qc.score"` yazan biri muhtemelen
    /// bir karşılaştırma yazmayı unutmuştur ve sessizce yanlış dallanmaktansa
    /// açıkça doğru saymak daha öngörülebilir.
    protected static bool Truthy(object? value) => value switch
    {
        null => false,
        bool b => b,
        _ => true,
    };
}

public sealed record LiteralExpression(object? Value) : Expression
{
    public override object? Evaluate(JsonElement context) => Value;
}

/// Noktalı yol: `qc.passed`, `script.section_count`.
public sealed record PathExpression(string Path) : Expression
{
    public override object? Evaluate(JsonElement context)
    {
        var current = context;

        foreach (var part in Path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object
                || !current.TryGetProperty(part, out var next))
            {
                // Olmayan yol null döner, patlamaz: workflow'un ilk
                // node'unda henüz var olmayan çıktılara referans normal.
                return null;
            }

            current = next;
        }

        return current.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => current.GetDouble(),
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => current.ToString(),
        };
    }
}

public sealed record NotExpression(Expression Inner) : Expression
{
    public override object Evaluate(JsonElement context) => !Truthy(Inner.Evaluate(context));
}

public sealed record AndExpression(Expression Left, Expression Right) : Expression
{
    // Kısa devre: sol yanlışsa sağ değerlendirilmez.
    public override object Evaluate(JsonElement context)
        => Truthy(Left.Evaluate(context)) && Truthy(Right.Evaluate(context));
}

public sealed record OrExpression(Expression Left, Expression Right) : Expression
{
    public override object Evaluate(JsonElement context)
        => Truthy(Left.Evaluate(context)) || Truthy(Right.Evaluate(context));
}

public sealed record ComparisonExpression(Expression Left, string Operator, Expression Right) : Expression
{
    public override object Evaluate(JsonElement context)
    {
        var left = Left.Evaluate(context);
        var right = Right.Evaluate(context);

        if (Operator is "==" or "!=")
        {
            var equal = Equals(Normalize(left), Normalize(right));
            return Operator == "==" ? equal : !equal;
        }

        // Sıralama karşılaştırmaları yalnızca sayılarda anlamlı. Metinler
        // için kültüre bağlı sıralama sürprizi üretirdi ("i" < "I"?).
        if (left is not double a || right is not double b)
        {
            return false;
        }

        return Operator switch
        {
            "<" => a < b,
            "<=" => a <= b,
            ">" => a > b,
            ">=" => a >= b,
            _ => false,
        };
    }

    /// Sayı ve metin karşılaştırmasında tip uyuşmazlığını çözer:
    /// `qc.score == "85"` ile `qc.score == 85` aynı sonucu vermeli.
    private static object? Normalize(object? value) => value switch
    {
        double d => d,
        string s when double.TryParse(s, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => value,
    };
}
