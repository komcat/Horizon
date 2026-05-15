namespace Horizon.Engine;

/// <summary>Severity of an engine <see cref="LogEntry"/>.</summary>
public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error
}

/// <summary>
/// A single log line emitted by the engine via <see cref="IHorizonEngine.LogEmitted"/>.
/// Front-ends subscribe and route as they wish — there is no ILogSink interface.
/// </summary>
public sealed record LogEntry(
    LogLevel Level,
    DateTimeOffset Timestamp,
    string Source,
    string Message);
