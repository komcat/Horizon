# Hexapod Scanning — Beam Alignment

> Status: **bench-tested 2026-05-18** on a no-signal DUT — the mechanism
> (motion, feedback streaming, hill-climb, halt, return-to-peak, noise gate) is
> verified. **Real-signal validation on an actual aligned product is pending.**
> Migrated from the legacy `bobcat_engine` C++ scanner.
> **2026-06-12:** added a second strategy (`GridScanStrategy`, an XY raster),
> catalog-signal feedback (any `signal.read` key incl. SiPhOG), and workflow
> steps `scan.hexapod` / `scan.grid` with matching Engineer blocks (§§3a, 4a, 8a).

Hexapod scanning is a **Layer 2** feature: it nudges a motion device's axes,
reads a live feedback signal after each move, and finds the peak of that signal —
used to align an optical part to a beam. Two strategies ship: a **coarse-to-fine
hill-climb** (climbs the gradient; fast) and a **grid raster** (maps the whole
field; robust against local maxima).

It is pure, UI-free code in `Horizon.Workflow` on top of `IHorizonEngine`
(Layer 1). The engine itself has no dependency on `IProcessService`, products, or
any UI; the `scan.*` workflow steps (§8a) bridge it into the process layer.

---

## 1. Where it lives

All code is in `src/Horizon.Workflow/Scanning/`, namespace
`Horizon.Workflow.Scanning`.

| File | Role |
|---|---|
| `ScanModels.cs` | `ScanParameters`, `ScanResult`, `ScanDataPoint`, `PeakInfo`, `BaselineInfo`, `AxisScanInfo`, `ScanProgress`; enums `ScanState`, `ScanMode`, `TerminationReason` |
| `IScanFeedbackSource.cs` | The feedback abstraction + `FeedbackDescriptor`, `FeedbackKind`, `FeedbackValue` |
| `Feedback/FeedbackSourceBase.cs` | Shared thread-safe latest-sample cache + averaging + sample counter |
| `Feedback/SmuFeedbackSource.cs` | Feedback from an SMU (`ISmuFacet`, true streaming) |
| `Feedback/MeasurementFeedbackSource.cs` | Feedback from a Keithley 6482 (`IMeasurementFacet`, fast streaming) |
| `Feedback/PowerSupplyFeedbackSource.cs` | Feedback from a power supply (`IPowerSupplyFacet`, polled) |
| `Feedback/SignalScanFeedbackSource.cs` | Feedback from any unified-catalog signal (`ISignalSource`, incl. product SiPhOG) — §4a |
| `Feedback/ScanFeedbackFactory.cs` | Builds a facet feedback source from a `FeedbackDescriptor` |
| `ScanContext.cs` | The algorithm ↔ hardware seam — motion, feedback, peak/noise tracking, progress |
| `IScanStrategy.cs` | The scan-algorithm interface |
| `CoarseToFineStrategy.cs` | The coarse-to-fine hill-climb (§3) |
| `GridScanStrategy.cs` | The XY-raster grid scan (§3a) |
| `HexapodScanEngine.cs` | Public orchestrator: baseline → scan → return-to-peak → verdict |

Workflow steps live in `Execution/StepDispatcher.cs` (`scan.hexapod`, `scan.grid`);
the Engineer blocks are authored in `FAAM.UI/ViewModels/OpCatalog.cs` +
`OpCatalogHelp.cs` (§8a). Console test rig: `Horizon.Workflow.Console` —
`ConsoleApp.HexapodScanMenuAsync`.

---

## 2. Architecture

```
HexapodScanEngine            orchestrator — lifecycle, baseline, verdict
  │  builds (or accepts a prebuilt feedbackOverride)
  ├─ IScanFeedbackSource     ← ScanFeedbackFactory (SMU / Measurement / PowerSupply)
  │     │                       or SignalScanFeedbackSource (any catalog signal, §4a)
  │     bridges facet events / signal reads → "give me a number now"
  └─ ScanContext             algorithm ↔ hardware seam (wraps IHorizonEngine + feedback)
        │  driven by
        └─ IScanStrategy  →  CoarseToFineStrategy (hill-climb, §3)
                          →  GridScanStrategy     (XY raster,  §3a)
```

- **`HexapodScanEngine`** — the only public entry point.
  `RunAsync(device, feedback, parameters, progress, strategy, feedbackOverride, ct)`
  returns a `ScanResult`. `strategy` defaults to `CoarseToFineStrategy`; pass a
  `GridScanStrategy` for a raster. `feedbackOverride`, when supplied, is used
  instead of building one from the `FeedbackDescriptor` — this is how a
  catalog-signal source (§4a) is injected; the engine still owns and disposes it.
- **`ScanContext`** — everything a strategy is allowed to do: `MoveRelativeAsync`,
  `MoveToPositionAsync`, `GetCurrentPositionAsync`, `GetMeasurementAsync`,
  `GetAveragedMeasurementAsync`, `SettleAsync`, `RecordDataPoint`,
  `UpdateGlobalPeak`, progress, logging. The strategy never touches
  `IHorizonEngine` directly.
- **`IScanStrategy`** — pluggable algorithm. Two ship: `CoarseToFineStrategy`
  (§3) and `GridScanStrategy` (§3a). The legacy SinglePass / Adaptive / Spiral
  strategies are still not ported.

---

## 3. The coarse-to-fine algorithm

Faithful port of bobcat's `CoarseToFineStrategy`. For each **step size**
(coarse → fine), for each **axis** (Z, Y, X by default):

1. Hill-climb the **+ direction**: move one step, wait the settle time, read
   feedback. A new max resets the decrease counter.
2. A direction **stops** on any of: `ConsecutiveDecreasesLimit` consecutive
   non-improving steps; one drop larger than `SignificantDecreaseThreshold`;
   travel reaching `MaxTotalDistanceMm`; a motion error; cancellation.
3. Return to the start, hill-climb the **− direction**, then settle on the
   better of the two and move there.
4. Between axes, if the global peak now beats the local reading by more than
   `ImprovementThreshold`, jump back to the global peak.

The motion calls (`IMotionFacet.MoveRelativeAsync` / `MoveToPositionAsync`)
already block until the move settles, so the strategy is plain `async`/`await`.

---

## 3a. The grid-scan (raster) algorithm

`GridScanStrategy` sweeps a **fixed lattice** of points about the start position
and measures the signal at every node — it does **no** hill-climbing. Because it
visits the whole field it cannot be trapped by a local maximum, at the cost of
more moves than a hill-climb. Use it to *map* a field; use coarse-to-fine to
*climb* one.

- **Serpentine raster** — rows along the second axis, columns along the first,
  flipping the column direction each row (boustrophedon) so the next node is
  always one step away. Travel is minimal.
- **Absolute addressing** — the start (centre) is captured once with
  `GetCurrentPositionAsync`; every node is an *absolute* target derived from it
  (centre ± offset), so there is no cumulative relative-move drift across the
  field. Other axes hold at their centre value.
- **Per node** — settle, `GetAveragedMeasurementAsync(AveragingCount)`,
  `RecordDataPoint`, `UpdateGlobalPeak`. The orchestrator returns the device to
  the global peak when the raster finishes (or halts), exactly as for §3.
- **Bounded** — node count is `(2·floor(extent/step)+1)²`, hard-capped at **4096
  nodes** (a Power-of-10 bounded-loop guard). Exceeding the cap fails validation
  with a clear "increase the pitch or reduce the extent" message.

It reuses `ScanParameters` rather than a new record, interpreting the fields a
grid needs:

| `ScanParameters` field | Grid meaning |
|---|---|
| `AxesToScan[0]`, `[1]` | the two grid axes (e.g. `X`, `Y`); only the first two are used |
| `StepSizes[0]` | the **pitch** between adjacent nodes (mm); further entries ignored |
| `MaxTotalDistanceMm` | **half-extent** per axis — the grid spans the start ± this |
| `MotionSettleMs`, `AveragingCount`, `ScanVelocityMmPerSec` | as for §3 |

The noise gate (§6) applies unchanged — the orchestrator computes σ / SNR over the
recorded nodes regardless of strategy.

---

## 4. Feedback abstraction

Layer 1 has no shared measurement hub; feedback arrives as facet *events*. The
`IScanFeedbackSource` abstraction bridges that to the algorithm's synchronous
"read a number" expectation, replacing bobcat's global `DataStore`.

| Source | Facet | Mechanism |
|---|---|---|
| `SmuFeedbackSource` | `ISmuFacet` | `StartStreamingAsync` + caches each `Sampled` event |
| `MeasurementFeedbackSource` | `IMeasurementFacet` | `StartStreamingAsync` + caches each `StateChanged` event |
| `PowerSupplyFeedbackSource` | `IPowerSupplyFacet` | No streaming — `RefreshAsync` + `GetState` on each read |

Each adapter keeps a **thread-safe latest-sample cache** (facet events fire on a
background poll thread) and a monotonic **`SampleCount`** so a caller can confirm
the stream is alive. `FeedbackDescriptor` (`Kind`, `Device`, `Channel`,
`ValueType`, `IntervalMs`) selects and configures the *facet* source; the
algorithm stays source-agnostic. `HexapodScanEngine` always disposes the source
in a `finally` block, so streaming is stopped even on cancel or fault.

---

## 4a. Catalog-signal feedback (SiPhOG and friends)

The three facet sources above only reach engine hardware (SMU / Measurement /
PowerSupply). To let a scan optimise **any** readable signal — including a
*product* signal the engine deliberately doesn't own, such as FAAM's SiPhOG —
`SignalScanFeedbackSource` bridges the **unified signal catalog**
(`Horizon.Workflow.Signals.ISignalSource`, the same catalog the `signal.read`
block uses) to `IScanFeedbackSource`.

- It wraps a read delegate `Func<string, CancellationToken, Task<Result<double>>>`
  (in practice `SignalRegistry.ReadAsync`) plus the chosen **signal key** (e.g.
  `smu.PIC.ch1.current`, or the product key `siphog.sagPwrV`).
- Reads are **on-demand**: each call resolves the current value through the
  registry — live I/O for SMU / PSU / Measurement, a cached frame for SiPhOG — so
  no streaming subscription is needed; `FeedbackSourceBase`'s averaging loop just
  polls it on its natural spacing.
- `StartAsync` **primes one read**: this both confirms a registered source *owns*
  the key (fail-fast when the product signal isn't present on this head) and sets
  `HasValidSample` for the orchestrator's first-sample wait.
- It is injected via `RunAsync(..., feedbackOverride:)`, not the
  `ScanFeedbackFactory`. `StepDispatcher` builds it from its `SignalRegistry`
  (`_signals`); `ScanFeedbackFactory` / `FeedbackDescriptor` are untouched and
  remain the path for the facet sources and legacy programs.

> SiPhOG frames update at ~10 Hz, so averaging several reads at the base ~50 ms
> spacing can re-sample the same frame. Set `AveragingCount = 1` (or a longer
> settle) when one fresh frame per node is preferred.

---

## 5. Scan parameters

`ScanParameters` is an `init`-only record. `ScanParameters.ForMode(ScanMode)`
builds the three presets (port of bobcat's `createScanParameters`).

| Parameter | Default | Notes |
|---|---|---|
| `AxesToScan` | `Z, Y, X` | Scan order |
| `StepSizes` (mm) | Coarse `5,2,1,0.5 µm` | Per mode — Fine `2,1,0.5 µm`; UltraFine `0.5,0.2,0.1 µm` |
| `MotionSettleMs` | 400 | Dwell after each move before measuring |
| `ScanVelocityMmPerSec` | 1.0 | mm/s. PI hexapod → system velocity; ACS gantry → every axis. 0 = leave unchanged |
| `ConsecutiveDecreasesLimit` | 3 | Stop a direction after N non-improving steps |
| `SignificantDecreaseThreshold` | 0.05 | Stop a direction on a relative drop > 5 % |
| `MaxTotalDistanceMm` | 0.2 (0.1 UltraFine) | Travel cap per direction |
| `ImprovementThreshold` | 0.01 | Return to global peak between axes if it beats here by > 1 % |
| `AveragingCount` | 3 | Samples averaged for the baseline reading |
| `MaxStepSizeMm` | 0.5 | Safety cap on any step size |
| `ScanTimeout` | 10 min | Overall timeout |
| `MinPeakSigma` | 3.0 | Noise gate, in σ — see §6 |

---

## 6. Noise gate — real peak vs noise

A scan on a misaligned / dark part just chases measurement noise. The result is
judged statistically rather than by a naive ratio (a `peak / baseline` ratio is
meaningless when the signal is small or bipolar).

- **Noise σ** — sample standard deviation of every recorded measurement.
- **Peak SNR** — `(peak − baseline) / σ`.
- **Verdict** — a *real peak* only when `PeakSNR ≥ MinPeakSigma` (default 3 σ).
- **Early abort** — after the first axis, if the SNR is already below the gate,
  the scan stops immediately and reports "no signal above noise" instead of
  grinding every axis and step size.

`ScanResult` carries `NoiseSigma`, `PeakSnr`, and `PeakIsSignificant`.

---

## 7. Halt and safety

- Cancelling the `CancellationToken` halts the scan; it still returns the stage
  to the best position found (legacy `HaltScan` contract) — the return-to-peak
  move runs on a fresh token.
- `GetCurrentPositionAsync` never fabricates a `(0,0,0)` position: if the live
  read fails it falls back to the last *valid* polled state, and if even that is
  unavailable it throws so the scan aborts cleanly — a scan move issued against
  a fabricated zero target would slam the stage to its absolute origin.

---

## 8. Console test rig

`Horizon.Workflow.Console` → menu **Hexapod scan**:

1. Pick the motion device and scan mode (Coarse / Fine / UltraFine).
2. Review / edit any parameter (or reset to the mode preset).
3. Pick the feedback source, device, channel.
4. Choose verbose mode:
   - **off** — a live progress bar; the description shows the current step and
     the running feedback `SampleCount`.
   - **on** — a scrolling per-step log (move, value, max, decreases, distance,
     sample count, positions); no progress bar.
5. Any key stops the scan; the result table shows baseline, peak, improvement,
   noise σ, peak SNR, and the verdict.

---

## 8a. Workflow steps & Engineer blocks

`StepDispatcher` exposes the engine as two ops, so a process program / deck (and
the Engineer timeline) can run a scan. Both share a private `RunScanStepAsync`
helper (feedback build → run → capture):

| Op | Strategy | Key params |
|---|---|---|
| `scan.hexapod` | `CoarseToFineStrategy` | `mode` (coarse/fine/ultrafine), `axes`, `stepSizesUm` (CSV), `settleMs`, `velocity` |
| `scan.grid` | `GridScanStrategy` | `axes` (two, default `X,Y`), `stepUm` (pitch), `extentMm` (half-extent), `settleMs`, `velocity`, `averaging` |

- **Device** — `step.Device` is the hexapod / gantry that scans.
- **Feedback** — preferred is a single **`signal`** key from the unified catalog
  (§4a); on FAAM.UI that includes `siphog.*`. For back-compat, when `signal` is
  absent the dispatcher falls back to the facet `FeedbackDescriptor` keys
  (`feedbackKind` / `feedbackDevice` / `feedbackChannel` / `feedbackValue` /
  `feedbackIntervalMs`) — e.g. bobcat-migrated programs.
- **Capture** — `captureAs` prefix writes `{prefix}_peak`, `_baseline`, `_snr`,
  `_significant` (`"1"`/`"0"`), `_state` into the workflow context. A scan that
  ran is a step **success** even if it found only noise (branch on
  `_significant`); only a setup / hardware error fails the step.

**Engineer blocks** — `OpCatalog` exposes both ops under a **Scan** category
(`DeviceKind.Motion`, so they sit on the scan device's lane), with help in
`OpCatalogHelp`. The feedback field is a **`signal` drop-down** (`ParamKind.SignalName`),
populated from `IProcessService.DescribeSignals()` — the same list as the
"Read Out" block. The scan ops are not in `Horizon.UI`'s editor (FAAM.UI only so
far).

---

## 9. Migration notes (from `bobcat_engine`)

- Source: `bobcat_engine/include/motions/scanning/` — `CoarseToFineStrategy`,
  `ScanContext`, `ScanTypes`, `AdaptiveScanController`, `ScanExecutor`.
- The coarse-to-fine algorithm, parameters, and presets are faithful ports.
- `std::thread` + `std::atomic` halt → a single `async Task` + `CancellationToken`.
- The global `DataStore` channel → the `IScanFeedbackSource` abstraction.
- Qt UI (`ScanControlWidget`, `ScanControlPanel`, `ScanController`) is **not**
  migrated — the console rig and the FAAM.UI Engineer blocks (§8a) are the
  front-ends so far.
- **Grid scan** is now implemented (§3a) as a fresh `IScanStrategy`, *not* a port
  of bobcat's three Grid Scan variants — it's a single-pitch serpentine raster on
  Horizon's own seam, so bobcat grid recipes still don't convert (see
  `BobcatRecipeConverter`).
- Not ported: the SinglePass / AdaptiveStep / Spiral strategies (stubs in
  bobcat) and rotation scanning (`RotationScanController`).

---

## 10. Pending / not yet done

- **Real-signal validation** on an aligned product — only bench-tested on a
  no-signal DUT so far. The grid-scan strategy and catalog-signal feedback are
  likewise unvalidated against a live signal.
- ~~Exposure via a `StepDispatcher` op~~ — **done** (§8a): `scan.hexapod` /
  `scan.grid` ops + FAAM.UI Engineer blocks. Still pending: a recipe-method
  domain so `.hrcp` recipes can invoke a scan.
- A Layer 3 scan **panel with a live feedback chart** (FAAM.UI has the Run-page
  `ScanPanel`; `Horizon.UI` has none, and neither charts the field/raster yet).
- The remaining scan strategies (SinglePass / AdaptiveStep / Spiral) and rotation
  scanning.

### Known external issue

On the `hex-left` PI hexapod, `SetSystemVelocity` is rejected for any value —
the scan logs a warning and continues at the controller's current velocity.
This is a Layer 1 / PI-adapter concern, not a scanning-module bug.
