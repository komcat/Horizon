# Layer 2 — Process Module Design

> Status: **DRAFT for review** — design 2026-05-15. No code yet.
> Builds on `docs/layer1-hardware-engine-design.md` (Layer 1, complete) and the
> 3-layer plan in `docs/headless-engine-concept.md`.

---

## 1. Context

Layer 1 (`Horizon.Engine`) is complete: all hardware behind `IHorizonEngine`,
coordinate-only, headless. **Layer 2 is the product / process module** — it
turns coordinates and primitive operations into *products, taught positions,
and runnable programs*. It sits on `IHorizonEngine` and is consumed by Layer 3
(UI). It depends on the engine **only** — it never touches hardware.

The legacy app has these capabilities in `Horizon.Process` + `Horizon.Core`,
but wired to the old `AppContext`/managers via reflection. Per the review,
Layer 2 is a **clean recreate** against `IHorizonEngine` — not a rewire of the
legacy executors.

## 2. Decisions locked in this review

| Decision | Choice |
|---|---|
| Process executors | **Recreate** — fresh process/recipe execution against `IHorizonEngine`, not the legacy `ProcessEngine`/`RecipeEngine`. |
| Initial scope | **Core first** — products, teach points / named positions, process + recipe execution. Defer dispense, trainer, calibration, scan. |
| Layer boundary | Layer 2 references **`Horizon.Engine` + `Horizon.Common` only**. No hardware, no `Horizon.Core`/legacy `Horizon.Process`. |
| File formats | **Redesign** — Layer 2 defines its own program / recipe / position formats; legacy `.hproc`/`.hrcp` are not reused (see §10). |

## 3. Scope

**In Layer 2 (core):**
- **Products** — per-product directories, the active product, product data I/O.
- **Teach points / named positions** — Layer 2 owns named positions; resolves
  a name → coordinates and drives the engine. (Layer 1 is coordinate-only.)
- **Process execution** — runnable step sequences (`.hproc`) against the engine.
- **Recipe execution** — the `.hrcp` scripting language (later phase, §11).
- **`IProcessService`** — the Layer 2 facade for Layer 3.

**Out (deferred, post-core):** DispenseExecutor, position-accuracy Trainer,
NeedleCameraCalibration, ScanEngine, MoveMap, AxisTransformService. Each is a
follow-on phase once the core runs.

## 4. Project structure

```
Horizon.Workflow            (new) — Layer 2: products, teach points,
                                    process + recipe execution, IProcessService
   references → Horizon.Engine, Horizon.Common
Horizon.Workflow.Console    (new) — Layer 2 test host (own console), so the
                                    Layer-1 Horizon.Console stays a pure
                                    "references only Horizon.Engine" boundary proof
```

`Horizon.Workflow` is one assembly (mirrors the one-assembly engine). Name is a
proposal — see §13.

## 5. `IProcessService` — the Layer 2 surface

The single interface Layer 3 consumes. Rough shape (refined during build):

```csharp
public interface IProcessService
{
    // Products
    IReadOnlyList<string> Products { get; }
    string? ActiveProduct { get; }
    Task<Result> LoadProductAsync(string name);
    Task<Result> CreateProductAsync(string name);

    // Teach points / named positions
    IReadOnlyList<string> GetPositionNames(string device);
    MotionPosition? GetPosition(string device, string name);
    Task<Result> TeachPositionAsync(string device, string name);   // capture current
    Task<Result> MoveToNamedPositionAsync(string device, string name, CancellationToken ct = default);

    // Process programs
    IReadOnlyList<string> ListProcessPrograms();
    Task<Result<WorkflowProgram>> LoadProcessAsync(string name);
    IOperatorPrompt? OperatorPrompt { get; set; }      // front-end prompt handler (§5.1)
    Task<Result> RunAsync(WorkflowProgram program, CancellationToken ct = default);
    void Pause();  void Resume();  void Step();        // breakpoint/step-mode control
    event Action<WorkflowStepEvent>? StepEvent;        // step started / completed / loop

    // Recipes (later phase)
    Task<Result> RunRecipeAsync(string recipeFile, CancellationToken ct = default);

    event Action<LogEntry>? LogEmitted;
}
```

### 5.1 `IOperatorPrompt` — the bidirectional callback (Phase 4)

`StepEvent` / `LogEmitted` make Layer 3 a *passive viewer*. Interactive steps
(`flow.askUser`, and later breakpoints) need the opposite: the runner must
**block mid-run** and call **up** to the front-end for an answer. That makes the
Layer 2 ↔ Layer 3 contract **bidirectional** — a front-end is an active
participant the runner can suspend on.

```csharp
public interface IOperatorPrompt
{
    Task<bool> ConfirmAsync(string title, string message, CancellationToken ct);
    // grows toward InputTextAsync / ChooseAsync without touching IProcessService
}
```

- **Not an engine facet.** Engine facets (`Motion`, `IO`, …) are called *down*
  into; this is an inverted dependency the runner calls *up* into. Layer 3
  *provides* it, Layer 2 *consumes* it — hence a settable `OperatorPrompt`
  property on `IProcessService`, the slot the front-end fills.
- **An interface, not a `Func`.** One interface absorbs future prompt shapes
  (text input, multi-choice) without a new `IProcessService` property per shape.
- **An awaitable method, not an `event`.** A fire-and-forget `event` cannot
  return a value or be awaited. A web front-end that needs an async round-trip
  implements `ConfirmAsync` with an internal event + `TaskCompletionSource` —
  that machinery stays in Layer 3; Layer 2 stays a plain `await`.
- **Behaviour:** `true` → continue; `false` → controlled abort
  (`Result.Ok("…operator declined…")`, *not* an error); `OperatorPrompt` null →
  the `askUser` step fails (a program with `Ask User` cannot run headless).
- The console implements `IOperatorPrompt` as a blocking yes/no — the reference
  every front-end mirrors (WPF → modal dialog; web → SignalR round-trip).

## 6. Product model

`ProductStore` — per-product directory under `%APPDATA%\Horizon\products\{name}/`
with `processes/`, `recipes/`, `positions.json`; active product tracked in
`products/active.json`. Loading a product makes its positions + programs the
active set. (Same layout as the legacy `ProductManager` — preserves existing
product folders.)

## 7. Teach points / named positions — Layer 2 owns this

This is the Layer 1 ↔ Layer 2 boundary made concrete:

- A **named position** = `(device, name) → MotionPosition` (engine coordinate).
- Stored per-product in `positions.json` (the separate positions file decided
  in the Layer 1 review — hardware config and position data stay separate).
- **Teach** = read the live coordinate from the engine
  (`engine.Motion.GetState(device).Position`) and save it under a name.
- **Recall** = look the name up → `MotionPosition` → `engine.Motion.MoveToPositionAsync`.
- The engine never sees the name. `MoveToNamedPositionAsync` is the only place
  name → coordinate resolution happens, and it lives here.

## 8. Process execution model (recreated)

- **`WorkflowProgram`** — ordered list of `WorkflowStep`.
- **`WorkflowStep`** — one operation: a kind + target device + parameters.
  Kinds: hardware ops (move, move-to-named, jog, set-output, extend/retract
  slide, snap, measure, set-velocity, …), control flow (loop start/end, wait,
  comment), variables (set, calculate-expression).
- **`WorkflowRunner`** — executes the sequence **async** against `IHorizonEngine`.
  Owns a `WorkflowContext` (thread-safe variable store, typed getters).
  Supports cancellation, pause/resume + step-by-step (a breakpoint gate), and
  raises `StepEvent` (started / completed / loop-iteration).
- **Dispatch** — recreated *typed*, no reflection: a small set of per-domain
  step executors map a `WorkflowStep` to the matching `IHorizonEngine` facet
  call (`engine.Motion.MoveToPositionAsync`, `engine.IO.SetOutputAsync`, …).
  The facets are the operation vocabulary.
- **Expressions** — a fresh recursive-descent evaluator over `WorkflowContext`
  variables (the legacy `ExpressionEvaluator` is small; recreate cleanly).
- **Plugins** — `IWorkflowPlugin`: metadata (id, name, category, parameter
  defs) + `BuildSteps()`. Reusable parameterised step templates; the parameter
  metadata is what lets Layer 3 auto-generate editor forms. Discovery by
  reflection over the assembly.

## 9. Engine touchpoints Layer 2 will need

Building Layer 2 will surface small `IHorizonEngine` gaps to add as we go:

- **Motion source for interlocks.** Interlock rules have an `EnforcementLayer`
  (`ScriptOnly` / `JogOnly` / `All`). A Layer 2 process move must be tagged
  `Process` so `ScriptOnly` rules apply; a console jog is `Jog`. The engine
  facet currently issues all moves with the default source — Layer 2 will need
  the engine to accept a motion-source/run-context. Small facet addition.
- **Execution arbitration.** The engine already has the `HardwareExecutionLock`
  concept internally; Layer 2's runner must acquire it so a process and a
  recipe can't run hardware concurrently.

These are noted now; resolved per-phase, not up front.

## 10. File formats — redesigned

Layer 2 defines **its own formats** — it does not read the legacy
`.hproc`/`.hrcp`:
- **Process programs** — a new JSON serialization of `WorkflowProgram`.
- **Recipes** — a fresh recipe script syntax for the recreated recipe
  interpreter (Phase 5).
- **Products / positions** — per-product directories with a new
  `positions.json` schema (the hardware-config / position-data split from the
  Layer 1 review still holds — positions are Layer 2 data, separate from
  `motor_devices_config.json`).

Existing bench product data is not migrated automatically — it is recreated
under the new formats (consistent with the recreate-everything direction). The
legacy WPF app keeps its own `.hproc`/`.hrcp` files independently.

## 11. Phasing (core)

| Phase | Deliverable |
|-------|-------------|
| 1 | `Horizon.Workflow` skeleton + `IProcessService` + `ProductStore` (list / active / load / create products). Console lists & switches products. |
| 2 | Teach points / named positions — positions store, teach (capture), recall (`MoveToNamedPositionAsync`). |
| 3 | Process execution — `WorkflowProgram`/`Step`/`Runner`/`Context`, typed dispatch to engine facets, loops, expressions, cancellation. Run a `.hproc`. |
| 4 | **Interactive execution** — `IOperatorPrompt` + `flow.askUser` (the bidirectional Layer 2 ↔ Layer 3 contract, §5.1); breakpoints + step-by-step. Plugin system + builder UI deferred. See `docs/layer2-phase4-tasks.md`. |
| 5 | Recipe execution — recreated `.hrcp` lexer / parser / interpreter against the engine. |
| 6+ | *(post-core)* Dispense, Trainer, Calibration, Scan. |

Each phase = library code + a `Horizon.Workflow.Console` section, testable on
the bench, mirroring Layer 1's per-step rhythm.

## 12. Verification approach

`Horizon.Workflow.Console` drives `IProcessService` the way Layer 3 will:
create/switch a product, teach a position and recall it (stage moves there),
load and run a `.hproc` against live hardware, watch step events. Each phase is
bench-verified before the next.

## 13. Resolved decisions

- **Formats redesigned** (§10) — Layer 2 uses its own program / recipe /
  position formats; legacy `.hproc`/`.hrcp` are not reused.
- **Two execution models** — recreate both: a step-based `WorkflowRunner`
  (Phase 3) and a recipe scripting interpreter (Phase 5).
- **Separate test host** — `Horizon.Workflow.Console`; the Layer 1
  `Horizon.Console` stays a pure "references only `Horizon.Engine`" boundary proof.
- **Project name** — `Horizon.Workflow` (proposal — rename freely before Phase 1).
- **Legacy coexistence** — the legacy WPF app keeps using `Horizon.Process` /
  `Horizon.Core` untouched; Layer 2 is built alongside.
