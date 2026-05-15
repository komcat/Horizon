namespace Horizon.Engine.Polling;

/// <summary>
/// Reusable polling scaffold. Wraps a <see cref="PeriodicTimer"/> and invokes an
/// async tick callback at a fixed interval until stopped or disposed.
/// Hardware facets (Step 2 onward) use one of these per domain/device to poll
/// status and raise <see cref="HorizonEngine.RaiseStatusChanged"/>.
/// Not started by anything in the skeleton.
/// </summary>
public sealed class PollLoop : IAsyncDisposable
{
    private readonly TimeSpan _interval;
    private readonly Func<CancellationToken, Task> _onTick;
    private CancellationTokenSource? _cts;
    private Task? _runner;

    public PollLoop(string name, TimeSpan interval, Func<CancellationToken, Task> onTick)
    {
        Name = name;
        _interval = interval;
        _onTick = onTick;
    }

    /// <summary>Diagnostic name for this loop (e.g. the domain it polls).</summary>
    public string Name { get; }

    public bool IsRunning => _runner is { IsCompleted: false };

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _runner = RunAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;

        _cts.Cancel();
        try
        {
            if (_runner is not null)
                await _runner.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected on stop
        }

        _cts.Dispose();
        _cts = null;
        _runner = null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                await _onTick(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected on stop
        }
    }

    public async ValueTask DisposeAsync()
        => await StopAsync().ConfigureAwait(false);
}
