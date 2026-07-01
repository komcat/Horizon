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
    private int _gridYRows = 12;
    private double _gridStepUm = 5;
    private int _maxGridNodes = 4096;

    private double _detectDeltaV = 0.02;
    private double _dropFraction = 0.3;
    private int _dropConfirmCount = 2;

    private bool _hillClimbEnabled = true;
    private bool _autoWEnabled;
    private int _autoSledWaitMs = 4000;

    private bool _autoVisionUCorrection;
    private double _autoVisionUConfirmDeg = 1.0;
    private double _autoVisionUMaxDeg = 3.0;

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
    public int GridYRows { get => _gridYRows; set => SetField(ref _gridYRows, value); }
    public double GridStepUm { get => _gridStepUm; set => SetField(ref _gridStepUm, value); }
    public int MaxGridNodes { get => _maxGridNodes; set => SetField(ref _maxGridNodes, value); }

    // --- Detection (shared by both stages) ---
    public double DetectDeltaV { get => _detectDeltaV; set => SetField(ref _detectDeltaV, value); }
    public double DropFraction { get => _dropFraction; set => SetField(ref _dropFraction, value); }
    public int DropConfirmCount { get => _dropConfirmCount; set => SetField(ref _dropConfirmCount, value); }

    // --- Hand-off chain (after grid search finds signal) ---
    public bool HillClimbEnabled { get => _hillClimbEnabled; set => SetField(ref _hillClimbEnabled, value); }
    public bool AutoWEnabled { get => _autoWEnabled; set => SetField(ref _autoWEnabled, value); }
    public int AutoSledWaitMs { get => _autoSledWaitMs; set => SetField(ref _autoSledWaitMs, value); }

    // --- Automatic vision U-correction (before the U phase of Start correction) ---
    public bool AutoVisionUCorrection { get => _autoVisionUCorrection; set => SetField(ref _autoVisionUCorrection, value); }
    public double AutoVisionUConfirmDeg { get => _autoVisionUConfirmDeg; set => SetField(ref _autoVisionUConfirmDeg, value); }
    public double AutoVisionUMaxDeg { get => _autoVisionUMaxDeg; set => SetField(ref _autoVisionUMaxDeg, value); }

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
        GridYRows = s.GridYRows;
        GridStepUm = s.GridStepUm;
        MaxGridNodes = s.MaxGridNodes;
        DetectDeltaV = s.DetectDeltaV;
        DropFraction = s.DropFraction;
        DropConfirmCount = s.DropConfirmCount;
        HillClimbEnabled = s.HillClimbEnabled;
        AutoWEnabled = s.AutoWEnabled;
        AutoSledWaitMs = s.AutoSledWaitMs;
        AutoVisionUCorrection = s.AutoVisionUCorrection;
        AutoVisionUConfirmDeg = s.AutoVisionUConfirmDeg;
        AutoVisionUMaxDeg = s.AutoVisionUMaxDeg;
    }

    private FindFirstLightSettings BuildSettings() => new()
    {
        Enabled = _enabled,
        Stage1Enabled = _stage1Enabled,
        LineJumpUm = _lineJumpUm,
        LineStepUm = _lineStepUm,
        LineSteps = _lineSteps,
        Stage2Enabled = _stage2Enabled,
        GridYRows = _gridYRows,
        GridStepUm = _gridStepUm,
        MaxGridNodes = _maxGridNodes,
        DetectDeltaV = _detectDeltaV,
        DropFraction = _dropFraction,
        DropConfirmCount = _dropConfirmCount,
        HillClimbEnabled = _hillClimbEnabled,
        AutoWEnabled = _autoWEnabled,
        AutoSledWaitMs = _autoSledWaitMs,
        AutoVisionUCorrection = _autoVisionUCorrection,
        AutoVisionUConfirmDeg = _autoVisionUConfirmDeg,
        AutoVisionUMaxDeg = _autoVisionUMaxDeg,
    };

    private void Save()
    {
        var settings = BuildSettings();

        if (settings.AutoSledWaitMs < 0)
        {
            Status = "Invalid: Auto-SLED wait must be ≥ 0 ms.";
            return;
        }

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
