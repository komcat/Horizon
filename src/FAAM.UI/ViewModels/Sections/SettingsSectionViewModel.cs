using System.Collections.ObjectModel;
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
    private string _hexStepPresetsText = "";
    private string _gantryStepPresetsText = "";
    private bool _showMainCameraOnStartup = true;
    private bool _autoLoginOperator = true;
    private string _status = "—";

    private string _machineNumber = "";
    private string _machineCredential = "";

    private string _newUserId = "";
    private string _newUserName = "";
    private AccessLevel _newUserLevel = AccessLevel.Operator;
    private User? _selectedUser;
    private AccessLevel _passwordLevel = AccessLevel.Operator;
    private string _directoryStatus = "—";

    public SettingsSectionViewModel(EngineHost host)
    {
        _store = host.Settings;
        _host = host;
        LoadFromStore();
        SaveCommand = new RelayCommand(Save);
        ReloadCommand = new RelayCommand(Reload);
        AddUserCommand = new RelayCommand(AddUser);
        DeleteUserCommand = new RelayCommand(DeleteUser);
        RefreshUsers();
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

    /// <summary>Hex-family jog step presets (µm), comma-separated. Stored as mm in AppSettings.</summary>
    public string HexStepPresetsText { get => _hexStepPresetsText; set => SetField(ref _hexStepPresetsText, value); }

    /// <summary>ACS / gantry-family jog step presets (mm), comma-separated.</summary>
    public string GantryStepPresetsText { get => _gantryStepPresetsText; set => SetField(ref _gantryStepPresetsText, value); }

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

    // --- Machine identity: stamped onto every DUT run (see Session / OperatorContext) ---

    /// <summary>
    /// This station's machine number. Blank falls back to the Windows machine name at
    /// sign-in, so a run is never stamped with nothing.
    /// </summary>
    public string MachineNumber { get => _machineNumber; set => SetField(ref _machineNumber, value); }

    /// <summary>
    /// The machine's credential string — its signature on the DUT record (calibration
    /// certificate, asset tag, qualification id — whatever the line records). Free text.
    /// </summary>
    public string MachineCredential { get => _machineCredential; set => SetField(ref _machineCredential, value); }

    // --- User directory (Admin only — this section is not offered below that level) ---

    /// <summary>Every registered employee, for the directory grid.</summary>
    public ObservableCollection<User> Users { get; } = new();

    /// <summary>The four access levels, for the level pickers.</summary>
    public static IReadOnlyList<AccessLevel> Levels { get; } = Enum.GetValues<AccessLevel>();

    /// <summary>Employee number for the row being added.</summary>
    public string NewUserId { get => _newUserId; set => SetField(ref _newUserId, value); }

    /// <summary>Display name for the row being added.</summary>
    public string NewUserName { get => _newUserName; set => SetField(ref _newUserName, value); }

    /// <summary>Level for the row being added.</summary>
    public AccessLevel NewUserLevel { get => _newUserLevel; set => SetField(ref _newUserLevel, value); }

    /// <summary>The directory row selected for deletion.</summary>
    public User? SelectedUser { get => _selectedUser; set => SetField(ref _selectedUser, value); }

    /// <summary>Which level's shared password the password box will set.</summary>
    public AccessLevel PasswordLevel
    {
        get => _passwordLevel;
        set { if (SetField(ref _passwordLevel, value)) OnPropertyChanged(nameof(PasswordLevelState)); }
    }

    /// <summary>Whether the selected level currently has a password (an unset level signs in freely).</summary>
    public string PasswordLevelState
        => _host.Users.HasLevelPassword(_passwordLevel) ? "password is set" : "NO password set — signs in freely";

    /// <summary>Outcome of the last directory / password edit.</summary>
    public string DirectoryStatus { get => _directoryStatus; private set => SetField(ref _directoryStatus, value); }

    public RelayCommand AddUserCommand { get; }
    public RelayCommand DeleteUserCommand { get; }

    private void RefreshUsers()
    {
        Users.Clear();

        // The built-in recovery Admin is not in users.json, so list it explicitly:
        // an Admin managing access needs to see that it exists rather than discover it
        // in the source. It cannot be added, removed or edited.
        Users.Add(UserStore.BuiltInAdmin);

        foreach (var u in _host.Users.AllUsers)
            Users.Add(u);
        OnPropertyChanged(nameof(PasswordLevelState));
    }

    private void AddUser()
    {
        var result = _host.Users.AddUser(new User
        {
            Id = NewUserId,
            DisplayName = NewUserName,
            Level = NewUserLevel,
        });
        if (!result.IsSuccess) { DirectoryStatus = result.Message; return; }

        _host.Users.Save();
        RefreshUsers();
        DirectoryStatus = $"Added {NewUserId} as {NewUserLevel}.";
        NewUserId = string.Empty;
        NewUserName = string.Empty;
    }

    private void DeleteUser()
    {
        if (SelectedUser == null) { DirectoryStatus = "Select an employee to remove."; return; }
        if (UserStore.IsBuiltInAdmin(SelectedUser.Id))
        {
            DirectoryStatus = "The built-in recovery Admin cannot be removed.";
            return;
        }

        var id = SelectedUser.Id;
        var result = _host.Users.RemoveUser(id);
        if (!result.IsSuccess) { DirectoryStatus = result.Message; return; }

        _host.Users.Save();
        RefreshUsers();
        SelectedUser = null;
        DirectoryStatus = $"Removed {id}. Past DUT runs keep the number they were stamped with.";
    }

    /// <summary>
    /// Sets — or, with an empty <paramref name="password"/>, clears — the shared password
    /// for <see cref="PasswordLevel"/>. Called from the view, which owns the
    /// <c>PasswordBox</c> (WPF will not bind a password, by design).
    /// </summary>
    public void ApplyLevelPassword(string password)
    {
        _host.Users.SetLevelPassword(PasswordLevel, password);
        _host.Users.Save();
        OnPropertyChanged(nameof(PasswordLevelState));
        DirectoryStatus = string.IsNullOrEmpty(password)
            ? $"{PasswordLevel} password cleared — that level now signs in on the employee number alone."
            : $"{PasswordLevel} password set.";
    }

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
        HexStepPresetsText = string.Join(", ", s.HexStepPresetsMm.Select(mm => Math.Round(mm * 1000.0, 4)));   // mm → µm for display
        GantryStepPresetsText = string.Join(", ", s.GantryStepPresetsMm);                                       // already mm
        ShowMainCameraOnStartup = s.ShowMainCameraOnStartup;
        AutoLoginOperator = s.AutoLoginOperator;
        MachineNumber = s.StationId;
        MachineCredential = s.MachineCredential;

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
            HexStepPresetsMm = ParseSteps(_hexStepPresetsText, 0.001, current.HexStepPresetsMm),   // µm entry → mm
            GantryStepPresetsMm = ParseSteps(_gantryStepPresetsText, 1.0, current.GantryStepPresetsMm), // mm entry
            ShowMainCameraOnStartup = _showMainCameraOnStartup,
            AutoLoginOperator = _autoLoginOperator,
            StationId = (_machineNumber ?? "").Trim(),
            MachineCredential = (_machineCredential ?? "").Trim(),
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
        _host.Users.Reload();
        LoadFromStore();
        RefreshUsers();
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

    /// <summary>
    /// Parses a comma/space-separated list of positive step sizes, multiplying each by
    /// <paramref name="unitToMm"/> (1.0 if the entries are mm, 0.001 if they are µm) so the
    /// result is always mm. Sorted ascending; keeps the old list if none parse.
    /// </summary>
    private static double[] ParseSteps(string text, double unitToMm, double[] fallback)
    {
        var values = new List<double>();
        foreach (var part in (text ?? "").Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
            if (double.TryParse(part, out var v) && v > 0)
                values.Add(v * unitToMm);
        values.Sort();
        return values.Count > 0 ? values.ToArray() : fallback;
    }
}
