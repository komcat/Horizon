using System.Globalization;
using Horizon.Process.Recipe.Ast;

namespace Horizon.Process.Recipe;

/// <summary>
/// Recursive descent parser that produces an AST from recipe tokens.
/// Collects parse errors and attempts recovery by skipping to next newline.
/// </summary>
public sealed class RecipeParser
{
    private readonly List<RecipeToken> _tokens;
    private readonly List<RecipeParseError> _errors = new();
    private int _pos;

    public IReadOnlyList<RecipeParseError> Errors => _errors;

    public RecipeParser(List<RecipeToken> tokens)
    {
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
    }

    public ProgramNode Parse()
    {
        var statements = new List<RecipeNode>();
        SkipNewlines();

        while (!IsAtEnd)
        {
            try
            {
                var stmt = ParseStatement();
                if (stmt != null)
                    statements.Add(stmt);
            }
            catch (RecipeParserException ex)
            {
                _errors.Add(new RecipeParseError(ex.Line, ex.Column, ex.Message));
                SkipToRecovery();
            }
        }

        return new ProgramNode { Statements = statements, Line = 1, Column = 1 };
    }

    private RecipeNode? ParseStatement()
    {
        SkipNewlines();
        if (IsAtEnd) return null;

        var token = Current;

        return token.Type switch
        {
            TokenType.Comment => ParseComment(),
            TokenType.KeywordSet => ParseSetVariable(),
            TokenType.KeywordLoop => ParseLoop(),
            TokenType.KeywordIf => ParseIf(),
            TokenType.Identifier => ParseMethodCall(),
            _ => throw new RecipeParserException(token.Line, token.Column,
                $"Unexpected token '{token.Value}' ({token.Type})")
        };
    }

    private CommentNode ParseComment()
    {
        var token = Consume(TokenType.Comment, "Expected comment");
        ConsumeNewlineOrEof();
        return new CommentNode { Text = token.Value, Line = token.Line, Column = token.Column };
    }

    private SetVariableNode ParseSetVariable()
    {
        var kw = Consume(TokenType.KeywordSet, "Expected 'set'");
        var name = Consume(TokenType.Identifier, "Expected variable name after 'set'");
        Consume(TokenType.Equals, "Expected '=' after variable name");
        var value = ParseExpression();
        ConsumeNewlineOrEof();

        return new SetVariableNode
        {
            Name = name.Value,
            Value = value,
            Line = kw.Line,
            Column = kw.Column
        };
    }

    private LoopNode ParseLoop()
    {
        var kw = Consume(TokenType.KeywordLoop, "Expected 'loop'");

        // Count is optional — if next token is '{', it's infinite
        RecipeExpression? count = null;
        if (Current.Type != TokenType.LeftBrace)
            count = ParseExpression();

        var body = ParseBlock();

        return new LoopNode
        {
            Count = count,
            Body = body,
            Line = kw.Line,
            Column = kw.Column
        };
    }

    private IfNode ParseIf()
    {
        var kw = Consume(TokenType.KeywordIf, "Expected 'if'");
        var condition = ParseComparison();
        var thenBranch = ParseBlock();

        List<RecipeNode>? elseBranch = null;
        SkipNewlines();
        if (!IsAtEnd && Current.Type == TokenType.KeywordElse)
        {
            Advance(); // consume 'else'
            elseBranch = ParseBlock();
        }

        return new IfNode
        {
            Condition = condition,
            ThenBranch = thenBranch,
            ElseBranch = elseBranch,
            Line = kw.Line,
            Column = kw.Column
        };
    }

    private MethodCallNode ParseMethodCall()
    {
        var device = Consume(TokenType.Identifier, "Expected device name");
        Consume(TokenType.Dot, "Expected '.' after device name");
        var method = Consume(TokenType.Identifier, "Expected method name after '.'");
        Consume(TokenType.LeftParen, "Expected '(' after method name");

        var args = new List<NamedArg>();
        if (Current.Type != TokenType.RightParen)
        {
            args.Add(ParseNamedArg());
            while (Current.Type == TokenType.Comma)
            {
                Advance(); // consume ','
                args.Add(ParseNamedArg());
            }
        }

        Consume(TokenType.RightParen, "Expected ')' after arguments");
        ConsumeNewlineOrEof();

        return new MethodCallNode
        {
            Device = device.Value,
            Method = method.Value,
            Args = args,
            Line = device.Line,
            Column = device.Column
        };
    }

    private NamedArg ParseNamedArg()
    {
        var name = Consume(TokenType.Identifier, "Expected parameter name");
        Consume(TokenType.Colon, "Expected ':' after parameter name");
        var value = ParseArgValue();

        return new NamedArg
        {
            Name = name.Value,
            Value = value,
            Line = name.Line,
            Column = name.Column
        };
    }

    private RecipeExpression ParseArgValue()
    {
        // String literal
        if (Current.Type == TokenType.StringLiteral)
        {
            var token = Advance();
            return new StringLiteral { Value = token.Value, Line = token.Line, Column = token.Column };
        }

        // expr(...) wrapper
        if (Current.Type == TokenType.KeywordExpr)
        {
            Advance(); // consume 'expr'
            Consume(TokenType.LeftParen, "Expected '(' after 'expr'");
            var expr = ParseExpression();
            Consume(TokenType.RightParen, "Expected ')' after expression");
            return expr;
        }

        // Boolean literals
        if (Current.Type == TokenType.KeywordTrue)
        {
            var token = Advance();
            return new BooleanLiteral { Value = true, Line = token.Line, Column = token.Column };
        }
        if (Current.Type == TokenType.KeywordFalse)
        {
            var token = Advance();
            return new BooleanLiteral { Value = false, Line = token.Line, Column = token.Column };
        }

        // Number or variable expression
        return ParseExpression();
    }

    // --- Expression parsing (precedence climbing) ---

    private RecipeExpression ParseComparison()
    {
        var left = ParseExpression();

        if (TryParseCompareOp(out var op))
        {
            var right = ParseExpression();
            return new ComparisonExpr
            {
                Left = left,
                Op = op,
                Right = right,
                Line = left.Line,
                Column = left.Column
            };
        }

        return left;
    }

    private RecipeExpression ParseExpression()
    {
        var left = ParseTerm();

        while (!IsAtEnd && Current.Type is TokenType.Plus or TokenType.Minus)
        {
            var opToken = Advance();
            var right = ParseTerm();
            var op = opToken.Type == TokenType.Plus ? BinaryOp.Add : BinaryOp.Subtract;
            left = new BinaryExpr
            {
                Left = left,
                Op = op,
                Right = right,
                Line = left.Line,
                Column = left.Column
            };
        }

        return left;
    }

    private RecipeExpression ParseTerm()
    {
        var left = ParseFactor();

        while (!IsAtEnd && Current.Type is TokenType.Star or TokenType.Slash)
        {
            var opToken = Advance();
            var right = ParseFactor();
            var op = opToken.Type == TokenType.Star ? BinaryOp.Multiply : BinaryOp.Divide;
            left = new BinaryExpr
            {
                Left = left,
                Op = op,
                Right = right,
                Line = left.Line,
                Column = left.Column
            };
        }

        return left;
    }

    private RecipeExpression ParseFactor()
    {
        if (IsAtEnd)
            throw new RecipeParserException(_tokens[^1].Line, _tokens[^1].Column, "Unexpected end of input in expression");

        var token = Current;

        // Unary minus
        if (token.Type == TokenType.Minus)
        {
            Advance();
            var operand = ParseFactor();
            return new UnaryExpr
            {
                Op = UnaryOp.Negate,
                Operand = operand,
                Line = token.Line,
                Column = token.Column
            };
        }

        // expr(...) wrapper — just evaluate the inner expression
        if (token.Type == TokenType.KeywordExpr)
        {
            Advance();
            Consume(TokenType.LeftParen, "Expected '(' after 'expr'");
            var expr = ParseExpression();
            Consume(TokenType.RightParen, "Expected ')' after expression");
            return expr;
        }

        // Parenthesized expression
        if (token.Type == TokenType.LeftParen)
        {
            Advance();
            var expr = ParseExpression();
            Consume(TokenType.RightParen, "Expected ')' after expression");
            return expr;
        }

        // Number literal
        if (token.Type == TokenType.NumberLiteral)
        {
            Advance();
            if (!double.TryParse(token.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                throw new RecipeParserException(token.Line, token.Column, $"Invalid number: {token.Value}");
            return new NumberLiteral { Value = value, Line = token.Line, Column = token.Column };
        }

        // Boolean literals
        if (token.Type == TokenType.KeywordTrue)
        {
            Advance();
            return new BooleanLiteral { Value = true, Line = token.Line, Column = token.Column };
        }
        if (token.Type == TokenType.KeywordFalse)
        {
            Advance();
            return new BooleanLiteral { Value = false, Line = token.Line, Column = token.Column };
        }

        // String literal (in expression context)
        if (token.Type == TokenType.StringLiteral)
        {
            Advance();
            return new StringLiteral { Value = token.Value, Line = token.Line, Column = token.Column };
        }

        // Variable reference
        if (token.Type == TokenType.Identifier)
        {
            Advance();
            return new VariableRef { Name = token.Value, Line = token.Line, Column = token.Column };
        }

        throw new RecipeParserException(token.Line, token.Column,
            $"Expected expression, got '{token.Value}' ({token.Type})");
    }

    private bool TryParseCompareOp(out CompareOp op)
    {
        if (IsAtEnd) { op = default; return false; }

        var token = Current;
        switch (token.Type)
        {
            case TokenType.Greater: op = CompareOp.GreaterThan; Advance(); return true;
            case TokenType.Less: op = CompareOp.LessThan; Advance(); return true;
            case TokenType.GreaterEqual: op = CompareOp.GreaterOrEqual; Advance(); return true;
            case TokenType.LessEqual: op = CompareOp.LessOrEqual; Advance(); return true;
            case TokenType.EqualEqual: op = CompareOp.Equal; Advance(); return true;
            case TokenType.BangEqual: op = CompareOp.NotEqual; Advance(); return true;
            default: op = default; return false;
        }
    }

    // --- Block parsing ---

    private List<RecipeNode> ParseBlock()
    {
        SkipNewlines();
        Consume(TokenType.LeftBrace, "Expected '{'");
        SkipNewlines();

        var statements = new List<RecipeNode>();
        while (!IsAtEnd && Current.Type != TokenType.RightBrace)
        {
            try
            {
                var stmt = ParseStatement();
                if (stmt != null)
                    statements.Add(stmt);
            }
            catch (RecipeParserException ex)
            {
                _errors.Add(new RecipeParseError(ex.Line, ex.Column, ex.Message));
                SkipToRecovery();
            }
            SkipNewlines();
        }

        Consume(TokenType.RightBrace, "Expected '}'");
        return statements;
    }

    // --- Helpers ---

    private RecipeToken Current => _tokens[_pos];
    private bool IsAtEnd => _pos >= _tokens.Count || Current.Type == TokenType.EOF;

    private RecipeToken Advance()
    {
        var token = _tokens[_pos];
        if (_pos < _tokens.Count - 1) _pos++;
        return token;
    }

    private RecipeToken Consume(TokenType expected, string errorMessage)
    {
        if (IsAtEnd || Current.Type != expected)
        {
            var token = IsAtEnd ? _tokens[^1] : Current;
            throw new RecipeParserException(token.Line, token.Column,
                $"{errorMessage} (got '{token.Value}' [{token.Type}])");
        }
        return Advance();
    }

    private void ConsumeNewlineOrEof()
    {
        if (IsAtEnd) return;
        if (Current.Type == TokenType.Newline)
        {
            Advance();
            return;
        }
        // Allow implicit newline before '}' (end of block)
        if (Current.Type == TokenType.RightBrace) return;

        throw new RecipeParserException(Current.Line, Current.Column,
            $"Expected end of line, got '{Current.Value}'");
    }

    private void SkipNewlines()
    {
        while (!IsAtEnd && Current.Type == TokenType.Newline)
            Advance();
    }

    private void SkipToRecovery()
    {
        while (!IsAtEnd && Current.Type != TokenType.Newline && Current.Type != TokenType.RightBrace)
            Advance();
        if (!IsAtEnd && Current.Type == TokenType.Newline)
            Advance();
    }

    private sealed class RecipeParserException : Exception
    {
        public int Line { get; }
        public int Column { get; }

        public RecipeParserException(int line, int column, string message)
            : base(message)
        {
            Line = line;
            Column = column;
        }
    }
}
