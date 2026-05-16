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

## Task 1 — `UserPromptHandler` contract (the headline task)

Add to `IProcessService`:

```csharp
public sealed record UserPrompt(string Title, string Message);

// IProcessService:
Func<UserPrompt, CancellationToken, Task<bool>>? UserPromptHandler { get; set; }
```

- New step op **`flow.askUser`** — params `title`, `message` (drop `blocking`;
  Layer 2 prompts are always blocking).
- `WorkflowRunner` needs the handler injected (ctor delegate, like the
  named-position recall delegate). On an `askUser` step:
  `await UserPromptHandler(new UserPrompt(title, message), ct)`.
  - `true`  → continue.
  - `false` → stop the run as a **controlled abort** ("operator declined") —
    not an error result; distinct message in the final `Result`.
  - handler **null** → step fails: `"no user-prompt handler registered"`
    (a program with `Ask User` cannot run truly headless).
- `ProcessService.RunAsync` passes `UserPromptHandler` into the `WorkflowRunner`.
- **Console reference handler**: wire `UserPromptHandler` in `ConsoleApp` to
  `AnsiConsole.Confirm($"{title} — {message}")`. This is the reference
  implementation every future front-end mirrors (WPF → modal dialog;
  web → SignalR round-trip).

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

## Task 3 — migrate "Probe SLED and PIC" as a `.hwf`

Source: `bobcat_engine/.../config/SIPHOG888/recipes/Probe SLED and PIC.json`.
Recreate as `%APPDATA%\Horizon\workflow\products\SIPHOG888\processes\Probe SLED and PIC.hwf`
using the new ops. Preserve `enabled` flags (the disabled UV/Dispenser/PP-tool
retracts, the disabled `Move to Position` steps, the disabled trailing DUT /
laser-off block all stay disabled). Gives Phase 4 a real program with two live
`Ask User` confirmation points to test against.

## Task 4 — remaining original Phase 4 scope (after the above)

- Breakpoints / pause / step-by-step — same pause-and-yield pattern as
  `askUser`; the front-end decides when to resume.
- Plugin system + program builder UI — likely defer to a later phase.

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

Fold the `UserPromptHandler` contract into `docs/layer2-process-module-design.md`
§5 (`IProcessService`) and add it to the Phase 4 scope there.
