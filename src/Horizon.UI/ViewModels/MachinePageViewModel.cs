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
    private bool _pageActive;
    private bool _disposed;

    public MachinePageViewModel(EngineHost host)
    {
        _host = host;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _sampler = new UiSampler(TimeSpan.FromMilliseconds(200));   // ~5 Hz status refresh
        ReconnectCommand = new AsyncRelayCommand(ReconnectAsync);

        Status = new StatusSectionViewModel();
        Smu = new SmuSectionViewModel(host);
        Measurement = new MeasurementSectionViewModel(host);
        Io = new IoSectionViewModel(host);
        Pneumatic = new PneumaticSectionViewModel(host);
        Motion = new MotionSectionViewModel(host);
        Laser = new LaserSectionViewModel(host);
        Camera = new CameraSectionViewModel(host);
        Sections = new object[]
        {
            Status,
            Motion,
            Io,
            Pneumatic,
            Laser,
            Measurement,
            Smu,
            Camera,
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

    /// <summary>The Measurement section, kept typed for the sampler to refresh its readout.</summary>
    public MeasurementSectionViewModel Measurement { get; }

    /// <summary>The IO section, kept typed for the sampler to refresh its pin states.</summary>
    public IoSectionViewModel Io { get; }

    /// <summary>The Pneumatic section, kept typed for the sampler to refresh its slide states.</summary>
    public PneumaticSectionViewModel Pneumatic { get; }

    /// <summary>The Motion section, kept typed for the sampler to refresh its axis positions.</summary>
    public MotionSectionViewModel Motion { get; }

    /// <summary>The Laser section, kept typed for the sampler to refresh its readouts.</summary>
    public LaserSectionViewModel Laser { get; }

    /// <summary>The Camera section, kept typed for the sampler to refresh its grab state.</summary>
    public CameraSectionViewModel Camera { get; }

    /// <summary>Engine + workflow log lines, oldest first, capped at <see cref="MaxLogLines"/>.</summary>
    public ObservableCollection<LogLineViewModel> Log { get; } = new();

    /// <summary>Re-runs the engine connect sequence.</summary>
    public AsyncRelayCommand ReconnectCommand { get; }

    public object CurrentSection
    {
        get => _currentSection;
        set { if (SetField(ref _currentSection, value)) UpdateCameraActive(); }
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
        _pageActive = true;
        _sampler.Tick += OnTick;
        _sampler.Start();
        OnTick();   // paint immediately rather than waiting one interval
        UpdateCameraActive();
    }

    public override void OnDeactivated()
    {
        _pageActive = false;
        _sampler.Tick -= OnTick;
        _sampler.Stop();
        UpdateCameraActive();
    }

    /// <summary>
    /// Runs the camera frame pipeline only while this page is shown and the
    /// Camera section is the active one — a grabbing camera then costs the UI
    /// nothing from any other section or page.
    /// </summary>
    private void UpdateCameraActive()
        => Camera.SetActive(_pageActive && ReferenceEquals(_currentSection, Camera));

    private void OnTick()
    {
        // The section readouts are cheap (cached state) — stay on the UI thread.
        Smu.Refresh();
        Measurement.Refresh();
        Io.Refresh();
        Pneumatic.Refresh();
        Motion.Refresh();
        Laser.Refresh();
        Camera.Refresh();

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
        Camera.Dispose();   // unsubscribes the camera FrameReady hook (UI cleanup, not teardown)

        // Pure UI cleanup only — no hardware teardown here. Stopping every
        // SMU/Measurement stream and disconnecting devices is owned by the
        // engine's ShutdownAsync, which App.OnExit awaits immediately after
        // this page is disposed. Doing it here too (unawaited, fire-and-forget)
        // would only race that ordered teardown.
    }
}
