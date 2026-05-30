using System.Collections.ObjectModel;
using FAAM.UI.Mvvm;

namespace FAAM.UI.ViewModels.Sections;

/// <summary>
/// The Machine page's IO section — a device picker, a live read-only list of
/// input pins, and a list of output pins each with a toggle. Refreshed from the
/// engine's cached <c>IoState</c> by the shared sampler each tick.
/// </summary>
public sealed class IoSectionViewModel : ObservableObject
{
    private readonly EngineHost _host;
    private string? _selectedDevice;
    private bool _connected;

    public IoSectionViewModel(EngineHost host)
    {
        _host = host;
        Devices = host.Engine.IO.DeviceNames;
        _selectedDevice = Devices.Count > 0 ? Devices[0] : null;
        RebuildPins();
    }

    /// <summary>Label shown in the Machine-page section rail.</summary>
    public string Title => "IO";

    public IReadOnlyList<string> Devices { get; }

    /// <summary>Read-only input pins of the selected device.</summary>
    public ObservableCollection<IoPinViewModel> Inputs { get; } = new();

    /// <summary>Output pins of the selected device, each with a toggle.</summary>
    public ObservableCollection<IoPinViewModel> Outputs { get; } = new();

    public string? SelectedDevice
    {
        get => _selectedDevice;
        set { if (SetField(ref _selectedDevice, value)) RebuildPins(); }
    }

    public bool Connected
    {
        get => _connected;
        private set => SetField(ref _connected, value);
    }

    private void RebuildPins()
    {
        Inputs.Clear();
        Outputs.Clear();
        var device = _selectedDevice;
        if (device == null) return;

        var info = _host.Engine.IO.GetDevice(device);
        if (info == null) return;

        foreach (var pin in info.InputPins)
            Inputs.Add(new IoPinViewModel(pin.Pin, pin.Name, null));
        foreach (var pin in info.OutputPins)
            Outputs.Add(new IoPinViewModel(pin.Pin, pin.Name,
                state => _host.Engine.IO.SetOutputAsync(device, pin.Pin, state)));
    }

    /// <summary>Refreshes connection state and every pin — called each sampler tick.</summary>
    public void Refresh()
    {
        var device = _selectedDevice;
        Connected = device != null && _host.Engine.IO.IsConnected(device);
        if (device == null) return;

        var state = _host.Engine.IO.GetState(device);
        foreach (var pin in Inputs)
            pin.Apply((state.Inputs & (1u << pin.Pin)) != 0);
        foreach (var pin in Outputs)
            pin.Apply((state.Outputs & (1u << pin.Pin)) != 0);
    }
}
