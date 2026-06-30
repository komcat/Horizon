using Horizon.Common;
using FAAM.UI.Mvvm;

namespace FAAM.UI.ViewModels.Sections;

/// <summary>
/// The Machine page's Settings section — view / edit / save / load the
/// application settings (<see cref="AppSettings"/>) backed by the shared
/// <see cref="AppSettingsStore"/>. Edits stay local until Save; Reload re-reads
/// the file from disk.
/// </summary>
public sealed class SettingsSectionViewModel : ObservableObject
{
    private readonly AppSettingsStore _store;
    private readonly EngineHost _host;

    private double _mmPerPixel;
    private double _cameraNeedleOffsetX;
    private double _cameraNeedleOffsetY;
    private double _cameraNeedleApproachMm;
    private double _cameraNeedleTouchZAdjustMm;
    private int _cameraNeedleDwellMs;
    private int _cameraNeedleDebounceMs;
    private string _moveClickDevice = "";
    private string _moveClickXAxis = "X";
    private string _moveClickYAxis = "Y";
    private double _moveClickXSign = 1.0;
    private double _moveClickYSign = 1.0;
    private string _hexVelocityPresetsText = "";
    private string _gantryVelocityPresetsText = "";
    private bool _showMainCameraOnStartup = true;
    private bool _autoLoginOperator = true;
    private string _status = "—";

    public SettingsSectionViewModel(EngineHost host)
    {
        _store = host.Settings;
        _host = host;
        LoadFromStore();
        SaveCommand = new RelayCommand(Save);
        ReloadCommand = new RelayCommand(Reload);
    }

    /// <summary>Label shown in the Machine-page section rail.</summary>
    public string Title => "Settings";

    /// <summary>Camera scale — real-world millimetres per image pixel.</summary>
    public double MmPerPixel { get => _mmPerPixel; set => SetField(ref _mmPerPixel, value); }

    /// <summary>Camera→needle X offset (mm): the relative gantry move that puts the
    /// needle under the camera-centered point (needle = cam + offset).</summary>
    public double CameraNeedleOffsetX { get => _cameraNeedleOffsetX; set => SetField(ref _cameraNeedleOffsetX, value); }

    /// <summary>Camera→needle Y offset (mm).</summary>
    public double CameraNeedleOffsetY { get => _cameraNeedleOffsetY; set => SetField(ref _cameraNeedleOffsetY, value); }

    /// <summary>Slow-approach distance before the needle-touch height on automated re-runs (mm).</summary>
    public double CameraNeedleApproachMm { get => _cameraNeedleApproachMm; set => SetField(ref _cameraNeedleApproachMm, value); }

    /// <summary>Manual touch-height tweak (mm): + presses deeper toward the surface, − backs off higher.</summary>
    public double CameraNeedleTouchZAdjustMm { get => _cameraNeedleTouchZAdjustMm; set => SetField(ref _cameraNeedleTouchZAdjustMm, value); }

    /// <summary>Epoxy dispense dwell while the shot output is ON (ms).</summary>
    public int CameraNeedleDwellMs { get => _cameraNeedleDwellMs; set => SetField(ref _cameraNeedleDwellMs, value); }

    /// <summary>Settle delay after toggling the shot output, on both the ON and OFF edges (ms).</summary>
    public int CameraNeedleDebounceMs { get => _cameraNeedleDebounceMs; set => SetField(ref _cameraNeedleDebounceMs, value); }

    public string MoveClickDevice { get => _moveClickDevice; set => SetField(ref _moveClickDevice, value); }
    public string MoveClickXAxis { get => _moveClickXAxis; set => SetField(ref _moveClickXAxis, value); }
    public string MoveClickYAxis { get => _moveClickYAxis; set => SetField(ref _moveClickYAxis, value); }
    public double MoveClickXSign { get => _moveClickXSign; set => SetField(ref _moveClickXSign, value); }
    public double MoveClickYSign { get => _moveClickYSign; set => SetField(ref _moveClickYSign, value); }

    /// <summary>Hex-family jog velocity presets (mm/s), comma-separated.</summary>
    public string HexVelocityPresetsText { get => _hexVelocityPresetsText; set => SetField(ref _hexVelocityPresetsText, value); }

    /// <summary>ACS / gantry-family jog velocity presets (mm/s), comma-separated.</summary>
    public string GantryVelocityPresetsText { get => _gantryVelocityPresetsText; set => SetField(ref _gantryVelocityPresetsText, value); }

    /// <summary>Pop up the main_camera window automatically when the app starts.</summary>
    public bool ShowMainCameraOnStartup
    {
        get => _showMainCameraOnStartup;
        set => SetField(ref _showMainCameraOnStartup, value);
    }

    /// <summary>Sign in automatically as the first active operator at startup (skip the login prompt).</summary>
    public bool AutoLoginOperator
    {
        get => _autoLoginOperator;
        set => SetField(ref _autoLoginOperator, value);
    }

    public string Status { get => _status; private set => SetField(ref _status, value); }

    public RelayCommand SaveCommand { get; }
    public RelayCommand ReloadCommand { get; }

    private void LoadFromStore()
    {
        var s = _store.Current;
        MmPerPixel = s.MmPerPixel;
        MoveClickDevice = s.MoveClickDevice;
        MoveClickXAxis = s.MoveClickXAxis;
        MoveClickYAxis = s.MoveClickYAxis;
        MoveClickXSign = s.MoveClickXSign;
        MoveClickYSign = s.MoveClickYSign;
        HexVelocityPresetsText = string.Join(", ", s.HexVelocityPresets);
        GantryVelocityPresetsText = string.Join(", ", s.GantryVelocityPresets);
        ShowMainCameraOnStartup = s.ShowMainCameraOnStartup;
        AutoLoginOperator = s.AutoLoginOperator;

        var cn = _host.Process.GetCameraNeedleCalibration();
        CameraNeedleOffsetX = cn.VcamneedleX;
        CameraNeedleOffsetY = cn.VcamneedleY;
        CameraNeedleApproachMm = cn.ApproachMm;
        CameraNeedleTouchZAdjustMm = cn.TouchZAdjustMm;
        CameraNeedleDwellMs = cn.DwellMs;
        CameraNeedleDebounceMs = cn.OutputDebounceMs;
    }

    private void Save()
    {
        var current = _store.Current;
        // `with` preserves fields this page doesn't edit (window layout, last program).
        _store.Update(current with
        {
            MmPerPixel = _mmPerPixel,
            MoveClickDevice = _moveClickDevice ?? "",
            MoveClickXAxis = string.IsNullOrWhiteSpace(_moveClickXAxis) ? "X" : _moveClickXAxis,
            MoveClickYAxis = string.IsNullOrWhiteSpace(_moveClickYAxis) ? "Y" : _moveClickYAxis,
            MoveClickXSign = _moveClickXSign,
            MoveClickYSign = _moveClickYSign,
            HexVelocityPresets = ParsePresets(_hexVelocityPresetsText, current.HexVelocityPresets),
            GantryVelocityPresets = ParsePresets(_gantryVelocityPresetsText, current.GantryVelocityPresets),
            ShowMainCameraOnStartup = _showMainCameraOnStartup,
            AutoLoginOperator = _autoLoginOperator,
        });
        try
        {
            _store.Save();
            _host.Process.SetCameraNeedleSettings(
                _cameraNeedleOffsetX, _cameraNeedleOffsetY, _cameraNeedleApproachMm,
                _cameraNeedleTouchZAdjustMm, _cameraNeedleDwellMs, _cameraNeedleDebounceMs);
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

    /// <summary>Parses a comma/space-separated list of positive velocities; keeps the old list if none parse.</summary>
    private static double[] ParsePresets(string text, double[] fallback)
    {
        var values = new List<double>();
        foreach (var part in (text ?? "").Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
            if (double.TryParse(part, out var v) && v > 0)
                values.Add(v);
        return values.Count > 0 ? values.ToArray() : fallback;
    }
}
