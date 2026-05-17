using System.Collections.ObjectModel;
using System.Windows.Threading;
using Horizon.Engine;
using Horizon.UI.Mvvm;
using Horizon.UI.Telemetry;
using Horizon.UI.ViewModels.Sections;

namespace Horizon.UI.ViewModels;

/// <summary>
/// The Machine page — hardware operation and diagnostics. Owns the engine-state
/// header, the global log panel, and a left section rail. A single shared
/// <see cref="UiSampler"/> pulls the engine's cached snapshot at a fixed cadence
/// (see <c>docs/layer3-ui-design.md</c> §7); it runs only while this page is
/// active, so the home launcher and other pages cost nothing.
/// </summary>
public sealed class MachinePageViewModel : PageViewModel, IDisposable
{
    private const int MaxLogLines = 1000;

    private readonly EngineHost _host;
    private readonly UiSampler _sampler;
    private readonly Dispatcher _dispatcher;

    private object _currentSection;
    private string _engineState = "—";
    private string _deviceSummary = "—";
    private bool _statusRefreshing;
    private bool _disposed;

    public MachinePageViewModel(EngineHost host)
    {
        _host = host;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _sampler = new UiSampler(TimeSpan.FromMilliseconds(200));   // ~5 Hz status refresh
        ReconnectCommand = new AsyncRelayCommand(ReconnectAsync);

        Status = new StatusSectionViewModel();
        Smu = new SmuSectionViewModel(host);
        Sections = new object[]
        {
            Status,
            new PlaceholderSectionViewModel("Motion", "Motion control arrives in Phase 3."),
            new PlaceholderSectionViewModel("IO", "IO control arrives in Phase 3."),
            new PlaceholderSectionViewModel("Pneumatic", "Pneumatic control arrives in Phase 3."),
            new PlaceholderSectionViewModel("Laser", "Laser control arrives in Phase 3."),
            new PlaceholderSectionViewModel("Measurement", "Measurement charts arrive in Phase 3."),
            Smu,
            new PlaceholderSectionViewModel("Camera", "Camera live view arrives in Phase 4."),
        };
        _currentSection = Status;

        // The log is global — capture buffered lines and subscribe for the page's lifetime.
        foreach (var entry in _host.Log.Snapshot())
            AppendLog(entry);
        _host.Log.Appended += OnLogAppended;
    }

    public override string Title => "Machine";

    /// <summary>Section rail entries — only Status is live in Phase 2.</summary>
    public IReadOnlyList<object> Sections { get; }

    /// <summary>The Status section, kept typed for the sampler to push snapshots into.</summary>
    public StatusSectionViewModel Status { get; }

    /// <summary>The SMU section, kept typed for the sampler to refresh its readout.</summary>
    public SmuSectionViewModel Smu { get; }

    /// <summary>Engine + workflow log lines, oldest first, capped at <see cref="MaxLogLines"/>.</summary>
    public ObservableCollection<LogLineViewModel> Log { get; } = new();

    /// <summary>Re-runs the engine connect sequence.</summary>
    public AsyncRelayCommand ReconnectCommand { get; }

    public object CurrentSection
    {
        get => _currentSection;
        set => SetField(ref _currentSection, value);
    }

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

    public override void OnActivated()
    {
        _sampler.Tick += OnTick;
        _sampler.Start();
        OnTick();   // paint immediately rather than waiting one interval
    }

    public override void OnDeactivated()
    {
        _sampler.Tick -= OnTick;
        _sampler.Stop();
    }

    private void OnTick()
    {
        // The SMU readout is cheap (cached telemetry) — stays on the UI thread.
        Smu.Refresh();

        // GetStatusSnapshot() reads each device's IsConnected, which takes the
        // adapter lock a 2 s hardware poll also holds during VISA/SCPI I/O — so
        // it can briefly block. Run it off the UI thread so the chart never
        // stalls; the result is applied back on the UI thread.
        _ = RefreshStatusAsync();
    }

    private async Task RefreshStatusAsync()
    {
        if (_statusRefreshing) return;   // a refresh is still in flight — skip this tick
        _statusRefreshing = true;
        try
        {
            var snapshot = await Task.Run(_host.Engine.GetStatusSnapshot).ConfigureAwait(true);
            EngineState = snapshot.State.ToString();
            var connected = snapshot.Devices.Count(d => d.IsConnected);
            DeviceSummary = $"{connected} / {snapshot.Devices.Count} connected";
            Status.Apply(snapshot);
        }
        finally
        {
            _statusRefreshing = false;
        }
    }

    private async Task ReconnectAsync()
    {
        var progress = new Progress<string>(line =>
            AppendLog(new LogEntry(LogLevel.Info, DateTimeOffset.Now, "Reconnect", line)));
        await _host.Engine.ConnectAllAsync(progress);
    }

    private void OnLogAppended(LogEntry entry)
    {
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

        OnDeactivated();
        _host.Log.Appended -= OnLogAppended;

        // Tidy — stop SMU streaming if the page is torn down while active.
        // (The engine also stops every stream on its own dispose.)
        _ = Smu.StopIfStreamingAsync();
    }
}
