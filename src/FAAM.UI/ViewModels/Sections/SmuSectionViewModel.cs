using System.Collections.ObjectModel;

namespace FAAM.UI.ViewModels.Sections;

/// <summary>
/// The Machine page's SMU section — a device picker plus one
/// <see cref="SmuChannelViewModel"/> per enabled channel of the selected
/// device, each with its own streaming control, readout, and trend chart.
/// </summary>
public sealed class SmuSectionViewModel : ObservableObject
{
    private readonly EngineHost _host;
    private string? _selectedDevice;
    private int _intervalMs = 100;
    private bool _connected;

    public SmuSectionViewModel(EngineHost host)
    {
        _host = host;
        Devices = host.Engine.Smu.DeviceNames;
        _selectedDevice = Devices.Count > 0 ? Devices[0] : null;
        RebuildChannels();
    }

    /// <summary>Label shown in the Machine-page section rail.</summary>
    public string Title => "SMU";

    public IReadOnlyList<string> Devices { get; }

    /// <summary>One entry per enabled channel of the selected device.</summary>
    public ObservableCollection<SmuChannelViewModel> Channels { get; } = new();

    public string? SelectedDevice
    {
        get => _selectedDevice;
        set { if (SetField(ref _selectedDevice, value)) RebuildChannels(); }
    }

    public int IntervalMs
    {
        get => _intervalMs;
        set => SetField(ref _intervalMs, value);
    }

    public bool Connected
    {
        get => _connected;
        private set => SetField(ref _connected, value);
    }

    private void RebuildChannels()
    {
        Channels.Clear();
        var device = _selectedDevice;
        if (device == null) return;

        var info = _host.Engine.Smu.GetDevice(device);
        var channels = info?.Channels ?? new[] { 1 };
        foreach (var channel in channels)
            Channels.Add(new SmuChannelViewModel(_host, device, channel, () => _intervalMs));
    }

    /// <summary>Refreshes connection state and every channel readout — called each sampler tick.</summary>
    public void Refresh()
    {
        var device = _selectedDevice;
        Connected = device != null && _host.Engine.Smu.IsConnected(device);
        foreach (var channel in Channels)
            channel.Refresh();
    }

    /// <summary>Stops streaming on every channel — called when the Machine page is disposed.</summary>
    public async Task StopIfStreamingAsync()
    {
        var device = _selectedDevice;
        if (device != null && _host.Engine.Smu.IsStreaming(device))
            await _host.Engine.Smu.StopStreamingAsync(device).ConfigureAwait(false);
    }
}
