namespace Horizon.Process.Recipe.Ast;

/// <summary>Base class for all AST nodes. Carries source location for error reporting and editor highlighting.</summary>
public abstract class RecipeNode
{
    public int Line { get; init; }
    public int Column { get; init; }
}

/// <summary>Root node containing all top-level statements.</summary>
public sealed class ProgramNode : RecipeNode
{
    public required List<RecipeNode> Statements { get; init; }
}

/// <summary>Variable assignment: <c>set name = expression</c></summary>
public sealed class SetVariableNode : RecipeNode
{
    public required string Name { get; init; }
    public required RecipeExpression Value { get; init; }
}

/// <summary>Device method call: <c>Device.method(arg: val, ...)</c></summary>
public sealed class MethodCallNode : RecipeNode
{
    public required string Device { get; init; }
    public required string Method { get; init; }
    public required List<NamedArg> Args { get; init; }
}

/// <summary>Loop block: <c>loop count { body }</c> or <c>loop { body }</c> for infinite.</summary>
public sealed class LoopNode : RecipeNode
{
    /// <summary>Null means infinite loop.</summary>
    public RecipeExpression? Count { get; init; }
    public required List<RecipeNode> Body { get; init; }
}

/// <summary>Conditional: <c>if condition { then } else { else }</c></summary>
public sealed class IfNode : RecipeNode
{
    public required RecipeExpression Condition { get; init; }
    public required List<RecipeNode> ThenBranch { get; init; }
    public List<RecipeNode>? ElseBranch { get; init; }
}

/// <summary>Line comment preserved for editor round-trip. Skipped by engine.</summary>
public sealed class CommentNode : RecipeNode
{
    public required string Text { get; init; }
}

/// <summary>Named argument in a method call: <c>name: value</c></summary>
public sealed class NamedArg
{
    public required string Name { get; init; }
    public required RecipeExpression Value { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }
}
