# Layer 2 Phase 4 — Interactive Execution & the User-Prompt Round-Trip

> Task file for the next session. Phases 1–3 of `Horizon.Workflow` are complete
> and verified on the bench. This file scopes Phase 4.

## Why Phase 4 changed shape

The original Phase 4 idea was "breakpoints / pause / step-by-step + plugin
system + builder UI". Migrating the bobcat program **"Probe SLED and PIC"**
surfaced a *new* requirement that belongs in Phase 4 and reframes it:

The legacy program contains **`Ask User`** steps — a blocking yes/no
confirmation:

```json
{ "type": "Ask User",
  "parameters": { "blocking": "true", "title": "Probe SLED?",
                  "message": "Please confirm to continue" } }
```

A headless runner cannot show a dialog. The running program must call **back up**
to its consumer (Layer 3 UI, or the test console) and **block mid-run** until an
answer arrives. This makes Layer 2 ↔ Layer 3 **bidirectional** — and it *is* the
core UI requirement: a front-end is an active participant the runner can suspend
on, not a passive viewer of `StepEvent` / `LogEmitted`.

`Ask User` and breakpoints are the same pattern (runner pauses, yields control
to the front-end), so Phase 4 = **interactive execution**.

## Task 1 — `IOperatorPrompt` contract (the headline task)

Model the user-prompt as an **interface** the front-end implements and registers
on `IProcessService` — not a raw `Func`. It is *not* an engine facet: the engine
facets (`Motion`, `IO`, …) are called **down** into; this is an inverted
dependency the runner calls **up** into. Layer 3 *provides* it, Layer 2
*consumes* it. So it lives as a settable property on `IProcessService`, the same
slot the front-end fills.

```csharp
namespace Horizon.Workflow;

/// <summary>
/// Layer 3 → Layer 2 callback contract: the runner suspends on these calls
/// and resumes when the front-end answers. The reference implementation is the
/// console; WPF / web mirror it. Implementations may marshal threads or do an
/// async round-trip internally — Layer 2 only sees the awaitable result.
/// </summary>
public interface IOperatorPrompt
{
    /// <summary>Blocking yes/no confirmation. Cancellation arrives via <paramref name="ct"/>.</summary>
    Task<bool> ConfirmAsync(string title, string message, CancellationToken ct);

    // Room to grow without touching IProcessService — add when needed:
    // Task<string?> InputTextAsync(string title, string message, CancellationToken ct);
    // Task<int?>    ChooseAsync(string title, IReadOnlyList<string> options, CancellationToken ct);
}

// IProcessService:
/// <summary>Front-end-supplied operator-prompt handler. Null = no handler;
/// a program with an askUser step cannot then run.</summary>
IOperatorPrompt? OperatorPrompt { get; set; }
```

**Why an interface, not a `Func`:** one interface absorbs future prompt shapes
(text input, multi-choice) without adding a new `IProcessService` property per
shape. For Phase 4's single `askUser` op only `ConfirmAsync` is needed; the
other methods are stubs/notes until a prompt op requires them.

**Why an awaitable method, not an `event`:** a fire-and-forget `event` (like
`StepEvent` / `LogEmitted`) cannot return a value or be awaited — blocking on one
would need a correlation ID + a separate `SubmitResponse` method + a
`TaskCompletionSource` in the runner. Keep that machinery in Layer 3: a web
front-end implements `ConfirmAsync` by *internally* doing the event + TCS
round-trip. Layer 2 stays a plain `await`.

- New step op **`flow.askUser`** — params `title`, `message` (drop `blocking`;
  Layer 2 prompts are always blocking).
- `WorkflowRunner` needs the prompt injected (ctor parameter, like the
  named-position recall delegate). On an `askUser` step:
  `await prompt.ConfirmAsync(title, message, ct)`.
  - `true`  → continue.
  - `false` → stop the run as a **controlled abort** ("operator declined") —
    not an error result; distinct message in the final `Result`.
  - prompt **null** → step fails: `"no operator-prompt handler registered"`
    (a program with `Ask User` cannot run truly headless).
- `ProcessService.RunAsync` passes `OperatorPrompt` into the `WorkflowRunner`.
- **Console reference handler**: implement `IOperatorPrompt` in `ConsoleApp`
  (`ConfirmAsync` → `AnsiConsole.Confirm($"{title} — {message}")`) and assign it
  to `OperatorPrompt`. This is the reference implementation every future
  front-end mirrors (WPF → modal dialog on the dispatcher thread;
  web → SignalR round-trip behind `ConfirmAsync`).

## Task 2 — new step ops for "Probe SLED and PIC"

The enabled steps of the program need these ops (all map to existing facets —
no engine changes expected):

| Legacy `type` | New op | Engine call |
|---|---|---|
| Set Auto Gain / Set Gain / Set Exposure | `camera.setExposure` (or 3 ops) | `Camera.ApplyExposureAsync` |
| Set Output **Name** / Clear Output Name | `io.setOutputByName` | `IO.SetOutputByNameAsync` (exists) |
| Set TEC Temp | `laser.setTemperature` | `Laser.SetTemperatureAsync` + TEC on |
| Set Laser Current | `laser.setCurrent` | `Laser.SetLaserCurrentAsync` + laser on |
| SMU Read Current | `measurement.readCurrent` | read channel → `WorkflowContext` var (`captureAs`) |
| Read DataStore | `measurement.read` | `Measurement.GetState` channel → log |
| Log Message | `flow.log` | emit a `LogEntry` |
| Ask User | `flow.askUser` | **Task 1** |
| Wait | `flow.wait` | exists |
| Set Velocity / Parallel Move / Move to Position | exist (Phase 3) | — |
| Slide Retract / Slide Retract All | exist (Phase 3) | — |

**Deferred — DUT ops** (`DUT Create` / `DUT Insert String` / `DUT Load`):
device-under-test result records (per-serial-number production data). A larger
Layer 2 feature. *All DUT steps in this program are `enabled:false`*, so they do
not block migration — skip for now, plan a later "DUT / result data" phase.

> **Design requirement for the DUT phase (from bench feedback):** during
> product development you often run a program without ever doing a "DUT start"
> (`DUT Create`). Today a `DUT Insert` with no active DUT errors out, which is
> annoying. The DUT phase must make `DUT Insert` **fall back to the last DUT
> that was run** when no DUT is currently active — write into that record
> instead of failing. Only error if there has *never* been a DUT. Implies the
> DUT store tracks a "last/current DUT" handle that survives a program run.

## Task 3 — migrate "Probe SLED and PIC" as a `.hwf`

Source: `bobcat_engine/.../config/SIPHOG888/recipes/Probe SLED and PIC.json`.
Recreate as `%APPDATA%\Horizon\workflow\products\SIPHOG888\processes\Probe SLED and PIC.hwf`
using the new ops. Preserve `enabled` flags (the disabled UV/Dispenser/PP-tool
retracts, the disabled `Move to Position` steps, the disabled trailing DUT /
laser-off block all stay disabled). Gives Phase 4 a real program with two live
`Ask User` confirmation points to test against.

## Task 4 — breakpoints + pause + step-by-step

Done — the same pause-and-yield pattern as `askUser`, but driven by front-end
controls instead of a prompt:

- **`PauseController`** — an async gate (`TaskCompletionSource`-based) shared
  between the `IProcessService` controls and the `WorkflowRunner`. Created
  per-run by `ProcessService.RunAsync`, so a finished/cancelled run never
  leaves stale paused state.
- **`WorkflowRunner`** awaits `PauseController.WaitAsync(step.Breakpoint, ct)`
  before every step. It blocks while paused — from `Pause()`, a one-shot
  `Step()`, or a `Breakpoint` on the upcoming step. Cancellation flows through
  `Task.WaitAsync(ct)`.
- **`WorkflowStep.Breakpoint`** — new bool; set it in the `.hwf` to pause
  before that step.
- **`IProcessService`** — `Pause()` / `Resume()` / `Step()` / `IsPaused` and an
  `event Action<bool>? PausedChanged` (raised on breakpoint-triggered pauses so
  the front-end can react).
- **Console** — `[p]` pause, `[s]` step, `[r]` resume, `[c]`/Esc cancel during
  a run; prints a `‖ paused` line on `PausedChanged`.

Deferred to a later phase: the plugin system + program builder UI.

## Verification (bench, SIPHOG888 loaded)

1. `Horizon.Workflow.Console` → load **SIPHOG888**.
2. Process programs → **Probe SLED and PIC** appears in the list.
3. Run it → camera exposure/gain set, slides retract, velocities set, parallel
   move runs, gantry moves to `seesled`.
4. At **"Probe SLED?"** the run **pauses** and the console shows a yes/no
   confirm. Answer **yes** → run continues; TEC 25 °C, laser 250 mA, move to
   `seepic`.
5. At **"Probe PIC"** confirm again. Answer **no** → run stops cleanly as
   "operator declined" (not an error).
6. Re-run, answer yes through to the end → SMU read, dark-current capture,
   move to `SN view`; run ends OK (disabled DUT/laser-off block skipped).

Success = a program with user-confirmation steps runs through `IProcessService`,
the runner pausing for and resuming on front-end answers — the bidirectional
Layer 2 ↔ Layer 3 contract demonstrated end to end.

## Doc to update

Fold the `IOperatorPrompt` contract into `docs/layer2-process-module-design.md`
§5 (`IProcessService`) and add it to the Phase 4 scope there.
