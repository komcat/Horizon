namespace Horizon.Workflow.Recipe;

/// <summary>A recipe parse error with a 1-based source location.</summary>
public sealed record RecipeParseError(int Line, int Column, string Message)
{
    public override string ToString() => $"line {Line}:{Column} — {Message}";
}

/// <summary>
/// A parsed recipe — its name, source text, statement AST, and any parse
/// errors. A program with <see cref="HasErrors"/> must not be run.
/// </summary>
public sealed class RecipeProgram
{
    public string Name { get; }
    public string Source { get; }
    public IReadOnlyList<RecipeParseError> Errors { get; }
    public bool HasErrors => Errors.Count > 0;

    /// <summary>The statement AST — walked by the interpreter (assembly-internal).</summary>
    internal IReadOnlyList<RecipeStatement> Statements { get; }

    internal RecipeProgram(
        string name, string source,
        IReadOnlyList<RecipeStatement> statements,
        IReadOnlyList<RecipeParseError> errors)
    {
        Name = name;
        Source = source;
        Statements = statements;
        Errors = errors;
    }
}

// --- AST (internal — the interpreter walks it) -----------------------------

/// <summary>Base of every recipe statement. <see cref="Line"/> is 1-based.</summary>
internal abstract record RecipeStatement(int Line);

/// <summary><c>set name = value</c>.</summary>
internal sealed record SetStatement(string Name, RecipeExpr Value, int Line) : RecipeStatement(Line);

/// <summary>
/// A method call statement: <c>domain.device.method(args)</c>, or
/// <c>domain.method(args)</c> for domainless built-ins (<see cref="Device"/> null).
/// </summary>
internal sealed record CallStatement(
    string Domain, string? Device, string Method,
    IReadOnlyList<RecipeArg> Args, int Line) : RecipeStatement(Line);

/// <summary><c>loop count … end</c>; <see cref="Count"/> null = infinite loop.</summary>
internal sealed record LoopStatement(
    RecipeExpr? Count, IReadOnlyList<RecipeStatement> Body, int Line) : RecipeStatement(Line);

/// <summary><c>if cond … else … end</c>; <see cref="Else"/> empty when there is no else.</summary>
internal sealed record IfStatement(
    RecipeExpr Condition,
    IReadOnlyList<RecipeStatement> Then,
    IReadOnlyList<RecipeStatement> Else, int Line) : RecipeStatement(Line);

/// <summary>One <c>name: value</c> argument of a <see cref="CallStatement"/>.</summary>
internal sealed record RecipeArg(string Name, RecipeExpr Value);

/// <summary>Base of every recipe expression.</summary>
internal abstract record RecipeExpr(int Line);

internal sealed record NumberLiteral(double Value, int Line) : RecipeExpr(Line);
internal sealed record StringLiteral(string Value, int Line) : RecipeExpr(Line);
internal sealed record BoolLiteral(bool Value, int Line) : RecipeExpr(Line);

/// <summary>A variable reference; <see cref="Name"/> may be dotted (e.g. <c>loop.iteration</c>).</summary>
internal sealed record VariableRef(string Name, int Line) : RecipeExpr(Line);

/// <summary>Unary negation.</summary>
internal sealed record UnaryExpr(RecipeExpr Operand, int Line) : RecipeExpr(Line);

/// <summary>Arithmetic or comparison of two operands.</summary>
internal sealed record BinaryExpr(RecipeExpr Left, BinaryOp Op, RecipeExpr Right, int Line) : RecipeExpr(Line);

/// <summary>Binary operators — arithmetic and comparison (comparison yields 1.0 / 0.0).</summary>
internal enum BinaryOp { Add, Sub, Mul, Div, Eq, Ne, Gt, Lt, Ge, Le }
