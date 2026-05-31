using Horizon.Common;
using Horizon.Engine;
using Horizon.UI.Telemetry;
using Horizon.Workflow;

namespace Horizon.UI;

/// <summary>
/// Layer 3 composition root. Owns the single <see cref="HorizonEngine"/> (Layer 1)
/// and <see cref="ProcessService"/> (Layer 2) for the whole application — the role
/// the legacy <c>AppContext</c> played, but as pure wiring with no hardware logic.
///
/// One process, one engine, one host (see <c>docs/layer3-ui-design.md</c> §5–6).
/// </summary>
public sealed class EngineHost : IAsyncDisposable
{
    /// <summary>The Layer 1 hardware engine.</summary>
    public HorizonEngine Engine { get; }

    /// <summary>The Layer 2 product / process facade, built on the engine.</summary>
    public ProcessService Process { get; }

    /// <summary>Buffered log surface — captures engine + workflow logs from launch.</summary>
    public LogHub Log { get; }

    /// <summary>Live telemetry history — ring buffers fed by engine sample events.</summary>
    public TelemetryStore Telemetry { get; }

    /// <summary>Application settings (mm/pixel, MoveClick mapping) — load/save to app_settings.json.</summary>
    public AppSettingsStore Settings { get; }

    /// <summary>Station operator directory (users.json) — drives the login picker.</summary>
    public UserStore Users { get; }

    /// <summary>The signed-in operator; pushes traceability identity down to Layer 2.</summary>
    public Session Session { get; }

    private bool _smuWired;
    private bool _measurementWired;

    public EngineHost()
    {
        Engine = new HorizonEngine();
        Process = new ProcessService(Engine);
        Log = new LogHub();
        Telemetry = new TelemetryStore();
        Settings = new AppSettingsStore();
        Users = new UserStore();

        // Station identity for DUT traceability: the configured id, or the Windows
        // machine name when none is set. One machine = one station.
        var stationId = string.IsNullOrWhiteSpace(Settings.Current.StationId)
            ? Environment.MachineName
            : Settings.Current.StationId;
        Session = new Session(Process, stationId);

        // Capture both logging surfaces from construction so startup-time
        // (InitializeAsync) lines are not lost before MainWindow exists.
        // (Facets — Engine.Smu etc. — do not exist until InitializeAsync; the
        // SMU streaming hook is wired in InitializeAndConnectAsync.)
        Engine.LogEmitted += Log.Add;
        Process.LogEmitted += Log.Add;
    }

    private void OnSmuSampled(SmuSample s)
    {
        Telemetry.Push($"{s.Device}.CH{s.Channel}.current", s.Current, s.Timestamp);
        Telemetry.Push($"{s.Device}.CH{s.Channel}.voltage", s.Voltage, s.Timestamp);
    }

    private void OnMeasurementChanged(MeasurementChanged m)
    {
        // The Keithley 6482 picoammeter polls continuously once connected, so
        // every poll update streams straight into telemetry — no Start needed.
        // Channels are 0-based in the state; telemetry keys are 1-based.
        var now = DateTime.UtcNow;
        var channels = m.State.Channels;
        for (int i = 0; i < channels.Count; i++)
            Telemetry.Push($"{m.Device}.CH{i + 1}.current", channels[i], now);
    }

    /// <summary>
    /// Brings the engine up: config load + device build, then connect-all.
    /// <see cref="HorizonEngine.InitializeAsync"/> does synchronous config file
    /// I/O, so it is pushed to a background thread — the UI thread never blocks.
    /// </summary>
    public async Task InitializeAndConnectAsync(IProgress<string> progress, CancellationToken ct = default)
    {
        progress.Report("Initializing engine…");
        await Task.Run(async () => await Engine.InitializeAsync(ct).ConfigureAwait(false), ct)
                  .ConfigureAwait(true);

        // Facets exist only after InitializeAsync — wire the SMU streaming hook
        // now. Each sample fills two telemetry channels (current + voltage),
        // keyed "{device}.CH{n}.{quantity}".
        Engine.Smu.Sampled += OnSmuSampled;
        _smuWired = true;

        // Same telemetry pipeline for the measure-only Keithley 6482 — driven
        // by the facet's per-poll StateChanged event rather than a stream.
        Engine.Measurement.StateChanged += OnMeasurementChanged;
        _measurementWired = true;

        await Engine.ConnectAllAsync(progress, ct).ConfigureAwait(true);
    }

    /// <summary>
    /// Detaches engine event hooks, then runs the engine's ordered hardware
    /// shutdown. <paramref name="progress"/> receives a line per teardown step —
    /// the <see cref="ShutdownWindow"/> binds it. Idempotent: the engine's
    /// shutdown is state-gated, so a second call (the App.OnExit safety net)
    /// no-ops.
    /// </summary>
    public async Task ShutdownAsync(IProgress<string>? progress = null)
    {
        Engine.LogEmitted -= Log.Add;
        Process.LogEmitted -= Log.Add;
        if (_smuWired)
            Engine.Smu.Sampled -= OnSmuSampled;
        if (_measurementWired)
            Engine.Measurement.StateChanged -= OnMeasurementChanged;
        await Engine.ShutdownAsync(progress).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await ShutdownAsync().ConfigureAwait(false);
}
