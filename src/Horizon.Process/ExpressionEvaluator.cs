using System.Globalization;

namespace Horizon.Process;

/// <summary>
/// Lightweight recursive-descent arithmetic evaluator.
/// Supports: +, -, *, /, parentheses, float literals, and variable references
/// resolved from a <see cref="ProcessContext"/>.
/// </summary>
public static class ExpressionEvaluator
{
    public static float Evaluate(string expression, ProcessContext context)
    {
        var parser = new Parser(expression.AsSpan(), context);
        var result = parser.ParseExpression();
        parser.SkipWhitespace();
        if (!parser.AtEnd)
            throw new FormatException($"Unexpected character '{parser.Current}' at position {parser.Position}");
        return result;
    }

    private ref struct Parser
    {
        private readonly ReadOnlySpan<char> _text;
        private readonly ProcessContext _context;
        private int _pos;

        public Parser(ReadOnlySpan<char> text, ProcessContext context)
        {
            _text = text;
            _context = context;
            _pos = 0;
        }

        public bool AtEnd => _pos >= _text.Length;
        public char Current => _text[_pos];
        public int Position => _pos;

        public void SkipWhitespace()
        {
            while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos]))
                _pos++;
        }

        // expr → term (('+' | '-') term)*
        public float ParseExpression()
        {
            var left = ParseTerm();

            while (true)
            {
                SkipWhitespace();
                if (AtEnd) break;

                if (Current == '+') { _pos++; left += ParseTerm(); }
                else if (Current == '-') { _pos++; left -= ParseTerm(); }
                else break;
            }

            return left;
        }

        // term → factor (('*' | '/') factor)*
        private float ParseTerm()
        {
            var left = ParseFactor();

            while (true)
            {
                SkipWhitespace();
                if (AtEnd) break;

                if (Current == '*') { _pos++; left *= ParseFactor(); }
                else if (Current == '/') { _pos++; left /= ParseFactor(); }
                else break;
            }

            return left;
        }

        // factor → NUMBER | VARIABLE | '(' expr ')' | '-' factor
        private float ParseFactor()
        {
            SkipWhitespace();
            if (AtEnd) throw new FormatException("Unexpected end of expression");

            // Unary minus
            if (Current == '-')
            {
                _pos++;
                return -ParseFactor();
            }

            // Unary plus
            if (Current == '+')
            {
                _pos++;
                return ParseFactor();
            }

            // Parenthesized sub-expression
            if (Current == '(')
            {
                _pos++;
                var result = ParseExpression();
                SkipWhitespace();
                if (AtEnd || Current != ')')
                    throw new FormatException($"Missing closing parenthesis at position {_pos}");
                _pos++;
                return result;
            }

            // Number literal: digits, optional dot, optional digits
            if (char.IsDigit(Current) || Current == '.')
            {
                return ParseNumber();
            }

            // Variable name: letters, digits, dots, underscores (must start with letter or underscore)
            if (char.IsLetter(Current) || Current == '_')
            {
                return ParseVariable();
            }

            throw new FormatException($"Unexpected character '{Current}' at position {_pos}");
        }

        private float ParseNumber()
        {
            var start = _pos;
            while (_pos < _text.Length && (char.IsDigit(_text[_pos]) || _text[_pos] == '.'))
                _pos++;

            // Support exponent notation (e.g., 1.5e-3)
            if (_pos < _text.Length && (_text[_pos] == 'e' || _text[_pos] == 'E'))
            {
                _pos++;
                if (_pos < _text.Length && (_text[_pos] == '+' || _text[_pos] == '-'))
                    _pos++;
                while (_pos < _text.Length && char.IsDigit(_text[_pos]))
                    _pos++;
            }

            var span = _text[start.._pos];
            if (!float.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                throw new FormatException($"Invalid number at position {start}");
            return value;
        }

        private float ParseVariable()
        {
            var start = _pos;
            while (_pos < _text.Length &&
                   (char.IsLetterOrDigit(_text[_pos]) || _text[_pos] == '.' || _text[_pos] == '_'))
                _pos++;

            var name = _text[start.._pos].ToString();
            return _context.GetNumeric(name);
        }
    }
}
