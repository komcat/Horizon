using System.Globalization;

namespace Horizon.Workflow;

/// <summary>
/// Boolean condition evaluator for deck transitions — comparisons
/// (<c>&gt; &lt; &gt;= &lt;= == !=</c>), logical <c>AND</c>/<c>OR</c>,
/// parentheses, numeric / quoted-string literals, and variable references
/// resolved from a <see cref="WorkflowContext"/>. Recursive-descent.
///
/// An empty expression evaluates to <c>true</c> ("no extra condition — proceed").
/// A variable that is missing or non-numeric simply makes a numeric comparison
/// <c>false</c> rather than throwing — so a deck with an uncaptured value stops
/// safely instead of faulting. Genuine syntax errors throw <see cref="FormatException"/>.
///
/// Grammar:
///   or   := and ( "OR" and )*
///   and  := unary ( "AND" unary )*
///   unary:= "(" or ")" | comparison
///   comparison := value OP value
///   value := number | "string" | identifier
/// </summary>
internal static class ConditionEvaluator
{
    /// <summary>Evaluates the condition. Empty/whitespace → true. Throws on syntax error.</summary>
    public static bool Evaluate(string? expression, WorkflowContext context)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return true;

        var parser = new Parser(expression, context);
        var value = parser.ParseOr();
        parser.ExpectEnd();
        return value;
    }

    /// <summary>Checks syntax without needing real values. Returns the error message, or null if valid.</summary>
    public static string? Validate(string? expression)
    {
        try
        {
            Evaluate(expression, new WorkflowContext());
            return null;
        }
        catch (FormatException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>A condition operand — numeric when it parses as a number, else text.</summary>
    private readonly record struct Operand(bool IsNumber, double Number, string Text)
    {
        public static Operand FromNumber(double n) => new(true, n, string.Empty);
        public static Operand FromText(string t)
            => double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                ? new Operand(true, n, t)
                : new Operand(false, 0, t);
    }

    private sealed class Parser
    {
        private readonly string _text;
        private readonly WorkflowContext _ctx;
        private int _pos;

        public Parser(string text, WorkflowContext ctx)
        {
            _text = text;
            _ctx = ctx;
        }

        public bool ParseOr()
        {
            var value = ParseAnd();
            while (MatchKeyword("OR"))
                value = ParseAnd() | value;   // no short-circuit — both sides must parse
            return value;
        }

        private bool ParseAnd()
        {
            var value = ParseUnary();
            while (MatchKeyword("AND"))
                value = ParseUnary() & value;
            return value;
        }

        private bool ParseUnary()
        {
            SkipWhitespace();
            if (Match('('))
            {
                var value = ParseOr();
                if (!Match(')')) throw new FormatException("Expected ')'");
                return value;
            }
            return ParseComparison();
        }

        private bool ParseComparison()
        {
            var left = ParseValue();
            var op = ParseOperator();
            var right = ParseValue();

            // Numeric comparison when both sides are numeric; otherwise an
            // ordinal string comparison. A type mismatch (one side a real
            // number, the other non-numeric text) is simply not-equal / false.
            if (left.IsNumber && right.IsNumber)
                return CompareNumbers(left.Number, op, right.Number);

            int cmp = string.CompareOrdinal(left.Text, right.Text);
            bool equal = !left.IsNumber && !right.IsNumber
                ? cmp == 0
                : false;   // number vs non-numeric text → never equal
            return op switch
            {
                "==" => equal,
                "!=" => !equal,
                ">"  => cmp > 0,
                "<"  => cmp < 0,
                ">=" => cmp >= 0,
                "<=" => cmp <= 0,
                _    => throw new FormatException($"Unknown operator '{op}'"),
            };
        }

        private static bool CompareNumbers(double l, string op, double r) => op switch
        {
            ">"  => l > r,
            "<"  => l < r,
            ">=" => l >= r,
            "<=" => l <= r,
            "==" => l == r,
            "!=" => l != r,
            _    => throw new FormatException($"Unknown operator '{op}'"),
        };

        private Operand ParseValue()
        {
            SkipWhitespace();
            if (_pos >= _text.Length)
                throw new FormatException("Expected a value");

            // Quoted string literal.
            if (_text[_pos] == '"')
            {
                _pos++;
                int s = _pos;
                while (_pos < _text.Length && _text[_pos] != '"') _pos++;
                if (_pos >= _text.Length) throw new FormatException("Unterminated string literal");
                var literal = _text[s.._pos];
                _pos++;   // closing quote
                return new Operand(false, 0, literal);
            }

            // Number literal or identifier.
            int start = _pos;
            while (_pos < _text.Length &&
                   (char.IsLetterOrDigit(_text[_pos]) || _text[_pos] is '.' or '_' or '-' or '+'))
                _pos++;
            if (_pos == start)
                throw new FormatException($"Expected a value at position {start}");

            var token = _text[start.._pos];
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
                return Operand.FromNumber(num);

            // Variable reference — resolved from the context.
            return Operand.FromText(_ctx.GetString(token));
        }

        private string ParseOperator()
        {
            SkipWhitespace();
            if (_pos + 1 < _text.Length)
            {
                var two = _text.Substring(_pos, 2);
                if (two is ">=" or "<=" or "==" or "!=")
                {
                    _pos += 2;
                    return two;
                }
            }
            if (_pos < _text.Length && _text[_pos] is '>' or '<')
                return _text[_pos++].ToString();

            throw new FormatException($"Expected a comparison operator at position {_pos}");
        }

        public void ExpectEnd()
        {
            SkipWhitespace();
            if (_pos != _text.Length)
                throw new FormatException($"Unexpected trailing text at position {_pos}");
        }

        private void SkipWhitespace()
        {
            while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos])) _pos++;
        }

        private bool Match(char c)
        {
            SkipWhitespace();
            if (_pos < _text.Length && _text[_pos] == c) { _pos++; return true; }
            return false;
        }

        /// <summary>Matches a whole-word keyword (case-insensitive), e.g. AND / OR.</summary>
        private bool MatchKeyword(string keyword)
        {
            SkipWhitespace();
            if (_pos + keyword.Length > _text.Length)
                return false;
            if (string.Compare(_text, _pos, keyword, 0, keyword.Length,
                                StringComparison.OrdinalIgnoreCase) != 0)
                return false;
            // Must be a standalone word, not the prefix of an identifier.
            int after = _pos + keyword.Length;
            if (after < _text.Length &&
                (char.IsLetterOrDigit(_text[after]) || _text[after] == '_'))
                return false;
            _pos = after;
            return true;
        }
    }
}
