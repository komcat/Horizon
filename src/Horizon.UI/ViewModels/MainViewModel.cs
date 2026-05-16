using System.Collections.ObjectModel;
using System.Windows.Threading;
using Horizon.Engine;
using Horizon.UI.Mvvm;
using Horizon.UI.Telemetry;

namespace Horizon.UI.ViewModels;

/// <summary>
/// View model for the hardware status dashboard. The engine produces device
/// state on its own background poll loops; this view model never polls
/// hardware itself — a single shared <see cref="UiSampler"/> pulls the engine's
/// cached <see cref="IHorizonEngine.GetStatusSnapshot"/> at a fixed display
/// cadence. <c>GetStatusSnapshot</c> is synchronous and non-blocking, so the UI
/// thread is never stalled (see <c>docs/layer3-ui-design.md</c> §7).
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const int MaxLogLines = 1000;

    private readonly EngineHost _host;
    private readonly UiSampler _sampler;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, DeviceRowViewModel> _rowIndex = new();

    private string _engineState = "—";
    private string _deviceSummary = "—";
    private bool _disposed;

    public MainViewModel(EngineHost host)
    {
        _host = host;
        _dispatcher = Dispatcher.CurrentDispatcher;   // constructed on the UI thread
        _sampler = new UiSampler(TimeSpan.FromMilliseconds(200));   // ~5 Hz status refresh
        ReconnectCommand = new AsyncRelayCommand(ReconnectAsync);
    }

    /// <summary>Hardware devices, one row per engine device. Updated in place each sampler tick.</summary>
    public ObservableCollection<DeviceRowViewModel> Devices { get; } = new();

    /// <summary>Engine + workflow log lines, oldest first, capped at <see cref="MaxLogLines"/>.</summary>
    public ObservableCollection<LogLineViewModel> Log { get; } = new();

    /// <summary>Re-runs the engine connect sequence.</summary>
    public AsyncRelayCommand ReconnectCommand { get; }

    public string EngineState
    {
        get => _engineState;
        private set => SetField(ref _engineState, value);
    }

    public string DeviceSummary
    {
        get => _deviceSummary;
        private set => SetField(ref _deviceSummary, value);
    }

    /// <summary>Loads buffered logs, subscribes for live lines, and starts the sampler.</summary>
    public void Start()
    {
        foreach (var entry in _host.Log.Snapshot())
            AppendLog(entry);

        _host.Log.Appended += OnLogAppended;

        _sampler.Tick += OnTick;
        _sampler.Start();
        OnTick();   // paint immediately rather than waiting one interval
    }

    private void OnTick()
    {
        var snapshot = _host.Engine.GetStatusSnapshot();
        EngineState = snapshot.State.ToString();

        var seen = new HashSet<string>(snapshot.Devices.Count);
        var connected = 0;

        foreach (var status in snapshot.Devices)
        {
            var key = $"{status.Domain}/{status.Name}";
            seen.Add(key);
            if (status.IsConnected)
                connected++;

            if (_rowIndex.TryGetValue(key, out var row))
            {
                row.Update(status);
            }
            else
            {
                row = new DeviceRowViewModel(status);
                _rowIndex[key] = row;
                Devices.Add(row);
            }
        }

        // Drop rows for devices no longer reported (rare, but keeps the view honest).
        for (var i = Devices.Count - 1; i >= 0; i--)
        {
            if (seen.Contains(Devices[i].Key))
                continue;
            _rowIndex.Remove(Devices[i].Key);
            Devices.RemoveAt(i);
        }

        DeviceSummary = $"{connected} / {snapshot.Devices.Count} connected";
    }

    private async Task ReconnectAsync()
    {
        var progress = new Progress<string>(line =>
            AppendLog(new LogEntry(LogLevel.Info, DateTimeOffset.Now, "Reconnect", line)));
        await _host.Engine.ConnectAllAsync(progress);
    }

    private void OnLogAppended(LogEntry entry)
    {
        // The hub fires on engine threads — marshal to the UI thread.
        if (_dispatcher.CheckAccess())
            AppendLog(entry);
        else
            _dispatcher.BeginInvoke(() => AppendLog(entry));
    }

    private void AppendLog(LogEntry entry)
    {
        Log.Add(new LogLineViewModel(entry));
        while (Log.Count > MaxLogLines)
            Log.RemoveAt(0);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _sampler.Tick -= OnTick;
        _sampler.Stop();
        _host.Log.Appended -= OnLogAppended;
    }
}
