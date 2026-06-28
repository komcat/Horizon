using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Threading;
using FAAM.UI.Mvvm;

namespace FAAM.UI.Siphog;

/// <summary>One row of the SiPhOG field/value table.</summary>
public sealed class SiphogFieldRow : ObservableObject
{
    private string _value = "—";
    public SiphogFieldRow(string name) => Name = name;
    public string Name { get; }
    public string Value { get => _value; set => SetField(ref _value, value); }
}

/// <summary>One SET_CURRENT preset button (sends <c>SET_CURRENT:&lt;mA&gt;</c>).</summary>
public sealed class SiphogPresetVm
{
    public SiphogPresetVm(int milliAmps, Func<int, Task> apply)
    {
        MilliAmps = milliAmps;
        ApplyCommand = new AsyncRelayCommand(() => apply(milliAmps));
    }

    public int MilliAmps { get; }
    public string Label => $"{MilliAmps} mA";
    public AsyncRelayCommand ApplyCommand { get; }
}

/// <summary>
/// View-model for the Run-page SiPhOG panel: connect/disconnect, a six-field
/// value table, a throttled data-rate readout, and SET_CURRENT preset buttons.
/// The client streams at ~200 Hz, so a <see cref="DispatcherTimer"/> samples the
/// latest frame + frame counter at a fixed UI rate rather than updating per frame.
/// </summary>
public sealed class SiphogPanelViewModel : ObservableObject, IDisposable
{
    // SET_CURRENT is limited to 100–200 mA by the server (see PROTOCOL.md §4.1).
    private static readonly int[] PresetCurrents = { 110, 200 };
    private const double DisplayHz = 10;   // UI refresh rate (throttle for the fast stream)
    private const double ReconnectSeconds = 3;   // how often to silently retry while disconnected
    private const double StallSeconds = 3;       // no frames for this long ⇒ treat the link as dead

    private readonly SiphogClient _client = new();
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _reconnectTimer;
    private readonly Stopwatch _rateClock = new();

    private long _lastFrameCount;
    private double _lastElapsed;
    private double _lastDataElapsed;   // _rateClock time when the frame counter last advanced (stall watchdog)
    private bool _isConnected;
    private bool _autoReconnect = true;   // keep retrying until the operator explicitly disconnects
    private bool _connecting;             // guards against overlapping connect attempts
    private string _dataRate = "—";
    private string _status = "Disconnected";
    private string _sagPercent = "—";
    private string _sagLevel = "none";

    public SiphogPanelViewModel()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;

        Fields = new ObservableCollection<SiphogFieldRow>
        {
            new("SLED Current (mA)"),
            new("Photo Current (µA)"),
            new("SLED Temp (°C)"),
            new("Target SAG PWR (V)"),
            new("SAG PWR (V)"),
            new("TEC Current (mA)"),
        };

        Presets = new ObservableCollection<SiphogPresetVm>(
            PresetCurrents.Select(ma => new SiphogPresetVm(ma, SetCurrentAsync)));

        ConnectCommand = new AsyncRelayCommand(ToggleConnectAsync);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000.0 / DisplayHz) };
        _timer.Tick += (_, _) => UpdateDisplay();

        // Periodically retries a connection while disconnected (and auto-reconnect is armed),
        // so the panel comes up on its own and silently recovers from server drops.
        _reconnectTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(ReconnectSeconds) };
        _reconnectTimer.Tick += (_, _) => _ = TryAutoConnectAsync();
        _reconnectTimer.Start();

        _client.ConnectionChanged += OnConnectionChanged;

        // Kick off the first attempt immediately rather than waiting a full interval.
        _ = TryAutoConnectAsync();
    }

    /// <summary>The underlying client — shared with the SiPhOG chart so both use one connection.</summary>
    public SiphogClient Client => _client;

    public ObservableCollection<SiphogFieldRow> Fields { get; }
    public ObservableCollection<SiphogPresetVm> Presets { get; }
    public AsyncRelayCommand ConnectCommand { get; }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (!SetField(ref _isConnected, value)) return;
            OnPropertyChanged(nameof(ConnectLabel));
        }
    }

    public string ConnectLabel => _isConnected ? "Disconnect" : "Connect";
    public string DataRate { get => _dataRate; private set => SetField(ref _dataRate, value); }
    public string Status { get => _status; private set => SetField(ref _status, value); }

    /// <summary>SAG PWR as a percentage of Target SAG PWR (e.g. "97.5%"), or "—".</summary>
    public string SagPercent { get => _sagPercent; private set => SetField(ref _sagPercent, value); }

    /// <summary>Traffic-light level for <see cref="SagPercent"/>: ok / warn / danger / none — drives the background.</summary>
    public string SagLevel { get => _sagLevel; private set => SetField(ref _sagLevel, value); }

    /// <summary>Re-arms auto-reconnect (used when the widget is re-shown); the timer does the rest.</summary>
    public void EnsureConnected()
    {
        _autoReconnect = true;
        if (!_isConnected)
            _ = TryAutoConnectAsync();
    }

    private async Task ToggleConnectAsync()
    {
        if (_isConnected)
        {
            // Explicit operator disconnect — stop trying to reconnect until they ask again.
            _autoReconnect = false;
            _client.Disconnect();   // OnConnectionChanged updates state + stops the timer
            return;
        }

        _autoReconnect = true;
        Status = $"Connecting to {SiphogClient.DefaultHost}:{SiphogClient.DefaultPort}…";
        await TryAutoConnectAsync(quiet: false).ConfigureAwait(true);
    }

    /// <summary>
    /// Attempts a connection if armed and not already connected/connecting. Failures are
    /// swallowed silently when <paramref name="quiet"/> (the periodic path) so a missing
    /// server just leaves the panel idle; an explicit operator click reports the error.
    /// </summary>
    private async Task TryAutoConnectAsync(bool quiet = true)
    {
        if (!_autoReconnect || _isConnected || _connecting)
            return;

        _connecting = true;
        try
        {
            await _client.ConnectAsync().ConfigureAwait(true);
            Status = "Connected";
        }
        catch (Exception ex)
        {
            if (!quiet)
                Status = $"Connect failed: {ex.Message}";
            // Quiet path: stay silent; the reconnect timer will try again.
        }
        finally
        {
            _connecting = false;
        }
    }

    private async Task SetCurrentAsync(int milliAmps)
    {
        if (!_isConnected)
        {
            Status = "Not connected.";
            return;
        }
        var reply = await _client.SendCommandAsync($"SET_CURRENT:{milliAmps}").ConfigureAwait(true);
        Status = reply;   // ACK:CURRENT:<mA> or ERR:<message>
    }

    private void OnConnectionChanged(bool connected)
    {
        // Fires on a background thread when the server drops us — marshal to UI.
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => OnConnectionChanged(connected));
            return;
        }

        IsConnected = connected;
        if (connected)
        {
            _lastFrameCount = 0;
            _lastElapsed = 0;
            _lastDataElapsed = 0;
            _rateClock.Restart();
            _timer.Start();
        }
        else
        {
            _timer.Stop();
            DataRate = "—";
            SagPercent = "—";
            SagLevel = "none";
            // A drop while still armed reads as a transient "Reconnecting…"; an explicit
            // operator disconnect (auto-reconnect off) reads as a settled "Disconnected".
            if (Status == "Connected")
                Status = _autoReconnect ? "Reconnecting…" : "Disconnected";
            foreach (var f in Fields) f.Value = "—";
        }
    }

    /// <summary>Timer tick (~10 Hz): sample the latest frame + compute the data rate.</summary>
    private void UpdateDisplay()
    {
        var frame = _client.Latest;
        if (frame is { } f)
        {
            Fields[0].Value = f.SledCurrentMa.ToString("0.00", CultureInfo.InvariantCulture);
            Fields[1].Value = f.PhotoCurrentUa.ToString("0.00", CultureInfo.InvariantCulture);
            Fields[2].Value = f.SledTempC.ToString("0.00", CultureInfo.InvariantCulture);
            Fields[3].Value = f.TargetSagPwrV.ToString("0.0000", CultureInfo.InvariantCulture);
            Fields[4].Value = f.SagPwrV.ToString("0.0000", CultureInfo.InvariantCulture);
            Fields[5].Value = f.TecCurrentMa.ToString("0.00", CultureInfo.InvariantCulture);

            // SAG / target as a percentage, with a traffic-light level.
            if (Math.Abs(f.TargetSagPwrV) > 1e-9)
            {
                var pct = f.SagPwrV / f.TargetSagPwrV * 100.0;
                SagPercent = $"{pct:0.0}%";
                SagLevel = pct > 95 ? "ok" : pct >= 85 ? "warn" : "danger";
            }
            else
            {
                SagPercent = "—";
                SagLevel = "none";
            }
        }

        var count = _client.FrameCount;
        var elapsed = _rateClock.Elapsed.TotalSeconds;
        var dt = elapsed - _lastElapsed;
        if (dt > 0)
        {
            var rate = (count - _lastFrameCount) / dt;
            DataRate = $"{rate:0.0} Hz";
        }

        // Stall watchdog: a silently-dead link (server hung / cable pulled) keeps the socket
        // "open" so ReadAsync never returns — but the 200 Hz stream stops. If the frame counter
        // hasn't advanced for StallSeconds, drop the link so auto-reconnect can recover it.
        if (count != _lastFrameCount)
            _lastDataElapsed = elapsed;
        else if (elapsed - _lastDataElapsed > StallSeconds)
        {
            _lastFrameCount = count;
            _lastElapsed = elapsed;
            _client.Disconnect();   // fires ConnectionChanged(false) → Reconnecting… → timer recovers
            return;
        }

        _lastFrameCount = count;
        _lastElapsed = elapsed;
    }

    public void Dispose()
    {
        _autoReconnect = false;
        _reconnectTimer.Stop();
        _timer.Stop();
        _client.ConnectionChanged -= OnConnectionChanged;
        _client.Dispose();
    }
}
