namespace Horizon.Station.UI.ViewModels;

/// <summary>One parameter row in the command-help panel — the field's label and its note.</summary>
public sealed record ParamHelpLine(string Label, string Note)
{
    public bool HasNote => !string.IsNullOrWhiteSpace(Note);
}

/// <summary>
/// Read-only view-model the Engineer right-hand panel binds to when a palette command is
/// clicked: the command's display name, category, summary, "how to use", and a row per
/// parameter (label + note). Merges <see cref="OpCatalog"/> (the op's params) with
/// <see cref="OpCatalogHelp"/> (the authored help). Built fresh per selection.
/// </summary>
public sealed class OpHelpViewModel
{
    public OpHelpViewModel(OpSpec spec)
    {
        Spec = spec;
        var help = OpCatalogHelp.For(spec.OpKey);

        Summary = help?.Summary ?? "No description authored yet for this command.";
        Usage = help?.Usage ?? "";
        HasUsage = !string.IsNullOrWhiteSpace(Usage);

        var notes = help?.ParamNotes is { } pn
            ? pn.ToDictionary(p => p.Key, p => p.Note, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        ParamLines = spec.Params
            .Select(p => new ParamHelpLine(p.Label, notes.TryGetValue(p.Key, out var n) ? n : ""))
            .ToList();
        HasParams = ParamLines.Count > 0;
    }

    public OpSpec Spec { get; }
    public string Display => Spec.Display;
    public string Category => Spec.Category;
    public string Summary { get; }
    public string Usage { get; }
    public bool HasUsage { get; }
    public IReadOnlyList<ParamHelpLine> ParamLines { get; }
    public bool HasParams { get; }
}
