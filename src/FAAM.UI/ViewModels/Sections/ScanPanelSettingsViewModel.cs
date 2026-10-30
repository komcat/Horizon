using System.Collections.ObjectModel;
using Horizon.Station.UI.ViewModels.Sections;

namespace FAAM.UI.ViewModels.Sections;

/// <summary>
/// Machine › Settings, FAAM panel: the Run-page Scanning panel's buttons — the hexapod pose slots
/// and the script buttons (faam_scan_panel.json; also editable by right-click on the Run page).
/// </summary>
public sealed class ScanPanelSettingsViewModel : ObservableObject, IHeadSettingsPanel
{
    private readonly EngineHost _host;

    public ScanPanelSettingsViewModel(EngineHost host)
    {
        _host = host;
        // The Run-page Scanning panel edits its buttons by right-click — show those saves here.
        ScanPanelStore.Changed += Load;
    }

    /// <summary>The position buttons (caption + saved six-axis pose), saved with the Save button.</summary>
    public ObservableCollection<ScanPositionRow> ScanPositions { get; } = new();

    /// <summary>The script buttons (caption + script name), saved with the Save button.</summary>
    public ObservableCollection<ScanScriptRow> ScanScripts { get; } = new();

    /// <summary>Scripts in the active product, for the script pickers.</summary>
    public ObservableCollection<string> ScriptNames { get; } = new();

    /// <summary>Saved script-preset names, for the preset pickers (refreshed on load / save).</summary>
    public ObservableCollection<string> PresetNames { get; } = new();

    /// <summary>The hexapod the position buttons move.</summary>
    public string ScanDevice => ScanPanelStore.Device;

    public string SavedFiles => "faam_scan_panel.json";

    public void Load()
    {
        var saved = ScanPanelStore.Load();
        ScanPositions.Clear();
        foreach (var p in saved.Positions)
            ScanPositions.Add(new ScanPositionRow(p));
        ScanScripts.Clear();
        foreach (var sc in saved.Scripts)
            ScanScripts.Add(new ScanScriptRow(sc.Label, sc.Script, sc.Preset));

        ScriptNames.Clear();
        foreach (var name in _host.Process.ListScripts())
            ScriptNames.Add(name);
        PresetNames.Clear();
        foreach (var preset in ScriptPresetStore.Load().Where(p => !string.IsNullOrWhiteSpace(p.Name)))
            PresetNames.Add(preset.Name.Trim());
    }

    public string? Validate()
    {
        var bad = ScanPositions.FirstOrDefault(r => r.IsSaved && !r.ToAxes().IsValid());
        return bad == null ? null : $"Scanning {bad.Label}: X..W must be numbers";
    }

    public void Save()
    {
        ScanPanelStore.Save(new ScanPanelSettings
        {
            Positions = ScanPositions.Select(r => r.ToSlot()).ToList(),
            Scripts = ScanScripts.Select(r => new ScanScriptSlot { Label = r.Label, Script = r.Script, Preset = r.Preset }).ToList(),
        });
    }
}

/// <summary>One editable Settings row: a Run-page Scanning position button — caption + saved hex-right pose.</summary>
public sealed class ScanPositionRow : ObservableObject
{
    private string _label;
    private bool _isSaved;
    private double _x, _y, _z, _u, _v, _w;

    public ScanPositionRow(ScanPositionSlot slot)
    {
        _label = slot.Label;
        _isSaved = slot.Position != null;
        var p = slot.Position ?? new ScanAxes();
        (_x, _y, _z, _u, _v, _w) = (p.X, p.Y, p.Z, p.U, p.V, p.W);
    }

    /// <summary>Button caption.</summary>
    public string Label { get => _label; set => SetField(ref _label, value); }

    /// <summary>False = no pose saved (the button refuses to move).</summary>
    public bool IsSaved { get => _isSaved; set => SetField(ref _isSaved, value); }

    public double X { get => _x; set => SetField(ref _x, value); }
    public double Y { get => _y; set => SetField(ref _y, value); }
    public double Z { get => _z; set => SetField(ref _z, value); }
    public double U { get => _u; set => SetField(ref _u, value); }
    public double V { get => _v; set => SetField(ref _v, value); }
    public double W { get => _w; set => SetField(ref _w, value); }

    public ScanAxes ToAxes() => new() { X = _x, Y = _y, Z = _z, U = _u, V = _v, W = _w };

    public ScanPositionSlot ToSlot() => new() { Label = _label, Position = _isSaved ? ToAxes() : null };
}

/// <summary>
/// One editable Settings row: a Run-page Scanning script button — caption + script preset name,
/// or a bare <c>.moon</c> script name (used only when no preset is set).
/// </summary>
public sealed class ScanScriptRow : ObservableObject
{
    private string _label;
    private string _script;
    private string _preset;

    public ScanScriptRow(string label, string script, string preset)
    {
        _label = label;
        _script = script;
        _preset = preset;
    }

    /// <summary>Button caption.</summary>
    public string Label { get => _label; set => SetField(ref _label, value); }

    /// <summary>Script name in the active product (blank = none) — run with no arguments when no preset is set.</summary>
    public string Script { get => _script; set => SetField(ref _script, value ?? ""); }

    /// <summary>Script preset name (blank = none); wins over <see cref="Script"/>.</summary>
    public string Preset { get => _preset; set => SetField(ref _preset, value ?? ""); }
}
