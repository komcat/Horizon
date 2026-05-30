using System.Collections.ObjectModel;
using FAAM.UI.Mvvm;

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

    public MotionSectionViewModel(EngineHost host)
    {
        _host = host;
        Devices = host.Engine.Motion.DeviceNames;
        _selectedDevice = Devices.Count > 0 ? Devices[0] : null;
        StopCommand = new AsyncRelayCommand(StopAsync, () => _selectedDevice != null);
        ApplyVelocityCommand = new AsyncRelayCommand(ApplyVelocityAsync, () => _selectedDevice != null);
        RebuildAxes();
    }

    /// <summary>Label shown in the Machine-page section rail.</summary>
    public string Title => "Motion";

    public IReadOnlyList<string> Devices { get; }

    /// <summary>One entry per installed axis of the selected device.</summary>
    public ObservableCollection<MotionAxisViewModel> Axes { get; } = new();

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

    public AsyncRelayCommand StopCommand { get; }
    public AsyncRelayCommand ApplyVelocityCommand { get; }

    private void RebuildAxes()
    {
        Axes.Clear();
        var device = _selectedDevice;
        if (device == null) return;

        var info = _host.Engine.Motion.GetDevice(device);
        if (info == null) return;

        foreach (var axis in info.Axes)
            Axes.Add(new MotionAxisViewModel(_host, device, axis, () => _jogStep));
    }

    /// <summary>Refreshes connection / motion state and every axis position — called each sampler tick.</summary>
    public void Refresh()
    {
        var device = _selectedDevice;
        Connected = device != null && _host.Engine.Motion.IsConnected(device);
        if (device == null) return;

        var state = _host.Engine.Motion.GetState(device);
        IsMoving = state.IsMoving;

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
}
