using System.Globalization;

namespace Horizon.Workflow.Recipe;

/// <summary>
/// A recipe runtime value — either a number or a string. Recipes are loosely
/// typed: arithmetic coerces to number, interpolation/display coerces to text.
/// </summary>
internal readonly struct RecipeValue
{
    private readonly double _number;
    private readonly string? _string;   // non-null => this is a string value

    private RecipeValue(double number, string? str)
    {
        _number = number;
        _string = str;
    }

    public static RecipeValue Number(double n) => new(n, null);
    public static RecipeValue Text(string s) => new(0, s ?? string.Empty);
    public static RecipeValue Bool(bool b) => new(b ? 1 : 0, null);
    public static readonly RecipeValue Zero = Number(0);

    public bool IsString => _string != null;

    /// <summary>Numeric view — a string is parsed (invariant), or 0 if not numeric.</summary>
    public double AsNumber()
        => _string == null
            ? _number
            : double.TryParse(_string, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0.0;

    /// <summary>Truthy = non-zero number, or non-empty string.</summary>
    public bool IsTruthy => _string == null ? _number != 0.0 : _string.Length != 0;

    /// <summary>Text view — for display, logging, and string interpolation.</summary>
    public string Display()
        => _string ?? _number.ToString(CultureInfo.InvariantCulture);

    public override string ToString() => Display();
}
