using System.Collections.ObjectModel;
using System.Windows;
using Horizon.Engine;
using Horizon.Station.UI.Process;
using Horizon.Station.UI.Views;
using Horizon.Workflow;

namespace Horizon.Station.UI.ViewModels.Sections;

/// <summary>
/// The Machine page's Motion section — a device picker, live per-axis position,
/// jog controls with a shared step, a device-velocity setter, and a prominent
/// Stop. Refreshed from the engine's cached <c>MotionDeviceState</c> by the
/// shared sampler each tick. ACS devices (gantry-main) also get per-axis
/// enable/disable, Run/Stop of program buffer <see cref="ProgramBuffer"/>, and a
/// one-click absolute move of Z to <see cref="ZTarget"/>.
/// </summary>
public sealed class MotionSectionViewModel : ObservableObject
{
    /// <summary>ACSPL+ buffer driven by the Run / Stop buffer buttons.</summary>
    public const int ProgramBuffer = 1;

    /// <summary>Label in <see cref="ProgramBuffer"/> the Run button starts from.</summary>
    public const string ProgramLabel = "STARTUP";

    /// <summary>Terminal line sent by Run — same as typing it in the SPiiPlus terminal.</summary>
    public static readonly string RunBufferCommandText = $"START {ProgramBuffer}, {ProgramLabel}";

    /// <summary>Terminal line sent by Stop buffer.</summary>
    public static readonly string StopBufferCommandText = $"STOP {ProgramBuffer}";

    /// <summary>Absolute Z target (mm) of the Move Z button.</summary>
    public const double ZTarget = 25.0;

    // Buffer state is not in the cached device state — read it on demand at most this often.
    private const long BufferPollMs = 500;

    private readonly EngineHost _host;
    private string? _selectedDevice;
    private bool _connected;
    private bool _isMoving;
    private double _jogStep = 1.0;
    private double _velocity = 10.0;
    private string _status = "—";
    private string _newPositionName = string.Empty;
    private bool _isAcs;
    private bool? _bufferRunning;
    private bool _bufferPollInFlight;
    private long _lastBufferPoll;
    private bool _isPi;
    private bool? _isReferenced;
    private bool _referencing;
    private CancellationTokenSource? _referenceCts;
    private string? _shownProduct;
    private NamedPositionViewModel? _selectedPosition;
    private bool _canMove;
    private int _selectedTab;
    private string _profileStatus = "—";
    private bool _profileReading;
    private string? _profileReadFor;   // device whose profile was last read (auto-read once per device / connect)
    private double _accRatio = MotionProfileRatios.Default.AccPerVelocity;
    private double _jerkRatio = MotionProfileRatios.Default.JerkPerAcc;
    private double _kdecRatio = MotionProfileRatios.Default.KdecPerAcc;
    private bool _ratiosSaving;

    public MotionSectionViewModel(EngineHost host)
    {
        _host = host;
        Devices = host.Engine.Motion.DeviceNames;
        _selectedDevice = Devices.Count > 0 ? Devices[0] : null;
        Map = new PositionMapViewModel(MapMoveAsync, UpdatePositionAsync);
        Map.MembershipChanged += name =>
            Positions.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))?.NotifyOnMapChanged();
        StopCommand = new AsyncRelayCommand(StopAsync, () => _selectedDevice != null);
        ApplyVelocityCommand = new AsyncRelayCommand(ApplyVelocityAsync, () => _selectedDevice != null);
        SaveNewPositionCommand = new AsyncRelayCommand(SaveNewPositionAsync, () => _selectedDevice != null);
        AddPositionCommand = new RelayCommand(AddPosition, () => _selectedDevice != null);
        MoveSelectedPositionCommand = new AsyncRelayCommand(MoveSelectedPositionAsync, () => _selectedPosition != null && _canMove);
        UpdateSelectedPositionCommand = new AsyncRelayCommand(UpdateSelectedPositionAsync, () => _selectedPosition != null && _canMove);
        EditSelectedPositionCommand = new RelayCommand(() => _selectedPosition?.Edit(), () => _selectedPosition != null);
        EnableAllCommand = new AsyncRelayCommand(() => SetAllEnabledAsync(true), () => _isAcs && _connected);
        DisableAllCommand = new AsyncRelayCommand(() => SetAllEnabledAsync(false), () => _isAcs && _connected);
        RunBufferCommand = new AsyncRelayCommand(RunBufferAsync, () => _isAcs && _connected && _bufferRunning != true);
        // Safety stop: available whenever connected, whatever the last-read buffer state.
        StopBufferCommand = new AsyncRelayCommand(StopBufferAsync, () => _isAcs && _connected);
        MoveZCommand = new AsyncRelayCommand(MoveZAsync, () => _isAcs && _connected && HasZ);
        ReferenceCommand = new AsyncRelayCommand(ReferenceAsync, () => _isPi && _connected && !_referencing);
        RefreshProfileCommand = new AsyncRelayCommand(ReadProfileAsync, () => _isAcs && _connected && !_profileReading);
        SaveRatiosCommand = new AsyncRelayCommand(SaveRatiosAsync, () => _isAcs && !_ratiosSaving);
        DefaultRatiosCommand = new RelayCommand(DefaultRatios, () => _isAcs && !_ratiosSaving);
        RebuildAxes();
    }

    /// <summary>Adaptive profile: ACC = DEC = this × VEL (edited; saved with <see cref="SaveRatiosCommand"/>).</summary>
    public double AccRatio { get => _accRatio; set => SetField(ref _accRatio, value); }

    /// <summary>Adaptive profile: JERK = this × ACC.</summary>
    public double JerkRatio { get => _jerkRatio; set => SetField(ref _jerkRatio, value); }

    /// <summary>Adaptive profile: KDEC = this × ACC.</summary>
    public double KdecRatio { get => _kdecRatio; set => SetField(ref _kdecRatio, value); }

    /// <summary>Saves the ratios (acs_profile_ratios.json) and re-applies them at the current velocity.</summary>
    public AsyncRelayCommand SaveRatiosCommand { get; }

    /// <summary>Fills the editor with the built-in ratios (10 / 10 / 2) — not saved until Save.</summary>
    public RelayCommand DefaultRatiosCommand { get; }

    /// <summary>Selected tab under the axis strip: 0 = Positions, 1 = ACS profile (ACS devices only).</summary>
    public int SelectedTab { get => _selectedTab; set => SetField(ref _selectedTab, value); }

    /// <summary>The selected ACS device's per-axis motion profile (VEL / ACC / DEC / JERK / KDEC), view only.</summary>
    public ObservableCollection<AxisMotionProfile> AcsProfile { get; } = new();

    /// <summary>When / whether the profile was last read.</summary>
    public string ProfileStatus { get => _profileStatus; private set => SetField(ref _profileStatus, value); }

    /// <summary>Re-reads the ACS motion profile of every axis from the controller.</summary>
    public AsyncRelayCommand RefreshProfileCommand { get; }

    /// <summary>Label shown in the Machine-page section rail.</summary>
    public string Title => "Motion";

    public IReadOnlyList<string> Devices { get; }

    /// <summary>One entry per installed axis of the selected device.</summary>
    public ObservableCollection<MotionAxisViewModel> Axes { get; } = new();

    /// <summary>The selected device's named positions (active product) — each recallable via its Move command.</summary>
    public ObservableCollection<NamedPositionViewModel> Positions { get; } = new();

    /// <summary>The position map canvas beside the list — the selected device's map in the active product.</summary>
    public PositionMapViewModel Map { get; }

    /// <summary>The grid's selected row — the target of the Move / Update / Edit toolbar buttons.</summary>
    public NamedPositionViewModel? SelectedPosition
    {
        get => _selectedPosition;
        set
        {
            if (!SetField(ref _selectedPosition, value)) return;
            RaisePositionCommands();
        }
    }

    /// <summary>The selected device's installed axes — one grid column each (raised on device change).</summary>
    public IReadOnlyList<string> AxisNames => Axes.Select(a => a.Name).ToList();

    public string? SelectedDevice
    {
        get => _selectedDevice;
        set { if (SetField(ref _selectedDevice, value)) RebuildAxes(); }
    }

    public bool Connected
    {
        get => _connected;
        private set
        {
            if (!SetField(ref _connected, value)) return;
            RaiseAcsCommands();
        }
    }
    public bool IsMoving { get => _isMoving; private set => SetField(ref _isMoving, value); }

    /// <summary>Relative distance applied by an axis jog −/+.</summary>
    public double JogStep
    {
        get => _jogStep;
        set => SetField(ref _jogStep, value);
    }

    /// <summary>Velocity (controller units) applied by <see cref="ApplyVelocityCommand"/>.</summary>
    public double Velocity
    {
        get => _velocity;
        set => SetField(ref _velocity, value);
    }

    /// <summary>Last command result, shown next to the controls.</summary>
    public string Status { get => _status; private set => SetField(ref _status, value); }

    /// <summary>Name to save the device's current pose under (new or existing position).</summary>
    public string NewPositionName
    {
        get => _newPositionName;
        set => SetField(ref _newPositionName, value);
    }

    /// <summary>True for ACS controllers — shows the enable / buffer / Move Z row.</summary>
    public bool IsAcs
    {
        get => _isAcs;
        private set
        {
            if (!SetField(ref _isAcs, value)) return;
            RaiseAcsCommands();
        }
    }

    /// <summary>Whether the selected device has an axis named Z (for the Move Z button).</summary>
    public bool HasZ => Axes.Any(a => a.Name.Equals("Z", StringComparison.OrdinalIgnoreCase));

    /// <summary>Last read state of <see cref="ProgramBuffer"/>; null = unknown.</summary>
    public bool? BufferRunning
    {
        get => _bufferRunning;
        private set
        {
            if (!SetField(ref _bufferRunning, value)) return;
            OnPropertyChanged(nameof(BufferStateText));
            RunBufferCommand.RaiseCanExecuteChanged();
        }
    }

    public string BufferStateText => _bufferRunning switch
    {
        true => "Running",
        false => "Stopped",
        null => "—",
    };

    public string RunBufferText => $"Run buffer {ProgramBuffer}";

    public string StopBufferText => $"Stop buffer {ProgramBuffer}";

    public string RunBufferTip => $"Sends \"{RunBufferCommandText}\" to the controller terminal";

    public string StopBufferTip => $"Safety stop: sends \"{StopBufferCommandText}\", then stops all motion on this device";

    public string BufferLabel => $"Buffer {ProgramBuffer}";

    public string MoveZText => $"Move Z → {ZTarget:0.###} mm";

    /// <summary>True for PI hexapods — shows the Reference row.</summary>
    public bool IsPi
    {
        get => _isPi;
        private set
        {
            if (!SetField(ref _isPi, value)) return;
            ReferenceCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Polled "every axis referenced" state; null = unknown / not reported.</summary>
    public bool? IsReferenced
    {
        get => _isReferenced;
        private set
        {
            if (!SetField(ref _isReferenced, value)) return;
            OnPropertyChanged(nameof(ReferenceStateText));
        }
    }

    /// <summary>True while a reference move started here is in progress.</summary>
    public bool Referencing
    {
        get => _referencing;
        private set
        {
            if (!SetField(ref _referencing, value)) return;
            OnPropertyChanged(nameof(ReferenceStateText));
            ReferenceCommand.RaiseCanExecuteChanged();
        }
    }

    public string ReferenceStateText => _referencing
        ? "Referencing…"
        : _isReferenced switch
        {
            true => "Referenced",
            false => "Not referenced",
            null => "—",
        };

    public AsyncRelayCommand ReferenceCommand { get; }

    public AsyncRelayCommand StopCommand { get; }
    public AsyncRelayCommand EnableAllCommand { get; }
    public AsyncRelayCommand DisableAllCommand { get; }
    public AsyncRelayCommand RunBufferCommand { get; }
    public AsyncRelayCommand StopBufferCommand { get; }
    public AsyncRelayCommand MoveZCommand { get; }
    public AsyncRelayCommand ApplyVelocityCommand { get; }
    public AsyncRelayCommand SaveNewPositionCommand { get; }
    public RelayCommand AddPositionCommand { get; }

    /// <summary>Moves the device to the selected row's position (section velocity).</summary>
    public AsyncRelayCommand MoveSelectedPositionCommand { get; }

    /// <summary>Re-teaches the selected row's position from the device's current pose (asks first).</summary>
    public AsyncRelayCommand UpdateSelectedPositionCommand { get; }

    /// <summary>Opens the editor for the selected row's position.</summary>
    public RelayCommand EditSelectedPositionCommand { get; }

    private void RebuildAxes()
    {
        foreach (var a in Axes) a.CommandFailed -= OnAxisCommandFailed;
        Axes.Clear();
        BufferRunning = null;
        var device = _selectedDevice;
        var info = device == null ? null : _host.Engine.Motion.GetDevice(device);
        IsAcs = info != null && info.ControllerType.Equals("ACS", StringComparison.OrdinalIgnoreCase);
        IsPi = info != null && info.ControllerType.Equals("PI", StringComparison.OrdinalIgnoreCase);
        IsReferenced = null;
        AcsProfile.Clear();
        _profileReadFor = null;
        ProfileStatus = "—";
        if (!_isAcs) SelectedTab = 0;   // the ACS profile tab is hidden for other controllers
        LoadRatios();
        if (device == null || info == null)
        {
            OnPropertyChanged(nameof(HasZ));
            OnPropertyChanged(nameof(AxisNames));
            return;
        }

        foreach (var axis in info.Axes)
        {
            var vm = new MotionAxisViewModel(_host, device, axis, () => _jogStep, showAbsoluteMove: _isPi);
            vm.CommandFailed += OnAxisCommandFailed;
            Axes.Add(vm);
        }
        OnPropertyChanged(nameof(HasZ));
        OnPropertyChanged(nameof(AxisNames));
        MoveZCommand.RaiseCanExecuteChanged();

        RebuildPositions();
    }

    /// <summary>
    /// Rebuilds the named-position grid (and the map) for the selected device from the active
    /// product, keeping the selected row (by name) when the same device / product is still shown.
    /// </summary>
    private void RebuildPositions()
    {
        var device = _selectedDevice;
        var product = _host.Process.ActiveProduct;
        var keepSelection = string.Equals(product, _shownProduct, StringComparison.OrdinalIgnoreCase)
            && Map.IsShowing(product, device) ? _selectedPosition?.Name : null;

        SelectedPosition = null;
        Positions.Clear();
        _shownProduct = product;
        var names = device == null ? Array.Empty<string>() : _host.Process.GetPositionNames(device);
        Map.Show(_shownProduct, device, names);
        if (device == null) return;

        var axes = _host.Engine.Motion.GetDevice(device)?.Axes ?? Array.Empty<string>();
        foreach (var name in names)
            Positions.Add(new NamedPositionViewModel(_host, device, name, axes, Map));
        if (keepSelection != null)
            SelectedPosition = Positions.FirstOrDefault(p => string.Equals(p.Name, keepSelection, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Refreshes connection / motion state and every axis position — called each sampler tick.</summary>
    public void Refresh()
    {
        var device = _selectedDevice;
        Connected = device != null && _host.Engine.Motion.IsConnected(device);
        var canMove = Connected && !_host.Process.IsRunning;
        Map.SetCanAct(canMove);
        if (canMove != _canMove)
        {
            _canMove = canMove;
            RaisePositionCommands();
        }
        if (device == null) return;

        var state = _host.Engine.Motion.GetState(device);
        IsMoving = state.IsMoving;

        // Pick up positions taught/edited on other pages (or a product switch) without
        // recreating the list every tick — only rebuild when the product or the set of names changes.
        if (!string.Equals(_host.Process.ActiveProduct, _shownProduct, StringComparison.OrdinalIgnoreCase)
            || !_host.Process.GetPositionNames(device).SequenceEqual(Positions.Select(p => p.Name)))
            RebuildPositions();

        // Light the map node(s) the device is standing on — from the cached pose, no extra I/O.
        if (Connected && state.Valid)
        {
            var axisNames = Axes.Select(a => a.Name).ToList();
            Map.UpdateAtPosition(name =>
                PositionMatch.Matches(_host.Process.GetPosition(device, name)?.Coordinates, state.Position, axisNames));
        }
        else
        {
            Map.ClearAtPosition();
        }

        // Axis index aligns with MotionPosition.Components (X,Y,Z,U,V,W).
        var components = state.Position.Components;
        for (int i = 0; i < Axes.Count && i < components.Count; i++)
            Axes[i].Apply(components[i]);

        // Axis index aligns with the EnabledMask bits; null mask = not reported (PI).
        var mask = Connected ? state.EnabledMask : null;
        for (int i = 0; i < Axes.Count; i++)
            Axes[i].ApplyEnabled(mask.HasValue ? (mask.Value & (1 << i)) != 0 : null);

        if (_isAcs && Connected) PollBuffer(device);
        else BufferRunning = null;

        // Read the ACS profile once per device selection / connect; Refresh re-reads it.
        if (!Connected) _profileReadFor = null;
        else if (_isAcs && _profileReadFor != device && !_profileReading) _ = ReadProfileAsync();

        IsReferenced = Connected ? state.IsReferenced : null;
    }

    /// <summary>
    /// References the hexapod (FRF, all axes) after an operator confirm, then re-checks
    /// the pivot point. STOP cancels it.
    /// </summary>
    private async Task ReferenceAsync()
    {
        var device = _selectedDevice;
        if (device == null) return;
        if (MessageBox.Show(
                $"Reference {device}? Every axis moves to its reference switch and back — " +
                "make sure the gripper and tooling are clear.",
                "Reference hexapod", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        using var cts = new CancellationTokenSource();
        _referenceCts = cts;
        Referencing = true;
        Status = $"Referencing {device}…";
        try
        {
            var r = await _host.Engine.Motion.ReferenceAsync(device, cts.Token).ConfigureAwait(true);
            if (!r.IsSuccess)
            {
                Status = r.Message;
                return;
            }
            var pivot = await _host.EnsureHexapodPivotAsync(device).ConfigureAwait(true);
            Status = pivot.IsSuccess ? $"{device} referenced. {pivot.Message}" : $"{device} referenced; {pivot.Message}";
        }
        catch (OperationCanceledException)
        {
            Status = $"Reference {device} — cancelled.";
        }
        finally
        {
            _referenceCts = null;
            Referencing = false;
        }
    }

    /// <summary>
    /// Reads the buffer state off the UI thread (the facet call is async), at most
    /// every <see cref="BufferPollMs"/> and never overlapping a previous read.
    /// </summary>
    private async void PollBuffer(string device)
    {
        var now = Environment.TickCount64;
        if (_bufferPollInFlight || now - _lastBufferPoll < BufferPollMs) return;
        _bufferPollInFlight = true;
        _lastBufferPoll = now;
        try
        {
            var r = await _host.Engine.Motion.IsBufferRunningAsync(device, ProgramBuffer).ConfigureAwait(true);
            // Ignore a late answer for a device the operator has since switched away from.
            if (device == _selectedDevice) BufferRunning = r.IsSuccess ? r.Value : null;
        }
        catch (Exception)
        {
            BufferRunning = null;
        }
        finally
        {
            _bufferPollInFlight = false;
        }
    }

    /// <summary>Reads VEL / ACC / DEC / JERK / KDEC of every axis of the selected ACS device (read only).</summary>
    private async Task ReadProfileAsync()
    {
        var device = _selectedDevice;
        if (device == null || !_isAcs) return;
        _profileReadFor = device;
        _profileReading = true;
        RefreshProfileCommand.RaiseCanExecuteChanged();
        ProfileStatus = "Reading…";
        try
        {
            var r = await _host.Engine.Motion.GetMotionProfileAsync(device).ConfigureAwait(true);
            if (device != _selectedDevice) return;   // operator switched devices meanwhile
            AcsProfile.Clear();
            if (r.IsSuccess)
            {
                foreach (var p in r.Value) AcsProfile.Add(p);
                ProfileStatus = $"Read {DateTime.Now:HH:mm:ss}";
            }
            else
            {
                ProfileStatus = r.Message;
            }
        }
        catch (Exception ex)
        {
            ProfileStatus = $"Read failed — {ex.Message}";
        }
        finally
        {
            _profileReading = false;
            RefreshProfileCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Shows the selected device's ratios in effect (defaults for non-ACS, where the editor is hidden).</summary>
    private void LoadRatios()
    {
        var r = (_selectedDevice == null ? null : _host.Engine.Motion.GetProfileRatios(_selectedDevice))
                ?? MotionProfileRatios.Default;
        AccRatio = r.AccPerVelocity;
        JerkRatio = r.JerkPerAcc;
        KdecRatio = r.KdecPerAcc;
    }

    private void DefaultRatios()
    {
        var d = MotionProfileRatios.Default;
        AccRatio = d.AccPerVelocity;
        JerkRatio = d.JerkPerAcc;
        KdecRatio = d.KdecPerAcc;
        ProfileStatus = "Defaults filled in — press Save to apply.";
    }

    /// <summary>
    /// Validates, saves and applies the edited ratios; when connected the engine re-derives every
    /// axis's ACC/DEC/JERK/KDEC from its current VEL, then the table is re-read. Refused mid-run.
    /// </summary>
    private async Task SaveRatiosAsync()
    {
        var device = _selectedDevice;
        if (device == null || !_isAcs) return;
        if (_host.Process.IsRunning)
        {
            ProfileStatus = "A program is running — save the ratios when it has finished.";
            return;
        }
        var ratios = new MotionProfileRatios(_accRatio, _jerkRatio, _kdecRatio);
        var why = ratios.Validate();
        if (why != null)
        {
            ProfileStatus = why;
            return;
        }

        _ratiosSaving = true;
        SaveRatiosCommand.RaiseCanExecuteChanged();
        DefaultRatiosCommand.RaiseCanExecuteChanged();
        ProfileStatus = "Saving ratios…";
        try
        {
            var r = await _host.Engine.Motion.SetProfileRatiosAsync(device, ratios).ConfigureAwait(true);
            if (device != _selectedDevice) return;
            if (!r.IsSuccess)
            {
                ProfileStatus = r.Message;
                return;
            }
            if (_connected)
            {
                await ReadProfileAsync().ConfigureAwait(true);
                ProfileStatus = $"Ratios saved and applied ({ProfileStatus}).";
            }
            else
            {
                ProfileStatus = "Ratios saved — they apply from the next velocity set.";
            }
        }
        catch (Exception ex)
        {
            ProfileStatus = $"Save failed — {ex.Message}";
        }
        finally
        {
            _ratiosSaving = false;
            SaveRatiosCommand.RaiseCanExecuteChanged();
            DefaultRatiosCommand.RaiseCanExecuteChanged();
        }
    }

    private void RaiseAcsCommands()
    {
        SaveRatiosCommand.RaiseCanExecuteChanged();
        DefaultRatiosCommand.RaiseCanExecuteChanged();
        RefreshProfileCommand.RaiseCanExecuteChanged();
        ReferenceCommand.RaiseCanExecuteChanged();
        EnableAllCommand.RaiseCanExecuteChanged();
        DisableAllCommand.RaiseCanExecuteChanged();
        RunBufferCommand.RaiseCanExecuteChanged();
        StopBufferCommand.RaiseCanExecuteChanged();
        MoveZCommand.RaiseCanExecuteChanged();
    }

    private void OnAxisCommandFailed(string message) => Status = message;

    private async Task SetAllEnabledAsync(bool enabled)
    {
        var device = _selectedDevice;
        if (device == null) return;
        if (!enabled && MessageBox.Show(
                $"Disable ALL axes of {device}? The servos release — an unbraked axis may drift or drop.",
                "Disable axes", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        foreach (var axis in Axes.Select(a => a.Name).ToList())
        {
            var r = await _host.Engine.Motion.SetAxisEnabledAsync(device, axis, enabled).ConfigureAwait(true);
            if (!r.IsSuccess)
            {
                Status = r.Message;
                return;
            }
        }
        Status = enabled ? "All axes enabled" : "All axes disabled";
    }

    private async Task RunBufferAsync()
    {
        var device = _selectedDevice;
        if (device == null) return;

        var r = await _host.Engine.Motion.SendTerminalCommandAsync(device, RunBufferCommandText).ConfigureAwait(true);
        Status = r.IsSuccess ? $"Sent \"{RunBufferCommandText}\"" : r.Message;
        _lastBufferPoll = 0; // re-read the state on the next tick
    }

    /// <summary>
    /// Safety stop: halts the buffer program (<c>STOP 1</c>), then kills the device's motion
    /// so a move the program already commanded doesn't run on. Motion is stopped even if the
    /// STOP command itself fails.
    /// </summary>
    private async Task StopBufferAsync()
    {
        var device = _selectedDevice;
        if (device == null) return;

        var stop = await _host.Engine.Motion.SendTerminalCommandAsync(device, StopBufferCommandText).ConfigureAwait(true);
        var kill = await _host.Engine.Motion.StopAsync(device).ConfigureAwait(true);
        Status = (stop.IsSuccess, kill.IsSuccess) switch
        {
            (true, true) => $"Sent \"{StopBufferCommandText}\"; motion stopped",
            (false, true) => $"{stop.Message} — motion stopped",
            (true, false) => $"Sent \"{StopBufferCommandText}\"; {kill.Message}",
            _ => $"{stop.Message}; {kill.Message}",
        };
        _lastBufferPoll = 0;
    }

    private async Task MoveZAsync()
    {
        var device = _selectedDevice;
        if (device == null) return;
        var axis = Axes.FirstOrDefault(a => a.Name.Equals("Z", StringComparison.OrdinalIgnoreCase))?.Name;
        if (axis == null) return;

        Status = $"Moving Z → {ZTarget:0.###} mm…";
        var r = await _host.Engine.Motion.MoveAbsoluteAsync(device, axis, ZTarget).ConfigureAwait(true);
        Status = r.IsSuccess ? $"Z at {ZTarget:0.###} mm" : r.Message;
    }

    private async Task StopAsync()
    {
        var device = _selectedDevice;
        if (device == null) return;
        _referenceCts?.Cancel();   // ends a reference wait started here (the engine also stops the device)
        PositionMapViewModel.CancelAllMoves();   // no map path (here or on the jog rail) starts a further leg
        var result = await _host.Engine.Motion.StopAsync(device).ConfigureAwait(true);
        Status = result.IsSuccess ? "Stopped" : result.Message;
    }

    private async Task ApplyVelocityAsync()
    {
        var device = _selectedDevice;
        if (device == null) return;
        var result = await _host.Engine.Motion.SetDeviceVelocityAsync(device, _velocity).ConfigureAwait(true);
        Status = result.IsSuccess ? $"Velocity set to {_velocity:G}" : result.Message;
    }

    /// <summary>Captures the device's current pose under <see cref="NewPositionName"/> (new or overwriting), then refreshes the list.</summary>
    private async Task SaveNewPositionAsync()
    {
        var device = _selectedDevice;
        if (device == null) return;

        var name = _newPositionName.Trim();
        if (string.IsNullOrEmpty(name))
        {
            Status = "Enter a position name to save.";
            return;
        }

        // Preserve velocity if the name already exists (teach only re-captures the pose).
        var velocity = _host.Process.GetPosition(device, name)?.Velocity ?? 0;
        var result = await _host.Process.TeachPositionAsync(device, name, velocity).ConfigureAwait(true);
        Status = result.IsSuccess ? $"Saved '{name}'" : result.Message;
        if (result.IsSuccess)
        {
            NewPositionName = string.Empty;
            RebuildPositions();
        }
    }

    /// <summary>
    /// Adds a position by manual coordinate entry — opens the position editor for
    /// <see cref="NewPositionName"/> (seeded with the device's current pose, or the
    /// existing values if the name is already taught) so the operator can type
    /// X/Y/Z/U/V/W, velocity, and camera/exposure without jogging the stage there.
    /// </summary>
    private void AddPosition()
    {
        var device = _selectedDevice;
        if (device == null) return;

        var name = _newPositionName.Trim();
        if (string.IsNullOrEmpty(name))
        {
            Status = "Enter a position name to add.";
            return;
        }

        var info = _host.Engine.Motion.GetDevice(device);
        if (info == null) return;

        // Seed from the existing position if the name is taken, else the live pose.
        var existing = _host.Process.GetPosition(device, name);
        var pose = existing?.Coordinates ?? _host.Engine.Motion.GetState(device).Position;

        var ctx = new PositionEditContext
        {
            Position = name,
            Device = info.Name,
            ControllerType = info.ControllerType,
            Axes = info.Axes,
            Velocity = existing?.Velocity ?? 0,
            Pose = pose,
            Cameras = _host.Engine.Camera.CameraIds,
            Camera = existing?.Camera,
            ExposureUs = existing?.ExposureUs,
        };

        new PositionEditorWindow(ctx, ApplyAddAsync)
        {
            Owner = Application.Current?.MainWindow,
        }.Show();
    }

    private void RaisePositionCommands()
    {
        MoveSelectedPositionCommand.RaiseCanExecuteChanged();
        UpdateSelectedPositionCommand.RaiseCanExecuteChanged();
        EditSelectedPositionCommand.RaiseCanExecuteChanged();
    }

    private async Task MoveSelectedPositionAsync()
    {
        if (_selectedPosition is not { } row) return;
        Status = $"Moving to {row.Name}…";
        Status = await MoveToPositionAsync(row.Name).ConfigureAwait(true);
    }

    private async Task UpdateSelectedPositionAsync()
    {
        if (_selectedPosition is not { } row) return;
        Status = await UpdatePositionAsync(row.Name).ConfigureAwait(true);
    }

    /// <summary>One map Move leg on <paramref name="device"/> at the section velocity, then a check that it really arrived.</summary>
    private async Task<MapMoveResult> MapMoveAsync(string device, string name)
    {
        var r = await _host.Process.MoveToNamedPositionAsync(device, name, _velocity).ConfigureAwait(true);
        if (!r.IsSuccess) return new MapMoveResult(false, r.Message);
        return await PositionMapViewModel.ConfirmArrivalAsync(_host, device, name).ConfigureAwait(true);
    }

    /// <summary>Recalls the position at the section velocity (Apply box) rather than its taught process velocity — grid toolbar.</summary>
    private async Task<string> MoveToPositionAsync(string name)
    {
        var device = _selectedDevice;
        if (device == null) return "No device selected.";
        var r = await _host.Process.MoveToNamedPositionAsync(device, name, _velocity).ConfigureAwait(true);
        return r.IsSuccess ? $"{device} at {name}." : r.Message;
    }

    /// <summary>
    /// Grid toolbar / map Update — after a confirm showing stored vs current values, re-teaches
    /// the position from the device's current pose (keeping its velocity / camera).
    /// </summary>
    private async Task<string> UpdatePositionAsync(string name)
    {
        var device = _selectedDevice;
        if (device == null) return "No device selected.";

        var row = Positions.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        var current = string.Join("  ", Axes.Select(a => $"{a.Name} {a.Position}"));
        if (MessageBox.Show(
                $"Overwrite '{name}' on {device} with the current axis position?\n\n" +
                $"Stored:   {row?.Coordinates}\nCurrent:  {current}",
                "Update position", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return $"Update of '{name}' cancelled.";

        var velocity = _host.Process.GetPosition(device, name)?.Velocity ?? 0;
        var r = await _host.Process.TeachPositionAsync(device, name, velocity).ConfigureAwait(true);
        row?.RefreshCoordinates();
        Status = r.IsSuccess ? $"Updated '{name}'" : r.Message;
        return r.IsSuccess ? $"Updated '{name}' to the current axis position." : r.Message;
    }

    private Task ApplyAddAsync(PositionEditResult r)
    {
        var saved = _host.Process.SetPositionFull(
            r.Device, r.Position, r.Pose, r.Velocity, r.Camera, r.ExposureUs);
        Status = saved.IsSuccess ? $"Saved '{r.Position}'." : saved.Message;
        if (saved.IsSuccess)
        {
            NewPositionName = string.Empty;
            RebuildPositions();
        }
        return Task.CompletedTask;
    }
}
