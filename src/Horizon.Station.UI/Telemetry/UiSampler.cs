using System.Windows.Threading;

namespace Horizon.Station.UI.Telemetry;

/// <summary>
/// The single shared display-cadence timer (see <c>docs/layer3-ui-design.md</c>
/// §7.3). Hardware data is produced on the engine's background poll loops; the
/// UI pulls a cached snapshot at this fixed cadence rather than reacting to
/// every producer event. One sampler is shared by all view models — the legacy
/// "a DispatcherTimer per panel" pattern is deliberately not repeated.
///
/// Runs at <see cref="DispatcherPriority.Background"/> so rendering and input
/// always take precedence — the UI thread can never be starved by sampling.
/// </summary>
public sealed class UiSampler
{
    private readonly DispatcherTimer _timer;

    /// <summary>Raised on the UI thread once per interval while started.</summary>
    public event Action? Tick;

    public UiSampler(TimeSpan interval)
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = interval };
        _timer.Tick += (_, _) => Tick?.Invoke();
    }

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();
}
