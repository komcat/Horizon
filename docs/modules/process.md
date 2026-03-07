# Horizon.Process

Process execution engine with command dispatching, plugin system, expression evaluation, and program persistence. Defines reusable process step templates and executes them against hardware managers.

## Dependencies

- **Horizon.Common** (project reference)
- No hardware references — process commands are routed to managers via `object?` references at runtime

## Files

### Core Framework

| File | Description |
|------|-------------|
| `ProcessEngine.cs` | Execution engine with exclusive lock, breakpoints, timeouts |
| `CommandDispatcher.cs` | Routes commands to handlers by manager type |
| `ProcessContext.cs` | Thread-safe runtime variable dictionary |
| `ProcessBuilder.cs` | Fluent API for constructing step sequences |
| `ExpressionEvaluator.cs` | Recursive-descent arithmetic parser |
| `ProcessPluginRegistry.cs` | Reflection-based plugin discovery and registry |

### Interfaces

| File | Description |
|------|-------------|
| `Interfaces/ICommandHandler.cs` | Handler contract for command execution |
| `Interfaces/IProcessPlugin.cs` | Plugin contract for step templates |

### Models

| File | Description |
|------|-------------|
| `Models/ProcessStep.cs` | Step type hierarchy (Action, Loop, Conditional, Marker) |
| `Models/ProcessProgram.cs` | Persistent program model (.hproc files) |
| `Models/PluginParamDef.cs` | Plugin parameter metadata |
| `Models/CommandMetadata.cs` | Handler introspection metadata |

### Command Handlers (8)

| File | Manager Type | Description |
|------|-------------|-------------|
| `Handlers/MotionCommandHandler.cs` | `motion` | Move, home, stop, position queries |
| `Handlers/IOCommandHandler.cs` | `io` | Set/read IO pins |
| `Handlers/PneumaticCommandHandler.cs` | `pneumatic` | Extend/retract slides |
| `Handlers/CameraCommandHandler.cs` | `camera` | Capture, exposure, grabbing control |
| `Handlers/CurrentMeasurementCommandHandler.cs` | `currentmeasurement` | Read current channels |
| `Handlers/LaserControllerCommandHandler.cs` | `lasercontroller` | TEC/laser control |
| `Handlers/SystemCommandHandler.cs` | `system` | Wait, log with interpolation |
| `Handlers/ContextCommandHandler.cs` | `context` | Variable get/set/calculate |

### Plugins (16)

| File | Category | Description |
|------|----------|-------------|
| `Plugins/MoveToPositionPlugin.cs` | Motion | Move to named position |
| `Plugins/MoveRelativePlugin.cs` | Motion | Single-axis relative move |
| `Plugins/MoveRelativeMultiAxisPlugin.cs` | Motion | X/Y/Z simultaneous move |
| `Plugins/MoveRelativeThetaPlugin.cs` | Motion | U/V/W rotational move |
| `Plugins/SetVelocityPlugin.cs` | Motion | Speed setting |
| `Plugins/HexScanPlugin.cs` | Motion | Coarse-to-fine hexapod scan |
| `Plugins/SetExposurePlugin.cs` | Camera | Exposure time in us |
| `Plugins/SetGainPlugin.cs` | Camera | Gain value |
| `Plugins/SetAutoExposurePlugin.cs` | Camera | Auto exposure on/off |
| `Plugins/SetAutoGainPlugin.cs` | Camera | Auto gain on/off |
| `Plugins/CaptureToFilePlugin.cs` | Camera | Capture image to file |
| `Plugins/WaitDelayPlugin.cs` | System | Pause in ms |
| `Plugins/LoopStartPlugin.cs` | System | Begin loop (count=0 for infinite) |
| `Plugins/LoopEndPlugin.cs` | System | End loop |
| `Plugins/CommentPlugin.cs` | System | Visual-only annotation |
| `Plugins/CalculateExpressionPlugin.cs` | Variables | Evaluate expression to variable |

## ProcessEngine

### Device Registry

Built via reflection on `AppContext` properties:
```
BuildDeviceRegistry(IAppContext context)
  → iterates public properties
  → finds manager types (CameraManager, UnifiedMotionManager, etc.)
  → registers each device with its manager instance
```

Additional handlers can be registered via `RegisterHandler(ICommandHandler)` and `RegisterDevice(name, manager, managerType)`.

### Exclusive Execution Lock

Only one process can execute at a time:
- `TryAcquire(owner)` — returns `true` if lock acquired
- `Release(owner)` — releases the lock (must match owner)

### Step Execution

```
ExecuteAsync(steps, context, ct)
  For each step:
    1. Fire OnStepStarted(index, device, method)
    2. Check breakpoints → ManualResetEventSlim gate
    3. Handle step type:
       - ActionStep → CommandDispatcher.Dispatch(...)
       - LoopStep → recursive execution with OnLoopIteration
       - ConditionalStep → evaluate condition → then/else branch
       - StepMarkerStep → skip (visualization only)
    4. Apply timeout (default 60s per action)
    5. Fire OnStepCompleted(index, success)
```

### Callbacks

| Callback | Parameters | When |
|----------|------------|------|
| `OnStepStarted` | `(int index, string device, string method)` | Before each step |
| `OnStepCompleted` | `(int index, bool success)` | After each step |
| `OnLoopIteration` | `(int index, int current, int total)` | Each loop iteration |

### Step-by-Step Mode

When enabled, the engine pauses after each step via `ManualResetEventSlim`. Call `Continue()` to advance to the next step. Breakpoints can be set on specific step indices.

## CommandDispatcher

Simple router: matches `managerType` string (case-insensitive) to an `ICommandHandler`:

```csharp
dispatcher.Dispatch("motion", manager, "Hexapod-1", "movetoabs", params, context)
  → MotionCommandHandler.ExecuteCommand(manager, "Hexapod-1", "movetoabs", params, context)
```

8 registered types: `motion`, `io`, `pneumatic`, `camera`, `currentmeasurement`, `lasercontroller`, `system`, `context`.

## ProcessContext

Thread-safe runtime variable storage using `lock` and `Dictionary<string, string>` with `OrdinalIgnoreCase` keys.

### Typed Access

| Method | Description |
|--------|-------------|
| `SetValue(key, string)` | Store string value |
| `SetValue(key, float)` | Store numeric as string |
| `SetValue(key, bool)` | Store boolean as string |
| `GetNumeric(key, default?)` | Parse to double |
| `GetString(key, default?)` | Get raw string |
| `GetBoolean(key, default?)` | Parse "true"/"false" |
| `HasValue(key)` | Check existence |
| `GetAllKeys()` | List all variable names |
| `GetSnapshot()` | Sorted `(key, value, type)` list for debugging |

### Variable Lifetime

Created fresh per execution. Discarded when execution completes. Not persisted.

## ProcessBuilder

Fluent API for constructing step sequences:

```csharp
var builder = new ProcessBuilder();
builder.AddAction("Hexapod-1", "movetoabs", new() { ["axis"] = "X", ["position"] = "10.0" });
builder.BeginLoop(5);
  builder.AddAction("Keithley-1", "read_current", new() { ["channel"] = "1" });
  builder.AddAction("system", "wait", new() { ["duration"] = "100" });
builder.EndLoop();
builder.BeginIf("reading", ComparisonType.GreaterThan, 1e-6);
  builder.AddAction("system", "log", new() { ["message"] = "Current exceeded threshold" });
builder.EndIf();
var steps = builder.Build();
```

Stack-based block context management for proper nesting of loops and conditionals.

## ExpressionEvaluator

Lightweight `ref struct` recursive-descent parser on `ReadOnlySpan<char>`.

### Supported Syntax

- Operators: `+`, `-`, `*`, `/` (with proper precedence)
- Parentheses: `(expr)`
- Unary: `+expr`, `-expr`
- Literals: integer, float, scientific notation (e.g., `1.5e-3`)
- Variables: any identifier → resolved from `ProcessContext`
- Division by zero: returns IEEE 754 `Infinity`

### Three Usage Patterns

1. **Plugin**: `CalculateExpressionPlugin` — evaluates expression, stores result in context variable
2. **Parameter prefix**: `expr:distance * 0.5` — any handler parameter with `expr:` prefix computes inline
3. **Log interpolation**: `{=posA - posB}` — in SystemCommandHandler log messages, `{=EXPRESSION}` evaluates and embeds

## ICommandHandler

```csharp
public interface ICommandHandler
{
    bool ExecuteCommand(
        object? manager,           // manager instance (null for system/context)
        string deviceName,
        string methodName,
        Dictionary<string, string> parameters,
        out string errorMessage,
        ProcessContext context
    );
    string ManagerType { get; }    // e.g., "motion", "io", "system"
}
```

## IProcessPlugin

```csharp
public interface IProcessPlugin
{
    string Id { get; }
    string DisplayName { get; }
    string Category { get; }
    string Description { get; }
    IReadOnlyList<PluginParamDef> Parameters { get; }
    void BuildSteps(ProcessBuilder builder, IReadOnlyDictionary<string, string> paramValues);
}
```

## PluginParamDef

Parameter metadata for auto-generating UI forms:

```csharp
record PluginParamDef(
    string Name,
    string DisplayName,
    PluginParamType Type,      // Text, Number, Boolean, Enum, FilePath,
                               // MotionDevice, CameraDevice, NamedPosition, Axis
    string Category,
    bool Required,
    string Default,
    string[] Options,          // for Enum type
    string UnitSuffix,         // e.g., "us", "mm", "deg"
    double? Min, Max, Step,
    int? DecimalPlaces,
    string? DependsOnDevice    // chained dropdown (e.g., NamedPosition depends on MotionDevice)
);
```

### Dependent Parameters

`DependsOnDevice` chains dropdowns: when a MotionDevice parameter changes, NamedPosition and Axis parameters refresh their available options from the selected device.

## MotionCommandHandler Methods

The most complex handler, supporting parameter resolution patterns:

| Method | Description |
|--------|-------------|
| `movetonamedposition` | Move to preset/teach position |
| `movetoabs` | Single-axis absolute move |
| `movetoabs6` | 6-axis simultaneous move |
| `moverelative` | Single-axis relative move |
| `moverelativemulti` | Multi-axis relative (starts all non-blocking, waits per-axis) |
| `homeaxis` | Reference finding |
| `stopaxis` / `stopall` / `stopalldevices` | Emergency stop |
| `getposition` | Read position to context variable |
| `setvelocity` | Set speed |
| `ismoving` / `isontarget` | Status queries |
| `waitforposition` | Block until on-target |

Parameter values support three prefixes:
- Literal: `"10.5"` — used as-is
- `context:KEY` — fetched from `ProcessContext`
- `expr:EXPRESSION` — evaluated by `ExpressionEvaluator`

## ProcessProgram Persistence

Programs serialize as `.hproc` JSON files in `%APPDATA%\Horizon\processes\`:

```json
{
  "Name": "Alignment Sequence",
  "Steps": [
    {
      "PluginId": "move_to_position",
      "Parameters": { "device": "Hexapod-1", "target_position": "Home" },
      "Enabled": true,
      "Uid": "a1b2c3d4",
      "CustomName": "Go Home"
    }
  ]
}
```

Last-opened file tracked via `.lastprogram` file.

## Thread Model

- `ProcessEngine`: exclusive lock (`TryAcquire`/`Release`) — only one execution at a time
- `ProcessContext`: `lock`-based dictionary access
- `ExpressionEvaluator`: `ref struct` (stack-only, no allocations)
- Command handlers execute on the calling thread (typically thread pool via `Task.Run`)
