# Recipe Migration — bobcat_engine → Horizon

> Status: **Stage 2 in progress** (updated 2026-05-19). This doc inventories the
> legacy `bobcat_engine` recipe system, maps it onto Horizon's Layer 2
> (`Horizon.Workflow`), and stages the migration. Stage 1 (converter, pre/post
> delays, Tier A ops) and the lighting facet extension are done; Stage 2 has
> Stage 2 is complete (conditionals, DUT results-store, sub-recipe, system
> control, update-position, device-link); Stage 3 has the `scan.hexapod` op
> done. See §5.

The goal: take the recipes authored for the legacy C++ `bobcat_engine`
(`C:/Users/Fabrinet/source/repos/komcat/komcat/bobcat_engine`) and run them on
Horizon's 3-layer engine.

---

## 1. Terminology — the most important thing to get straight

Bobcat and Horizon both use the word "recipe", but for **different things**.
This table is the Rosetta stone for the whole migration:

| Bobcat concept | Horizon Layer 2 equivalent |
|---|---|
| **recipe** — a JSON file, `recipe_name` + ordered `steps[]` | `WorkflowProgram` (`Execution/WorkflowProgram.cs`) |
| **program** — a JSON file, `program_name` + ordered `recipes[]` | `WorkflowDeck` (`Execution/WorkflowDeck.cs`) + `DeckStore` |
| step `type` — display string, e.g. `"Move to Position"` | step `Op` — key string, e.g. `motion.moveToPosition` |
| step `nickname` | `WorkflowStep.Name` |
| step `parameters` — `string → string` dict | `WorkflowStep.Parameters` — `string → string` dict |
| `IProcessPlugin` (process type) | a `case` in `Execution/StepDispatcher.cs` |
| `SequenceContext` — per-run variable store | `WorkflowContext` (`Execution/WorkflowContext.cs`) |
| `DataStore` — live measurement channel cache | `TelemetryStore` (Layer 3) — no Layer 2 op yet |

> ⚠️ **`Horizon.Workflow/Recipe/`** (the `.hrcp` lexer/parser/interpreter) is a
> *separate* scripting language and is **not** the target of this migration.
> Bobcat recipes map onto `WorkflowProgram` + `StepDispatcher`.

So "migrating a bobcat recipe" = converting one bobcat *recipe* JSON into one
Horizon `WorkflowProgram` JSON, and "migrating a bobcat program" = converting it
into a `WorkflowDeck`.

---

## 2. File format conversion

### Bobcat recipe JSON

```json
{
  "next_id_counter": 1,
  "product_file": ".../positions.SIPHOG888.json",
  "recipe_name": "Initial Machine",
  "version": "1.0",
  "steps": [
    {
      "enabled": true,
      "id": "XBP992N",
      "nickname": "Slide Retract 1 UV",
      "type": "Slide Retract",
      "parameters": {
        "slide_name": "UV_Head",
        "timeout_ms": "10000",
        "debounce_delay_ms": "100",
        "inbounce_delay_ms": "100"
      }
    }
  ]
}
```

### Horizon `WorkflowProgram` JSON

```json
{
  "Name": "Initial Machine",
  "Steps": [
    {
      "Id": "XBP992N",
      "Name": "Slide Retract 1 UV",
      "Op": "pneumatic.retract",
      "Device": "UV_Head",
      "Enabled": true,
      "Parameters": { }
    }
  ]
}
```

### Converter responsibilities

A converter (proposed: `Horizon.Workflow/Migration/BobcatRecipeConverter.cs`)
must do five things per step:

1. **Map `type` → `Op`** via a lookup table (see §3).
2. **Hoist the device parameter into `Device`.** Bobcat keeps the target device
   inside `parameters` under varying keys — `device`, `io_device`, `slide_name`,
   `motion_device`, `camera_name`, `psu_device`, `tec_device`, etc. Horizon has a
   typed `WorkflowStep.Device` field. The converter must know, per step type,
   which param key is the device.
3. **Rename remaining parameter keys** where Horizon's dispatcher expects
   different names (e.g. bobcat `target_position` → Horizon `position`).
4. **Lower `delay_before_ms` / `delay_after_ms`.** Every bobcat step supports
   pre/post delays; `WorkflowStep` has no such field. Either (a) add
   `DelayBeforeMs`/`DelayAfterMs` to `WorkflowStep`, or (b) emit explicit
   `flow.wait` steps around the converted step. **Recommendation: option (a)** —
   keeps step count and `Id` stable.
5. **Report unsupported step types** rather than silently dropping them — emit a
   conversion report listing every step whose `type` has no `Op` mapping.

Bobcat's `next_id_counter` and `product_file` are dropped; Horizon binds
programs to products through `ProgramStore` (per-product I/O), not an embedded
absolute path.

Bobcat **program** files (`program_name` + `recipes[]` with absolute
`recipe_file` paths) convert to `WorkflowDeck`. Bobcat programs have no
transition conditions between recipes — every converted deck link gets an
unconditional transition.

---

## 3. Step-type coverage

Bobcat defines **~150 step types** across 31 `IProcessPlugin` files.
`StepDispatcher` currently implements **~50 ops**. The gap is not 100 small
steps — it is **6 entire subsystems that do not exist in Horizon**.

Coverage is graded in four tiers.

### Tier A — covered or trivial (≈40 step types)

The facet call already exists in `IHorizonEngine`; the converter + a thin
`StepDispatcher` case is all that is needed.

| Bobcat step(s) | Horizon op | Note |
|---|---|---|
| Set Output | `io.setOutput` | ✅ |
| Set Output Name | `io.setOutputByName` | ✅ |
| Clear Output Name | — | **add** — `io.setOutputByName` with `state=false` |
| Toggle Output | — | **add** — toggle + dwell + toggle back |
| Slide Extend / Retract / Retract All | `pneumatic.extend` / `.retract` / `.retractAll` | ✅ |
| Move to Position | `motion.moveToPosition` | ✅ |
| Move Relative | `motion.moveRelative` | ✅ |
| Move Relative Multi-Axis | `motion.parallelMove` | near — verify relative variant |
| Set Velocity | `motion.setVelocity` | ✅ — bobcat splits PI vs ACS; Horizon facet handles both |
| Parallel Move | `motion.parallelMove` | ✅ |
| Read Position | — | **add** — `motion.GetPositionAsync` → context keys |
| SMU ×12 (Connect, Set V/I, Compliance, Output, Read, Polling) | `smu.*` | ✅ — `SMU Configure` preset needs composing |
| PSU ×8 | `psu.*` | ✅ — `PSU Configure` preset needs composing |
| TEC ×6 (Set Temp, Set Laser Current, Read, Connect) | `tec.*` / `laser.*` | ✅ |
| Lighting ×6 (Set Intensity, Turn On/Off, All On/Off, Configure) | `lighting.*` | ✅ — `ILightingFacet` extended with `SetNamedChannelAsync` / `SetAllAsync` / `SetGroupAsync` / `ConfigureGroupAsync` |
| Set Exposure | `camera.setExposure` | ✅ |
| Set Gain / Auto Exposure / Auto Gain | — | **add** — facet may need extending |
| Wait | `flow.wait` | ✅ |
| Log Message | `flow.log` | ✅ |
| Comment | `flow.comment` | ✅ |
| Ask User | `flow.askUser` | ✅ — handled in `WorkflowRunner` |
| Loop Start / End | `flow.loopStart` / `.loopEnd` | ✅ — handled in `WorkflowRunner` |

**Cross-cutting Tier A gap:** `delay_before_ms` / `delay_after_ms` on every
step — see §2 item 4.

### Tier B — missing, Layer-2-only (no new hardware) — *the high-value gap*

Small amounts of code, no Layer 1 work, unblocks most real production recipes.

| Feature | Bobcat steps | Horizon today |
|---|---|---|
| **Conditionals** | Conditional If / Else / End | ✅ done (2026-05-19) — `flow.if`/`else`/`endif` in `WorkflowRunner`, condition via `ConditionEvaluator`. |
| **Sub-Recipe** | Sub-Recipe | ✅ done (2026-05-19) — `flow.subProgram` recurses into the child program at run time with the shared context. |
| **System control** | Stop Sequence, Group Start/End, Clear Context, Manual Adjust | ✅ done (2026-05-19) — `flow.stop` / `flow.groupStart/End` / `var.clear`; Manual Adjust → confirm prompt. |
| **DataStore / Context** | Read DataStore, Set Context Value, Set Context Boolean | DataStore **not ported** (redundant — see §5); Set Context Value/Boolean ≈ existing `var.set`. |
| **Update Position** | Update Position Axis / All | ✅ done (2026-05-19) — `motion.updatePosition` / `updatePositionAxis` write through `PositionStore`. |
| **Device Link** | Register / Compensate Device Link | ✅ done (2026-05-19) — `motion.registerDeviceLink` / `compensateDeviceLink`, persisted in `DeviceLinkStore`. |

### Tier C — missing, needs a Layer 1 facet extension

| Feature | Bobcat steps | Blocker |
|---|---|---|
| Advanced scanning | XCAN, XCAN Extended, Grid Scan ×3, Axis Scan, Fast Align XY / Z-XY | ✅ partial (2026-05-19) — `scan.hexapod` exposes `HexapodScanEngine`; **XCAN / XCAN Extended** convert. Grid Scan ×3, Axis Scan and PI Fast-Align FSM are separate algorithms still not ported. |
| Camera capture | Capture to File / Context / DUT, Crop ROI, Capture & Confirm | `ICameraFacet` has only `SnapToFileAsync`. No capture-to-memory, no ROI crop. |
| Graph motion | Move to Graph Node, Parallel Move (graphNode), Move to High/Low-Mag Camera | No path-graph or camera-offset-calibration subsystem in Layer 1/2. |
| Global-frame motion | Move Relative (Global) | Needs a transformation-matrix store. |
| Auto Focus | Auto Focus | Needs a focus-metric image pipeline. |

### Tier D — missing whole subsystems (each its own project)

These are **not "migration"** — they are building subsystems Horizon never had.

| Subsystem | Bobcat steps | Reality |
|---|---|---|
| **Vision profiles** | 12 (Execute Profile, Pattern Match, Circle Detect, D4Sigma Z-Scan, continuous CV, GotoDetect) | `IVisionFacet` is a *minimal HALCON bridge* — `AnalyzeFrame` / `CountBlobs` only. No profile JSON format, no plugin pipeline, no world-coordinate calc. |
| **Beam server** | 10 "Beam:" steps | Bobcat talks to an external TCP beam-detection server (default `127.0.0.1:5555`). No such server or client in Horizon. |
| ~~**DUT**~~ | 8 (Create, Load, Close, Insert String/Double/Integer, Insert / Bulk Insert from Context) | ✅ done (2026-05-19) — Horizon owns per-unit traceability; built as a Layer 2 results-store, see §7. (No longer Tier D.) |
| **Trainers / alignment** | Accuracy Trainer, Yaw Correction ×2, Yaw Trainer, Z-Offset, Edge Transfer, Planar Adjustment, Placement Correction Retrace | Each is a trained-model file + an alignment algorithm; several need interactive wizards. |
| **Coordinate systems** | 10 CoordinateAlign steps (Teach / Align Live / Transform / local points) | Affine-transform teach/align subsystem — does not exist. |
| **Dispense & needle calibration** | Dispense, Calibrate Needle Z, Calibrate Camera-to-Needle Offset | Multi-stage interactive calibration workflows with their own persisted files. |
| **Aux window** | 5 (crosshair, click-to-move, chart channel/target) | Layer 3 UI-overlay features — Layer 2 has no hook for them. |
| **Lua scripting** | Run Script, Run Script (Inline) | Horizon deliberately uses the `.hrcp` interpreter instead. **Will not port** — recipes using Lua steps must be rewritten. |

---

## 4. Infrastructure gaps (the real work)

Independent of step count, these are the structural pieces missing from
Horizon Layer 2:

1. **No conditional control flow.** `WorkflowRunner` handles `flow.loopStart/End`
   and `flow.askUser` inline but has no If/Else. A bobcat recipe with any
   `Conditional If` cannot run. *(Tier B)*
2. **No pre/post-step delay.** `WorkflowStep` lacks `DelayBeforeMs/AfterMs`.
   *(Tier A)*
3. **No sub-recipe composition.** *(Tier B)*
4. **No DUT data store.** A whole persistence subsystem. *(Tier D)*
5. **No DataStore op.** Live measurement channels are visible to Layer 3
   (`TelemetryStore`) but not readable from a workflow step. *(Tier B)*
6. **Vision facet is a stub.** Real vision profiles need a profile format +
   plugin pipeline. *(Tier D)*
7. **No external beam server.** *(Tier D)*
8. **No trainer / coordinate-system subsystems.** *(Tier D)*
9. **Scan algorithms not exposed as steps.** `HexapodScanEngine` exists but
   isn't a workflow op. *(Tier C)*

---

## 5. Staged migration plan

### Stage 1 — Converter + Tier A → run the bundled examples ✅

Implemented 2026-05-18:

- **`Migration/BobcatRecipeConverter.cs`** — pure converter: recipe JSON →
  `WorkflowProgram`, program JSON → `WorkflowDeck`. Table-driven `type → Op` map
  with per-type device-key hoisting, parameter renames, and an issue list.
  Unsupported step types are emitted as **disabled `flow.comment` placeholders**
  that preserve the original `type`/`parameters` (key `_bobcatType`).
- **`Migration/BobcatMigration.cs`** — folder driver: reads a directory of
  bobcat JSON, classifies each file (`steps` → recipe, `recipes` → program),
  writes `.hwf`/`.hwd` to an output folder, and drops `conversion-report.txt`.
  Output is **standalone files for review** — it does not write into a
  product's `ProgramStore`/`DeckStore`.
- **`WorkflowStep.DelayBeforeMs` / `DelayAfterMs`** added; `WorkflowRunner`
  honours them around each dispatched step. Bobcat's `delay_before_ms` /
  `inbounce_delay_ms` and `delay_after_ms` / `debounce_delay_ms` /
  `outbounce_delay_ms` lower onto these; `timeout_ms` is dropped.
- New Tier A ops in `StepDispatcher`: **`io.toggleOutput`** (pulse + dwell +
  restore) and **`motion.readPosition`** (position → context vars). Clear Output
  Name maps to `io.setOutputByName` with `state=false`; camera Set Gain / Auto
  map onto the existing multi-field `camera.setExposure`.
- Console entry point: **`Horizon.Workflow.Console` → "Migrate bobcat
  recipes"** — prompts for input/output folders, runs the conversion, prints a
  summary.

**Lighting (follow-on, 2026-05-19):** `ILightingFacet` extended with
`SetNamedChannelAsync`, `SetAllAsync`, `SetGroupAsync` and `ConfigureGroupAsync`
(`LightingChannelInfo` gained `Group`/`Color`). `StepDispatcher` ops
`lighting.setNamedChannel` / `setAll` / `setGroup` / `configureGroup` added, and
all six bobcat `Light:` step types now convert — bobcat addresses lighting
channels by name, so they map to `lighting.setNamedChannel`.

**Not done in Stage 1:** Read DataStore (Tier B — converts to an unsupported
placeholder for now).

- **Exit criterion:** the 8 `SIPHOG888` example recipes in
  `bobcat_engine/docs/example_programs/SIPHOG888/recipes/` convert and run
  end-to-end. They use 14 step types — all Tier A except **Read DataStore**,
  which becomes a no-op placeholder until Stage 2.

### Stage 2 — Tier B → real production recipes

- ✅ **Conditional If / Else / End** (2026-05-19) — `WorkflowRunner` handles
  `flow.if` / `flow.else` / `flow.endif` with forward-only index jumps over a
  stateless nesting-aware scan (no frame stack — there is no break/continue;
  loops nest independently). `flow.if` carries a `condition` expression
  evaluated by the existing `ConditionEvaluator`. The converter folds bobcat's
  `context_key` / `comparison` / `threshold` into that expression — including
  `IS_TRUE`/`IS_FALSE` (→ `== 1`/`== 0`) and `EXISTS`/`NOT_EXISTS`
  (→ `!= ""`/`== ""`).
- ✅ **DUT / results store** (2026-05-19) — the engine-owns-traceability
  subsystem; design + as-built in §7. All 8 bobcat DUT step types convert.
- ✅ **Sub-Recipe** (2026-05-19) — runtime resolution: `flow.subProgram`
  recurses into a child program with the *shared* context (variables + DUT run),
  depth-capped at 16. Not convert-time flatten — keeps the child a first-class,
  independently editable program. The converter maps the bobcat `recipe_file`
  path to the child's name (file stem).
- ✅ **System control** (2026-05-19) — `flow.stop` (Stop Sequence — a controlled,
  successful early end), `flow.groupStart/End` (organisational markers, logged),
  `var.clear` (Clear Context). Manual Adjust converts to a `flow.askUser` confirm
  prompt (jog step-size / reference images not carried — flagged in the report).
- Context ops: Set Context Value / Set Context Boolean — **already covered by
  the existing `var.set` op**; the converter just needs table entries
  (Boolean = `var.set` with `"1"`/`"0"`). No new runtime code.
- ✅ **Update Position** (2026-05-19) — `motion.updatePosition` overwrites a
  named position with the device's current coordinates; `motion.updatePositionAxis`
  replaces one axis from a context value. Both write through `PositionStore` via
  a `savePosition` delegate threaded into `StepDispatcher`.
- ✅ **Device Link** (2026-05-19) — `motion.registerDeviceLink` captures the
  offset between two devices' current positions; `motion.compensateDeviceLink`
  moves the linked device to restore that offset (per-axis via the `axes` param).
  Links persist machine-globally in `DeviceLinkStore` (`device_links.json`).

> **DataStore is not ported** (decided 2026-05-19). Bobcat's DataStore is a live
> measurement-channel cache; Horizon's Layer 1 facets already are that cache and
> `StepDispatcher` reads them directly (`measurement.read`, `smu.readCurrent`,
> `psu.readCurrent`, all with `captureAs`). `Read DataStore` stays an unsupported
> placeholder — the converter's report points the author at the equivalent
> facet-read op.

### Stage 3 — Tier C → facet extensions

- ✅ **Scan op** (2026-05-19) — `scan.hexapod` exposes `HexapodScanEngine`
  (coarse-to-fine beam alignment) as a step: feedback descriptor + scan
  parameters from step params, result captured to `{captureAs}_peak/_baseline/
  _snr/_significant/_state`. The converter maps bobcat **XCAN / XCAN Extended**
  (the motion side converts cleanly; the feedback source must be set by hand —
  the bobcat DataStore channel name has no reliable facet mapping).
- Extend `ICameraFacet`: capture-to-context, ROI crop. *(vision — deferred)*
- Port Axis Scan / Grid Scan / Fast-Align as discrete scan strategies — these
  are separate algorithms `HexapodScanEngine` does not cover; not yet done.

### Stage 4+ — Tier D → per-subsystem projects

Each scoped and prioritised separately: Vision profiles, trainers, coordinate
systems, dispense/calibration, beam server. (DUT was promoted to Stage 2 — see
§7.) Lua scripting is **out of scope** — Lua-bearing recipes are rewritten
against `.hrcp` or native steps.

---

## 6. Decisions

Settled for Stage 1:

- **Pre/post delay:** ✅ added `DelayBeforeMs`/`DelayAfterMs` fields to
  `WorkflowStep` (keeps step count and `Id`s stable vs. emitting `flow.wait`).
- **Converter output:** ✅ standalone `.hwf`/`.hwd` files in a review folder —
  not written into `ProgramStore`/`DeckStore`. Wiring into the stores can come
  later once converted recipes are validated.
- **Unsupported steps:** ✅ emit a disabled `flow.comment` placeholder that
  preserves the original `type`/`parameters`, plus a report entry.

Settled 2026-05-19:

- **DataStore:** ✅ not ported — redundant with the Layer 1 facet caches.
- **DUT / results store:** ✅ in scope — the engine owns per-unit traceability,
  so a Layer 2 results-store subsystem will be built (Stage 2, design §7).
- **Set Context Value/Boolean:** ✅ map onto the existing `var.set` op — no new
  runtime code, converter table entries only.

- **Sub-Recipe:** ✅ resolved at run time (2026-05-19) — `flow.subProgram`
  recurses into the child program, keeping it first-class and editable, rather
  than flattening it into the parent at convert time.

---

## 7. DUT / results store — design (implemented 2026-05-19)

Horizon owns per-unit traceability: each physical Device Under Test gets a
persistent record of what every run measured for it.

> **As built:** `Horizon.Workflow/Dut/` — `DutModels.cs` (`DutRecord` /
> `DutRun` / `DutEntry`, `DutValueType`, `DutVerdict`) and `DutStore.cs`.
> `WorkflowContext.Dut` holds the active run. `ProcessService` opens/finalises
> the record around `RunAsync`/`RunDeck` (both gained an optional `serial`
> argument); `IProcessService.ListDutRecords()` lists recorded serials.
> `StepDispatcher` has `dut.record` / `dut.recordFromContext` /
> `dut.recordGroup`. The console "Run program" flow prompts for a serial.

Two scope decisions (2026-05-19) shape this — see §6: **Horizon is the
standalone system of record** (no MES), and **the serial number is a run/deck
parameter**, not a mid-recipe step. Together they dissolve bobcat's imperative
`DUT Create/Load/Close` lifecycle into Horizon's run lifecycle — a notable
*re-design*, not a port.

**Where it lives** — Layer 2, `Horizon.Workflow`, alongside `ProductStore` /
`PositionStore` / `ProgramStore`. New types: `DutRecord`, `DutRun`, `DutEntry`,
`DutStore`.

**Data model** — run-grouped (not bobcat's flat time series), so each run
carries its own verdict and disposition/yield analytics fall out:

```
DutRecord { Product, Serial, CreatedUtc, List<DutRun> Runs }
DutRun    { StartedUtc, EndedUtc, Source (program/deck name),
            Verdict (Pass | Fail | Aborted), List<DutEntry> Entries }
DutEntry  { Timestamp, Key, Value, DutValueType (String|Double|Integer|Object) }
```

`Object` carries a JSON fragment (for `dut.recordGroup`).

**Storage** — one JSON file per serial under the product directory:
`<productDir>/dut/<serial>.json` (mirrors `processes/`, `decks/`). A run appends
one `DutRun`; a unit crossing multiple stations/recipes accumulates history.

**Lifecycle** — driven by the run, not by steps:

1. A run starts with a serial number (run/deck parameter — entered or
   barcode-scanned once). No serial → no record; `dut.*` steps no-op.
2. The run opens/creates the `DutRecord` and begins an in-memory `DutRun`.
3. `dut.*` steps append `DutEntry`s to the current run.
4. At run end the run sets `Verdict` (from the run's `Result` — Ok→Pass,
   step failure→Fail, cancellation→Aborted) and `EndedUtc`, appends the
   `DutRun`, and saves the file.

**New step ops** (`StepDispatcher`) — the 8 bobcat DUT steps collapse to 3
recording ops; Create/Load/Close are gone (run lifecycle):

| Op | Replaces |
|---|---|
| `dut.record` (key, value, type) | DUT Insert String / Double / Integer |
| `dut.recordFromContext` (key ← context var) | DUT Insert from Context |
| `dut.recordGroup` (group ← context vars by prefix) | DUT Bulk Insert from Context |

**Touch points** — `IProcessService.RunAsync` / `RunDeck` gain an optional
serial argument; the active `DutRun` is held for the run's duration. Converter
maps all 8 bobcat DUT types — the 5 Insert steps onto the 3 ops, Create/Load/
Close onto `flow.comment` with a report note. **No `IOperatorPrompt` change**
and **no mid-recipe lifecycle** — both eliminated by the run-parameter decision.
