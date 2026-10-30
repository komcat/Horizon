using System.Collections.Concurrent;
using FAAM.UI.Process;
using FAAM.UI.Views;
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

    /// <summary>
    /// Process-wide busy signal. The alignment panels (Hill Climber, U-correction,
    /// FAU pre-align) all drive the shared hexapod, so they bracket their runs here;
    /// the process panel blocks its step / DUT-start buttons while it is busy.
    /// </summary>
    public ActivityGate Activity { get; } = new();

    /// <summary>
    /// Publish/subscribe seam for operator actions (manual jogs + each section of the
    /// "Start correction" chain). Publishers push an <see cref="OperatorActivity"/>; the
    /// <see cref="DutActivityMirror"/> records them into the open DUT run. This is how
    /// operator work reaches the DUT log without hardwiring logging into each routine.
    /// </summary>
    public OperatorActivityHub Activities { get; } = new();

    /// <summary>
    /// Fans one alignment capture/confirm prompt out to every open camera surface — the
    /// Run-page main feed and any camera pop-out (<see cref="Views.DisplayWindow"/>) — so the
    /// operator can Capture / Apply / Cancel from whichever view they are looking at. The Run
    /// page passes this hub to the alignment panel; surfaces register themselves as they open.
    /// </summary>
    public CapturePromptHub CapturePrompts { get; } = new();

    /// <summary>Open blocking operator prompts — the tower light blinks yellow while any is up.</summary>
    public OperatorAttention Attention { get; } = new();

    /// <summary>
    /// Raised when the safety watchdog trips (E-Stop / watched input). Layer 3 run
    /// surfaces (the Run-page process panel, alignment panels) subscribe to cancel
    /// their in-flight run tokens so the operator sequence cannot issue the next move
    /// after the engine halts the current one. The physical motion stop is enforced
    /// by the engine (safety_watch.json <c>emergencyActions.stopMotion</c>) and
    /// re-issued here as a backstop in <see cref="OnSafetyTripped"/>.
    /// </summary>
    public event Action<SafetyTrip>? SafetyAbort;

    /// <summary>Mirrors published operator activities into the open interactive DUT run.</summary>
    private readonly DutActivityMirror _dutActivityMirror;

    // OEE: the Run-page steps and the manual alignment / scan panels hold the activity gate
    // while they execute — that counts as "running" alongside Layer 2's own program runs.
    private readonly object _oeeLock = new();
    private IDisposable? _oeeActivity;

    /// <summary>The station tower light — follows the OEE state (see <see cref="TowerLight"/>).</summary>
    public TowerLight Tower { get; }

    private bool _smuWired;
    private bool _measurementWired;
    private bool _cameraWired;
    private bool _safetyWired;
    // Last-seen connected state per camera — drives the connect edge that forces
    // auto-exposure on (so it fires on first connect AND on Reconnect, once each).
    private readonly ConcurrentDictionary<string, bool> _cameraConnected = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The head this station runs as — its own pages and process identity (<see cref="IStationHead"/>).</summary>
    public IStationHead Head { get; }

    public EngineHost(IStationHead head)
    {
        Head = head;
        // The head's own Engineer blocks + help, before any editor is built.
        ViewModels.OpCatalog.Register(head.Ops);
        ViewModels.OpCatalogHelp.Register(head.OpHelp);
        Engine = new HorizonEngine();
        Process = new ProcessService(Engine);
        Log = new LogHub();
        Telemetry = new TelemetryStore();
        Settings = new AppSettingsStore();
        Users = new UserStore();

        // The sign-in gate + traceability stamp. It reads the machine number and
        // credential string from Settings at each login (not once here), so editing the
        // station's identity takes effect on the next sign-in rather than a restart.
        Session = new Session(Process, Settings);

        // Capture both logging surfaces from construction so startup-time
        // (InitializeAsync) lines are not lost before the shell exists.
        Engine.LogEmitted += Log.Add;
        Process.LogEmitted += Log.Add;

        // Mirror operator actions (jogs + correction-sequence sections) into the open DUT run.
        // FAAM installs the Sagnac signal probe on the Run page, where the SiPhOG client lives.
        _dutActivityMirror = new DutActivityMirror(Activities, Process, Log);
        Activity.Changed += OnActivityChangedForOee;
        Tower = new TowerLight(Engine, Process.Oee, Log, () => Attention.Waiting || CapturePrompts.IsShown);

        // FAAM is a single-purpose head, but Layer 2 scopes named positions to a
        // product — so ensure ours exists and is active, and fold any legacy
        // faam_teach_points.json into it. Pure file I/O; no engine connection.
        EnsureProductLoaded();
    }

    /// <summary>
    /// Guarantees the head's default product exists and is the active one, then lets the head
    /// run its one-time product setup (<see cref="IStationHead.OnProductReady"/>).
    /// </summary>
    private void EnsureProductLoaded()
    {
        var product = Head.DefaultProductName;
        if (!Process.Products.Contains(product, StringComparer.OrdinalIgnoreCase))
            Process.CreateProduct(product);
        if (!string.Equals(Process.ActiveProduct, product, StringComparison.OrdinalIgnoreCase))
            Process.LoadProduct(product);
        Head.OnProductReady(this);
    }

    /// <summary>
    /// Installs (or clears) the live-signal probe used to enrich jog activities in the DUT
    /// log. FAAM calls this from the Run page with a SiPhOG Sagnac reader; the generic
    /// activity/DUT layer stays signal-agnostic. Pass null to clear.
    /// </summary>
    public void SetActivitySignalProbe(Func<KeyValuePair<string, double>?>? probe)
        => _dutActivityMirror.SignalProbe = probe;

    private void OnSmuSampled(SmuSample s)
    {
        Telemetry.Push($"{s.Device}.CH{s.Channel}.current", s.Current, s.Timestamp);
        Telemetry.Push($"{s.Device}.CH{s.Channel}.voltage", s.Voltage, s.Timestamp);
    }

    /// <summary>
    /// On the rising edge of a camera's connection (disconnected → connected),
    /// deliberately forces auto-exposure ON. Fires once per connect — at startup
    /// bring-up and again after a Reconnect.
    /// </summary>
    private void OnCameraStateChanged(CameraStateChanged e)
    {
        var wasConnected = _cameraConnected.TryGetValue(e.Camera, out var prev) && prev;
        _cameraConnected[e.Camera] = e.State.Connected;
        if (e.State.Connected && !wasConnected)
            _ = ForceAutoExposureAsync(e.Camera);
    }

    /// <summary>
    /// Applies <c>AutoExposure = true</c> to a camera, preserving its other exposure
    /// settings (gain, frame rate). Failures are logged, never fatal.
    /// </summary>
    private async Task ForceAutoExposureAsync(string camera)
    {
        try
        {
            var current = Engine.Camera.GetState(camera).Exposure;
            var result = await Engine.Camera
                .ApplyExposureAsync(camera, current with { AutoExposure = true })
                .ConfigureAwait(false);
            Log.Add(new LogEntry(
                result.IsSuccess ? LogLevel.Info : LogLevel.Warning, DateTimeOffset.Now, "FAAM",
                result.IsSuccess
                    ? $"Camera {camera}: auto-exposure enabled on connect."
                    : $"Camera {camera}: enabling auto-exposure failed — {result.Message}"));
        }
        catch (Exception ex)
        {
            Log.Add(new LogEntry(LogLevel.Warning, DateTimeOffset.Now, "FAAM",
                $"Camera {camera}: enabling auto-exposure errored — {ex.Message}"));
        }
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
    /// Safety-watchdog trip handler. Two responsibilities, in order:
    /// (1) raise <see cref="SafetyAbort"/> so Layer 3 run surfaces cancel their in-flight
    /// run tokens — this is the missing link that lets a tripped E-Stop actually halt the
    /// running step <em>sequence</em> (not just the current move); and
    /// (2) re-issue a physical stop on every motion device as a backstop, so motion still
    /// stops even if the engine's own enforcement config is ever cleared.
    /// </summary>
    private void OnSafetyTripped(SafetyTrip trip)
    {
        Log.Add(new LogEntry(LogLevel.Warning, DateTimeOffset.Now, "Safety",
            $"Safety trip: {trip.Title} — {trip.Message} Cancelling running sequences + stopping motion."));

        // (1) Cancel running operator sequences first, so they cannot issue the next move.
        try { SafetyAbort?.Invoke(trip); }
        catch (Exception ex)
        {
            Log.Add(new LogEntry(LogLevel.Error, DateTimeOffset.Now, "Safety",
                $"Safety abort notify failed — {ex.Message}"));
        }

        // (2) Physical backstop (fire-and-forget; never block the watchdog thread).
        _ = StopAllMotionBackstopAsync();
    }

    /// <summary>Stops every motion device — the Layer 3 backstop for a safety trip.</summary>
    private async Task StopAllMotionBackstopAsync()
    {
        try
        {
            foreach (var device in Engine.Motion.DeviceNames)
            {
                try { await Engine.Motion.StopAsync(device).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    Log.Add(new LogEntry(LogLevel.Error, DateTimeOffset.Now, "Safety",
                        $"E-Stop: stopping {device} failed — {ex.Message}"));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Add(new LogEntry(LogLevel.Error, DateTimeOffset.Now, "Safety",
                $"E-Stop motion backstop errored — {ex.Message}"));
        }
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

        // Force auto-exposure ON whenever a camera connects. Subscribed before
        // ConnectAllAsync so the connect edges raised during bring-up are caught.
        Engine.Camera.StateChanged += OnCameraStateChanged;
        _cameraWired = true;

        // Safety: on a watchdog trip, cancel running operator sequences (via SafetyAbort)
        // and re-issue a physical motion stop. Subscribed before ConnectAllAsync so a trip
        // during hardware bring-up is caught.
        Engine.Safety.SafetyTripped += OnSafetyTripped;
        _safetyWired = true;

        await Engine.ConnectAllAsync(progress, ct).ConfigureAwait(true);

        // Safety net: force auto-exposure on any camera already connected by the
        // time bring-up finishes, in case its connect edge fired before we saw it.
        // Gated by _cameraConnected so it never double-applies with the event hook.
        foreach (var cam in Engine.Camera.CameraIds)
        {
            if (Engine.Camera.IsConnected(cam) && !(_cameraConnected.TryGetValue(cam, out var c) && c))
            {
                _cameraConnected[cam] = true;
                await ForceAutoExposureAsync(cam).ConfigureAwait(true);
            }
        }

        // The C-887 forgets its pivot point at power-down (back to 0,0,0) — put the
        // configured one back on every connected PI hexapod.
        foreach (var device in Engine.Motion.DeviceNames)
        {
            if (!IsPiHexapod(device) || !Engine.Motion.IsConnected(device)) continue;
            progress.Report($"Motion: checking {device} pivot point…");
            await EnsureHexapodPivotAsync(device).ConfigureAwait(true);
        }
    }

    /// <summary>True for a PI motion device (the hexapods) — the ones with a pivot point and FRF referencing.</summary>
    public bool IsPiHexapod(string device)
        => string.Equals(Engine.Motion.GetDevice(device)?.ControllerType, "PI", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the hexapod's pivot point and, when it differs from the configured one
    /// (<see cref="HexapodPivotDefaults"/> — typically 0,0,0 after a controller power-down),
    /// writes the configured value. Logged either way; failures are never fatal.
    /// </summary>
    public async Task<Result> EnsureHexapodPivotAsync(string device)
    {
        var wanted = HexapodPivotDefaults.Get(device);
        try
        {
            var read = await Engine.Motion.GetPivotAsync(device).ConfigureAwait(false);
            if (read.IsSuccess && read.Value.Matches(wanted))
            {
                Log.Add(new LogEntry(LogLevel.Info, DateTimeOffset.Now, "FAAM",
                    $"{device}: pivot point {read.Value} already as configured."));
                return Result.Ok($"Pivot already {wanted}.");
            }

            var before = read.IsSuccess ? read.Value.ToString() : $"unreadable ({read.Message})";
            var set = await Engine.Motion.SetPivotAsync(device, wanted).ConfigureAwait(false);
            Log.Add(new LogEntry(set.IsSuccess ? LogLevel.Info : LogLevel.Warning, DateTimeOffset.Now, "FAAM",
                set.IsSuccess
                    ? $"{device}: pivot point was {before} — set to configured {wanted}."
                    : $"{device}: pivot point is {before}; setting {wanted} failed — {set.Message}"));
            return set.IsSuccess ? Result.Ok($"Pivot set to {wanted} (was {before}).") : set;
        }
        catch (Exception ex)
        {
            Log.Add(new LogEntry(LogLevel.Warning, DateTimeOffset.Now, "FAAM",
                $"{device}: pivot point check errored — {ex.Message}"));
            return Result.Fail(ErrorCode.OperationFailed, ex.Message);
        }
    }

    /// <summary>
    /// Detaches engine event hooks, then runs the engine's ordered hardware
    /// shutdown. <paramref name="progress"/> receives a line per teardown step.
    /// Idempotent: the engine's shutdown is state-gated, so a second call (the
    /// App.OnExit safety net) no-ops.
    /// </summary>
    public async Task ShutdownAsync(IProgress<string>? progress = null)
    {
        Activity.Changed -= OnActivityChangedForOee;
        await Tower.StopAsync().ConfigureAwait(false);               // lamps off while IO is still connected
        await Process.Oee.EndSessionAsync().ConfigureAwait(false);   // station offline (OEE)
        Engine.LogEmitted -= Log.Add;
        Process.LogEmitted -= Log.Add;
        _dutActivityMirror.Dispose();
        if (_smuWired)
            Engine.Smu.Sampled -= OnSmuSampled;
        if (_measurementWired)
            Engine.Measurement.StateChanged -= OnMeasurementChanged;
        if (_cameraWired)
            Engine.Camera.StateChanged -= OnCameraStateChanged;
        if (_safetyWired)
            Engine.Safety.SafetyTripped -= OnSafetyTripped;
        await Engine.ShutdownAsync(progress).ConfigureAwait(false);
    }

    /// <summary>Marks the station online for OEE and starts the tower light — called once the shell is up.</summary>
    public void StartOeeSession()
    {
        Process.Oee.StartSession("FAAM.UI", Session.StationId);
        Tower.Start();
    }

    private void OnActivityChangedForOee()
    {
        lock (_oeeLock)
        {
            var busy = Activity.ProcessBusy || Activity.AlignmentBusy;
            if (busy && _oeeActivity == null)
            {
                _oeeActivity = Process.Oee.BeginProduction("activity", Activity.ProcessBusy ? "run-page" : "alignment");
            }
            else if (!busy && _oeeActivity != null)
            {
                _oeeActivity.Dispose();
                _oeeActivity = null;
            }
        }
    }

    public async ValueTask DisposeAsync() => await ShutdownAsync().ConfigureAwait(false);
}
