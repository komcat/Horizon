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
}
