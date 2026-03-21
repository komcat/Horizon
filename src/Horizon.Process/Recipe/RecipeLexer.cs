using System.Globalization;
using System.Text;

namespace Horizon.Process.Recipe;

/// <summary>
/// Single-pass tokenizer for .hrcp recipe scripts.
/// Newlines are significant as statement terminators.
/// Identifiers allow embedded hyphens for device names like Keithley-1.
/// </summary>
public sealed class RecipeLexer
{
    private readonly string _source;
    private int _pos;
    private int _line = 1;
    private int _col = 1;

    private static readonly Dictionary<string, TokenType> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["set"] = TokenType.KeywordSet,
        ["loop"] = TokenType.KeywordLoop,
        ["if"] = TokenType.KeywordIf,
        ["else"] = TokenType.KeywordElse,
        ["expr"] = TokenType.KeywordExpr,
        ["true"] = TokenType.KeywordTrue,
        ["false"] = TokenType.KeywordFalse,
    };

    public RecipeLexer(string source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public List<RecipeToken> Tokenize()
    {
        var tokens = new List<RecipeToken>();
        bool lastWasNewline = false;

        while (_pos < _source.Length)
        {
            char c = Current;

            // Skip spaces and tabs (not newlines)
            if (c is ' ' or '\t')
            {
                Advance();
                continue;
            }

            // Skip \r (handle \r\n as single newline)
            if (c == '\r')
            {
                Advance();
                continue;
            }

            // Newline (collapse consecutive)
            if (c == '\n')
            {
                if (!lastWasNewline)
                    tokens.Add(new RecipeToken(TokenType.Newline, "\\n", _line, _col));
                lastWasNewline = true;
                _line++;
                _col = 1;
                _pos++;
                continue;
            }

            lastWasNewline = false;

            // Comment
            if (c == '#')
            {
                tokens.Add(ReadComment());
                continue;
            }

            // String literal
            if (c == '"')
            {
                tokens.Add(ReadString());
                continue;
            }

            // Number (digit or dot followed by digit)
            if (char.IsDigit(c) || (c == '.' && Peek(1) is char next && char.IsDigit(next)))
            {
                tokens.Add(ReadNumber());
                continue;
            }

            // Identifier or keyword
            if (IsIdentStart(c))
            {
                tokens.Add(ReadIdentifier());
                continue;
            }

            // Two-char operators
            if (c == '>' && Peek(1) == '=') { tokens.Add(MakeToken(TokenType.GreaterEqual, ">=", 2)); continue; }
            if (c == '<' && Peek(1) == '=') { tokens.Add(MakeToken(TokenType.LessEqual, "<=", 2)); continue; }
            if (c == '=' && Peek(1) == '=') { tokens.Add(MakeToken(TokenType.EqualEqual, "==", 2)); continue; }
            if (c == '!' && Peek(1) == '=') { tokens.Add(MakeToken(TokenType.BangEqual, "!=", 2)); continue; }

            // Single-char tokens
            var singleType = c switch
            {
                '.' => TokenType.Dot,
                '(' => TokenType.LeftParen,
                ')' => TokenType.RightParen,
                '{' => TokenType.LeftBrace,
                '}' => TokenType.RightBrace,
                ':' => TokenType.Colon,
                ',' => TokenType.Comma,
                '=' => TokenType.Equals,
                '+' => TokenType.Plus,
                '-' => TokenType.Minus,
                '*' => TokenType.Star,
                '/' => TokenType.Slash,
                '>' => TokenType.Greater,
                '<' => TokenType.Less,
                _ => (TokenType?)null
            };

            if (singleType is { } st)
            {
                tokens.Add(MakeToken(st, c.ToString(), 1));
                continue;
            }

            // Unknown character — skip with error token
            tokens.Add(new RecipeToken(TokenType.EOF, c.ToString(), _line, _col));
            Advance();
        }

        // Ensure final newline before EOF
        if (tokens.Count > 0 && tokens[^1].Type != TokenType.Newline)
            tokens.Add(new RecipeToken(TokenType.Newline, "\\n", _line, _col));

        tokens.Add(new RecipeToken(TokenType.EOF, "", _line, _col));
        return tokens;
    }

    private char Current => _source[_pos];

    private char? Peek(int offset)
    {
        int idx = _pos + offset;
        return idx < _source.Length ? _source[idx] : null;
    }

    private void Advance()
    {
        _pos++;
        _col++;
    }

    private RecipeToken MakeToken(TokenType type, string value, int length)
    {
        var token = new RecipeToken(type, value, _line, _col);
        _pos += length;
        _col += length;
        return token;
    }

    private RecipeToken ReadComment()
    {
        int startCol = _col;
        Advance(); // skip '#'
        var sb = new StringBuilder();
        while (_pos < _source.Length && Current != '\n' && Current != '\r')
        {
            sb.Append(Current);
            Advance();
        }
        return new RecipeToken(TokenType.Comment, sb.ToString().Trim(), _line, startCol);
    }

    private RecipeToken ReadString()
    {
        int startLine = _line;
        int startCol = _col;
        Advance(); // skip opening '"'
        var sb = new StringBuilder();
        while (_pos < _source.Length && Current != '"')
        {
            if (Current == '\\' && Peek(1) is char esc)
            {
                Advance(); // skip backslash
                sb.Append(esc switch
                {
                    'n' => '\n',
                    't' => '\t',
                    '\\' => '\\',
                    '"' => '"',
                    _ => esc
                });
                Advance();
            }
            else
            {
                if (Current == '\n') { _line++; _col = 0; }
                sb.Append(Current);
                Advance();
            }
        }
        if (_pos < _source.Length) Advance(); // skip closing '"'
        return new RecipeToken(TokenType.StringLiteral, sb.ToString(), startLine, startCol);
    }

    private RecipeToken ReadNumber()
    {
        int startCol = _col;
        int start = _pos;

        // Integer part
        while (_pos < _source.Length && char.IsDigit(Current)) Advance();

        // Decimal part
        if (_pos < _source.Length && Current == '.' && Peek(1) is char d && char.IsDigit(d))
        {
            Advance(); // skip '.'
            while (_pos < _source.Length && char.IsDigit(Current)) Advance();
        }

        // Scientific notation
        if (_pos < _source.Length && Current is 'e' or 'E')
        {
            Advance();
            if (_pos < _source.Length && Current is '+' or '-') Advance();
            while (_pos < _source.Length && char.IsDigit(Current)) Advance();
        }

        string text = _source[start.._pos];
        return new RecipeToken(TokenType.NumberLiteral, text, _line, startCol);
    }

    private RecipeToken ReadIdentifier()
    {
        int startCol = _col;
        int start = _pos;

        // First char: letter or underscore
        Advance();

        // Subsequent: letter, digit, underscore, or hyphen followed by alphanumeric
        while (_pos < _source.Length)
        {
            char c = Current;
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                Advance();
            }
            else if (c == '-' && Peek(1) is char after && (char.IsLetterOrDigit(after) || after == '_'))
            {
                // Allow hyphen only if followed by alphanumeric (Keithley-1, IO-Device)
                Advance(); // skip '-'
                Advance(); // skip next char
            }
            else
            {
                break;
            }
        }

        string text = _source[start.._pos];

        // Check for keyword
        if (Keywords.TryGetValue(text, out var kwType))
            return new RecipeToken(kwType, text, _line, startCol);

        return new RecipeToken(TokenType.Identifier, text, _line, startCol);
    }

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';
}
