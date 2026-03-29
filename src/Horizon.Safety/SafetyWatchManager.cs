using System.Text.Json;
using System.Text.Json.Serialization;
using Horizon.Common;
using Horizon.IO;
using Horizon.IO.Models;
using Horizon.Safety.Models;
using Serilog;

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

    // --- Enforcement ---
    private EmergencyActions? _emergencyActions;
    private Action? _stopAllMotion;
    private Action? _retractAllPneumatics;
    private Action<IOKillAction>? _killIO;
    private Action? _releaseStepGates;

    // Shared CTS for execution cancellation — UI panels link into this
    private CancellationTokenSource? _executionCts;
    private readonly object _ctsLock = new();

    public event Action<string, string, bool>? SafetyTriggered;
    public event Action? SafetyCleared;

    public bool IsSafetyFlagRaised { get; private set; }
    public bool IsCriticalFlagRaised { get; private set; }

    /// <summary>Exposes the loaded enforcement config for external checks (e.g. SafetyGate).</summary>
    public EmergencyActions? EmergencyActionsConfig => _emergencyActions;

    /// <summary>True when the watch loop is actively running.</summary>
    public bool IsRunning => _watchTask != null && !_watchTask.IsCompleted;

    /// <summary>Read-only snapshot of the current watched inputs.</summary>
    public IReadOnlyList<WatchedInput> WatchedInputs => _watchedInputs;

    public int PollIntervalMs => _pollIntervalMs;

    public SafetyWatchManager(EziIOManager ioManager, SafetyEventLog eventLog)
    {
        _ioManager = ioManager;
        _eventLog = eventLog;
    }

    /// <summary>
    /// Injects enforcement actions from the Core/AppContext layer.
    /// Called after construction but before StartWatching().
    /// </summary>
    public void SetEnforcementActions(
        Action? stopAllMotion,
        Action? retractAllPneumatics,
        Action<IOKillAction>? killIO,
        Action? releaseStepGates)
    {
        _stopAllMotion = stopAllMotion;
        _retractAllPneumatics = retractAllPneumatics;
        _killIO = killIO;
        _releaseStepGates = releaseStepGates;
    }

    /// <summary>
    /// Creates a linked CancellationTokenSource that cancels when either
    /// the panel's own token or the safety watchdog triggers.
    /// The caller is responsible for disposing the returned CTS.
    /// </summary>
    public CancellationTokenSource LinkExecutionCts(CancellationToken panelToken)
    {
        lock (_ctsLock)
        {
            // Create a fresh internal CTS for this execution session
            _executionCts?.Dispose();
            _executionCts = new CancellationTokenSource();
            return CancellationTokenSource.CreateLinkedTokenSource(panelToken, _executionCts.Token);
        }
    }

    public void Configure(SafetyWatchConfig config)
    {
        _pollIntervalMs = config.PollIntervalMs > 0 ? config.PollIntervalMs : 50;
        _watchedInputs.Clear();
        _watchedInputs.AddRange(config.WatchedInputs.Where(w => w.Enabled));
        _previousStates.Clear();
        _emergencyActions = config.EmergencyActions;
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

        // Reset the execution CTS so new executions can link fresh tokens
        lock (_ctsLock)
        {
            _executionCts?.Dispose();
            _executionCts = null;
        }

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

                        ExecuteEmergencyActions(key, input.IsCritical);

                        SafetyTriggered?.Invoke(input.AlertTitle, input.AlertMessage, input.IsCritical);
                    }
                }

                _previousStates[key] = currentState;
            }
        }
    }

    private void ExecuteEmergencyActions(string source, bool isCritical)
    {
        if (_emergencyActions == null) return; // Passive mode

        Log.Warning("[Safety] Executing emergency actions for {Source} (critical={IsCritical})", source, isCritical);

        // 1. Cancel execution CTS — fastest, signals all linked tokens immediately
        if (_emergencyActions.CancelExecution)
        {
            try
            {
                lock (_ctsLock)
                {
                    _executionCts?.Cancel();
                }
                // Release step gates to prevent deadlock if engine is paused at breakpoint
                _releaseStepGates?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Safety] Error cancelling execution");
            }
        }

        // 2. Stop all motion — highest physical priority
        if (_emergencyActions.StopMotion)
        {
            try { _stopAllMotion?.Invoke(); }
            catch (Exception ex)
            {
                Log.Error(ex, "[Safety] Error stopping motion");
            }
        }

        // 3. Kill IO outputs
        if (_emergencyActions.KillIO != null)
        {
            try { _killIO?.Invoke(_emergencyActions.KillIO); }
            catch (Exception ex)
            {
                Log.Error(ex, "[Safety] Error killing IO outputs");
            }
        }

        // 4. Retract pneumatics
        if (_emergencyActions.RetractPneumatics)
        {
            try { _retractAllPneumatics?.Invoke(); }
            catch (Exception ex)
            {
                Log.Error(ex, "[Safety] Error retracting pneumatics");
            }
        }

        _eventLog.LogEvent(SafetySeverity.Emergency, source, "EnforcementExecuted",
            "Emergency actions completed", ErrorCode.SafetyEnforcementExecuted);
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

    /// <summary>Add a watched input and restart watching.</summary>
    public void AddWatchedInput(WatchedInput input)
    {
        _watchedInputs.Add(input);
        _previousStates.Remove($"{input.DeviceName}:{input.PinName}");
    }

    /// <summary>Remove a watched input by device + pin name and restart watching.</summary>
    public bool RemoveWatchedInput(string deviceName, string pinName)
    {
        var idx = _watchedInputs.FindIndex(w =>
            w.DeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase) &&
            w.PinName.Equals(pinName, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return false;
        var key = $"{_watchedInputs[idx].DeviceName}:{_watchedInputs[idx].PinName}";
        _watchedInputs.RemoveAt(idx);
        _previousStates.Remove(key);
        return true;
    }

    /// <summary>Save the current config to safety_watch.json.</summary>
    public void SaveConfig(string? configDir = null)
    {
        configDir ??= ConfigLoader.GetConfigDirectory();
        Directory.CreateDirectory(configDir);

        var config = new SafetyWatchConfig
        {
            PollIntervalMs = _pollIntervalMs,
            WatchedInputs = _watchedInputs.ToList(),
            EmergencyActions = _emergencyActions
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

        var path = Path.Combine(configDir, "safety_watch.json");
        File.WriteAllText(path, JsonSerializer.Serialize(config, options));
        Log.Information("[Safety] Config saved to {Path}", path);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopWatching();
        lock (_ctsLock)
        {
            _executionCts?.Dispose();
            _executionCts = null;
        }
    }
}
