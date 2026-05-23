# Hexapod Scanning — Beam Alignment

> Status: **bench-tested 2026-05-18** on a no-signal DUT — the mechanism
> (motion, feedback streaming, hill-climb, halt, return-to-peak, noise gate) is
> verified. **Real-signal validation on an actual aligned product is pending.**
> Migrated from the legacy `bobcat_engine` C++ scanner.

Hexapod scanning is a **Layer 2** feature: a coarse-to-fine hill-climb that
nudges a motion device's axes, reads a live feedback signal after each move, and
walks toward the peak of that signal — used to align an optical part to a beam.

It is pure, UI-free code in `Horizon.Workflow` on top of `IHorizonEngine`
(Layer 1). It has no dependency on `IProcessService`, products, or any UI.

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
| `Feedback/ScanFeedbackFactory.cs` | Builds the right feedback source from a `FeedbackDescriptor` |
| `ScanContext.cs` | The algorithm ↔ hardware seam — motion, feedback, peak/noise tracking, progress |
| `IScanStrategy.cs` | The scan-algorithm interface |
| `CoarseToFineStrategy.cs` | The coarse-to-fine hill-climb (the one implemented strategy) |
| `HexapodScanEngine.cs` | Public orchestrator: baseline → scan → return-to-peak → verdict |

Console test rig: `Horizon.Workflow.Console` — `ConsoleApp.HexapodScanMenuAsync`.

---

## 2. Architecture

```
HexapodScanEngine            orchestrator — lifecycle, baseline, verdict
  │  builds
  ├─ IScanFeedbackSource     ← ScanFeedbackFactory (SMU / Measurement / PowerSupply)
  │     bridges engine facet events → "give me a number now"
  └─ ScanContext             algorithm ↔ hardware seam (wraps IHorizonEngine + feedback)
        │  driven by
        └─ IScanStrategy  →  CoarseToFineStrategy
```

- **`HexapodScanEngine`** — the only public entry point. `RunAsync(device,
  feedback, parameters, progress, ct)` returns a `ScanResult`.
- **`ScanContext`** — everything a strategy is allowed to do: `MoveRelativeAsync`,
  `MoveToPositionAsync`, `GetCurrentPositionAsync`, `GetMeasurementAsync`,
  `SettleAsync`, `RecordDataPoint`, `UpdateGlobalPeak`, progress, logging. The
  strategy never touches `IHorizonEngine` directly.
- **`IScanStrategy`** — pluggable algorithm. Only `CoarseToFineStrategy` exists;
  the legacy SinglePass / Adaptive / Grid / Spiral strategies are not ported.

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
`ValueType`, `IntervalMs`) selects and configures the source; the algorithm
stays source-agnostic. `HexapodScanEngine` always disposes the source in a
`finally` block, so streaming is stopped even on cancel or fault.

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

## 9. Migration notes (from `bobcat_engine`)

- Source: `bobcat_engine/include/motions/scanning/` — `CoarseToFineStrategy`,
  `ScanContext`, `ScanTypes`, `AdaptiveScanController`, `ScanExecutor`.
- The coarse-to-fine algorithm, parameters, and presets are faithful ports.
- `std::thread` + `std::atomic` halt → a single `async Task` + `CancellationToken`.
- The global `DataStore` channel → the `IScanFeedbackSource` abstraction.
- Qt UI (`ScanControlWidget`, `ScanControlPanel`, `ScanController`) is **not**
  migrated — the console rig is the only front-end so far.
- Not ported: the SinglePass / AdaptiveStep / Grid / Spiral strategies (stubs in
  bobcat) and rotation scanning (`RotationScanController`).

---

## 10. Pending / not yet done

- **Real-signal validation** on an aligned product — only bench-tested on a
  no-signal DUT so far.
- Exposure via `IProcessService`, a `StepDispatcher` op, and a recipe-method
  domain, so process programs and recipes can invoke a scan.
- A Layer 3 (`Horizon.UI`) scan panel with a live feedback chart.
- The remaining scan strategies and rotation scanning.

### Known external issue

On the `hex-left` PI hexapod, `SetSystemVelocity` is rejected for any value —
the scan logs a warning and continues at the controller's current velocity.
This is a Layer 1 / PI-adapter concern, not a scanning-module bug.
