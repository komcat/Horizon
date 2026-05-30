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

    private double _mmPerPixel;
    private string _moveClickDevice = "";
    private string _moveClickXAxis = "X";
    private string _moveClickYAxis = "Y";
    private double _moveClickXSign = 1.0;
    private double _moveClickYSign = 1.0;
    private string _hexVelocityPresetsText = "";
    private string _gantryVelocityPresetsText = "";
    private string _status = "—";

    public SettingsSectionViewModel(EngineHost host)
    {
        _store = host.Settings;
        LoadFromStore();
        SaveCommand = new RelayCommand(Save);
        ReloadCommand = new RelayCommand(Reload);
    }

    /// <summary>Label shown in the Machine-page section rail.</summary>
    public string Title => "Settings";

    /// <summary>Camera scale — real-world millimetres per image pixel.</summary>
    public double MmPerPixel { get => _mmPerPixel; set => SetField(ref _mmPerPixel, value); }

    public string MoveClickDevice { get => _moveClickDevice; set => SetField(ref _moveClickDevice, value); }
    public string MoveClickXAxis { get => _moveClickXAxis; set => SetField(ref _moveClickXAxis, value); }
    public string MoveClickYAxis { get => _moveClickYAxis; set => SetField(ref _moveClickYAxis, value); }
    public double MoveClickXSign { get => _moveClickXSign; set => SetField(ref _moveClickXSign, value); }
    public double MoveClickYSign { get => _moveClickYSign; set => SetField(ref _moveClickYSign, value); }

    /// <summary>Hex-family jog velocity presets (mm/s), comma-separated.</summary>
    public string HexVelocityPresetsText { get => _hexVelocityPresetsText; set => SetField(ref _hexVelocityPresetsText, value); }

    /// <summary>ACS / gantry-family jog velocity presets (mm/s), comma-separated.</summary>
    public string GantryVelocityPresetsText { get => _gantryVelocityPresetsText; set => SetField(ref _gantryVelocityPresetsText, value); }

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
    }

    private void Save()
    {
        var current = _store.Current;
        _store.Update(new AppSettings
        {
            MmPerPixel = _mmPerPixel,
            MoveClickDevice = _moveClickDevice ?? "",
            MoveClickXAxis = string.IsNullOrWhiteSpace(_moveClickXAxis) ? "X" : _moveClickXAxis,
            MoveClickYAxis = string.IsNullOrWhiteSpace(_moveClickYAxis) ? "Y" : _moveClickYAxis,
            MoveClickXSign = _moveClickXSign,
            MoveClickYSign = _moveClickYSign,
            HexVelocityPresets = ParsePresets(_hexVelocityPresetsText, current.HexVelocityPresets),
            GantryVelocityPresets = ParsePresets(_gantryVelocityPresetsText, current.GantryVelocityPresets),
        });
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
