# Horizon.Safety

Safety watchdog subsystem that polls IO input pins for edge transitions and raises safety alerts. Provides event logging with file persistence.

## Dependencies

- **Horizon.Common** (project reference)
- **Horizon.IO** (project reference) — reads input pins via `EziIOManager`

## Files

| File | Description |
|------|-------------|
| `SafetyWatchManager.cs` | Async polling loop with edge detection |
| `SafetyEventLog.cs` | Thread-safe event logger with JSONL file persistence |
| `Models/SafetyTypes.cs` | Enums, config records, event records |

## SafetyWatchManager

Polls configured IO input pins at a fixed interval (default 50ms) and detects edge transitions.

### Configuration

Loaded from `safety_watch.json`:

```json
{
  "PollIntervalMs": 50,
  "WatchedInputs": [
    {
      "DeviceName": "IO-Module-1",
      "PinName": "DoorSensor",
      "Trigger": "Falling",
      "AlertTitle": "Door Opened",
      "AlertMessage": "Safety enclosure door is open",
      "IsCritical": true,
      "Enabled": true
    }
  ]
}
```

### Edge Detection

For each watched input, tracks previous state and detects edges:

| TriggerEdge | Fires When |
|-------------|------------|
| `Rising` | Pin goes `false → true` |
| `Falling` | Pin goes `true → false` |
| `Both` | Pin changes in either direction |

### Safety Flags

Two flags set on trigger:
- `IsSafetyFlagRaised` — set on any trigger
- `IsCriticalFlagRaised` — set only when `WatchedInput.IsCritical` is true

`AcknowledgeSafetyFlag()` clears both and fires `SafetyCleared` event.

### Events

- `SafetyTriggered(string alertTitle, string alertMessage, bool isCritical)` — fired on each edge detection
- `SafetyCleared` — fired on acknowledge

### Lifecycle

```
Configure(SafetyWatchConfig) → set watched inputs + poll interval
LoadConfig(configDir) → load from JSON + Configure
StartWatching() → create PeriodicTimer + launch async watch task
StopWatching() → cancel timer, wait up to 2s for task
```

### Pin Reading

Reads via `EziIOManager.GetDevice(deviceName)`:
1. Get device, check connected
2. Resolve pin name to index
3. Read all inputs via `ReadInputs(out uint)`
4. Extract bit: `(inputs & (1u << pin)) != 0`

### Disposal

Implements `IDisposable`: `Dispose()` → `StopWatching()`.

## SafetyEventLog

Thread-safe event logger with in-memory buffer and JSONL file persistence.

### In-Memory Buffer

`LinkedList<SafetyEvent>` with maximum 1000 events. Oldest removed on overflow.

### File Persistence

Appends events as JSONL (one JSON object per line) to `%APPDATA%\Horizon\logs\safety_events.jsonl`.

File rotation: when file exceeds 10MB, renames with timestamp suffix and starts a new file.

All file I/O silently catches exceptions — safety monitoring continues even if logging fails.

### Event Methods

| Method | Description |
|--------|-------------|
| `LogEvent(severity, source, eventType, message, code)` | Core logging, returns `SafetyEvent` |
| `LogSafetyTrigger(source, title, message, isCritical)` | Convenience: maps to Critical/Warning severity |
| `LogProcessStart(processName)` | Info-level process tracking |
| `LogProcessStop(processName, success)` | Info or Warning depending on success |
| `GetRecentEvents(count)` | Returns last N events (newest first) |
| `GetEventsSince(since)` | Filter by timestamp |
| `AcknowledgeEvent(eventId)` | Mark event as acknowledged |

### SafetyEvent Record

```csharp
record SafetyEvent
{
    ulong Id;                   // auto-incremented
    DateTimeOffset Timestamp;
    SafetySeverity Severity;    // Info, Warning, Critical, Emergency
    string Source;              // e.g., "IO-Module-1:DoorSensor"
    string EventType;          // e.g., "safety_trigger"
    ErrorCode ErrorCode;
    string Message;
    bool Acknowledged;          // mutable (settable)
}
```

## Critical Dependency

**IO must be connected before Safety starts watching.** In the connection sequence, IO devices connect in Phase 1, and `SafetyWatchManager.StartWatching()` runs in Phase 2. The watch loop returns `false` for any pin on a disconnected device.

## Thread Model

- `SafetyWatchManager`: single async task via `PeriodicTimer` — all pin reads are sequential within the loop
- `SafetyEventLog`: all methods must be called from the same thread (or externally synchronized)
- Events (`SafetyTriggered`, `SafetyCleared`) fire from the watch loop thread — UI consumers must marshal to dispatcher
