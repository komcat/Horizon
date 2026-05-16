using System.Globalization;

namespace Horizon.Workflow;

/// <summary>
/// Minimal arithmetic expression evaluator — <c>+ - * /</c>, parentheses,
/// numeric literals, and variable references resolved from a
/// <see cref="WorkflowContext"/>. Recursive-descent.
/// </summary>
internal static class ExpressionEvaluator
{
    public static double Evaluate(string expression, WorkflowContext context)
    {
        var parser = new Parser(expression, context);
        var value = parser.ParseExpression();
        parser.ExpectEnd();
        return value;
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

        public double ParseExpression()        // + and -
        {
            var value = ParseTerm();
            while (true)
            {
                if (Match('+')) value += ParseTerm();
                else if (Match('-')) value -= ParseTerm();
                else return value;
            }
        }

        private double ParseTerm()             // * and /
        {
            var value = ParseFactor();
            while (true)
            {
                if (Match('*')) value *= ParseFactor();
                else if (Match('/')) value /= ParseFactor();
                else return value;
            }
        }

        private double ParseFactor()
        {
            if (Match('('))
            {
                var value = ParseExpression();
                if (!Match(')')) throw new FormatException("Expected ')'");
                return value;
            }
            if (Match('-')) return -ParseFactor();
            if (Match('+')) return ParseFactor();

            SkipWhitespace();
            int start = _pos;
            while (_pos < _text.Length &&
                   (char.IsLetterOrDigit(_text[_pos]) || _text[_pos] is '.' or '_'))
                _pos++;
            if (_pos == start)
                throw new FormatException($"Unexpected character at {start}");

            var token = _text[start.._pos];
            return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var num)
                ? num
                : _ctx.GetNumeric(token);      // variable reference
        }

        public void ExpectEnd()
        {
            SkipWhitespace();
            if (_pos != _text.Length)
                throw new FormatException($"Unexpected trailing text at {_pos}");
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
    }
}
