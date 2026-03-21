namespace Horizon.Process.Recipe.Ast;

/// <summary>Base class for all expression AST nodes.</summary>
public abstract class RecipeExpression
{
    public int Line { get; init; }
    public int Column { get; init; }
}

/// <summary>Numeric literal: <c>42</c>, <c>3.14</c>, <c>1.5e-3</c></summary>
public sealed class NumberLiteral : RecipeExpression
{
    public required double Value { get; init; }
}

/// <summary>Double-quoted string: <c>"Home"</c></summary>
public sealed class StringLiteral : RecipeExpression
{
    public required string Value { get; init; }
}

/// <summary>Boolean literal: <c>true</c> or <c>false</c></summary>
public sealed class BooleanLiteral : RecipeExpression
{
    public required bool Value { get; init; }
}

/// <summary>Variable reference: <c>delay</c>, <c>reading</c></summary>
public sealed class VariableRef : RecipeExpression
{
    public required string Name { get; init; }
}

/// <summary>Binary arithmetic: <c>left op right</c></summary>
public sealed class BinaryExpr : RecipeExpression
{
    public required RecipeExpression Left { get; init; }
    public required BinaryOp Op { get; init; }
    public required RecipeExpression Right { get; init; }
}

/// <summary>Unary expression: <c>-expr</c></summary>
public sealed class UnaryExpr : RecipeExpression
{
    public required UnaryOp Op { get; init; }
    public required RecipeExpression Operand { get; init; }
}

/// <summary>Comparison: <c>left op right</c> — evaluates to bool at runtime.</summary>
public sealed class ComparisonExpr : RecipeExpression
{
    public required RecipeExpression Left { get; init; }
    public required CompareOp Op { get; init; }
    public required RecipeExpression Right { get; init; }
}

public enum BinaryOp { Add, Subtract, Multiply, Divide }

public enum UnaryOp { Negate }

public enum CompareOp
{
    GreaterThan,
    LessThan,
    GreaterOrEqual,
    LessOrEqual,
    Equal,
    NotEqual,
}
