namespace Horizon.Engine;

/// <summary>A safety-watched IO input.</summary>
public sealed record WatchedInputInfo(
    string Device,
    string Pin,
    string TriggerEdge,
    bool IsCritical);

/// <summary>Live status of a motion interlock rule.</summary>
public sealed record InterlockRuleInfo(
    string Name,
    bool Enabled,
    bool Satisfied,
    string Target,
    string BlockReason);

/// <summary>Point-in-time view of the safety domain.</summary>
public sealed record SafetyState(
    bool IsWatching,
    bool FlagRaised,
    bool CriticalRaised,
    bool InterlocksEnabled,
    bool OverrideMode);

/// <summary>Raised on <see cref="ISafetyFacet.SafetyTripped"/> when a watched input fires.</summary>
public sealed record SafetyTrip(string Title, string Message, bool IsCritical);
