using System.Text;

namespace Horizon.Workflow.Recipe;

/// <summary>
/// Single-pass tokenizer for the <c>.hrec</c> recipe language. Whitespace is
/// insignificant except newlines (statement separators); <c>#</c> starts a
/// line comment. Identifiers may contain a hyphen when it is followed by a
/// letter or <c>_</c>, so device names like <c>gantry-main</c> lex as one
/// token while <c>a - b</c> (spaces) is subtraction.
/// </summary>
internal sealed class RecipeLexer
{
    private readonly string _src;
    private int _pos;
    private int _line = 1;
    private int _col = 1;

    private RecipeLexer(string source) => _src = source ?? string.Empty;

    /// <summary>Tokenizes <paramref name="source"/> to a list ending with <see cref="TokenType.EndOfFile"/>.</summary>
    public static IReadOnlyList<RecipeToken> Tokenize(string source)
    {
        var lexer = new RecipeLexer(source);
        var tokens = new List<RecipeToken>();
        RecipeToken tok;
        do
        {
            tok = lexer.Next();
            tokens.Add(tok);
        }
        while (tok.Type != TokenType.EndOfFile);
        return tokens;
    }

    private bool AtEnd => _pos >= _src.Length;
    private char Cur => _src[_pos];
    private char Peek(int ahead = 1) => _pos + ahead < _src.Length ? _src[_pos + ahead] : '\0';

    private char Advance()
    {
        var c = _src[_pos++];
        if (c == '\n') { _line++; _col = 1; }
        else { _col++; }
        return c;
    }

    private bool Match(char expected)
    {
        if (AtEnd || Cur != expected) return false;
        Advance();
        return true;
    }

    private RecipeToken Next()
    {
        SkipInsignificant();
        if (AtEnd) return new RecipeToken(TokenType.EndOfFile, string.Empty, _line, _col);

        int line = _line, col = _col;
        char c = Cur;

        if (c == '\n') { Advance(); return new RecipeToken(TokenType.Newline, "\\n", line, col); }
        if (c == '"') return ReadString(line, col);
        if (char.IsDigit(c)) return ReadNumber(line, col);
        if (IsIdentStart(c)) return ReadIdentifier(line, col);
        return ReadOperator(line, col);
    }

    /// <summary>Skips spaces, tabs, carriage returns, and <c>#</c> comments — but not newlines.</summary>
    private void SkipInsignificant()
    {
        while (!AtEnd)
        {
            char c = Cur;
            if (c is ' ' or '\t' or '\r') Advance();
            else if (c == '#') { while (!AtEnd && Cur != '\n') Advance(); }
            else break;
        }
    }

    private RecipeToken ReadString(int line, int col)
    {
        Advance();   // opening quote
        var sb = new StringBuilder();
        while (!AtEnd && Cur != '"')
        {
            char c = Advance();
            if (c == '\\' && !AtEnd)
            {
                char e = Advance();
                sb.Append(e switch { 'n' => '\n', 't' => '\t', '"' => '"', '\\' => '\\', _ => e });
            }
            else
            {
                sb.Append(c);
            }
        }
        if (AtEnd) return new RecipeToken(TokenType.Error, "unterminated string", line, col);
        Advance();   // closing quote
        return new RecipeToken(TokenType.String, sb.ToString(), line, col);
    }

    private RecipeToken ReadNumber(int line, int col)
    {
        int start = _pos;
        while (!AtEnd && char.IsDigit(Cur)) Advance();

        // fractional part
        if (!AtEnd && Cur == '.' && char.IsDigit(Peek())) { Advance(); while (!AtEnd && char.IsDigit(Cur)) Advance(); }

        // exponent: e[+/-]digits — only consumed when real digits follow
        if (!AtEnd && (Cur == 'e' || Cur == 'E'))
        {
            char p1 = Peek();
            bool isExponent = char.IsDigit(p1) || ((p1 == '+' || p1 == '-') && char.IsDigit(Peek(2)));
            if (isExponent)
            {
                Advance();                                   // e / E
                if (Cur is '+' or '-') Advance();            // optional sign
                while (!AtEnd && char.IsDigit(Cur)) Advance();
            }
        }

        return new RecipeToken(TokenType.Number, _src[start.._pos], line, col);
    }

    private RecipeToken ReadIdentifier(int line, int col)
    {
        int start = _pos;
        Advance();   // first char (already known to be an identifier start)
        while (!AtEnd)
        {
            char c = Cur;
            if (IsIdentPart(c)) Advance();
            // a hyphen continues the identifier only when followed by a letter
            // or '_' — keeps 'gantry-main' whole while 'a-1' stays as 'a','-','1'
            else if (c == '-' && IsIdentStart(Peek())) Advance();
            else break;
        }
        string text = _src[start.._pos];
        return new RecipeToken(KeywordOrIdentifier(text), text, line, col);
    }

    private RecipeToken ReadOperator(int line, int col)
    {
        char c = Advance();
        return c switch
        {
            '+' => new RecipeToken(TokenType.Plus, "+", line, col),
            '-' => new RecipeToken(TokenType.Minus, "-", line, col),
            '*' => new RecipeToken(TokenType.Star, "*", line, col),
            '/' => new RecipeToken(TokenType.Slash, "/", line, col),
            '(' => new RecipeToken(TokenType.LParen, "(", line, col),
            ')' => new RecipeToken(TokenType.RParen, ")", line, col),
            ',' => new RecipeToken(TokenType.Comma, ",", line, col),
            '.' => new RecipeToken(TokenType.Dot, ".", line, col),
            ':' => new RecipeToken(TokenType.Colon, ":", line, col),
            '=' => Match('=') ? new RecipeToken(TokenType.Equal, "==", line, col)
                              : new RecipeToken(TokenType.Assign, "=", line, col),
            '!' => Match('=') ? new RecipeToken(TokenType.NotEqual, "!=", line, col)
                              : new RecipeToken(TokenType.Error, "!", line, col),
            '>' => Match('=') ? new RecipeToken(TokenType.GreaterEqual, ">=", line, col)
                              : new RecipeToken(TokenType.Greater, ">", line, col),
            '<' => Match('=') ? new RecipeToken(TokenType.LessEqual, "<=", line, col)
                              : new RecipeToken(TokenType.Less, "<", line, col),
            _   => new RecipeToken(TokenType.Error, c.ToString(), line, col),
        };
    }

    private static TokenType KeywordOrIdentifier(string text) => text switch
    {
        "set"   => TokenType.Set,
        "loop"  => TokenType.Loop,
        "if"    => TokenType.If,
        "else"  => TokenType.Else,
        "end"   => TokenType.End,
        "true"  => TokenType.True,
        "false" => TokenType.False,
        _       => TokenType.Identifier,
    };

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';
    private static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c == '_';
}
