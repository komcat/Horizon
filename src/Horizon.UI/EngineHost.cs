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

    private bool _smuWired;

    public EngineHost()
    {
        Engine = new HorizonEngine();
        Process = new ProcessService(Engine);
        Log = new LogHub();
        Telemetry = new TelemetryStore();

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

        await Engine.ConnectAllAsync(progress, ct).ConfigureAwait(true);
    }

    public async ValueTask DisposeAsync()
    {
        Engine.LogEmitted -= Log.Add;
        Process.LogEmitted -= Log.Add;
        if (_smuWired)
            Engine.Smu.Sampled -= OnSmuSampled;
        await Engine.DisposeAsync().ConfigureAwait(false);
    }
}
