using Horizon.Common;
using Horizon.Core.Interlock;
using Horizon.Safety;

namespace Horizon.Engine;

/// <summary>
/// Safety facet — wraps the kept <see cref="SafetyWatchManager"/> (IO watchdog)
/// and <see cref="InterlockService"/> (pre-motion interlocks). Enforcement
/// delegates and the motion interlock check are wired by the engine; this
/// facet exposes read state, acknowledge, and the override toggle only.
/// </summary>
internal sealed class SafetyFacet : ISafetyFacet, IDisposable
{
    private readonly SafetyWatchManager _watch;
    private readonly InterlockService _interlock;
    private readonly Action<HardwareStatusChanged> _emitStatus;
    private readonly Action<LogLevel, string> _log;

    public SafetyFacet(
        SafetyWatchManager watch,
        InterlockService interlock,
        Action<HardwareStatusChanged> emitStatus,
        Action<LogLevel, string> log)
    {
        _watch = watch;
        _interlock = interlock;
        _emitStatus = emitStatus;
        _log = log;

        _watch.SafetyTriggered += OnSafetyTriggered;
        _watch.SafetyCleared += OnSafetyCleared;
    }

    public SafetyState GetState() => new(
        _watch.IsRunning,
        _watch.IsSafetyFlagRaised,
        _watch.IsCriticalFlagRaised,
        _interlock.Enabled,
        _interlock.OverrideMode);

    public IReadOnlyList<WatchedInputInfo> WatchedInputs =>
        _watch.WatchedInputs
            .Select(w => new WatchedInputInfo(w.DeviceName, w.PinName, w.Trigger.ToString(), w.IsCritical))
            .ToList();

    public IReadOnlyList<InterlockRuleInfo> GetInterlockStatuses() =>
        _interlock.GetAllRuleStatuses()
            .Select(r => new InterlockRuleInfo(
                r.RuleName, r.RuleEnabled, r.IsSatisfied, r.TargetDescription, r.BlockReason))
            .ToList();

    public bool OverrideMode => _interlock.OverrideMode;

    public Task<Result> SetOverrideAsync(bool enabled)
    {
        _interlock.OverrideMode = enabled;
        return Task.FromResult(Result.Ok());
    }

    public Task<Result> AcknowledgeAsync()
    {
        _watch.AcknowledgeSafetyFlag();
        _log(LogLevel.Info, "Safety flag acknowledged.");
        return Task.FromResult(Result.Ok());
    }

    public void Dispose()
    {
        _watch.SafetyTriggered -= OnSafetyTriggered;
        _watch.SafetyCleared -= OnSafetyCleared;
    }

    public event Action<SafetyTrip>? SafetyTripped;
    public event Action? SafetyCleared;

    // --- internals ---

    private void OnSafetyTriggered(string title, string message, bool isCritical)
    {
        _log(isCritical ? LogLevel.Error : LogLevel.Warning,
            $"SAFETY {(isCritical ? "CRITICAL" : "TRIP")}: {title} — {message}");
        SafetyTripped?.Invoke(new SafetyTrip(title, message, isCritical));
        _emitStatus(new HardwareStatusChanged("Safety", "Watchdog"));
    }

    private void OnSafetyCleared()
    {
        SafetyCleared?.Invoke();
        _emitStatus(new HardwareStatusChanged("Safety", "Watchdog"));
    }
}
