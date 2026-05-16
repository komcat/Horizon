namespace Horizon.Workflow.Recipe;

/// <summary>
/// Runtime variable store for one recipe execution — a case-insensitive map of
/// name → <see cref="RecipeValue"/>. Fresh per run.
/// </summary>
internal sealed class RecipeContext
{
    private readonly Dictionary<string, RecipeValue> _vars = new(StringComparer.OrdinalIgnoreCase);

    public void Set(string name, RecipeValue value) => _vars[name] = value;

    public bool TryGet(string name, out RecipeValue value) => _vars.TryGetValue(name, out value);

    /// <summary>A copy of all variables — for live debugging / a context watch.</summary>
    public IReadOnlyDictionary<string, RecipeValue> Snapshot()
        => new Dictionary<string, RecipeValue>(_vars, StringComparer.OrdinalIgnoreCase);
}
