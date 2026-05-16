using Horizon.Common;

namespace Horizon.Engine;

/// <summary>
/// Safety domain of the engine — the IO watchdog and motion interlocks.
/// Read + acknowledge + override only; enforcement (stop motion, retract
/// pneumatics, kill IO) and interlock arbitration are internal to the engine
/// and cannot be bypassed by a caller.
/// </summary>
public interface ISafetyFacet
{
    /// <summary>Point-in-time safety + interlock state.</summary>
    SafetyState GetState();

    /// <summary>The configured safety-watched IO inputs.</summary>
    IReadOnlyList<WatchedInputInfo> WatchedInputs { get; }

    /// <summary>Live status of every motion interlock rule.</summary>
    IReadOnlyList<InterlockRuleInfo> GetInterlockStatuses();

    /// <summary>Whether interlock override (bypass) is active.</summary>
    bool OverrideMode { get; }

    /// <summary>Enables or disables interlock override.</summary>
    Task<Result> SetOverrideAsync(bool enabled);

    /// <summary>Clears a raised safety flag so execution can resume.</summary>
    Task<Result> AcknowledgeAsync();

    /// <summary>Raised when a watched input trips the safety watchdog.</summary>
    event Action<SafetyTrip>? SafetyTripped;

    /// <summary>Raised when a raised safety flag is acknowledged/cleared.</summary>
    event Action? SafetyCleared;
}
