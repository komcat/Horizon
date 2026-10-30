using System.Collections.ObjectModel;
using Horizon.Common;

namespace Horizon.Station.UI.ViewModels.Sections;

/// <summary>
/// The Machine page's Power-supply section — a device picker plus one
/// <see cref="PowerSupplyChannelViewModel"/> per channel of the selected
/// device (UDP3305S has 3, Keysight E36103B 1, E36441A 4). Each channel has
/// its own V/I setpoints and output toggle.
/// </summary>
public sealed class PowerSupplySectionViewModel : ObservableObject
{
    private readonly EngineHost _host;
    private string? _selectedDevice;
    private bool _connected;
    private string _status = "—";
    private bool _refreshInFlight;

    public PowerSupplySectionViewModel(EngineHost host)
    {
        _host = host;
        Devices = host.Engine.PowerSupply.DeviceNames;
        _selectedDevice = Devices.Count > 0 ? Devices[0] : null;
        RebuildChannels();

        ConnectCommand = new AsyncRelayCommand(
            () => RunAsync(d => _host.Engine.PowerSupply.ConnectAsync(d), "Connect"), HasDevice);
        DisconnectCommand = new AsyncRelayCommand(
            () => RunAsync(_host.Engine.PowerSupply.DisconnectAsync, "Disconnect"), HasDevice);
    }

    /// <summary>Label shown in the Machine-page section rail.</summary>
    public string Title => "Power supply";

    public IReadOnlyList<string> Devices { get; }

    /// <summary>One entry per channel of the selected device.</summary>
    public ObservableCollection<PowerSupplyChannelViewModel> Channels { get; } = new();

    public string? SelectedDevice
    {
        get => _selectedDevice;
        set { if (SetField(ref _selectedDevice, value)) RebuildChannels(); }
    }

    public bool Connected { get => _connected; private set => SetField(ref _connected, value); }

    public string Status { get => _status; private set => SetField(ref _status, value); }

    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand DisconnectCommand { get; }

    /// <summary>Adapter type of the selected device (e.g. <c>unit_t_udp3305s</c>).</summary>
    public string DeviceType => _selectedDevice == null
        ? "—"
        : _host.Engine.PowerSupply.GetDevice(_selectedDevice)?.ControllerType ?? "—";

    private void RebuildChannels()
    {
        foreach (var old in Channels)
            old.PropertyChanged -= OnChannelPropertyChanged;
        Channels.Clear();
        OnPropertyChanged(nameof(DeviceType));
        var device = _selectedDevice;
        if (device == null) return;

        var count = _host.Engine.PowerSupply.GetDevice(device)?.ChannelCount ?? 1;
        for (var ch = 1; ch <= count; ch++)
        {
            var vm = new PowerSupplyChannelViewModel(_host, () => _selectedDevice, ch);
            vm.PropertyChanged += OnChannelPropertyChanged;
            Channels.Add(vm);
        }
    }

    // Surface each channel's last command result (incl. out-of-range refusals) on the status line.
    private void OnChannelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PowerSupplyChannelViewModel.LastStatus)
            && sender is PowerSupplyChannelViewModel { LastStatus: { } status })
            Status = status;
    }

    /// <summary>
    /// Drives one hardware poll (forces the engine to re-read every channel
    /// via <see cref="IPowerSupplyFacet.RefreshAsync"/>) and updates the WPF
    /// bindings. Called each sampler tick (5 Hz); guarded against re-entry so
    /// the ~9 SCPI round-trips per refresh cannot queue duplicates. The
    /// engine's own 2 s background poll still runs as a safety net.
    /// </summary>
    public async Task RefreshAsync()
    {
        var device = _selectedDevice;
        if (device == null)
        {
            Connected = false;
            return;
        }
        if (_refreshInFlight) return;

        _refreshInFlight = true;
        try
        {
            // RefreshAsync runs every channel's MEAS:V / MEAS:I / OUTP? — for
            // the 3-channel UDP3305S that's 9 SCPI queries (LAN or USBTMC), ~100 ms.
            // Run off the UI thread.
            await Task.Run(() => _host.Engine.PowerSupply.RefreshAsync()).ConfigureAwait(true);

            Connected = _host.Engine.PowerSupply.IsConnected(device);
            var state = _host.Engine.PowerSupply.GetState(device);
            foreach (var ch in Channels)
                ch.Apply(state.Channels.FirstOrDefault(c => c.Channel == ch.Channel));
            if (!string.IsNullOrEmpty(state.LastError))
                Status = state.LastError;
        }
        finally { _refreshInFlight = false; }
    }

    private bool HasDevice() => _selectedDevice != null;

    private async Task RunAsync(Func<string, Task<Result>> operation, string okMessage)
    {
        var device = _selectedDevice;
        if (device == null) return;
        var result = await operation(device).ConfigureAwait(true);
        Status = result.IsSuccess ? okMessage : result.Message;
    }
}
