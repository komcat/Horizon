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
| Engine boundary | **In-process.** `Horizon.UI` references `Horizon.Workflow` + `Horizon.Engine` directly. The HTTP/SignalR boundary stays deferred until a *web* UI is actually wanted (`headless-engine-concept §7`). |
| One-process rule | Run `Horizon.UI` **or** legacy `Horizon.App`, never both — a single process owns the hardware SDKs. |
| Why a UI now | Trainer, Vision, and Dispense are **inherently interactive** (live feed, click-to-teach, ROI, visual verify). They cannot be finished as headless Layer 2 phases — they are Layer 3 features built on Layer 2's runner. |

## 3. Scope

**In Layer 3:**
- Hardware status + manual control surfaces (motion/jog, IO, pneumatic, camera,
  measurement, laser) — driven by `IHorizonEngine` facets.
- Product management + teach-points + process/recipe run UI — driven by
  `IProcessService`.
- **Live camera feed** rendering (raw frames → `BitmapSource` at the UI edge).
- The **interactive teaching surfaces**: Trainer, Vision model authoring,
  Dispense verify — the features that justify a real UI.
- The front-end implementation of the operator-feedback contract
  (`IOperatorPrompt`, extended — §7).

**Out:**
- Hardware logic, workflow logic — all below the interface line.
- Hardware config authoring — that is `Horizon.ConfigTool`
  (`docs/config-tool-design.md`), separate dev tooling.
- Web UI — a later client of a future HTTP surface, not this document.

## 4. UI framework — open question

`Horizon.UI` is greenfield, so the framework is a fresh choice (the no-refactor
decision means we are **not** bound to legacy WPF + AvalonDock).

| Option | For | Against |
|---|---|---|
| **WPF** *(recommended)* | Team knows it; hardware stack is Windows-only anyway so cross-platform buys nothing; mature camera-bitmap path; HALCON has WPF display widgets. | Windows-locked (no real cost here). |
| Avalonia | Cross-platform, modern XAML. | New to the team; no benefit while every hardware SDK is Windows P/Invoke. |
| WinUI 3 | Modern Windows. | Less mature tooling; no team experience. |

Recommendation: **WPF**, but **without AvalonDock** — a fixed, purpose-built
operator layout rather than the legacy free-floating dock panels. Confirm in
review.

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

## 6. Engine wiring + status flow

- **Composition root** creates `IHorizonEngine`, calls `InitializeAsync` /
  `ConnectAllAsync`, then constructs the Layer 2 `IProcessService` on top.
  A startup/splash screen drives `ConnectAllAsync(IProgress<string>)`.
- **No `DispatcherTimer` polling.** Layer 1 already runs internal poll loops and
  raises `StatusChanged` / `PositionChanged` / `FrameReady`; Layer 2 raises
  `StepEvent` / `LogEmitted`. Layer 3 panels **subscribe** to these events and
  marshal to the UI thread — the per-panel polling of legacy `Horizon.App` is
  gone.
- **Camera feed.** `ICameraFacet.FrameReady` delivers **raw bytes + pixel
  format**. The `byte[] → BitmapSource` conversion is a Layer 3 edge concern —
  exactly as `headless-engine-concept §5` describes. No engine change needed.
- **MVVM**: ViewModels depend on the facet/`IProcessService` interfaces; a
  `MotionViewModel` taking `IMotionFacet` physically cannot reach IO.

## 7. The interactive contract — extending `IOperatorPrompt`

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
> (§9) — listed here to fix the *direction*, not the final signatures.

## 8. The three interactive features

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

## 9. Phasing

| Phase | Deliverable |
|---|---|
| 1 | `Horizon.UI` skeleton — composition root wiring `IHorizonEngine` + `IProcessService`; startup/connect screen; hardware status dashboard (event-driven). |
| 2 | Manual control surfaces — motion/jog, IO, pneumatic, laser, measurement — over the engine facets. |
| 3 | Live camera feed panel — `FrameReady` → `BitmapSource`; snapshot; exposure/gain controls. |
| 4 | Product + teach-points + process/recipe run UI over `IProcessService`; step highlighting; context watch; `IOperatorPrompt.ConfirmAsync` as a modal. |
| 5 | Extend `IOperatorPrompt` (text / choose / pick-point / pick-ROI); teaching-overlay infrastructure on the live feed. |
| 6 | **Trainer** wizard. |
| 7 | **Vision** model authoring + inspection UI. |
| 8 | **Dispense** verify UI + Layer 2 dispense runner sequence. |

Phases 1–4 deliver a usable operator app at parity with the headless cores.
Phases 5–8 deliver the interactive features that justify the UI. Each phase =
code + bench-verified, the established per-step rhythm.

## 10. Open questions for review

- Confirm **WPF (no AvalonDock)** vs. Avalonia for `Horizon.UI` (§4).
- Fixed operator layout vs. dockable panels — the legacy app was dockable; an
  operator station may prefer fixed task screens.
- Should `Horizon.UI` and the engine stay **one process** (in-process,
  recommended) for the foreseeable future, or is a web UI on the roadmap soon
  enough to justify the HTTP boundary now?
- `IOperatorPrompt` extension (§7) — does it grow on `IProcessService`, or does
  a richer teaching contract warrant its own interface?
- Does Layer 3 own a settings/preferences surface (the legacy `UserPreferences`),
  or is that also deferred?
