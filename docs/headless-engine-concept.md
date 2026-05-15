# Headless Core Engine — Concept Draft

> Status: **DRAFT for review** — not yet approved or scheduled.
> Author: design discussion, 2026-05-15.
> Purpose: capture the plan to split Horizon into a UI-free core engine plus
> thin front-ends (console now, web later). Review and revise before any code.

---

## 1. Goal

Create a **core engine with no UI dependency** — a headless host that can:

- Initialize hardware from JSON config (motors, camera, IO, current measurement,
  laser, pneumatic).
- Expose per-hardware status.
- Provide a camera live feed (without a WPF widget).
- Run product-dependent programs (`.hproc` / `.hrcp`) against the engine.

The console host is the **first** front-end. The WPF app stays as a second
front-end. A web UI becomes possible as a third.

---

## 2. Current state — we are ~90% there

`Horizon.Core.AppContext` is already the headless engine. Confirmed:

- **No code outside `Horizon.App` references WPF.** Every manager
  (`CameraManager`, `UnifiedMotionManager`, `EziIOManager`,
  `CurrentMeasurementManager`, `LaserControllerManager`, `PneumaticManager`,
  `SafetyWatchManager`) and both engines (`ProcessEngine`, `RecipeEngine`) are
  `net8.0` with zero UI dependency.
- Only `Horizon.App` is `net8.0-windows` + `UseWPF`.

This is **not a rewrite**. It is: add a second host that drives `AppContext`,
and break a small number of delegate couplings.

### Reality check

"No UI dependency" does **not** mean cross-platform. Every hardware SDK is
Windows-native P/Invoke (ACS, PI, Fastech, VISA, uEye). The console host stays
`net8.0-windows` and Windows-only. The win is removing WPF → headless
automation, CI smoke tests, scripted runs, remote operation.

---

## 3. Couplings to break

Small, well-defined list. None touch hardware logic.

| # | Coupling | Today (App) | Console replacement |
|---|----------|-------------|---------------------|
| 1 | `ILogSink` | `SerilogLogSink` | `ConsoleLogSink` (interface already in Core) |
| 2 | `PromptUser` delegate (`DispenseExecutor`, camera-verify) | WPF dialog | `Console.ReadLine()` prompt |
| 3 | Startup connection sequence | `SplashWindow` async connect | Extract into `AppContext.InitializeAsync()` / `ConnectAllAsync()` with stdout progress |
| 4 | `ProcessStepNotify` / step callbacks | routed to panels | routed to stdout lines |

---

## 4. Proposed project structure

```
Horizon.Engine.Host   (new, net8.0-windows, NO UseWPF) ── console front-end
    └─ Horizon.Core (AppContext)  ◄── unchanged, already headless
Horizon.App           (WPF shell)  ◄── unchanged, second front-end
```

Console host = a command loop over the engine: `connect`, `status`, `motor`,
`io`, `cam`, `run <product> <program>`, etc. The "status menu per hardware" is
just iterating managers and printing connection/value state — each manager
already exposes that for the panels.

---

## 5. Camera live feed (console)

The WPF live-feed widget **cannot** be hosted in a console — it needs
`PresentationFramework` + a Dispatcher, reintroducing the dependency we are
removing.

We do not need it. `CameraManager` already produces raw frames; the WPF
`LiveFeedPanel` only does `byte[] → BitmapSource` conversion at the edge. The
frame data is UI-free. Expose frames from the engine; let each front-end render
its own way.

| Option | What it is | Trade-off |
|--------|-----------|-----------|
| **A. MJPEG over HTTP** *(recommended)* | Engine hosts a small HTTP endpoint; `cam stream` prints a URL viewable in any browser/VLC | Real live feed, zero UI deps, works remotely, ~80 lines. Natural path to a web UI. |
| **B. Snapshot-to-file** | `cam snap` writes a PNG; optional refresh loop | Dead simple, good for diagnostics, not truly live. |
| **C. Detached WPF viewer** | Keep existing widget in a separate mini-app, fed over IPC | Reuses widget, but back to maintaining a WPF process. |

Plan: **A as primary**, **B included for free** (trivial).

---

## 6. Engine boundary — in-process vs out-of-process

### The question

- **In-process (library):** the front-end references `Horizon.Core` and calls
  managers directly. Engine and UI are the same process.
- **Out-of-process:** the engine runs as its own program; console / web / product
  program are separate processes that talk to it across the process boundary.

### Why it matters

Hardware can be opened by only **one** process — whichever loaded the SDK DLLs.
If the engine is just a library, the console and the WPF app are two separate
copies of the engine, both trying to open the same hardware → conflict. A web UI
cannot be in-process with a desktop console at all.

If the engine is its **own process** with a communication surface, one engine
owns the hardware and every front-end is a thin client.

```
        ┌─────────────────────────────┐
        │  Horizon.Engine (service)   │  ← owns ALL hardware, one instance
        │  AppContext + all managers  │
        └────────────┬────────────────┘
                     │  IPC surface
        ┌────────────┼────────────┬──────────────┐
   Console CLI    WPF app      Web UI      Product program
```

### Terminology

- **IPC (Inter-Process Communication):** mechanisms that let separate processes
  exchange data (the OS isolates processes — no shared memory, no direct calls).
- **RPC (Remote Procedure Call):** an IPC style that makes calling a function in
  another process look like a local call — arguments serialized, shipped, run
  remotely, result shipped back.

### Options (.NET on Windows)

| Mechanism | What it is | Good for | Cost |
|-----------|-----------|----------|------|
| Named Pipes | Windows IPC channel; local-only, fast | Local console ↔ engine | Low-level; serialize messages yourself |
| **Local HTTP / REST** (ASP.NET Core) | Engine hosts a small web server on `localhost`; clients send HTTP | Console, web UI, any language, remote | Medium; design endpoints |
| gRPC | RPC framework; `.proto` contract → typed stubs; built-in streaming | Typed multi-client, streaming | Medium; learn `.proto`, not browser-friendly |
| SignalR / WebSockets | Server pushes events to clients in real time | Live status, logs, frames pushed to web | Added alongside REST |

### Recommendation: ASP.NET Core hosting REST + SignalR on `localhost`

1. **One contract serves every client.** Console and future web UI make the same
   HTTP calls. gRPC and named pipes cannot be called from a browser directly.
2. **Request/response → REST.** e.g. `POST /motors/{device}/move`,
   `GET /hardware/status`, `POST /products/{name}/run` — maps onto existing
   managers.
3. **Live data → SignalR.** Hardware status, process step events, log lines
   pushed instead of polled — replaces per-panel `DispatcherTimer` polling.
4. **Camera feed fits naturally.** MJPEG becomes `GET /cameras/{id}/stream` on the
   same server; browser shows it with an `<img>` tag.
5. **Localhost-only by default** (bind `127.0.0.1`); not network-exposed unless
   chosen. Flipping that switch later = operate the engine from a tablet.

`PromptUser` becomes a SignalR round-trip: engine pushes "need confirmation",
client answers.

### Trade-off

In-process is less work today (no serialization, no server). Out-of-process with
HTTP is more upfront work but is what makes the web UI possible at all, and makes
console + WPF + web into the same kind of client. Building in-process now means
rebuilding this layer later anyway.

---

## 7. Pragmatic middle path — defer the network decision

Structure so the in-process / out-of-process choice is not locked in:

1. Define a clean **`IHorizonEngine` interface** in Core — every operation a
   front-end needs.
2. `AppContext` implements it directly (in-process).
3. Console / WPF code talks **only** to `IHorizonEngine`, never to managers
   directly.
4. When the web UI is wanted, add an ASP.NET host wrapping `AppContext`, plus an
   `HttpHorizonEngineClient : IHorizonEngine` that talks HTTP. Existing clients
   keep working — they just get a different `IHorizonEngine` implementation.

The console phase ships fast in-process; the network boundary becomes a clean
later addition, not a rewrite.

### `IHorizonEngine` — rough surface (to be refined)

```csharp
public interface IHorizonEngine
{
    // Lifecycle
    Task InitializeAsync(CancellationToken ct);
    Task ConnectAllAsync(IProgress<string>? progress, CancellationToken ct);

    // Status
    HardwareStatusSnapshot GetHardwareStatus();
    event Action<HardwareStatusChanged> StatusChanged;

    // Motion / IO / Pneumatic / Camera / Measurement / Laser
    Task<Result> MoveAsync(string device, string axis, double position, CancellationToken ct);
    Task<Result> SetOutputAsync(string device, string pin, bool state);
    Task<Result> ExtendSlideAsync(string slide);
    Task<CameraFrame> SnapAsync(string camera, CancellationToken ct);
    // ... etc per domain

    // Product programs
    Task<Result> RunProgramAsync(string product, string programFile, CancellationToken ct);
    event Action<ProcessStepEvent> StepEvent;

    // Logs
    event Action<LogEntry> LogEmitted;
}
```

(Names illustrative — final surface derived from what panels currently call.)

---

## 8. Suggested phasing

| Phase | Deliverable |
|-------|-------------|
| 1 | Define `IHorizonEngine`; `AppContext` implements it; extract `InitializeAsync` / `ConnectAllAsync` from `SplashWindow`. |
| 2 | `Horizon.Engine.Host` console project: `ConsoleLogSink`, command loop, hardware status menus. |
| 3 | Console `PromptUser` implementation; run `.hproc` / `.hrcp` with stdout step events. |
| 4 | Camera live feed — MJPEG endpoint (Option A) + snapshot (Option B). |
| 5 | *(later, if web UI confirmed)* ASP.NET host wrapping `AppContext`; REST + SignalR; `HttpHorizonEngineClient`. |
| 6 | *(later)* Web UI as a client of the HTTP surface. |

Phases 1–4 deliver the headless console engine. Phases 5–6 are the web path and
can be decided after reviewing this draft.

---

## 9. Open questions for review

- Confirm: engine runs **in-process first** (Phase 1–4), HTTP surface deferred to
  Phase 5? Or commit to out-of-process from the start?
- Should the engine eventually be a **Windows Service**, or a plain background
  `.exe` launched on demand?
- Camera feed: is **MJMPEG-over-HTTP** acceptable, or is snapshot-on-demand enough
  for the console phase?
- Does the WPF app get refactored to consume `IHorizonEngine` too (unifies all
  front-ends), or stay as-is for now?
