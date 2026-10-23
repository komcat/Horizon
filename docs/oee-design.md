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
  level — `AccessPolicy.Downtime`): each edge asks for the engineer / technician's name
  (`StationStateWindow`, required) plus an optional reason / note; the signed-in operator is
  logged alongside.
- The header **pika** follows the state: hops + zaps while running, blinks in standby, sleeps
  (Zzz) when idle, red "!" when down.
