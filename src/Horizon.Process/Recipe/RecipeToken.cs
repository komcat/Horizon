namespace Horizon.Process.Recipe;

public enum TokenType
{
    // Literals
    Identifier,
    NumberLiteral,
    StringLiteral,

    // Punctuation
    Dot,
    LeftParen,
    RightParen,
    LeftBrace,
    RightBrace,
    Colon,
    Comma,

    // Assignment
    Equals,

    // Arithmetic
    Plus,
    Minus,
    Star,
    Slash,

    // Comparison
    Greater,
    Less,
    GreaterEqual,
    LessEqual,
    EqualEqual,
    BangEqual,

    // Keywords
    KeywordSet,
    KeywordLoop,
    KeywordIf,
    KeywordElse,
    KeywordExpr,
    KeywordTrue,
    KeywordFalse,

    // Structure
    Comment,
    Newline,
    EOF,
}

public readonly record struct RecipeToken(
    TokenType Type,
    string Value,
    int Line,
    int Column)
{
    public override string ToString() => Type switch
    {
        TokenType.Newline => $"[Newline L{Line}]",
        TokenType.EOF => "[EOF]",
        _ => $"[{Type} \"{Value}\" L{Line}:{Column}]"
    };
}
