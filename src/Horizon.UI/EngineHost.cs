using Horizon.Engine;
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

    public EngineHost()
    {
        Engine = new HorizonEngine();
        Process = new ProcessService(Engine);
        Log = new LogHub();

        // Capture both logging surfaces from construction so startup-time
        // (InitializeAsync) lines are not lost before MainWindow exists.
        Engine.LogEmitted += Log.Add;
        Process.LogEmitted += Log.Add;
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

        await Engine.ConnectAllAsync(progress, ct).ConfigureAwait(true);
    }

    public async ValueTask DisposeAsync()
    {
        Engine.LogEmitted -= Log.Add;
        Process.LogEmitted -= Log.Add;
        await Engine.DisposeAsync().ConfigureAwait(false);
    }
}
