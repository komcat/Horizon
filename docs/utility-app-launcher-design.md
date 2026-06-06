# Utility App Launcher (Run-page "phone" springboard)

Status: **Design — not yet implemented.** This captures the agreed architecture and
the locked decisions before any code is written. Brief: the Run page's middle area
(today a `TabControl{U correction, FAU pre-align}` + a stacked Hill Climber panel +
the SiPhOG connect panel) becomes a **phone-style app springboard**. Idle, it shows a
grid of utility-app icons with live status badges; tapping one swaps that app's panel
into the area with a top-left **Back** button that returns to the home grid. The four
apps are **U correction, FAU pre-align, Hill Climber, SiPhOG connection.** A new
Layer 2 → Layer 3 control seam lets workflow command blocks open/close and start/stop
each app, and the home-grid badges surface which apps are running.

## Goal (from the mockup)

```
 HOME (idle)                          APP OPEN (e.g. Hill Climber)
┌─────────────────────────┐         ┌─────────────────────────┐
│  ┌─────┐    ┌─────┐      │         │ [← Back]  HILL CLIMBER ● │
│  │ U   │    │ FAU │      │         ├─────────────────────────┤
│  │ corr│    │ pre │      │  tap →  │                         │
│  └─────┘    └─────┘      │         │   (existing HillClimb   │
│  ┌─────┐    ┌─────┐      │         │    panel content,       │
│  │Hill │    │SiPhOG      │         │    unchanged)           │
│  │climb●│   │ conn ◉     │         │                         │
│  └─────┘    └─────┘      │         │                         │
└─────────────────────────┘         └─────────────────────────┘
   ● = running badge                    Back → returns to HOME
   ◉ = connected badge                  (app keeps running)
```

- The **springboard replaces** the current middle-area cols (the alignment
  `TabControl` + Hill Climber stack + SiPhOG connect panel) with a single
  `UtilityAppHost` control.
- The **SiPhOG SAG/target chart stays where it is, over the camera column.** It is a
  cross-cutting always-on visualization, not an app screen (see "The trade-off").
- Apps **keep running in the background** when you navigate away — navigation never
  disposes a panel (see "Two independent axes").

## Decisions (locked)

| Topic | Decision | Rationale |
|---|---|---|
| Is this a new layer? | **No.** The apps are Layer 3 panels; the springboard is an **app-shell inside L3**, plus an **inverted facet** (L2→L3 provider) for command control. "L4 apps" is only a nickname. | Keeps the L3→L2→L1 dependency rule intact. The L2→L3 seam reuses the existing `IOperatorPrompt` / `SetHillClimbFeedbackProvider` pattern. |
| How many on screen | **One app at a time** (full phone springboard). All **4** apps go in the launcher. | User choice. Honors the phone metaphor; maximizes per-app area. |
| Command-block scope | **Both navigate + run** — `app.open` / `app.home` (foreground) **and** `app.start` / `app.stop` (run the routine). | User choice. Lets a deck both guide the operator's screen and drive a routine. |
| Generality | **Concrete 4 apps now.** Hardcode the four in `UtilityAppHost`; generalize to a registry-driven `IUtilityApp` framework only if a 5th app appears. | User choice (YAGNI). The contract below is still defined so the four are uniform and a later registry is a small refactor. |
| Panel changes | **Wrap, don't rewrite.** The Back/title/badge is host-provided chrome around each existing panel. Panels gain only a uniform run-state event. | Minimizes risk to the working alignment/SiPhOG panels. |

## The core insight: two independent axes

Conflating these will cause real bugs (dropped SiPhOG socket, killed climb):

| Axis | Question | Example |
|---|---|---|
| **Foreground** | Which app screen is *visible*? | Operator is looking at Hill Climber |
| **Run-state** | Which app's *routine is active*, regardless of what's shown? | SiPhOG streams **and** hill-climb scans while the operator navigated to U correction |

Consequences:

- **Navigation must never dispose/recreate a panel.** All four are instantiated once
  (as they are today, in `RunView`'s constructor) and kept alive; navigation toggles
  `Visibility` only. Disposing SiPhOG would drop the socket; disposing Hill Climber
  would abort a live climb.
- The **home-grid badge is driven by run-state**, which is decoupled from foreground.
  An app shows "● running" on the springboard while another app is foreground.

## The app contract (Layer 3)

A small uniform contract so the host treats all four identically. (Defined now even
though the host is concrete — it keeps the four uniform and makes a later registry a
trivial refactor.)

```csharp
// FAAM.UI — one per utility app.
enum AppRunState { Idle, Running, Connected, Error }

interface IUtilityApp
{
    string    Id    { get; }        // "ucorrection","faupre","hillclimb","siphog"
    string    Title { get; }
    object    Icon  { get; }        // glyph or image source
    UIElement Content { get; }      // the existing panel, unchanged

    AppRunState State        { get; }   // drives the home-grid badge
    string?     StatusDetail { get; }   // "Scanning…", "V=1.83", "Disconnected"
    event Action StateChanged;          // host subscribes → repaint badge

    Task StartAsync(CancellationToken ct);  // command-block "run"
    Task StopAsync();                       // command-block "stop"
}
```

Adapters (thin — the panels already expose the source state):

| App | State source today | Maps to |
|---|---|---|
| Hill Climber | `HillClimbEngine.StateChanged` / `IsRunning`; color-coded `StateText` | `Running`/`Idle`/`Error` directly |
| SiPhOG | `SiphogClient.ConnectionChanged` / `IsConnected` | `Connected`/`Idle` |
| U correction | `_running` bool + `StatusText` (no event today) | `Running`/`Idle` — **add a small `StateChanged` event** |
| FAU pre-align | `_running` bool + `StatusText` (no event today) | `Running`/`Idle` — **add a small `StateChanged` event** |

`StartAsync`/`StopAsync` route to each panel's existing start path: Hill Climber's
`OnStart`/`OnStop`, SiPhOG's `EnsureConnected()`/`Client.Disconnect()`, and the
U-corr/FAU-pre start handlers. Mutual exclusion is **already enforced** by the
`ActivityGate` (`ActivityKind.Alignment` is taken by all three alignment routines, so
two cannot run at once); SiPhOG is passive and takes no gate.

## `UtilityAppHost` (the L3 app shell)

A new `UserControl` that replaces the middle-area columns in `RunView.xaml`:

- **Home view** — a 2×2 grid of icon buttons; each shows Title + badge bound to its
  `IUtilityApp.State`/`StatusDetail` via `StateChanged`.
- **App view** — host-owned chrome (`← Back`, Title, status dot) above a
  `ContentControl`/`Grid` that shows the selected app's `Content`. All four `Content`
  panels live in the visual tree at once (cached); switching toggles `Visibility` so
  background run-state survives navigation.
- **Back** returns to Home **without** stopping the app.

`RunView` wiring changes: today it `Attach`-es the four panels directly. Instead it
constructs the four panels, wraps each in its `IUtilityApp` adapter, hands the four to
`UtilityAppHost`, and registers the host into Layer 2 (below). The SiPhOG chart,
`SetHillClimbFeedbackProvider`, and `AddSignalSource` wiring are unchanged.

## The Layer 2 → Layer 3 command seam

Modeled exactly on `IProcessService.SetHillClimbFeedbackProvider`:

```csharp
// IProcessService (Layer 2)
void SetUtilityAppHost(IUtilityAppHost? host);

// Layer 2 interface; implemented by the L3 UtilityAppHost control.
interface IUtilityAppHost
{
    Result        Open(string appId);     // foreground (navigate)
    Result        Home();                 // back to launcher
    Task<Result>  StartAsync(string appId, CancellationToken ct);
    Task<Result>  StopAsync(string appId);
    AppRunState   GetState(string appId); // for flow.if* gating later
}
```

If no host is registered (console/headless runs), every call returns
`Result.Fail(...)` with a clear message — identical to how the hill-climb feedback
seam behaves with no provider. No boundary violation, no crash.

### New workflow steps (`StepDispatcher`) + catalog (`OpCatalog`)

Same recipe as the existing `hillclimb.*` cases. All marshal to the UI thread inside
the host (`Open`/`Home` are instantaneous; `StartAsync`/`StopAsync` await the
routine). Steps fail clearly when `_utilityAppHost == null`.

| Op key | Category | Params | Behavior |
|---|---|---|---|
| `app.open` | App | `appId` (Text) | Foreground the app's screen. |
| `app.home` | App | — | Return to the launcher home grid. |
| `app.start` | App | `appId` (Text) | Foreground **and** run the routine; awaits completion. |
| `app.stop` | App | `appId` (Text) | Stop the routine (leaves the screen as-is). |

`OpCatalog` entries follow the existing shape, e.g.:

```csharp
new("app.start", "Open & Run App", "App", DeviceKind.None,
    new[] { new ParamSpec("appId", "App", ParamKind.Text, "hillclimb") }),
```

**Overlap note — `hillclimb.start` already exists.** `app.start("hillclimb")` and
`hillclimb.start` would both run a climb. Resolution: keep `hillclimb.*` as the
**parameterized** path (distance/targetV/captureAs) and treat `app.start` as the
**simple "open this app and press its Start"** path. `app.start` delegates to the
panel's start handler (default params), so they don't fight. Document this in the
catalog tooltips. (A later cleanup could make `hillclimb.start` the only run path and
have `app.*` be navigation-only, but the locked decision is "both".)

## The trade-off (accepted)

A one-app-at-a-time springboard **loses the current side-by-side** of the Hill Climber
panel (col 1) next to the SiPhOG numeric readouts (col 2). Mitigation that makes this
acceptable (and why "all 4, one at a time" was chosen):

- The **SiPhOG SAG/target chart stays over the camera**, so the live signal is still
  visible during a climb.
- The Hill Climber panel shows V start/cur/peak itself.
- What's actually hidden during a climb is the SiPhOG **numeric channel table** (SLED
  current, photo current, temp) — a setup-time concern, rarely watched mid-climb.

If this bites in practice, the fallback is the "pinnable secondary widget" option
(pin one app — e.g. SiPhOG readouts — as a small overlay during a climb), deferred.

## Phasing

- **Phase 1 — pure L3, zero L2 changes.** Build `UtilityAppHost` (home grid + swap +
  Back chrome + status badges) wrapping the existing panels; add the small
  `StateChanged` events to U-corr/FAU-pre. Fully shippable and reversible on its own.
- **Phase 2 — the L2 seam.** `IUtilityAppHost` + `IProcessService.SetUtilityAppHost`
  + the four `app.*` steps in `StepDispatcher` + `OpCatalog` entries. This is what
  lets command blocks drive the apps.
- **Phase 3 — polish.** Real icons, badge animation, an explicit "running in
  background" treatment on the home grid, and (if needed) the pinnable secondary
  widget.

## Files touched (anticipated)

- **New**: `src/FAAM.UI/Views/UtilityAppHost.xaml(.cs)`; `src/FAAM.UI/Apps/IUtilityApp.cs`
  + four thin adapters (`Apps/*AppAdapter.cs`).
- **L3 edits**: `Views/RunView.xaml` (replace middle-area cols with the host),
  `Views/RunView.xaml.cs` (construct/wrap/register), `Views/UCorrectionPanel.xaml.cs`
  + `Views/FauPreAlignPanel.xaml.cs` (add `StateChanged`).
- **L2 edits (Phase 2)**: `Horizon.Workflow/IProcessService.cs` +
  `ProcessService.cs` (`SetUtilityAppHost`, field), `Execution/StepDispatcher.cs`
  (`app.*` cases), `FAAM.UI/ViewModels/OpCatalog.cs` (catalog entries).
- **New L2 type (Phase 2)**: `Horizon.Workflow/IUtilityAppHost.cs` (+ `AppRunState`
  if shared with L2; otherwise keep the enum L3 and pass strings across the seam).

## Open questions (for later phases)

- Should `flow.ifAppState` (gate a deck on an app's run-state) exist? Useful for "if
  hill-climb succeeded, continue" — but `signal.read` + `flow.ifsignal` may already
  cover the SiPhOG-value case. Defer until a concrete deck needs it.
- Icon source: glyph font vs bundled PNGs. Glyphs are theme-friendly (matches the
  `SystemColors` theme rule); decide in Phase 3.
