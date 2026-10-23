# OEE — station uptime / utilization tracking

How the station spends each 24 h day, recorded by Layer 2 and shown by FAAM's **OEE** page
(Home › OEE). Total available time = 24 h (a local calendar day; 23 / 25 h across a DST change).

## States

Every instant of the day is exactly one state, by priority:

| State | Meaning |
|---|---|
| **Down** | An engineer / technician declared the station down. Wins over everything — counts even while FAAM is closed, until someone brings it back UP. |
| **Offline** | No Horizon head (FAAM.UI) running. |
| **Running** | Something executing: a program / deck / recipe / `.moon` script (bracketed in `ProcessService`), a Run-page step or a manual alignment / scan (FAAM's `ActivityGate`). |
| **Standby** | Online, nothing executing, for **less than 30 min** — loading / handling between units. Counts as production. |
| **Idle** | Online, nothing executing for **30 min or more** — the *whole* gap is idle (it turns from Standby to Idle retroactively once it reaches 30 min). |

Derived totals: **Production** = Running + Standby; **Up** = Production + Idle (FAAM online and not down).

## KPIs (over elapsed time — the full 24 h for a past day, "so far" for today)

- **Uptime** = Up ÷ elapsed
- **Availability** = 1 − Down ÷ elapsed
- **Utilization** = Production ÷ elapsed
- **Quality** = Pass ÷ (Pass + Fail) of finished DUT runs (Aborted not judged)
- **OEE** = Utilization × Quality — performance needs an ideal cycle time and is not tracked (taken as 100 %)

## Layer 2 — `Horizon.Workflow/Oee/`

- `OeeStore` — machine-global SQLite `%APPDATA%\Horizon\oee.db`, times as UTC unix ms:
  `session` (online; `last_seen` heartbeat every 60 s so a crash still has an end),
  `run` (executions), `downtime` (start / end, `down_by`, `up_by`, reason, up note, station),
  `unit` (finished DUTs + verdict). Degrades to a no-op if the DB cannot be opened.
- `OeeTracker` (`IProcessService.Oee`) — `StartSession` / `EndSessionAsync`,
  re-entrant `BeginProduction(kind, name)` tokens (outermost bracket = one run row),
  `GoDown(name, reason)` / `GoUp(name, note)`, `RecordUnit`, live `Status`, `GetDayReport`.
  Writes go through one background writer (callers never block on disk). On start it closes
  what a crashed process left open (sessions / runs end at their last heartbeat); open
  downtime is kept. Idle is never stored — it is derived.
- `OeeCalculator` — pure classification of a window into `OeeSegment`s. The window is widened
  by the idle threshold on both sides first, so a gap straddling midnight is judged by its
  real length.

## Layer 3 — FAAM.UI

- `EngineHost.StartOeeSession` (shell up) / `ShutdownAsync` → `EndSessionAsync` (station offline).
  `EngineHost` also holds a production token while the `ActivityGate` is busy (Run-page steps,
  alignment / scan panels).
- **OEE page** (`Views/OeeView`, Operator level): live state badge, day picker, 24 h timeline
  (`OeeTimelineBar`), KPI tiles, time breakdown, downtime log, last-7-days table.
- **DOWN / UP toggle** (`Views/DowntimeBar`, on the Engineer page and the OEE page, Technician
  level — `AccessPolicy.Downtime`), via `StationStateWindow`:
  - **DOWN** asks for the engineer / technician's **name**, a **category** (editable preset list:
    Mechanical, Electrical, Pneumatic, Motion / stage, Optics / vision, Software / controls,
    Material / parts, Calibration, Preventive maintenance, Utilities, Other) and the **reason**
    (what is wrong, as seen now). Name + reason required.
  - **UP** shows what was recorded at down time and asks for the **name**, the real **root cause**
    and the **troubleshooting / action taken** — all required.
  - The signed-in operator is logged alongside.
- **Maintenance history** (`MaintenanceHistoryWindow`, OEE page › Downtime log › "Maintenance
  history…"): every down → up entry over a date range (7 / 30 days / 1 year quick picks), free-text
  search over category / reason / root cause / action / names, summary (count, total, MTTR,
  longest, time by category) and CSV export. Backed by `OeeTracker.GetMaintenanceHistory` →
  `OeeStore.SearchDowntimes`. The `downtime` table gained `category`, `root_cause`,
  `action_taken` (added in place to an existing `oee.db`); `up_note` is kept for older rows.
- The shell **header info bar** takes the state colour (green running, light green standby,
  amber idle, red down) with an "UP · RUNNING" / "UP · STANDBY" / "IDLE" / "DOWN" label and since-
  time (down: who declared it). The **pika** follows: hops + zaps while running, blinks in
  standby, faded fur + Zzz when idle, red fur + white "!" when down.
- Down **persists across restarts**: the open `downtime` row is never closed by crash recovery,
  so FAAM reopens DOWN (red tower, red header) and the closed-app time counts as Down until
  someone signs it back UP.

## Tower light (`FAAM.UI/TowerLight.cs`)

The IOTower stack follows the same state, so the floor reads the machine from across the room:

| Station state | Green | Yellow | Red | Buzzer |
|---|---|---|---|---|
| Running | steady | | | |
| Standby (< 30 min, waiting for the next unit) | blink 0.5 s | | | |
| Idle (>= 30 min) | | steady | | |
| Down (declared) | | | steady | steady 3 s on the declaration, then off |
| Waiting on the operator (askUser confirm, serial entry, alert acknowledge, jog popup, Run-page capture prompt) | | blink 0.5 s | | |
| Safety stop (E-Stop / watchdog, until Acknowledge) | | | blink | 3 beeps on the trip |
| FAAM closed | off | off | off | off |

Priority: safety stop > down > waiting on the operator > running / standby / idle — so a program
blocked on a prompt shows yellow blinking, not green. Prompts are counted by `OperatorAttention`
(`EngineHost.Attention`, held by `OperatorPrompt` while a blocking dialog is up) plus
`CapturePromptHub.IsShown`; timed waits (`flow.waitPopup`) and the motion STOP window don't count.

A 250 ms background pump writes only changed pins (each write bounded to 1 s; a failing write
is logged and retried after 5 s). A manual toggle on Machine › IO holds until the next state
change. Device + pin names live in `%APPDATA%\Horizon\config\faam_tower_light.json` (written with
defaults on first run: `IOTower` / `Tower_Green` / `Tower_Yellow` / `Tower_Red` / `Tower_Buzzer`,
`safetyBeeps: 3`, `downBuzzerSeconds: 3`, `enabled: true`; a Down carried over from before a restart
does not buzz, and declaring UP silences it at once). `EngineHost` starts it with the OEE session and switches all
lamps off at shutdown, before the IO devices disconnect.
