using Horizon.Common;
using Horizon.IO;
using Horizon.IO.Models;
using Horizon.Safety.Models;

namespace Horizon.Safety;

public sealed class SafetyWatchManager : IDisposable
{
    private readonly EziIOManager _ioManager;
    private readonly SafetyEventLog _eventLog;
    private readonly List<WatchedInput> _watchedInputs = new();
    private readonly Dictionary<string, bool> _previousStates = new();
    private int _pollIntervalMs = 50;

    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;
    private Task? _watchTask;
    private bool _disposed;

    public event Action<string, string, bool>? SafetyTriggered;
    public event Action? SafetyCleared;

    public bool IsSafetyFlagRaised { get; private set; }
    public bool IsCriticalFlagRaised { get; private set; }

    public SafetyWatchManager(EziIOManager ioManager, SafetyEventLog eventLog)
    {
        _ioManager = ioManager;
        _eventLog = eventLog;
    }

    public void Configure(SafetyWatchConfig config)
    {
        _pollIntervalMs = config.PollIntervalMs > 0 ? config.PollIntervalMs : 50;
        _watchedInputs.Clear();
        _watchedInputs.AddRange(config.WatchedInputs.Where(w => w.Enabled));
        _previousStates.Clear();
    }

    public int LoadConfig(string? configDir = null)
    {
        configDir ??= ConfigLoader.GetConfigDirectory();
        var config = ConfigLoader.LoadJson<SafetyWatchConfig>(configDir, "safety_watch");
        if (config == null) return 0;
        Configure(config);
        return _watchedInputs.Count;
    }

    public void StartWatching()
    {
        StopWatching();
        _cts = new CancellationTokenSource();
        _timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_pollIntervalMs));
        _watchTask = WatchLoopAsync(_cts.Token);
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

    public void AcknowledgeSafetyFlag()
    {
        IsSafetyFlagRaised = false;
        IsCriticalFlagRaised = false;
        SafetyCleared?.Invoke();
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

            foreach (var input in _watchedInputs)
            {
                if (ct.IsCancellationRequested) break;

                bool currentState = ReadPin(input.DeviceName, input.PinName);
                var key = $"{input.DeviceName}:{input.PinName}";

                if (_previousStates.TryGetValue(key, out bool prevState))
                {
                    bool shouldTrigger = input.Trigger switch
                    {
                        TriggerEdge.Rising => !prevState && currentState,
                        TriggerEdge.Falling => prevState && !currentState,
                        TriggerEdge.Both => prevState != currentState,
                        _ => false
                    };

                    if (shouldTrigger)
                    {
                        IsSafetyFlagRaised = true;
                        if (input.IsCritical)
                            IsCriticalFlagRaised = true;

                        _eventLog.LogSafetyTrigger(
                            key, input.AlertTitle, input.AlertMessage, input.IsCritical);
                        SafetyTriggered?.Invoke(input.AlertTitle, input.AlertMessage, input.IsCritical);
                    }
                }

                _previousStates[key] = currentState;
            }
        }
    }

    private bool ReadPin(string deviceName, string pinName)
    {
        var device = _ioManager.GetDevice(deviceName);
        if (device == null || !device.IsConnected) return false;

        var pin = device.GetPinByName(pinName);
        if (pin == null) return false;

        var err = device.ReadInputs(out uint inputs);
        if (err != IOError.Success) return false;

        return (inputs & (1u << pin.Value)) != 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopWatching();
    }
}
