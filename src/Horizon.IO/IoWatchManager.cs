using Horizon.Common;
using Horizon.IO.Models;
using Serilog;

namespace Horizon.IO;

/// <summary>
/// The "io_watch" domain: watches IO inputs and drives IO outputs on a configured
/// edge (a switch driving an output pin). Polls each bound input on a timer,
/// debounces, and toggles the bound output. Pure IO — it never reaches into other
/// facets (cf. <c>SafetyWatchManager</c>, which is a cross-facet supervisor).
/// </summary>
public sealed class IoWatchManager : IDisposable
{
    private readonly EziIOManager _ioManager;
    private readonly List<IoBinding> _bindings = new();
    private readonly Dictionary<string, bool> _previousInputs = new();
    private readonly Dictionary<string, bool> _outputStates = new();
    private readonly Dictionary<string, DateTime> _lastFired = new();
    private int _pollIntervalMs = 50;
    private bool _enabled = true;

    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;
    private Task? _watchTask;
    private bool _disposed;

    /// <summary>Raised when a binding fires: the binding and the new output state.</summary>
    public event Action<IoBinding, bool>? BindingFired;

    public bool IsRunning => _watchTask != null && !_watchTask.IsCompleted;
    public bool Enabled => _enabled;
    public int PollIntervalMs => _pollIntervalMs;
    public IReadOnlyList<IoBinding> Bindings => _bindings;

    public IoWatchManager(EziIOManager ioManager) => _ioManager = ioManager;

    public void Configure(IoWatchConfig config)
    {
        _enabled = config.Enabled;
        _pollIntervalMs = config.PollingIntervalMs > 0 ? config.PollingIntervalMs : 50;
        _bindings.Clear();
        _bindings.AddRange(config.Bindings.Where(b => b.Enabled));
        _previousInputs.Clear();
        _outputStates.Clear();
        _lastFired.Clear();
    }

    public int LoadConfig(string? configDir = null)
    {
        configDir ??= ConfigLoader.GetConfigDirectory();
        var config = ConfigLoader.LoadJson<IoWatchConfig>(configDir, "io_watch");
        if (config == null) return 0;
        Configure(config);
        return _bindings.Count;
    }

    public void StartWatching()
    {
        StopWatching();
        if (!_enabled || _bindings.Count == 0) return;
        _cts = new CancellationTokenSource();
        _timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_pollIntervalMs));
        _watchTask = WatchLoopAsync(_cts.Token);
        Log.Information("[IoWatch] Watching {Count} binding(s) at {Interval} ms", _bindings.Count, _pollIntervalMs);
    }

    public void StopWatching()
    {
        _cts?.Cancel();
        _timer?.Dispose();
        _timer = null;
        try { _watchTask?.Wait(2000); } catch { }
        _cts?.Dispose();
        _cts = null;
        _watchTask = null;
    }

    private async Task WatchLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _timer != null)
        {
            try
            {
                if (!await _timer.WaitForNextTickAsync(ct))
                    break;
            }
            catch (OperationCanceledException) { break; }

            foreach (var b in _bindings)
            {
                if (ct.IsCancellationRequested) break;

                bool current = ReadPin(b.Input.Device, b.Input.PinName);
                var key = $"{b.Input.Device}:{b.Input.PinName}";

                if (_previousInputs.TryGetValue(key, out bool prev))
                {
                    bool edge = b.Trigger switch
                    {
                        IoEdge.Rising => !prev && current,
                        IoEdge.Falling => prev && !current,
                        IoEdge.Both => prev != current,
                        _ => false
                    };

                    if (edge && PassesDebounce(b))
                        FireBinding(b);
                }

                _previousInputs[key] = current;
            }
        }
    }

    /// <summary>True if enough time has elapsed since this binding last fired.</summary>
    private bool PassesDebounce(IoBinding b)
    {
        if (b.DebounceMs <= 0) return true;
        var now = DateTime.UtcNow;
        if (_lastFired.TryGetValue(b.Name, out var last)
            && (now - last).TotalMilliseconds < b.DebounceMs)
            return false;
        _lastFired[b.Name] = now;
        return true;
    }

    /// <summary>Toggles the binding's output pin and reports the new state.</summary>
    private void FireBinding(IoBinding b)
    {
        var okey = $"{b.Output.Device}:{b.Output.PinName}";
        bool next = !(_outputStates.TryGetValue(okey, out var cur) && cur);

        var err = _ioManager.SetOutputByName(b.Output.Device, b.Output.PinName, next);
        if (err != IOError.Success)
        {
            Log.Warning("[IoWatch] {Name}: SetOutput {Out} failed ({Err})", b.Name, okey, err);
            return;
        }

        _outputStates[okey] = next;
        Log.Information("[IoWatch] {Name}: {Out} = {State}", b.Name, okey, next ? "ON" : "OFF");
        BindingFired?.Invoke(b, next);
    }

    private bool ReadPin(string deviceName, string pinName)
    {
        var device = _ioManager.GetDevice(deviceName);
        if (device == null || !device.IsConnected) return false;

        var pin = device.GetPinByName(pinName);
        if (pin == null) return false;

        if (device.ReadInputs(out uint inputs) != IOError.Success) return false;
        return (inputs & (1u << pin.Value)) != 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopWatching();
    }
}
