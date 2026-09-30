using System.Collections.ObjectModel;
using Horizon.Common;
using Horizon.Engine;
using FAAM.UI.Mvvm;

namespace FAAM.UI.ViewModels.Sections;

/// <summary>
/// Lighting adjustment shown in the Machine page's Camera section (so exposure and
/// illumination are tuned side by side). One row per light of the selected
/// controller — a channel group (main_camera_light) or an ungrouped channel — with a
/// 0..100 % slider and the call-outs the light has channels for (red / green / blue /
/// white = R+G+B mix / collimate).
/// All hardware calls go through the engine facet (off the UI thread).
/// </summary>
public sealed class LightingPanelViewModel : ObservableObject
{
    private readonly EngineHost _host;
    private string? _selectedDevice;
    private bool _connected;
    private string _status = "—";

    public LightingPanelViewModel(EngineHost host)
    {
        _host = host;
        Devices = host.Engine.Lighting.DeviceNames;

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => _selectedDevice != null && !_connected);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, () => _selectedDevice != null && _connected);
        AllOffCommand = new AsyncRelayCommand(AllOffAsync, () => _selectedDevice != null && _connected);

        SelectedDevice = Devices.Count > 0 ? Devices[0] : null;
    }

    public IReadOnlyList<string> Devices { get; }
    public bool HasDevices => Devices.Count > 0;
    public bool HasMultipleDevices => Devices.Count > 1;

    public string? SelectedDevice
    {
        get => _selectedDevice;
        set { if (SetField(ref _selectedDevice, value)) Rebuild(); }
    }

    private string? _camera;

    /// <summary>
    /// The Camera section's selected camera. Only lights configured for this camera
    /// (plus lights tied to no camera) are shown.
    /// </summary>
    public string? Camera
    {
        get => _camera;
        set { if (SetField(ref _camera, value)) Rebuild(); }
    }

    /// <summary>True when the selected camera has no light on this controller — shows a note instead of rows.</summary>
    public bool NoLightsForCamera => Lights.Count == 0;

    /// <summary>The note shown when <see cref="NoLightsForCamera"/>.</summary>
    public string NoLightsText => $"No lighting configured for {(_camera ?? "this camera")} (lighting_devices.json › \"camera\").";

    public bool Connected
    {
        get => _connected;
        private set
        {
            if (!SetField(ref _connected, value)) return;
            ConnectCommand.RaiseCanExecuteChanged();
            DisconnectCommand.RaiseCanExecuteChanged();
            AllOffCommand.RaiseCanExecuteChanged();
            if (value) SyncFromHardware();
        }
    }

    public string Status { get => _status; private set => SetField(ref _status, value); }

    public ObservableCollection<LightControlViewModel> Lights { get; } = new();

    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand DisconnectCommand { get; }
    public AsyncRelayCommand AllOffCommand { get; }

    /// <summary>Tracks the connection (cheap, cached) — called from the Camera section's sampler tick.</summary>
    public void Refresh()
    {
        if (_host.Engine.State == EngineState.ShuttingDown) return;
        Connected = _selectedDevice != null && _host.Engine.Lighting.IsConnected(_selectedDevice);
    }

    private void Rebuild()
    {
        Lights.Clear();
        try
        {
            AddRows();
        }
        finally
        {
            OnPropertyChanged(nameof(NoLightsForCamera));
            OnPropertyChanged(nameof(NoLightsText));
        }
    }

    private void AddRows()
    {
        var device = _selectedDevice;
        var info = device == null ? null : _host.Engine.Lighting.GetDevice(device);
        if (device == null || info == null) return;

        foreach (var light in info.Lights)
        {
            var channels = info.Channels.Where(c => InLight(c, light)).ToList();
            if (!ForCamera(channels)) continue;
            var hasRgb = channels.Any(c => LightColors.IsPrimary(c.Color));
            var hasCollimate = channels.Any(c => LightColors.Matches(LightColor.Collimate, c.Color));
            Lights.Add(new LightControlViewModel(_host, device, light, hasRgb, hasCollimate, SetStatus));
        }
        Refresh();
        if (_connected) SyncFromHardware();
    }

    /// <summary>
    /// Seeds each row from the controller's read-back state: the brightest lit channel
    /// sets the slider; the call-out is the one whose channels are exactly the lit set
    /// (e.g. only L1 lit → collimate; R+G+B lit → white), else white.
    /// </summary>
    private void SyncFromHardware()
    {
        var device = _selectedDevice;
        var info = device == null ? null : _host.Engine.Lighting.GetDevice(device);
        if (device == null || info == null) return;

        var state = _host.Engine.Lighting.GetState(device);
        if (!state.Valid) return;

        foreach (var row in Lights)
        {
            var channels = info.Channels.Where(c => InLight(c, row.Name)).ToList();
            var lit = channels
                .Select(c => (Info: c, State: state.Channels.FirstOrDefault(s => s.Channel == c.Channel)))
                .Where(x => x.State is { On: true, Intensity: > 0 })
                .ToList();

            var percent = lit.Count == 0 ? 0 : lit.Max(x => x.State!.Percent);
            var litSet = lit.Select(x => x.Info.Channel).ToHashSet();
            var color = Enum.GetValues<LightColor>()
                .Where(k => channels.Any(c => LightColors.Matches(k, c.Color)))
                .Select(k => (LightColor?)k)
                .FirstOrDefault(k => litSet.SetEquals(
                    channels.Where(c => LightColors.Matches(k!.Value, c.Color)).Select(c => c.Channel)))
                ?? row.Color;   // all off / mixed: keep the row's current call-out
            row.SetFromHardware(percent, color);
        }
    }

    private async Task ConnectAsync()
    {
        var device = _selectedDevice;
        if (device == null) return;
        Status = $"Connecting {device}…";
        var r = await _host.Engine.Lighting.ConnectAsync(device).ConfigureAwait(true);
        await _host.Engine.Lighting.RefreshAsync().ConfigureAwait(true);
        Refresh();
        Status = r.IsSuccess ? $"{device} connected" : r.Message;
    }

    private async Task DisconnectAsync()
    {
        var device = _selectedDevice;
        if (device == null) return;
        var r = await _host.Engine.Lighting.DisconnectAsync(device).ConfigureAwait(true);
        Refresh();
        Status = r.IsSuccess ? $"{device} disconnected" : r.Message;
    }

    private async Task AllOffAsync()
    {
        var device = _selectedDevice;
        if (device == null) return;
        var r = await _host.Engine.Lighting.SetAllAsync(device, 0).ConfigureAwait(true);
        if (r.IsSuccess)
            foreach (var row in Lights)
                row.SetFromHardware(0, row.Color);
        Status = r.IsSuccess ? "All lights off" : r.Message;
    }

    private void SetStatus(string text) => Status = text;

    /// <summary>
    /// Whether a light belongs on the selected camera: any of its channels is tied to
    /// that camera, or none of them is tied to a camera at all.
    /// </summary>
    private bool ForCamera(IReadOnlyList<LightingChannelInfo> channels)
    {
        var tied = channels.Where(c => !string.IsNullOrWhiteSpace(c.Camera)).ToList();
        return tied.Count == 0
               || (_camera != null && tied.Any(c => c.Camera.Equals(_camera, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool InLight(LightingChannelInfo c, string light)
        => string.IsNullOrWhiteSpace(c.Group)
            ? c.Name.Equals(light, StringComparison.OrdinalIgnoreCase)
            : c.Group.Equals(light, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One light row. Slider moves are coalesced: at most one command is in flight, and
/// the latest value is re-sent when it completes, so a drag never queues a backlog
/// on the controller.
/// </summary>
public sealed class LightControlViewModel : ObservableObject
{
    private readonly EngineHost _host;
    private readonly string _device;
    private readonly Action<string> _report;
    private double _percent;
    private LightColor _color = LightColor.White;
    private bool _applying;
    private bool _pending;
    private bool _suppressApply;

    public LightControlViewModel(
        EngineHost host, string device, string name, bool hasRgb, bool hasCollimate, Action<string> report)
    {
        _host = host;
        _device = device;
        _report = report;
        Name = name;
        HasRgb = hasRgb;
        HasCollimate = hasCollimate;
        // A light with only a collimate channel starts on that call-out.
        if (!hasRgb && hasCollimate) _color = LightColor.Collimate;

        OffCommand = new RelayCommand(() => Percent = 0);
        FullCommand = new RelayCommand(() => Percent = 100);
        RedCommand = new RelayCommand(() => SelectColor(LightColor.Red));
        GreenCommand = new RelayCommand(() => SelectColor(LightColor.Green));
        BlueCommand = new RelayCommand(() => SelectColor(LightColor.Blue));
        WhiteCommand = new RelayCommand(() => SelectColor(LightColor.White));
        CollimateCommand = new RelayCommand(() => SelectColor(LightColor.Collimate));
        WhiteCollimateCommand = new RelayCommand(() => SelectColor(LightColor.WhiteCollimate));
    }

    public string Name { get; }

    /// <summary>True when the light has red/green/blue channels — shows Red/Green/Blue/White (mix).</summary>
    public bool HasRgb { get; }

    /// <summary>True when the light has a collimate channel — shows the Collimate call-out.</summary>
    public bool HasCollimate { get; }

    /// <summary>True when the light has both — shows the White + collimate call-out.</summary>
    public bool HasRgbAndCollimate => HasRgb && HasCollimate;

    /// <summary>True when the light is driven by colour call-outs (vs. a plain percent on all channels).</summary>
    public bool HasColors => HasRgb || HasCollimate;

    /// <summary>Brightness 0..100 % (whole percent). Setting it sends the change to the controller.</summary>
    public double Percent
    {
        get => _percent;
        set
        {
            var v = Math.Round(Math.Clamp(double.IsFinite(value) ? value : 0, 0, 100));
            if (!SetField(ref _percent, v)) return;
            OnPropertyChanged(nameof(Summary));
            if (!_suppressApply) _ = ApplyAsync();
        }
    }

    public LightColor Color
    {
        get => _color;
        private set { if (SetField(ref _color, value)) OnPropertyChanged(nameof(Summary)); }
    }

    /// <summary>Read-out next to the slider, e.g. "Red 40 %" / "Off".</summary>
    public string Summary => _percent <= 0
        ? "Off"
        : HasColors ? $"{LightColors.Display(_color)} {_percent:0} %" : $"{_percent:0} %";

    public RelayCommand OffCommand { get; }
    public RelayCommand FullCommand { get; }
    public RelayCommand RedCommand { get; }
    public RelayCommand GreenCommand { get; }
    public RelayCommand BlueCommand { get; }
    public RelayCommand WhiteCommand { get; }
    public RelayCommand CollimateCommand { get; }
    public RelayCommand WhiteCollimateCommand { get; }

    /// <summary>Updates the row from read-back state without sending anything.</summary>
    public void SetFromHardware(double percent, LightColor color)
    {
        _suppressApply = true;
        try
        {
            Color = color;
            Percent = percent;
        }
        finally { _suppressApply = false; }
    }

    private void SelectColor(LightColor color)
    {
        Color = color;
        if (_percent <= 0)
            Percent = 50;          // picking a colour on a dark light turns it on (applies)
        else
            _ = ApplyAsync();
    }

    private async Task ApplyAsync()
    {
        if (_applying) { _pending = true; return; }
        _applying = true;
        try
        {
            do
            {
                _pending = false;
                var percent = _percent;
                var color = _color;
                var r = HasColors
                    ? await _host.Engine.Lighting.SetLightColorAsync(_device, Name, color, percent).ConfigureAwait(true)
                    : await _host.Engine.Lighting.SetLightPercentAsync(_device, Name, percent).ConfigureAwait(true);
                _report(r.IsSuccess ? $"{Name}: {Summary}" : r.Message);
                if (!r.IsSuccess) break;          // don't hammer a failing controller
            } while (_pending);
        }
        finally { _applying = false; }
    }
}
