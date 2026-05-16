# Layer 3 — Front-End UI Design

> Status: **DRAFT for review** — design 2026-05-16. No code yet.
> Builds on `docs/layer1-hardware-engine-design.md` (Layer 1, complete),
> `docs/layer2-process-module-design.md` (Layer 2, core phases 1–5 complete +
> bench-tested) and the 3-layer plan in `docs/headless-engine-concept.md`.

---

## 1. Context

The 3-layer split is built bottom-up:

```
Layer 3  Front-end UI  ← THIS DOCUMENT       depends on interfaces only
Layer 2  Horizon.Workflow — product/process  depends on IHorizonEngine        ✅
Layer 1  Horizon.Engine — hardware engine    owns all hardware                 ✅
```

Layers 1 and 2 are done and bench-verified. Their only front-ends so far are
the Spectre.Console test rigs (`Horizon.Console`, `Horizon.Workflow.Console`) —
deliberate boundary proofs, not products. **Layer 3 is the real operator UI.**

Layer 3 consumes `IProcessService` (Layer 2) and `IHorizonEngine` (Layer 1) —
**interfaces only**. It never touches hardware adapters or workflow internals
directly.

## 2. Decisions locked in this review

| Decision | Choice |
|---|---|
| Legacy `Horizon.App` | **Not refactored.** Left as-is. It is acceptable if the Layer 1/2 recreate work breaks it — it is not the path forward. |
| Layer 3 form | **A brand-new, greenfield application** (`Horizon.UI`), sibling to `Horizon.App` — not a port of the AvalonDock panel app. |
| UI framework | **WPF (C#)**, `net8.0-windows` — confirmed. No AvalonDock; a fixed, purpose-built operator layout. |
| Engine boundary | **In-process.** `Horizon.UI` references `Horizon.Workflow` + `Horizon.Engine` directly. The HTTP/SignalR boundary stays deferred until a *web* UI is actually wanted (`headless-engine-concept §7`). |
| One-process rule | Run `Horizon.UI` **or** legacy `Horizon.App`, never both — a single process owns the hardware SDKs. |
| Window model | One `MainWindow` + one or more detachable `DisplayWindow`s (§6). |
| Thread rule | **The main UI thread (thread 0) must never block or stall.** All hardware I/O, frame conversion and data production stays on background threads (§7, §8). |
| Why a UI now | Trainer, Vision, and Dispense are **inherently interactive** (live feed, click-to-teach, ROI, visual verify). They cannot be finished as headless Layer 2 phases — they are Layer 3 features built on Layer 2's runner. |

## 3. Scope

**In Layer 3:**
- Hardware status + manual control surfaces (motion/jog, IO, pneumatic, camera,
  measurement, laser) — driven by `IHorizonEngine` facets.
- Product management + teach-points + process/recipe run UI — driven by
  `IProcessService`.
- **Live camera feed** rendering at full frame rate (§7.2).
- Background **telemetry pipelines** — motion coordinates, measurement data —
  readable by the UI without stalling it (§7.3, §7.4).
- The **interactive teaching surfaces**: Trainer, Vision model authoring,
  Dispense verify — the features that justify a real UI.
- The front-end implementation of the operator-feedback contract
  (`IOperatorPrompt`, extended — §9).

**Out:**
- Hardware logic, workflow logic — all below the interface line.
- Hardware config authoring — that is `Horizon.ConfigTool`
  (`docs/config-tool-design.md`), separate dev tooling.
- Web UI — a later client of a future HTTP surface, not this document.

## 4. UI framework — WPF (confirmed)

`Horizon.UI` is greenfield, so the framework is a fresh choice. **WPF is
confirmed**: the team knows it; the hardware stack is Windows-only P/Invoke so
cross-platform buys nothing; the camera-bitmap path (`WriteableBitmap`) is
mature; HALCON ships WPF display widgets. Avalonia / WinUI were considered and
rejected — no benefit while every SDK is Windows-native.

WPF is used **without AvalonDock** — a fixed, purpose-built operator layout, not
the legacy free-floating dock panels. MVVM throughout; ViewModels depend on the
Layer 1/2 interfaces only.

## 5. Project structure

```
Horizon.UI            (new) — Layer 3 operator application
   references → Horizon.Workflow (IProcessService),
                Horizon.Engine   (IHorizonEngine),
                Horizon.Common
Horizon.App           (legacy WPF) — untouched, may break, not maintained forward
Horizon.Console       — Layer 1 console test rig (kept)
Horizon.Workflow.Console — Layer 2 console test rig (kept)
```

`Horizon.UI` is `net8.0-windows` + `UseWPF`. It is the **only** front-end that
references both Layer 1 and Layer 2 — it composes them: the engine is created
once, handed to `Horizon.Workflow`, and both interfaces are exposed to the UI
via a thin composition root (mirrors the role legacy `AppContext` played, but
as pure wiring with no hardware logic).

## 6. Window shell — hub-and-spoke

The shell is a **home launcher + five full-screen pages**. Each page has a
**Back** button returning to the home launcher. This is an operator-station
model — pick a task, do it, come back — not a persistent always-visible nav.

```
                        ┌──────────────────┐
                        │  Home launcher   │   five tiles
                        └────────┬─────────┘
   ┌────────┬─────────┬──────────┴───┬─────────────┬──────────────┐
  Run     Machine    Process       Programming    Process
                     Trainer                      Sequence
```

| Page | Tile | Contents |
|------|------|----------|
| **Home** | — | Five large tiles: **Run**, **Machine**, **Process Trainer**, **Programming**, **Process Sequence**. The first screen after startup. |
| **Machine** | Machine | Hardware operation + diagnostics. The **only page with an internal left nav rail**: Status, Motion, IO, Pneumatic, Laser, Measurement, Camera. Owns the `HORIZON` engine-state header + the global log panel. The Phase 1 status dashboard becomes its **Status** section. **Run is *not* in this rail** — Run is a top-level tile. |
| **Run** | Run | Program list + Start / Pause / Stop, and a large widget region: live-feed view, overlay chart, operator-confirm widgets — the *replaceable interactive surface* a running program drives (`IOperatorPrompt`, §9). |
| **Process Trainer** | Process Trainer | Left sub-list: **Trainer system**, **Dispense**, **Calibration (needle)**. Each opens its teaching UI — the interactive features of §10. |
| **Programming** | Programming | A **WPF-built editor** for Horizon's own process programs / `.hrec` recipes — a syntax-aware text surface (not an embedded external editor). |
| **Process Sequence** | Process Sequence | Process-step sequencing. **Layout mirrors Process Trainer** (Back + title + left sub-list + content area); content is a placeholder for now and is fleshed out in a later phase. |

**Implementation.** `ShellWindow` + `ShellViewModel` with a `CurrentPage`
property. `HomeView` shows the tiles; a tile click sets `CurrentPage`. Each page
is a `UserControl` resolved from its view model by an implicit `DataTemplate`
(the standard WPF MVVM swap — no `Frame`/`Page`, no back-stack journal). The
**Machine** page nests a *second* `ContentControl` for its section nav rail. A
page's **Back** button calls `ShellViewModel.GoHome()`.

### 6.1 Camera display windows

| Window | Role |
|---|---|
| **`DisplayWindow`** | A dedicated camera live-view window, launched from the Machine → Camera section. **At least one**; more can be spawned (multi-monitor, multiple cameras, side-by-side). Each binds to one camera's `FrameChannel` (§7.2). Fullscreen-capable. |

`DisplayWindow` is a **separate top-level `Window`**, not a page inside the
shell, so it can be moved/fullscreened independently and its render cost is
isolatable.

**Optional per-window UI thread.** WPF supports multiple UI threads, each with
its own `Dispatcher`. A `DisplayWindow` doing heavy frame work *may* be created
on its **own dedicated UI thread** so its rendering load can never stall the
`ShellWindow`'s thread 0. The `FrameChannel` is thread-safe shared state, so a
display window on thread *N* pulls from it independently. Decision per §12 —
start single-threaded, promote a display window to its own thread only if
profiling shows thread-0 contention.

## 7. Data pipelines — off-thread by design

This section is the core of the performance requirement. Three live data
streams flow from Layer 1 into the UI: **camera frames**, **motion
coordinates**, **measurement samples**. All three obey one principle.

### 7.1 Principle — decouple production rate from display rate

Layer 1 already produces data on **background threads**: camera SDK callbacks
fire `FrameReady` on SDK threads; per-domain poll loops raise `PositionChanged`
/ `StatusChanged` / measurement `Sample`. Layer 3 must **never**:

- do data production, frame conversion, or a blocking hardware call on thread 0;
- marshal *every* producer event onto the dispatcher — a high-rate event storm
  floods the dispatcher queue and freezes the UI.

Instead, **every pipeline is: background producer → thread-safe latest-state
holder → UI samples it at a fixed display cadence.** The producer never waits
for the consumer; the consumer never waits for the producer.

Two delivery shapes, chosen by data character:

| Shape | Used for | Why |
|---|---|---|
| **Latest-only buffer** (drop intermediate) | Camera frames | Frames are large and drop-tolerant — only the newest matters. Queuing them causes lag + unbounded memory. |
| **Pub-sub into a store** (keep history) | Motion coords, measurement | Small payloads; charts/teach math need recent history, so accumulate in a bounded store. |

> On the previous project's "pub-sub from the camera manager": pub-sub is fine
> for *event delivery*, and Layer 1 already does it. The change here is that
> camera **frames are not pushed to the UI per-frame** — pushing couples frame
> rate to UI render speed and floods the dispatcher. Frames go into a shared
> latest-frame buffer the UI **pulls** at its own refresh rate. Telemetry keeps
> the pub-sub-into-a-store model.

### 7.2 Camera frame pipeline — full frame rate

```
Camera SDK thread          conversion worker (bg Task)        UI thread (display window)
  ICameraFacet.FrameReady  ─▶  debayer / format convert  ─▶   CompositionTarget.Rendering
  (raw bytes + format)         (only if needed)               ├─ new frame? (seq check)
        │                            │                        ├─ WriteableBitmap.Lock()
        └────────▶  FrameChannel  ◀──┘                        ├─ memcpy back buffer
                    (per camera)                               ├─ AddDirtyRect / Unlock()
                    double/triple buffer                       └─ (no new frame → skip)
                    + sequence number
```

- **`FrameChannel`** — one per camera. Internally a double/triple buffer: the
  producer writes the newest frame into a spare buffer and atomically swaps;
  a monotonically increasing **sequence number** marks "new frame available."
  Frame buffers are **pooled and reused** — no per-frame allocation, no GC
  pressure on the hot path.
- **Producer** — `ICameraFacet.FrameReady` (SDK thread). It does the minimum:
  drop the raw bytes into the channel and return immediately, so the SDK grab
  loop is never blocked.
- **Conversion stage** — if a pixel-format conversion is needed (Bayer → RGB,
  packed mono, etc.) it runs on a **dedicated background worker**, never on the
  SDK thread (would stall grabbing) and never on the UI thread. If the camera
  already delivers a directly displayable format, this stage is skipped.
- **Consumer** — each `DisplayWindow` hooks `CompositionTarget.Rendering`
  (fires once per compositor refresh, ~60 Hz, synced to the render pipeline).
  On each tick it checks the channel's sequence number; if unchanged, it does
  **nothing**; if changed, it does a cheap `Lock → memcpy → AddDirtyRect →
  Unlock` into its `WriteableBitmap`. The expensive work (conversion) is
  already done — the UI thread only does a memory copy.
- **Frame dropping is intentional.** If the UI cannot keep up, intermediate
  frames are simply overwritten in the channel — the display always shows the
  newest frame, never a backlog. Camera capture FPS and display FPS are
  independent.
- **Multiple display windows** subscribe to the same `FrameChannel`; each owns
  its own `WriteableBitmap` and pulls independently at its own refresh.
- **Escalation path** — if `WriteableBitmap` CPU copy ever bottlenecks at very
  high resolution/rate, swap the consumer for `D3DImage` + a GPU texture. The
  `FrameChannel` boundary stays the same — only the display sink changes.

### 7.3 Motion coordinate pipeline

```
Layer 1 poll loop  ─▶  PositionChanged / StatusChanged  ─▶  MotionTelemetryStore
  (bg thread)                                               (ConcurrentDictionary<device,status>)
                                                                     │
UI sampler (one shared timer, ~20–30 Hz)  ◀─────────────────────────┘
  reads store → updates bound VM properties on thread 0
```

- **`MotionTelemetryStore`** — a thread-safe latest-value store
  (`ConcurrentDictionary<string, MotionDeviceStatus>`), written by Layer 1's
  motion poll-loop events. Pure write-latest; no UI involvement.
- **One shared UI sampler** — a single `DispatcherTimer` (or a
  `CompositionTarget.Rendering` tick) at ~20–30 Hz reads the store and pushes
  values into bound ViewModel properties. **One sampler for all motion VMs**,
  not one timer per panel — the legacy `DispatcherTimer`-per-panel pattern is
  explicitly not repeated.
- Coordinates are thus always available for read in the background and surface
  to the UI at a smooth, bounded cadence — independent of how fast the engine
  polls hardware.

### 7.4 Measurement data pipeline

- A **`MeasurementHub`-style** store: per-channel **ring buffer** (fixed
  capacity, e.g. 500 samples — the proven legacy design), fed by Layer 1's
  measurement `Sample` events on the poll-loop thread. Channel key convention
  `"{Device}.CH{N}"` carried over.
- **Charts** render on `CompositionTarget.Rendering`, snapshotting the ring
  buffer into pre-allocated arrays (the legacy chart pattern — it performed
  well; reuse it). SkiaSharp for the chart surface.
- Numeric read-outs use the same ~20–30 Hz shared sampler as motion (§7.3).

### 7.5 Why this keeps thread 0 alive

- Thread 0 only ever **reads snapshots and renders** — bounded, cheap work.
- Producers (SDK callbacks, poll loops, conversion workers) run on background
  threads and never touch the UI.
- The UI consumes at *its* cadence (~60 Hz frames, ~20–30 Hz telemetry),
  decoupled from however fast hardware produces.
- No event is marshalled per-occurrence to the dispatcher — no event storm.

## 8. Engine wiring + threading rules

- **Composition root** creates `IHorizonEngine`, calls `InitializeAsync` /
  `ConnectAllAsync`, then constructs the Layer 2 `IProcessService` on top. A
  startup/splash screen drives `ConnectAllAsync(IProgress<string>)`.
- **No `DispatcherTimer` polling of hardware.** Layer 1 already runs the poll
  loops; Layer 3 subscribes to events into the §7 stores. The only UI timers
  are the display-rate samplers of §7.3/§7.4.
- **Hardware commands from the UI** (move, set output, snap, …) `await` the
  async facet methods. The facets are already async and serialized per device
  (Layer 1 §5), so an `await`ing UI stays fully responsive — the click handler
  returns to the dispatcher immediately.
- **Threading rules, explicit:**
  - *Thread 0 (UI):* read snapshots, bind, render, `await` facet calls. Never a
    blocking hardware call, never frame conversion, never `.Result`/`.Wait()`.
  - *SDK callback threads:* drop raw frame into `FrameChannel`, return fast.
  - *Conversion workers:* dedicated background `Task`s for debayer/format.
  - *Layer 1 poll loops:* already background; feed the telemetry stores.
- **MVVM**: a `MotionViewModel` taking `IMotionFacet` physically cannot reach
  IO — modularity enforced by the compiler, as in Layer 1.

## 9. The interactive contract — extending `IOperatorPrompt`

Layer 2's operator callback (`layer2-process-module-design.md §5.1`) is
deliberately minimal:

```csharp
public interface IOperatorPrompt
{
    Task<bool> ConfirmAsync(string title, string message, CancellationToken ct);
}
```

`ConfirmAsync` is enough for `flow.askUser` ("operator declined → abort"). It is
**not** enough for visual teaching, where the operator looks at a live frame and
returns coordinates / an ROI / a selection. Trainer, Vision, and Dispense each
need a richer round-trip.

The contract must **grow** (Layer 2 owns the interface; Layer 3 implements it):

```csharp
public interface IOperatorPrompt
{
    Task<bool>          ConfirmAsync(string title, string message, CancellationToken ct);
    Task<string?>       InputTextAsync(string title, string prompt, CancellationToken ct);
    Task<int?>          ChooseAsync(string title, IReadOnlyList<string> options, CancellationToken ct);
    // visual teaching round-trips (the Layer 3 justification):
    Task<Point2D?>      PickPointAsync(string camera, string instruction, CancellationToken ct);
    Task<RoiRegion?>    PickRoiAsync(string camera, string instruction, CancellationToken ct);
}
```

- Each new method is an **awaitable round-trip**, never a fire-and-forget event
  — the runner blocks mid-step until the operator answers (or cancels).
- The console test rigs stub the visual methods as "not supported headless"
  (a teaching step cannot run without a UI — same rule as `askUser`).
- `Horizon.UI` implements them as modal teaching overlays on the live feed.
- This keeps Trainer/Vision/Dispense as **Layer 2 runner steps** that simply
  call *up* through `IOperatorPrompt` — the interactivity lives in Layer 3, the
  sequencing stays in Layer 2.

> The exact shape of `PickPointAsync` / `PickRoiAsync` is refined per phase
> (§11) — listed here to fix the *direction*, not the final signatures.

## 10. The three interactive features

These are the reason Layer 3 exists. Each is a Layer 2 runner capability +
a Layer 3 teaching surface.

| Feature | Layer 2 side | Layer 3 side |
|---|---|---|
| **Trainer** (position-accuracy) | Runner steps that capture corners / reference points; coordinate math. | 3-step wizard over the live feed; click-to-capture; frame-overlay of taught points. See `docs/position-accuracy-trainer.md` for the math. |
| **Vision** (HALCON) | `IVisionFacet` already in Layer 1; Layer 2 stores trained models as product data. | ROI drawing on live frames, model train/test, visual match confirmation. |
| **Dispense** | `DispenseExecutor` recreated as a Layer 2 runner sequence (deferred core item). | Camera-verify prompt = operator confirms needle/glue placement on the live feed before descent. |

All three were deferred in `layer2-process-module-design.md §3` as "post-core."
**They depend on Layer 3** — they are sequenced after the Layer 3 core, not
before.

## 11. Phasing

Phasing follows the hub-and-spoke structure of §6 — the shell first, then each
page, then the Machine page's sections.

| Phase | Deliverable |
|---|---|
| 1 | ✅ `Horizon.UI` skeleton — composition root wiring `IHorizonEngine` + `IProcessService`; startup/connect screen; status dashboard fed by the shared sampler. |
| 2 | **Hub-and-spoke shell** — `HomeView` (five tiles) + `ShellWindow` + Back navigation; **Machine** page wrapping the Phase 1 dashboard as its **Status** section + the left nav rail skeleton (Run excluded); **Process Trainer** and **Process Sequence** pages as placeholder shells (Back + title + left sub-list + content area). |
| 3 | Machine page sections — Motion/jog, IO, Pneumatic, Laser, Measurement control surfaces over the engine facets; measurement pipeline (§7.4) + charts. |
| 4 | Machine → **Camera** section + camera frame pipeline (§7.2) — `FrameChannel`, conversion worker, `DisplayWindow` with `CompositionTarget.Rendering` + `WriteableBitmap`; exposure/gain controls. |
| 5 | **Run** page — program list, Start / Pause / Stop, `IProcessService` run + step events; `IOperatorPrompt.ConfirmAsync` as a modal; the replaceable widget region. |
| 6 | **Programming** page — WPF editor for process programs / `.hrec` recipes. |
| 7 | Extend `IOperatorPrompt` (text / choose / pick-point / pick-ROI); teaching-overlay infrastructure on the live feed. |
| 8 | **Process Trainer** page — Trainer system. |
| 9 | Process Trainer — Vision model authoring + inspection. |
| 10 | Process Trainer — Dispense verify UI + Calibration (needle); Layer 2 dispense runner sequence. |
| 11 | **Process Sequence** page — real content (process-step sequencing). |

Phases 2–6 deliver a usable operator app at parity with the headless cores.
Phases 7–11 deliver the interactive features that justify the UI. Each phase =
code + bench-verified, the established per-step rhythm.

## 12. Open questions for review

- **Shell navigation is resolved** (§6) — hub-and-spoke: a four-tile home
  launcher, full-screen pages with Back. No persistent nav rail except inside
  the Machine page.
- **Naming resolved** — the page is **Process Trainer**. **Programming
  resolved** — a WPF-built editor, not an embedded external editor.
- **Process Sequence vs Programming** — both touch process authoring. Process
  Sequence currently mirrors Process Trainer's layout as a placeholder; its
  real scope (step sequencing vs. the Programming editor's text editing) is
  defined when Phase 11 starts.
- Should the `HORIZON` engine-state header + global log live only on the
  Machine page (as the mockup shows), or be shell-wide chrome visible on every
  page?
- `DisplayWindow` on its own dedicated UI thread from the start, or
  single-threaded until profiling shows thread-0 contention? (§6.)
- `WriteableBitmap` vs. `D3DImage`/GPU for the frame sink — start with
  `WriteableBitmap` (§7.2 escalation path) unless a known high-res/high-FPS
  camera already forces the GPU path.
- Display sampler cadence — ~20–30 Hz for telemetry assumed; confirm against
  operator expectations.
- `IOperatorPrompt` extension (§9) — does it grow on `IProcessService`, or does
  a richer teaching contract warrant its own interface?
- Does Layer 3 own a settings/preferences surface (the legacy `UserPreferences`),
  or is that also deferred?
