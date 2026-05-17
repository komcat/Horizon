# SMU Continuous Streaming — Design

> Design doc for review. **No code yet.** Scopes continuous measurement
> streaming for the SMU facet (Keithley 2602B) — IV sweeps, dark-current-over-
> time, and live SMU charts — across Layer 1, 2 and 3.
> Builds on the bench-verified Layer 1 SMU facet (`docs/layer1-new-facets-bench-test.md`)
> and the Layer 3 telemetry design (`docs/layer3-ui-design.md` §7.4 / §8).

---

## 1. Context

The Layer 1 SMU facet (`ISmuFacet` → `SmuFacet` → `Horizon.Smu`) is bench-
verified: it connects a Keithley 2602B over TSP, sources voltage/current, and
exposes a 2-second **status** poll. What it cannot do:

- **Read a measurement on demand** into a recipe/workflow variable.
- **Stream measurements continuously** for an IV sweep, a dark-current capture,
  or a live chart.

A SourceMeter that can source but not surface its measurements is half an
instrument. This doc designs the streaming path end to end.

## 2. Current state

| Layer | Component | Streaming today |
|---|---|---|
| L1 | `Horizon.Smu` (`SmuManager`) | none — thin pass-through, no poll loop |
| L1 | `SmuFacet` | generic `PollLoop`, fixed 2 s **status** snapshot |
| L1 | — | **no `MeasurementHub`** in `Horizon.Engine` (the legacy one is `Horizon.Core`, old app) |
| L2 | `RecipeMethodRegistry` / `StepDispatcher` | drive ops only — no read/capture |
| L3 | `Horizon.UI` | telemetry store designed (§7.4) but **not built** |

### Reference implementations

- **bobcat `SMUPollingManager`** — per-device, per-channel polling; configurable
  interval; `CURRENT`/`VOLTAGE` measurement type; background thread workers;
  signal-based data distribution. Plus a **fast mode** (NPLC ≈ 0.1, tight read
  loop, ~20 Hz). See `bobcat_engine/include/SMU/SMUPollingManager.*`,
  `FAST_MODE_*.md`, `POLLING_IMPLEMENTATION.md`.
- **Legacy `CurrentMeasurementManager`** — `StartPolling(500 ms)`,
  `StartFastPolling` (NPLC 0.1), `StopPolling`. Same shape, older codebase.

These are the trusted references for the TSP fast-measurement settings and the
polling-control surface.

## 3. Architecture

`docs/layer3-ui-design.md` §8 locks the layering: **poll loops live in Layer 1;
Layer 3 subscribes to events and never polls hardware.** §7.4 places the
history buffer (the "`MeasurementHub`-style" ring buffer) in **Layer 3**.

```
Layer 1  Horizon.Smu        2602B fast-read mode (low NPLC, autozero off,
         (adapter)          fixed range) — a quick measure.iv() read
              │
         SmuFacet           streaming poll loop, controllable interval;
              │             Start/StopStreamingAsync; emits one event/sample
              │  event Sampled(SmuSample)   — background thread
              ▼
Layer 3  TelemetryStore     §7.4 ring buffer: per channel, fixed 500 samples,
         (history)          key "{Device}.CH{N}"; thread-safe; pull-based
              │
         SMU panel + chart  SkiaSharp on CompositionTarget.Rendering,
                            snapshots the ring buffer into pre-allocated arrays

Layer 2  recipe / workflow  one-shot point reads (smu.readCurrent → store:);
                            an IV sweep is a `loop` of point reads
```

**Rationale.** Streaming history (a ring buffer) is a *consumer* concern — the
chart needs it, the engine does not. Keeping it in L3 matches §7.4 and avoids a
hub in Layer 1 that only the UI would use. Layer 1 stays a pure producer: it
emits one `Sampled` event per measurement and holds no history.

## 4. The single-channel problem (decide first)

`ISmuFacet` and `Keithley2602BAdapter` are **single-channel** today — the
adapter drives `smua` or `smub`, whichever is the first `enabledChannels`
entry. But the 2602B is **dual-channel**, and a chart/sweep wants to address
each channel independently.

**Decision needed.** Streaming should be **channel-addressed**: read, stream,
and sample methods take an explicit `channel` argument (1 → `smua`, 2 → `smub`).
This widens the facet surface (`ISmuFacet` methods gain a `channel` parameter or
channel-typed overloads) and the adapter (`ISmuDevice` read methods take a
channel). The existing drive methods (`SetSourceVoltageAsync`, …) can stay
single-channel for now or be widened in the same pass — **recommend widening
them too**, so the whole SMU surface is consistently channel-addressed rather
than half-and-half.

This is the largest design ripple; everything below assumes it is taken.

## 5. Phase 1 — adapter fast-read

**Goal:** a quick, on-demand measurement read, plus a fast/normal mode toggle.

- `ISmuDevice` gains:
  - `SmuReading ReadMeasurement(int channel)` — returns current + voltage +
    timestamp in one round-trip (`print(smuX.measure.iv())`).
  - `SmuError SetFastMode(bool fast)` — fast = low NPLC + autozero off + fixed
    range; normal = restore configured NPLC/autorange.
- `Keithley2602BAdapter` — implement both. Fast-mode TSP settings (mine the
  exact values from bobcat `FAST_MODE_IMPLEMENTATION.md`): `measure.nplc`
  reduced (~0.001–0.1), `measure.autozero = AUTOZERO_OFF`, fixed `measure.rangei`.
- `Keithley2400Adapter` — implement `ReadMeasurement` (simple `:READ?`); fast
  mode optional (the 2400 is not on this bench — a `NotSupported` stub is fine).
- `SmuReading` — new record in `Horizon.Smu.Models`: `Channel`, `Current`,
  `Voltage`, `Timestamp`.

## 6. Phase 2 — facet streaming mechanism

**Goal:** the Start/Stop streaming control the user asked for.

`ISmuFacet` gains:

```csharp
/// <summary>Begins continuous sampling of a channel at the given interval.</summary>
Task<Result> StartStreamingAsync(string device, int channel, int intervalMs);

/// <summary>Stops streaming on a device and restores normal measurement mode.</summary>
Task<Result> StopStreamingAsync(string device);

bool IsStreaming(string device);

/// <summary>Raised once per streamed sample, on a background thread.</summary>
event Action<SmuSample>? Sampled;
```

- `SmuSample` — engine-owned record: `Device`, `Channel`, `Current`, `Voltage`,
  `Timestamp`.
- **Streaming poll loop** — a *second*, dedicated `PollLoop` per facet (or per
  device), separate from the 2 s status poll, created at the requested
  `intervalMs`. `StartStreamingAsync` puts the adapter into fast mode and starts
  it; `StopStreamingAsync` stops it and restores normal mode.
- **Status poll coexists.** The 2 s status poll keeps running so `GetState`
  stays valid; the streaming loop is additive. (Open option: pause the status
  poll while streaming to avoid bus contention — decide during build.)
- **One stream per device.** Per-channel streaming on one device at the same
  time is deferred — `StartStreamingAsync` streams one channel; a second call
  switches channel. (bobcat supports per-channel concurrency; Horizon can add it
  later if a dual-channel chart needs it.)
- Cancellation: streaming stops on `StopStreamingAsync`, on disconnect, and on
  facet dispose.

## 7. Phase 3 — Layer 2 read/capture ops

**Goal:** close the original Layer 2 gap — recipes/workflows can read.

New ops in `RecipeMethodRegistry` **and** `StepDispatcher`:

| Op | Captures |
|---|---|
| `smu.readCurrent(channel, store)` | current → variable |
| `smu.readVoltage(channel, store)` | voltage → variable |
| `psu.readVoltage(channel, store)` | actual output voltage → variable |
| `psu.readCurrent(channel, store)` | actual output current → variable |
| `tec.readTemperature(store)` | temperature → variable |
| `tec.waitForTemperature(celsius, toleranceC, timeoutMs)` | blocks until settled |

- Implementation: call the facet's `RefreshAsync()` (added this session) for a
  fresh poll, then `GetState()`, then capture via `store:` — mirroring
  `measurement.readCurrent`. SMU may instead use the Phase 1 `ReadMeasurement`
  directly for a true single-shot.
- **No `smu.startStream` / `stopStream` recipe ops.** Streaming is an L1→L3
  (chart) concern. An IV sweep in a recipe is a `loop` of point reads — simpler,
  deterministic, already expressible. *(Open decision — flag if you want
  explicit streaming-control ops for recipes after all.)*

Phase 3 is independent of 4–5 and immediately useful — it can land and be
bench-tested on its own.

## 8. Phase 4 — Layer 3 telemetry store

**Goal:** the §7.4 history buffer, SMU as its first consumer.

- A `TelemetryStore` (or `MeasurementHub`): per-channel **ring buffer**, fixed
  capacity (500 samples — the proven legacy size), key `"{Device}.CH{N}"`.
- Thread-safe; **pull-based** — producers append on the background event
  thread, the UI snapshots into pre-allocated arrays at its own cadence.
- Fed by `SmuFacet.Sampled`. Designed so the Measurement facet's per-sample
  events become a second producer later with no change.
- Home: `Horizon.UI/Telemetry/` (alongside `UiSampler`), unless a sharable
  `Horizon.Telemetry` is wanted — **recommend it stays in `Horizon.UI`** until a
  second consumer (web UI) exists.

## 9. Phase 5 — Layer 3 SMU panel + chart

- **SMU control surface** — source mode, level, compliance, output on/off,
  channel select — the four console SMU menu actions, as a WPF panel.
- **Live chart** — SkiaSharp, rendered on `CompositionTarget.Rendering`,
  snapshotting the ring buffer (the proven legacy chart pattern, §7.4).
- **Start / Stop streaming** button bound to `StartStreamingAsync`.
- Folds into the Layer 3 Machine page (`docs/layer3-ui-design.md` §3 / step 3).

## 10. Open decisions

1. **Channel-addressed facet (§4)** — recommend yes; widen the whole SMU
   surface, not just the streaming methods.
2. **Status poll during streaming (§6)** — coexist, or pause it? Decide on the
   bench from observed bus contention.
3. **L2 streaming-control ops (§7)** — recommend none; point reads only.
4. **Telemetry store home (§8)** — recommend `Horizon.UI`; promote to a shared
   project only when a web UI needs it.
5. **2400 fast mode (§5)** — recommend `ReadMeasurement` yes, fast mode stubbed
   (2400 not on this bench).

## 11. Task / build order

| # | Layer | Deliverable | Independent? |
|---|---|---|---|
| 1 | L1 | `ISmuDevice.ReadMeasurement` + fast mode; `Keithley2602BAdapter` impl; `SmuReading` model | yes |
| 2 | L1 | `SmuFacet` streaming loop + `Start/StopStreamingAsync` + `Sampled` event; channel-addressed surface (§4) | needs 1 |
| 3 | L2 | `smu`/`psu`/`tec` read + capture ops; `tec.waitForTemperature` | needs 1 (independent of 2) |
| 4 | L3 | `TelemetryStore` ring buffer | needs 2 |
| 5 | L3 | SMU control panel + live chart | needs 4 |

Phases 1–3 are self-contained and bench-testable without any UI; 4–5 are the
Layer 3 build. Each phase = code + a `Horizon.Console` or `Horizon.UI` slice,
bench-tested before the next.

## 12. Docs to update on completion

- `docs/layer3-ui-design.md` §7.4 — note the `TelemetryStore` is built and SMU
  is its first producer.
- `docs/layer1-hardware-engine-design.md` — the extended `ISmuFacet` surface
  (streaming + channel-addressed).
- `docs/layer2-process-module-design.md` — the new read/capture ops.
