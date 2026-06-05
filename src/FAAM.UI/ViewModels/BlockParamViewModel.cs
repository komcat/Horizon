using System.Collections.ObjectModel;
using FAAM.UI.Mvvm;

namespace FAAM.UI.ViewModels;

/// <summary>
/// One typed parameter field of a <see cref="ProcessBlockViewModel"/>.
/// <see cref="StringValue"/> is the canonical persisted value (it becomes the
/// entry in <c>WorkflowStep.Parameters</c>); the widget is chosen from
/// <see cref="Kind"/> by <see cref="ParamTemplateSelector"/>.
/// </summary>
public sealed class BlockParamViewModel : ObservableObject
{
    private readonly ParamSpec _spec;
    private string _stringValue;

    public BlockParamViewModel(ParamSpec spec, string? initial = null)
    {
        _spec = spec;
        _stringValue = initial ?? spec.Default;

        // SetClear renders as the bool check box (see ParamTemplateSelector), so
        // it needs no options. AxisName gets the fixed axis list; PositionName
        // options are filled by the owning block from the chosen device.
        if (spec.Kind == ParamKind.AxisName)
            foreach (var a in new[] { "X", "Y", "Z", "U", "V", "W" })
                Options.Add(a);

        // Comparison operators for the guided If blocks — fixed list (matches the
        // dispatcher's structured-condition parser).
        if (spec.Kind == ParamKind.Operator)
            foreach (var o in new[] { ">", ">=", "<", "<=", "==", "!=" })
                Options.Add(o);
    }

    public string Key => _spec.Key;
    public string Label => _spec.Label;
    public ParamKind Kind => _spec.Kind;

    /// <summary>The persisted value — bound by the text / number / combo templates.</summary>
    public string StringValue
    {
        get => _stringValue;
        set { if (SetField(ref _stringValue, value)) OnPropertyChanged(nameof(BoolValue)); }
    }

    /// <summary>
    /// Two-way bool view over <see cref="StringValue"/> for <see cref="ParamKind.Bool"/>
    /// fields. Truthy set matches the dispatcher's <c>Bool()</c> parser.
    /// </summary>
    public bool BoolValue
    {
        get => _stringValue.Trim().ToLowerInvariant() is "true" or "1" or "on" or "yes";
        set => StringValue = value ? "true" : "false";
    }

    /// <summary>Combo options for <see cref="ParamKind.PositionName"/> / <see cref="ParamKind.AxisName"/> / <see cref="ParamKind.SetClear"/>.</summary>
    public ObservableCollection<string> Options { get; } = new();

    /// <summary>Replaces the position-name options (called when the block's device changes).</summary>
    public void SetOptions(IEnumerable<string> options)
    {
        Options.Clear();
        foreach (var o in options)
            Options.Add(o);
    }
}
