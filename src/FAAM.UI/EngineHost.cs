using FAAM.UI.Process;
using FAAM.UI.Telemetry;
using Horizon.Common;
using Horizon.Engine;
using Horizon.Workflow;

namespace FAAM.UI;

/// <summary>
/// FAAM's Layer 3 composition root — a second operator head on the same Horizon
/// engine stack. Owns the single <see cref="HorizonEngine"/> (Layer 1) and
/// <see cref="ProcessService"/> (Layer 2) for the whole application; the role
/// Horizon.UI's own <c>EngineHost</c> plays.
///
/// One process, one engine, one host. FAAM.UI and Horizon.UI must not run at the
/// same time against the same rig — whichever launches owns the hardware SDKs.
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

    /// <summary>The single product FAAM operates under (Layer 2 positions are product-scoped).</summary>
    public const string ProductName = "FAAM";

    /// <summary>Motion device whose legacy teach points migrate into the product.</summary>
    private const string TeachDevice = "gantry-main";

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
        // (InitializeAsync) lines are not lost before the shell exists.
        Engine.LogEmitted += Log.Add;
        Process.LogEmitted += Log.Add;

        // FAAM is a single-purpose head, but Layer 2 scopes named positions to a
        // product — so ensure ours exists and is active, and fold any legacy
        // faam_teach_points.json into it. Pure file I/O; no engine connection.
        EnsureProductLoaded();
    }

    /// <summary>
    /// Guarantees the FAAM product exists and is the active one, then performs a
    /// one-time, idempotent import of legacy <c>faam_teach_points.json</c> points
    /// that are not already present in the product.
    /// </summary>
    private void EnsureProductLoaded()
    {
        if (!Process.Products.Contains(ProductName, StringComparer.OrdinalIgnoreCase))
            Process.CreateProduct(ProductName);
        if (!string.Equals(Process.ActiveProduct, ProductName, StringComparison.OrdinalIgnoreCase))
            Process.LoadProduct(ProductName);

        var legacy = new TeachPointStore().All;
        var imported = 0;
        foreach (var (name, pose) in legacy)
        {
            if (Process.GetPosition(TeachDevice, name) != null)
                continue;   // already migrated / edited — never clobber
            Process.SetPosition(TeachDevice, name, pose, velocity: 0);
            imported++;
        }
        if (imported > 0)
            Log.Add(new LogEntry(LogLevel.Info, DateTimeOffset.Now, "FAAM",
                $"Migrated {imported} legacy teach point(s) into product '{ProductName}'."));
    }

    private void OnSmuSampled(SmuSample s)
    {
        Telemetry.Push($"{s.Device}.CH{s.Channel}.current", s.Current, s.Timestamp);
        Telemetry.Push($"{s.Device}.CH{s.Channel}.voltage", s.Voltage, s.Timestamp);
    }

    private void OnMeasurementChanged(MeasurementChanged m)
    {
        // The Keithley 6482 picoammeter polls continuously once connected, so
        // every poll update streams straight into telemetry. Channels are 0-based
        // in the state; telemetry keys are 1-based.
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

        // Facets exist only after InitializeAsync — wire the telemetry streaming
        // hooks now. SMU fills current + voltage; the measure-only Keithley 6482
        // is driven by its per-poll StateChanged event.
        Engine.Smu.Sampled += OnSmuSampled;
        _smuWired = true;
        Engine.Measurement.StateChanged += OnMeasurementChanged;
        _measurementWired = true;

        await Engine.ConnectAllAsync(progress, ct).ConfigureAwait(true);
    }

    /// <summary>
    /// Detaches engine event hooks, then runs the engine's ordered hardware
    /// shutdown. <paramref name="progress"/> receives a line per teardown step.
    /// Idempotent: the engine's shutdown is state-gated, so a second call (the
    /// App.OnExit safety net) no-ops.
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
