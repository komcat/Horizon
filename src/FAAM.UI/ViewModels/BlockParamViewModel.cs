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

        // Colour call-outs — fixed list (matches the dispatcher's lighting.setLightColor parser).
        if (spec.Kind == ParamKind.LightColor)
            foreach (var c in Horizon.Engine.LightColors.Names)
                Options.Add(c);
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

    /// <summary>
    /// Live source for options that can change while the editor is open (the device's
    /// saved positions). When set, <see cref="RefreshOptions"/> re-reads it — the combo
    /// calls that each time its drop-down opens.
    /// </summary>
    public Func<IEnumerable<string>>? OptionsProvider { get; set; }

    /// <summary>Re-reads <see cref="OptionsProvider"/> (no-op without one), keeping the typed value.</summary>
    public void RefreshOptions()
    {
        if (OptionsProvider == null) return;
        var keep = _stringValue;
        SetOptions(OptionsProvider());
        if (_stringValue != keep) StringValue = keep;
    }

    /// <summary>
    /// Replaces the options (called when the block's device changes, or on refresh). Synced
    /// in place — only stale items removed / new ones inserted — so the combo's current
    /// selection isn't dropped when it is still in the list.
    /// </summary>
    public void SetOptions(IEnumerable<string> options)
    {
        var next = options.ToList();
        for (int i = Options.Count - 1; i >= 0; i--)
            if (!next.Contains(Options[i]))
                Options.RemoveAt(i);
        for (int i = 0; i < next.Count; i++)
        {
            int at = Options.IndexOf(next[i]);
            if (at == i) continue;
            if (at >= 0) Options.Move(at, i);
            else Options.Insert(i, next[i]);
        }
    }
}
