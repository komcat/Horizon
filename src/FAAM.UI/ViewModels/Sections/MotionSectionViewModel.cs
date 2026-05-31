using System.Collections.ObjectModel;
using System.Windows;
using FAAM.UI.Mvvm;
using FAAM.UI.Process;
using FAAM.UI.Views;

namespace FAAM.UI.ViewModels.Sections;

/// <summary>
/// The Machine page's Motion section — a device picker, live per-axis position,
/// jog controls with a shared step, a device-velocity setter, and a prominent
/// Stop. Refreshed from the engine's cached <c>MotionDeviceState</c> by the
/// shared sampler each tick.
/// </summary>
public sealed class MotionSectionViewModel : ObservableObject
{
    private readonly EngineHost _host;
    private string? _selectedDevice;
    private bool _connected;
    private bool _isMoving;
    private double _jogStep = 1.0;
    private double _velocity = 10.0;
    private string _status = "—";
    private string _newPositionName = string.Empty;

    public MotionSectionViewModel(EngineHost host)
    {
        _host = host;
        Devices = host.Engine.Motion.DeviceNames;
        _selectedDevice = Devices.Count > 0 ? Devices[0] : null;
        StopCommand = new AsyncRelayCommand(StopAsync, () => _selectedDevice != null);
        ApplyVelocityCommand = new AsyncRelayCommand(ApplyVelocityAsync, () => _selectedDevice != null);
        SaveNewPositionCommand = new AsyncRelayCommand(SaveNewPositionAsync, () => _selectedDevice != null);
        AddPositionCommand = new RelayCommand(AddPosition, () => _selectedDevice != null);
        RebuildAxes();
    }

    /// <summary>Label shown in the Machine-page section rail.</summary>
    public string Title => "Motion";

    public IReadOnlyList<string> Devices { get; }

    /// <summary>One entry per installed axis of the selected device.</summary>
    public ObservableCollection<MotionAxisViewModel> Axes { get; } = new();

    /// <summary>The selected device's named positions (active product) — each recallable via its Move command.</summary>
    public ObservableCollection<NamedPositionViewModel> Positions { get; } = new();

    public string? SelectedDevice
    {
        get => _selectedDevice;
        set { if (SetField(ref _selectedDevice, value)) RebuildAxes(); }
    }

    public bool Connected { get => _connected; private set => SetField(ref _connected, value); }
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

    public AsyncRelayCommand StopCommand { get; }
    public AsyncRelayCommand ApplyVelocityCommand { get; }
    public AsyncRelayCommand SaveNewPositionCommand { get; }
    public RelayCommand AddPositionCommand { get; }

    private void RebuildAxes()
    {
        Axes.Clear();
        var device = _selectedDevice;
        if (device == null) return;

        var info = _host.Engine.Motion.GetDevice(device);
        if (info == null) return;

        foreach (var axis in info.Axes)
            Axes.Add(new MotionAxisViewModel(_host, device, axis, () => _jogStep));

        RebuildPositions();
    }

    /// <summary>Rebuilds the named-position list for the selected device from the active product.</summary>
    private void RebuildPositions()
    {
        Positions.Clear();
        var device = _selectedDevice;
        if (device == null) return;

        var axes = _host.Engine.Motion.GetDevice(device)?.Axes ?? Array.Empty<string>();
        foreach (var name in _host.Process.GetPositionNames(device))
            Positions.Add(new NamedPositionViewModel(_host, device, name, axes));
    }

    /// <summary>Refreshes connection / motion state and every axis position — called each sampler tick.</summary>
    public void Refresh()
    {
        var device = _selectedDevice;
        Connected = device != null && _host.Engine.Motion.IsConnected(device);
        if (device == null) return;

        var state = _host.Engine.Motion.GetState(device);
        IsMoving = state.IsMoving;

        // Pick up positions taught/edited on other pages without recreating the
        // list every tick — only rebuild when the set of names actually changes.
        if (!_host.Process.GetPositionNames(device).SequenceEqual(Positions.Select(p => p.Name)))
            RebuildPositions();

        // Axis index aligns with MotionPosition.Components (X,Y,Z,U,V,W).
        var components = state.Position.Components;
        for (int i = 0; i < Axes.Count && i < components.Count; i++)
            Axes[i].Apply(components[i]);
    }

    private async Task StopAsync()
    {
        var device = _selectedDevice;
        if (device == null) return;
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
