namespace Horizon.Workflow.Recipe;

/// <summary>Lexical token kinds for the <c>.hrec</c> recipe language.</summary>
internal enum TokenType
{
    // literals / names
    Identifier,     // foo, gantry-main, Keithley_6482
    Number,         // 20, 3.14, 1e-9, 0.05
    String,         // "..."

    // keywords
    Set, Loop, If, Else, End, True, False,

    // arithmetic
    Plus, Minus, Star, Slash,

    // assignment / comparison
    Assign,         // =
    Equal, NotEqual, Greater, Less, GreaterEqual, LessEqual,

    // punctuation
    LParen, RParen,
    Comma, Dot, Colon,

    // structural
    Newline,        // statement separator
    EndOfFile,
    Error,          // an unrecognised character (Text holds it)
}

/// <summary>
/// One lexical token. <see cref="Line"/> / <see cref="Column"/> are 1-based and
/// used for parse-error reporting.
/// </summary>
internal readonly record struct RecipeToken(TokenType Type, string Text, int Line, int Column)
{
    public override string ToString() => $"{Type}('{Text}') @ {Line}:{Column}";
}
