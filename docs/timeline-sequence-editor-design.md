# Timeline (Gantt) sequence editor + parallel execution

Status: **Phase 1 in progress.** Layer 2 `flow.parallel` (fork-join + fail-fast +
lane-tagged events) is implemented and bench-verified — a two-stage parallel jog demo
(`parallel-jog-demo.hwf`) runs both branches concurrently with a barrier between
stages. The flat editor was patched to preserve `Branches` across load/run/save. The
**Gantt editor itself (Layer 3) is the remaining Phase 1 work.** Original brief: a
FAAM.UI Engineer-page rework that replaces the
flat block-list editor with a horizontal **timeline/Gantt** that lays device "lanes"
down the left and sequence order across the top, and — the headline feature — lets an
author run operations **in parallel with a join barrier** ("move A *and* move B, then
wait for both") instead of strictly one-after-another.

## Goal (from the mockup)

```
Motion | gantry-main | [ Move to ]
       | hex-right   |     [ Move to ]
IO     |             |                 [ Set xx ]
Pneum. |             |                       [ Set xx ]
PSU/SMU/TEC/Vision   |  (lanes visible, empty)
                     |  <-------- horizontal scroll -------->
```

- **Left column** = device **lanes**, grouped by domain (Motion, IO, Pneumatic, PSU,
  SMU, TEC, Vision …).
- **Horizontal axis = ordering, not wall-clock time.** Left→right is sequence order.
- **Vertical overlap = concurrency.** Blocks in the same band run together.
- **Band boundary = a barrier.** The next band starts only when everything in the
  current one has finished.
- A **"Last status"** column (live per-lane readout) sits between the labels and the
  timeline — Phase 2.

## Decisions (locked)

| Topic | Decision |
|---|---|
| Executable model | **Compile to `flow.parallel`.** The Gantt is a Layer 3 view; it compiles to the existing flat `WorkflowProgram` via one new fork-join op. Backward-compatible; the runner core is untouched; old programs are unaffected. |
| Horizontal axis | **Ordering / stages.** A band is a parallel group ended by a barrier; durations are whatever they are at run time. Deterministic and safe. |
| Parallel failure | **Fail-fast + cancel siblings.** The first failing branch cancels the other in-flight branches and stops the program. |
| Editor | **Replace** the flat Engineer editor with the Gantt (over the same product programs). |

## Why decouple the view from the executable model

The visual model (stages × lanes) and the on-disk/executable model (a flat
`WorkflowProgram`) are kept separate. The Gantt **compiles down** to flat steps and
**round-trips back**. Benefits:

- Layer 2 gets one small, additive op — no second program format, no new runner/store.
- A program authored as a plain sequence and one authored in the Gantt are the **same
  file**; any consumer (Horizon.UI, console, deck runner) still runs it.
- A pre-existing sequential program renders as a faithful column-per-step Gantt.

This builds directly on precedent: `motion.parallelMove` already does fork-join via
`Task.WhenAll` (`StepDispatcher.cs:51`); the runner already recurses for
`flow.subProgram`; `WorkflowContext` is already thread-safe (`ConcurrentDictionary`).

## Layer 2 — execution (additive, backward-compatible)

### Model

```csharp
public sealed record WorkflowStep
{
    // ...existing fields (Id, Name, Op, Device, Parameters, Enabled, Breakpoint,
    //    DelayBeforeMs, DelayAfterMs) unchanged...
    public List<WorkflowBranch> Branches { get; init; } = new();   // NEW — only for flow.parallel
}

/// One concurrent lane of a flow.parallel step: an ordered mini-sequence.
public sealed record WorkflowBranch
{
    public string Lane { get; init; } = string.Empty;   // device/row id, for UI + event routing
    public List<WorkflowStep> Steps { get; init; } = new();
}
```

`Branches` is empty for every existing step, so old `.hwf` files deserialize unchanged.

### `flow.parallel` op — handled in `WorkflowRunner` (not the dispatcher)

Branches need full step semantics (delays, conditionals, pause), so `flow.parallel`
lives in the runner loop alongside `flow.subProgram`, **not** in `StepDispatcher`
(which only executes single ops). Each branch is driven by a **recursive
`WorkflowRunner`** over `branch.Steps`, all started together, joined with
`Task.WhenAll`:

- **Fail-fast:** a linked `CancellationTokenSource`; the first branch to fail cancels
  the token, stopping the siblings' next step. Report the first non-cancellation
  failure.
- **Pause/breakpoint:** branches share the program's single `PauseController`, so a
  pause halts the whole program. Per-branch breakpoints are Phase 2.
- **Context:** shared `WorkflowContext` is concurrency-safe for variables; branches
  that write the *same* var are an authoring error we surface, not a crash.

### Events (live highlighting)

`WorkflowStepEvent` gains an optional `string? Lane`. The `flow.parallel` handler wraps
each sub-runner's `onStep` to tag events with the branch lane, so the Gantt can light
up the right block. Top-level steps carry `Lane = null` (their lane is derived from
`Device`).

## Layer 3 — the Gantt editor (FAAM.UI, replaces the flat editor)

### Lanes

Built from the facets (all available after `InitializeAsync`), grouped by domain:

| Domain | Source |
|---|---|
| Motion | `Engine.Motion.DeviceNames` |
| IO | `Engine.IO.DeviceNames` |
| Pneumatic | `Engine.Pneumatic.SlideNames` |
| PowerSupply / SMU / TEC | `Engine.PowerSupply.DeviceNames` / `Smu.DeviceNames` / `Tec.DeviceNames` |
| Vision / Camera | `Engine.Camera.CameraIds` |
| Control | device-less flow ops (`flow.wait`, `flow.log`, `flow.if` …) |

Lanes with no catalog ops yet (PSU/SMU/TEC/Vision) render as visible-but-empty
scaffolding — matching the mockup — until ops are added (Phase 2).

### Editing model (view-model only)

```
TimelineProgram
  └─ Stages: ordered list (the columns / barriers)
       └─ per-lane Blocks: { OpSpec + params + device(lane) }, ordered within the stage
```

- Drag an op from the palette onto a **(lane, stage)** cell. A lane may hold a short
  sequence within a stage.
- Add/remove/reorder **stages** (columns); drag blocks between lanes and stages.
- Barriers are the implicit lines between stages; "run these in parallel" = drop
  blocks into the same stage on different lanes.
- Reuses today's data-driven palette wholesale: `OpCatalog`/`OpSpec`/`ParamSpec`,
  `ParamKind` templates, and per-device param options (positions, pins, axes).

### Compile (Gantt → `WorkflowProgram`)

For each stage, in order:
- **One lane active** → emit that lane's ops inline as ordinary sequential steps.
- **Multiple lanes active** → emit a single `flow.parallel` step; one `WorkflowBranch`
  per active lane (`Lane` = device, `Steps` = that lane's ordered ops).

### Round-trip (`WorkflowProgram` → Gantt)

Walk the top-level steps:
- An ordinary step → its own single-lane stage, placed on the lane for its `Device`
  (or the Control lane for device-less ops). Op prefix (`motion.*`, `io.*`, …) selects
  the domain group.
- A `flow.parallel` step → one stage; each branch becomes a lane track.

A flat sequential program therefore shows as one column per step — faithful and still
runnable everywhere.

## Backward compatibility

- `.hwf` JSON: `Branches` defaults empty; old files load unchanged; files without
  `flow.parallel` are byte-identical round-trips.
- Horizon.UI / console / deck runner: unaffected — a `flow.parallel` step is just
  another step they execute via the (extended) runner. They don't need the Gantt to
  run a parallel program.

## Files (Phase 1)

- **Horizon.Workflow**: `WorkflowProgram.cs` (`Branches` + `WorkflowBranch`,
  `WorkflowStepEvent.Lane`), `Execution/WorkflowRunner.cs` (`flow.parallel` handler +
  recursive branch runs + fail-fast CTS), JSON round-trip covered by the existing
  serializer.
- **FAAM.UI**: new timeline view + view-models (`Timeline*`), compile/round-trip
  mapper, lane builder from facets; the `EngineerView` becomes the Gantt; keep
  `OpCatalog`, `ProcessBlockViewModel` param editing, run/`RunStatus` wiring.

## Phasing

- **Phase 1**: `flow.parallel` + lane-tagged events in Layer 2; the Gantt editor over
  the **Motion / IO / Pneumatic / Control** ops that exist today; all lanes visible;
  compile + round-trip; run with live per-lane highlight; fail-fast parallel.
- **Phase 2**: the live "Last status" column; catalog ops for PSU/SMU/TEC/Vision;
  per-branch breakpoints; richer barrier/stage editing; nested parallel (a branch that
  itself forks).

## Open questions for Phase 2

- Should a lane allow a *dependency* on a specific block in an earlier lane (true DAG),
  or is stage-barrier ordering enough? (Start with stages; revisit if needed.)
- Cross-branch variable writes — warn at author time, or lock per-lane namespaces?
