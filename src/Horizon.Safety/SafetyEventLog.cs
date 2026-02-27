using System.Text.Json;
using Horizon.Common;
using Horizon.Safety.Models;

namespace Horizon.Safety;

public sealed class SafetyEventLog
{
    private const int MaxEvents = 1000;
    private const long MaxFileSize = 10 * 1024 * 1024; // 10MB

    private readonly object _lock = new();
    private readonly LinkedList<SafetyEvent> _events = new();
    private ulong _nextId = 1;
    private readonly string? _logDirectory;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public SafetyEventLog(string? logDirectory = null)
    {
        _logDirectory = logDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Horizon", "logs");
    }

    public SafetyEvent LogEvent(SafetySeverity severity, string source, string eventType, string message, ErrorCode code = ErrorCode.Success)
    {
        var evt = new SafetyEvent
        {
            Id = _nextId++,
            Timestamp = DateTimeOffset.UtcNow,
            Severity = severity,
            Source = source,
            EventType = eventType,
            ErrorCode = code,
            Message = message
        };

        lock (_lock)
        {
            _events.AddLast(evt);
            while (_events.Count > MaxEvents)
                _events.RemoveFirst();
        }

        WriteToFile(evt);
        return evt;
    }

    public SafetyEvent LogSafetyTrigger(string source, string title, string message, bool isCritical)
    {
        var severity = isCritical ? SafetySeverity.Critical : SafetySeverity.Warning;
        var code = isCritical ? ErrorCode.SafetyCriticalFault : ErrorCode.SafetyInputTriggered;
        return LogEvent(severity, source, title, message, code);
    }

    public SafetyEvent LogProcessStart(string processName)
    {
        return LogEvent(SafetySeverity.Info, "ProcessEngine", "ProcessStart", $"Process started: {processName}");
    }

    public SafetyEvent LogProcessStop(string processName, bool success)
    {
        var severity = success ? SafetySeverity.Info : SafetySeverity.Warning;
        return LogEvent(severity, "ProcessEngine", "ProcessStop", $"Process stopped: {processName} ({(success ? "OK" : "FAILED")})");
    }

    public List<SafetyEvent> GetRecentEvents(int count)
    {
        lock (_lock)
        {
            return _events.Reverse().Take(count).ToList();
        }
    }

    public List<SafetyEvent> GetEventsSince(DateTimeOffset since)
    {
        lock (_lock)
        {
            return _events.Where(e => e.Timestamp >= since).ToList();
        }
    }

    public bool AcknowledgeEvent(ulong eventId)
    {
        lock (_lock)
        {
            var evt = _events.FirstOrDefault(e => e.Id == eventId);
            if (evt == null) return false;
            evt.Acknowledged = true;
            return true;
        }
    }

    private void WriteToFile(SafetyEvent evt)
    {
        if (_logDirectory == null) return;
        try
        {
            Directory.CreateDirectory(_logDirectory);
            var filePath = Path.Combine(_logDirectory, "safety_events.jsonl");

            // Rotate if too large
            if (File.Exists(filePath) && new FileInfo(filePath).Length > MaxFileSize)
            {
                var rotatedPath = Path.Combine(_logDirectory,
                    $"safety_events_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}.jsonl");
                File.Move(filePath, rotatedPath);
            }

            var json = JsonSerializer.Serialize(evt, JsonOptions);
            File.AppendAllText(filePath, json + Environment.NewLine);
        }
        catch { /* Logging should never throw */ }
    }
}
