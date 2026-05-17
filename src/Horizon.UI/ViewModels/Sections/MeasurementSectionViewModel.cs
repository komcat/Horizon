using System.Collections.ObjectModel;
using Horizon.UI.Mvvm;

namespace Horizon.UI.ViewModels.Sections;

/// <summary>
/// The Machine page's Measurement section — a Keithley 6482 picoammeter picker,
/// a compact source-voltage config strip, one readout per enabled channel, and a
/// combined live current chart. Unlike the SMU section, the picoammeter polls
/// continuously once connected, so the chart streams without an explicit Start.
/// </summary>
public sealed class MeasurementSectionViewModel : ObservableObject
{
    private readonly EngineHost _host;
    private string? _selectedDevice;
    private bool _connected;
    private bool _isStreaming;
    private double _sourceVoltage;
    private bool _outputEnabled;
    private string _status = "—";

    public MeasurementSectionViewModel(EngineHost host)
    {
        _host = host;
        Devices = host.Engine.Measurement.DeviceNames;
        _selectedDevice = Devices.Count > 0 ? Devices[0] : null;
        StartCommand = new AsyncRelayCommand(StartStreamingAsync, () => !_isStreaming && _selectedDevice != null);
        StopCommand = new AsyncRelayCommand(StopStreamingAsync, () => _isStreaming);
        ApplyVoltageCommand = new AsyncRelayCommand(ApplyVoltageAsync, () => _selectedDevice != null);
        ToggleOutputCommand = new AsyncRelayCommand(ToggleOutputAsync, () => _selectedDevice != null);
        ResetCommand = new AsyncRelayCommand(ResetAsync, () => _selectedDevice != null);
        RebuildChannels();
    }

    /// <summary>Label shown in the Machine-page section rail.</summary>
    public string Title => "Measurement";

    public IReadOnlyList<string> Devices { get; }

    /// <summary>One entry per enabled channel of the selected device.</summary>
    public ObservableCollection<MeasurementChannelViewModel> Channels { get; } = new();

    public string? SelectedDevice
    {
        get => _selectedDevice;
        set { if (SetField(ref _selectedDevice, value)) RebuildChannels(); }
    }

    public bool Connected
    {
        get => _connected;
        private set => SetField(ref _connected, value);
    }

    /// <summary>True while the device is fast-streaming (~20–80 Hz) rather than normal-rate polling.</summary>
    public bool IsStreaming
    {
        get => _isStreaming;
        private set
        {
            if (!SetField(ref _isStreaming, value)) return;
            StartCommand.RaiseCanExecuteChanged();
            StopCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Source-voltage setpoint (V) entered for the next <see cref="ApplyVoltageCommand"/>.</summary>
    public double SourceVoltage
    {
        get => _sourceVoltage;
        set => SetField(ref _sourceVoltage, value);
    }

    public bool OutputEnabled
    {
        get => _outputEnabled;
        private set { if (SetField(ref _outputEnabled, value)) OnPropertyChanged(nameof(OutputButtonText)); }
    }

    public string OutputButtonText => _outputEnabled ? "Output Off" : "Output On";

    /// <summary>Last config-action result, shown next to the controls.</summary>
    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public AsyncRelayCommand ApplyVoltageCommand { get; }
    public AsyncRelayCommand ToggleOutputCommand { get; }
    public AsyncRelayCommand ResetCommand { get; }

    private void RebuildChannels()
    {
        Channels.Clear();
        var device = _selectedDevice;
        if (device == null) return;

        var info = _host.Engine.Measurement.GetDevice(device);
        var channels = info?.EnabledChannels is { Count: > 0 } enabled
            ? enabled
            : Enumerable.Range(1, Math.Max(info?.ChannelCount ?? 2, 1)).ToList();
        foreach (var channel in channels)
            Channels.Add(new MeasurementChannelViewModel(_host, device, channel));
    }

    /// <summary>Refreshes connection state and every channel readout — called each sampler tick.</summary>
    public void Refresh()
    {
        var device = _selectedDevice;
        Connected = device != null && _host.Engine.Measurement.IsConnected(device);
        IsStreaming = device != null && _host.Engine.Measurement.IsStreaming(device);
        foreach (var channel in Channels)
            channel.Refresh();
    }

    private async Task StartStreamingAsync()
    {
        var device = _selectedDevice;
        if (device == null) return;
        var result = await _host.Engine.Measurement.StartStreamingAsync(device).ConfigureAwait(true);
        IsStreaming = _host.Engine.Measurement.IsStreaming(device);
        Status = result.IsSuccess ? "Fast streaming…" : result.Message;
    }

    private async Task StopStreamingAsync()
    {
        var device = _selectedDevice;
        if (device == null) return;
        var result = await _host.Engine.Measurement.StopStreamingAsync(device).ConfigureAwait(true);
        IsStreaming = _host.Engine.Measurement.IsStreaming(device);
        Status = result.IsSuccess ? "Streaming stopped" : result.Message;
    }

    /// <summary>Stops fast streaming if active — called when the Machine page is disposed.</summary>
    public async Task StopIfStreamingAsync()
    {
        var device = _selectedDevice;
        if (device != null && _host.Engine.Measurement.IsStreaming(device))
            await _host.Engine.Measurement.StopStreamingAsync(device).ConfigureAwait(false);
    }

    private async Task ApplyVoltageAsync()
    {
        var device = _selectedDevice;
        if (device == null) return;
        var result = await _host.Engine.Measurement.SetVoltageAsync(device, _sourceVoltage).ConfigureAwait(true);
        Status = result.IsSuccess ? $"Source set to {_sourceVoltage:G} V" : result.Message;
    }

    private async Task ToggleOutputAsync()
    {
        var device = _selectedDevice;
        if (device == null) return;
        var next = !_outputEnabled;
        var result = await _host.Engine.Measurement.SetVoltageEnabledAsync(device, next).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            OutputEnabled = next;
            Status = next ? "Source output ON" : "Source output OFF";
        }
        else
        {
            Status = result.Message;
        }
    }

    private async Task ResetAsync()
    {
        var device = _selectedDevice;
        if (device == null) return;
        var result = await _host.Engine.Measurement.ResetAsync(device).ConfigureAwait(true);
        OutputEnabled = false;
        Status = result.IsSuccess ? "Instrument reset" : result.Message;
    }
}
