using Horizon.Common;

namespace Horizon.Safety.Models;

public enum SafetySeverity
{
    Info,
    Warning,
    Critical,
    Emergency
}

public enum TriggerEdge
{
    Rising,
    Falling,
    Both
}

public record WatchedInput
{
    public string DeviceName { get; init; } = string.Empty;
    public string PinName { get; init; } = string.Empty;
    public TriggerEdge Trigger { get; init; } = TriggerEdge.Both;
    public string AlertTitle { get; init; } = string.Empty;
    public string AlertMessage { get; init; } = string.Empty;
    public bool IsCritical { get; init; }
    public bool Enabled { get; init; } = true;
}

public record SafetyEvent
{
    public ulong Id { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public SafetySeverity Severity { get; init; }
    public string Source { get; init; } = string.Empty;
    public string EventType { get; init; } = string.Empty;
    public ErrorCode ErrorCode { get; init; }
    public string Message { get; init; } = string.Empty;
    public bool Acknowledged { get; set; }
}

public record SafetyWatchConfig
{
    public int PollIntervalMs { get; init; } = 50;
    public List<WatchedInput> WatchedInputs { get; init; } = new();
    /// <summary>
    /// Enforcement actions to execute on safety trigger. Null = passive mode (backward compatible).
    /// </summary>
    public EmergencyActions? EmergencyActions { get; init; }
}

/// <summary>
/// Defines what enforcement actions to take when a safety input triggers.
/// </summary>
public record EmergencyActions
{
    public bool StopMotion { get; init; }
    public bool RetractPneumatics { get; init; }
    public bool CancelExecution { get; init; }
    public bool BlockNewExecution { get; init; } = true;
    public IOKillAction? KillIO { get; init; }
}

/// <summary>
/// Defines which IO output pins to turn off on safety trigger.
/// </summary>
public record IOKillAction
{
    public bool KillAll { get; init; }
    public List<IOPinTarget> Pins { get; init; } = new();
}

public record IOPinTarget
{
    public string DeviceName { get; init; } = string.Empty;
    public string PinName { get; init; } = string.Empty;
}
