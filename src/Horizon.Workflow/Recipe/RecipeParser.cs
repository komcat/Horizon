using System.Globalization;

namespace Horizon.Workflow.Recipe;

/// <summary>
/// Recursive-descent parser: tokens → <see cref="RecipeProgram"/>. Parse errors
/// are collected with a source location and recovery skips to the next newline,
/// so one bad line does not abort parsing the rest of the recipe.
///
/// Expression precedence (low → high): comparison, additive, multiplicative,
/// unary, primary.
/// </summary>
internal sealed class RecipeParser
{
    private readonly IReadOnlyList<RecipeToken> _tokens;
    private readonly List<RecipeParseError> _errors = new();
    private int _pos;

    private RecipeParser(IReadOnlyList<RecipeToken> tokens) => _tokens = tokens;

    /// <summary>Lexes and parses <paramref name="source"/> into a recipe program.</summary>
    public static RecipeProgram Parse(string name, string source)
    {
        var tokens = RecipeLexer.Tokenize(source);
        var parser = new RecipeParser(tokens);
        var statements = parser.ParseProgram();
        return new RecipeProgram(name, source ?? string.Empty, statements, parser._errors);
    }

    /// <summary>
    /// Parses a single expression — used for <c>{=expr}</c> string
    /// interpolation. Returns null on any parse error or trailing junk.
    /// </summary>
    internal static RecipeExpr? ParseExpressionSnippet(string text)
    {
        var parser = new RecipeParser(RecipeLexer.Tokenize(text ?? string.Empty));
        try
        {
            parser.SkipNewlines();
            var expr = parser.ParseExpression();
            parser.SkipNewlines();
            return parser.AtEnd ? expr : null;
        }
        catch (ParseException)
        {
            return null;
        }
    }

    // --- token cursor ---

    private RecipeToken Cur => _tokens[_pos];
    private bool AtEnd => Cur.Type == TokenType.EndOfFile;

    private RecipeToken Advance()
    {
        var tok = _tokens[_pos];
        if (_pos < _tokens.Count - 1) _pos++;
        return tok;
    }

    private bool Check(TokenType t) => Cur.Type == t;

    private bool Match(TokenType t)
    {
        if (!Check(t)) return false;
        Advance();
        return true;
    }

    private RecipeToken Expect(TokenType t, string what)
        => Check(t) ? Advance() : throw ErrorHere($"expected {what}, found '{Cur.Text}'");

    private ParseException ErrorHere(string message)
        => new(new RecipeParseError(Cur.Line, Cur.Column, message));

    private void SkipNewlines()
    {
        while (Check(TokenType.Newline)) Advance();
    }

    private void RecoverToNewline()
    {
        while (!AtEnd && !Check(TokenType.Newline)) Advance();
    }

    // --- program / statements ---

    private List<RecipeStatement> ParseProgram()
    {
        var stmts = new List<RecipeStatement>();
        SkipNewlines();
        while (!AtEnd)
        {
            TryParseInto(stmts);
            SkipNewlines();
        }
        return stmts;
    }

    /// <summary>Parses one statement, recording and recovering from a parse error.</summary>
    private void TryParseInto(List<RecipeStatement> into)
    {
        try
        {
            into.Add(ParseStatement());
        }
        catch (ParseException ex)
        {
            _errors.Add(ex.ParseError);
            RecoverToNewline();
        }
    }

    private RecipeStatement ParseStatement() => Cur.Type switch
    {
        TokenType.Set        => ParseSet(),
        TokenType.Loop       => ParseLoop(),
        TokenType.If         => ParseIf(),
        TokenType.Identifier => ParseCall(),
        _ => throw ErrorHere($"unexpected '{Cur.Text}' — expected a statement"),
    };

    private RecipeStatement ParseSet()
    {
        int line = Cur.Line;
        Advance();   // 'set'
        var name = Expect(TokenType.Identifier, "variable name").Text;
        Expect(TokenType.Assign, "'='");
        return new SetStatement(name, ParseExpression(), line);
    }

    private RecipeStatement ParseLoop()
    {
        int line = Cur.Line;
        Advance();   // 'loop'
        RecipeExpr? count = (Check(TokenType.Newline) || AtEnd) ? null : ParseExpression();
        var body = ParseBlock(TokenType.End);
        Expect(TokenType.End, "'end' to close the loop");
        return new LoopStatement(count, body, line);
    }

    private RecipeStatement ParseIf()
    {
        int line = Cur.Line;
        Advance();   // 'if'
        var condition = ParseExpression();
        var then = ParseBlock(TokenType.Else, TokenType.End);
        var elseBody = Match(TokenType.Else) ? ParseBlock(TokenType.End) : new List<RecipeStatement>();
        Expect(TokenType.End, "'end' to close the if");
        return new IfStatement(condition, then, elseBody, line);
    }

    /// <summary>Parses statements until a stop keyword (not consumed) or EOF.</summary>
    private List<RecipeStatement> ParseBlock(params TokenType[] stop)
    {
        var stmts = new List<RecipeStatement>();
        SkipNewlines();
        while (!AtEnd && Array.IndexOf(stop, Cur.Type) < 0)
        {
            TryParseInto(stmts);
            SkipNewlines();
        }
        return stmts;
    }

    private RecipeStatement ParseCall()
    {
        int line = Cur.Line;
        var segments = new List<string> { Advance().Text };
        while (Match(TokenType.Dot))
            segments.Add(Expect(TokenType.Identifier, "a name after '.'").Text);

        if (segments.Count is < 2 or > 3)
            throw ErrorHere(
                $"a call must be domain.method or domain.device.method (got {segments.Count} segment(s))");

        Expect(TokenType.LParen, "'(' to start arguments");
        var args = ParseArgs();
        Expect(TokenType.RParen, "')' to close arguments");

        return segments.Count == 2
            ? new CallStatement(segments[0], null, segments[1], args, line)
            : new CallStatement(segments[0], segments[1], segments[2], args, line);
    }

    private List<RecipeArg> ParseArgs()
    {
        var args = new List<RecipeArg>();
        SkipNewlines();
        if (Check(TokenType.RParen)) return args;
        do
        {
            SkipNewlines();
            var name = Expect(TokenType.Identifier, "an argument name").Text;
            Expect(TokenType.Colon, "':' after the argument name");
            args.Add(new RecipeArg(name, ParseExpression()));
            SkipNewlines();
        }
        while (Match(TokenType.Comma));
        return args;
    }

    // --- expressions ---

    private RecipeExpr ParseExpression() => ParseComparison();

    private RecipeExpr ParseComparison()
    {
        var left = ParseAdditive();
        BinaryOp? op = Cur.Type switch
        {
            TokenType.Equal        => BinaryOp.Eq,
            TokenType.NotEqual     => BinaryOp.Ne,
            TokenType.Greater      => BinaryOp.Gt,
            TokenType.Less         => BinaryOp.Lt,
            TokenType.GreaterEqual => BinaryOp.Ge,
            TokenType.LessEqual    => BinaryOp.Le,
            _ => null,
        };
        if (op is null) return left;
        int line = Cur.Line;
        Advance();
        return new BinaryExpr(left, op.Value, ParseAdditive(), line);
    }

    private RecipeExpr ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (Check(TokenType.Plus) || Check(TokenType.Minus))
        {
            var op = Check(TokenType.Plus) ? BinaryOp.Add : BinaryOp.Sub;
            int line = Cur.Line;
            Advance();
            left = new BinaryExpr(left, op, ParseMultiplicative(), line);
        }
        return left;
    }

    private RecipeExpr ParseMultiplicative()
    {
        var left = ParseUnary();
        while (Check(TokenType.Star) || Check(TokenType.Slash))
        {
            var op = Check(TokenType.Star) ? BinaryOp.Mul : BinaryOp.Div;
            int line = Cur.Line;
            Advance();
            left = new BinaryExpr(left, op, ParseUnary(), line);
        }
        return left;
    }

    private RecipeExpr ParseUnary()
    {
        if (!Check(TokenType.Minus)) return ParsePrimary();
        int line = Cur.Line;
        Advance();
        return new UnaryExpr(ParseUnary(), line);
    }

    private RecipeExpr ParsePrimary()
    {
        var tok = Cur;
        switch (tok.Type)
        {
            case TokenType.Number:
                Advance();
                if (!double.TryParse(tok.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                    throw new ParseException(new RecipeParseError(tok.Line, tok.Column, $"invalid number '{tok.Text}'"));
                return new NumberLiteral(n, tok.Line);

            case TokenType.String:
                Advance();
                return new StringLiteral(tok.Text, tok.Line);

            case TokenType.True:
                Advance();
                return new BoolLiteral(true, tok.Line);

            case TokenType.False:
                Advance();
                return new BoolLiteral(false, tok.Line);

            // 'loop' is a keyword at statement start, but in an expression it
            // begins the dotted variable 'loop.iteration'.
            case TokenType.Identifier:
            case TokenType.Loop:
            {
                var segments = new List<string> { Advance().Text };
                while (Match(TokenType.Dot))
                    segments.Add(Expect(TokenType.Identifier, "a name after '.'").Text);
                return new VariableRef(string.Join(".", segments), tok.Line);
            }

            case TokenType.LParen:
                Advance();
                var inner = ParseExpression();
                Expect(TokenType.RParen, "')'");
                return inner;

            default:
                throw ErrorHere($"expected a value, found '{tok.Text}'");
        }
    }

    /// <summary>Carries a <see cref="RecipeParseError"/> up to the recovery point.</summary>
    private sealed class ParseException : Exception
    {
        public RecipeParseError ParseError { get; }
        public ParseException(RecipeParseError error) => ParseError = error;
    }
}
