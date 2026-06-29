using FAAM.UI.Mvvm;
using Horizon.Common;

namespace FAAM.UI.ViewModels;

/// <summary>
/// The Engineer page's "Find First Light" tab — view / edit / save the post-FAU grid
/// search tunables (<see cref="FindFirstLightSettings"/>) backed by the shared
/// <see cref="AppSettingsStore"/>. Edits stay local until Save; Reload re-reads the file.
/// Save validates the values by mapping them onto a <c>GridSearchConfig</c> and calling
/// its <c>Validate()</c>, surfacing any range error in <see cref="Status"/>.
/// </summary>
public sealed class FindFirstLightSettingsViewModel : ObservableObject
{
    private readonly AppSettingsStore _store;

    private bool _enabled = true;

    private bool _stage1Enabled = true;
    private double _lineJumpUm = 60;
    private double _lineStepUm = 5;
    private int _lineSteps = 24;

    private bool _stage2Enabled = true;
    private double _gridExtentUm = 60;
    private double _gridStepUm = 5;
    private int _maxGridNodes = 4096;

    private double _detectDeltaV = 0.02;
    private double _dropFraction = 0.3;
    private int _dropConfirmCount = 2;

    private bool _hillClimbEnabled = true;
    private bool _autoWEnabled;

    private string _status = "—";

    public FindFirstLightSettingsViewModel(EngineHost host)
    {
        _store = host.Settings;
        LoadFromStore();
        SaveCommand = new RelayCommand(Save);
        ReloadCommand = new RelayCommand(Reload);
    }

    /// <summary>Master switch — when off the whole find-first-light search is skipped after FAU pre-align.</summary>
    public bool Enabled { get => _enabled; set => SetField(ref _enabled, value); }

    // --- Stage 1: X line search ---
    public bool Stage1Enabled { get => _stage1Enabled; set => SetField(ref _stage1Enabled, value); }
    public double LineJumpUm { get => _lineJumpUm; set => SetField(ref _lineJumpUm, value); }
    public double LineStepUm { get => _lineStepUm; set => SetField(ref _lineStepUm, value); }
    public int LineSteps { get => _lineSteps; set => SetField(ref _lineSteps, value); }

    // --- Stage 2: XY grid fallback ---
    public bool Stage2Enabled { get => _stage2Enabled; set => SetField(ref _stage2Enabled, value); }
    public double GridExtentUm { get => _gridExtentUm; set => SetField(ref _gridExtentUm, value); }
    public double GridStepUm { get => _gridStepUm; set => SetField(ref _gridStepUm, value); }
    public int MaxGridNodes { get => _maxGridNodes; set => SetField(ref _maxGridNodes, value); }

    // --- Detection (shared by both stages) ---
    public double DetectDeltaV { get => _detectDeltaV; set => SetField(ref _detectDeltaV, value); }
    public double DropFraction { get => _dropFraction; set => SetField(ref _dropFraction, value); }
    public int DropConfirmCount { get => _dropConfirmCount; set => SetField(ref _dropConfirmCount, value); }

    // --- Hand-off chain (after grid search finds signal) ---
    public bool HillClimbEnabled { get => _hillClimbEnabled; set => SetField(ref _hillClimbEnabled, value); }
    public bool AutoWEnabled { get => _autoWEnabled; set => SetField(ref _autoWEnabled, value); }

    public string Status { get => _status; private set => SetField(ref _status, value); }

    public RelayCommand SaveCommand { get; }
    public RelayCommand ReloadCommand { get; }

    private void LoadFromStore()
    {
        var s = _store.Current.FindFirstLight;
        Enabled = s.Enabled;
        Stage1Enabled = s.Stage1Enabled;
        LineJumpUm = s.LineJumpUm;
        LineStepUm = s.LineStepUm;
        LineSteps = s.LineSteps;
        Stage2Enabled = s.Stage2Enabled;
        GridExtentUm = s.GridExtentUm;
        GridStepUm = s.GridStepUm;
        MaxGridNodes = s.MaxGridNodes;
        DetectDeltaV = s.DetectDeltaV;
        DropFraction = s.DropFraction;
        DropConfirmCount = s.DropConfirmCount;
        HillClimbEnabled = s.HillClimbEnabled;
        AutoWEnabled = s.AutoWEnabled;
    }

    private FindFirstLightSettings BuildSettings() => new()
    {
        Enabled = _enabled,
        Stage1Enabled = _stage1Enabled,
        LineJumpUm = _lineJumpUm,
        LineStepUm = _lineStepUm,
        LineSteps = _lineSteps,
        Stage2Enabled = _stage2Enabled,
        GridExtentUm = _gridExtentUm,
        GridStepUm = _gridStepUm,
        MaxGridNodes = _maxGridNodes,
        DetectDeltaV = _detectDeltaV,
        DropFraction = _dropFraction,
        DropConfirmCount = _dropConfirmCount,
        HillClimbEnabled = _hillClimbEnabled,
        AutoWEnabled = _autoWEnabled,
    };

    private void Save()
    {
        var settings = BuildSettings();

        // Validate by mapping onto the Layer-2 config — same ranges the engine enforces.
        try
        {
            settings.ToGridSearchConfig().Validate();
        }
        catch (ArgumentException ex)
        {
            Status = $"Invalid: {ex.Message}";
            return;
        }

        // `with` preserves every other AppSettings field this tab doesn't edit.
        _store.Update(_store.Current with { FindFirstLight = settings });
        try
        {
            _store.Save();
            Status = "Saved to app_settings.json";
        }
        catch (Exception ex)
        {
            Status = $"Save failed: {ex.Message}";
        }
    }

    private void Reload()
    {
        _store.Reload();
        LoadFromStore();
        Status = "Reloaded from disk";
    }
}
