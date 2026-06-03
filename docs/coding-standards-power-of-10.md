# Mission-critical coding standard — NASA/JPL "Power of 10" (C# adaptation)

Horizon drives motion hardware in a closed loop against live sensor feedback
(active alignment: `HillClimbEngine`, `CoarseToFineStrategy`, the FAU pre-align /
U-correction routines). A runaway loop or a hung I/O call can crash a hexapod into
a part. This document defines the coding rules we hold that code to, how they are
**enforced at compile time**, and how we tighten them over time.

## Why Power of 10 (and not JSF++/MISRA)

The well-known safety-critical standards are:

| Standard | Origin | Notes |
|---|---|---|
| **NASA/JPL "Power of 10"** | NASA JPL (G. Holzmann, 2006) | 10 short rules. **What we follow** — matches our needs and adapts cleanly to managed C#. |
| **JSF Air Vehicle C++** | Lockheed Martin F-35 (the "Air Force" standard) | A **C++** standard; ~40% (pointers, preprocessor, `new`/`delete`) is N/A in C#. |
| **MISRA C / C++** | Automotive | The rulebook JSF++ derives from; also C/C++. |
| **DO-178C** | Avionics (RTCA) | A *certification process*, not a coding standard. |

We adopt the **Power of 10 principles** and express them as rules the C# compiler
and Roslyn analyzers can enforce. Pure C/C++ rules (memory, pointers, preprocessor)
are satisfied by the managed runtime and dropped.

## The rules, mapped to C# enforcement

| # | Power of 10 rule | How we apply it in C# | Enforced by |
|---|---|---|---|
| 1 | Simple control flow; no recursion | No unbounded recursion; no `goto` | `S2190` = **error** |
| 2 | Every loop has a fixed upper bound | Every `while`/`for` has an explicit cap; **no statically-infinite loop**. Also temporal: every hardware `await` is time-bounded with a timeout. | `S2189` = **error**; `CA2016`/`CA2247` = **error**; convention + review |
| 3 | No dynamic allocation after init | N/A — GC-managed. Avoid per-step allocation in hot loops where it matters. | review |
| 4 | Short functions / low complexity | Keep methods focused; watch complexity warnings | `S138`, `S1541`, `S3776` = warning (→ error as cleaned) |
| 5 | Assertions / validate invariants | `Config.Validate()` at entry; guard sensor reads (finite-value checks) | convention |
| 6 | Smallest possible scope | Declare locals where used | `IDE0059` = warning |
| 7 | Check every return value; validate inputs | Check every `Result`/`Result<T>`; validate parameters and **sensor inputs at the boundary** | `CA1806`, `S2201` = warning; `Nullable` = on |
| 8 | Limit the preprocessor | N/A in C# (no real preprocessor) | — |
| 9 | Limit pointers | N/A — `AllowUnsafeBlocks` only in UI/App; no pointer arithmetic in engine/workflow | review |
| 10 | Max warnings + static analysis, zero warnings | Analyzers at `latest`/`Recommended`, `EnforceCodeStyleInBuild`; critical rules are errors | `Directory.Build.props` + `.editorconfig` |

## What's wired up

- **`Directory.Build.props`** (repo root) — applies `AnalysisLevel=latest`,
  `AnalysisMode=Recommended`, `EnforceCodeStyleInBuild`, `Nullable`, and references
  **SonarAnalyzer.CSharp** to every project.
- **`.editorconfig`** (repo root) — per-rule severities. The mission-critical rules
  (`S2189`, `S2190`, `CA2016`, `CA2247`) are **error**; the noisier ones are
  **warning** so the brownfield solution still builds.

We deliberately did **not** flip a blanket `TreatWarningsAsErrors`: on a 24-project
brownfield solution that would bury the high-value findings under thousands of
pre-existing style warnings. Instead the rules that catch *unsafe behaviour* are hard
errors everywhere, and the *quality* rules are warnings you ratchet up.

### When an error-level rule fires on legitimate code

A genuine service loop (`while (true) { await ...; }`) or similar will trip `S2189`.
That's the rule working — every unbounded construct must be *seen and justified*.
Suppress it **at the single occurrence** with a reason, never globally:

```csharp
#pragma warning disable S2189 // justified: telemetry pump runs for process lifetime; exits on token cancel
while (true) { ... }
#pragma warning restore S2189
```

## The ratchet (how we tighten over time)

1. **Now:** unsafe-behaviour rules are errors solution-wide; quality rules are warnings.
2. **Per cleaned assembly:** add a folder-scoped `.editorconfig` promoting `S138`,
   `S1541`, `S3776`, `CA1806` to **error** for that assembly. Start with the active-
   alignment code (`Horizon.Workflow/HillClimb`, `Scanning`, `FauPreAlign`) and the
   motion facet (`Horizon.Engine/Facets`).
3. **Eventually:** once an assembly is clean, set `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`
   for it via a scoped `Directory.Build.props`.

## Conventions for active-alignment code (beyond the analyzers)

These are review rules the tools can't fully check:

- **Bounded loops.** Every climbing/scanning loop has an explicit iteration or travel
  cap (`ZBudget_um`, `ZStallLimit`, `MaxLateralStepsPerDirection`,
  `MaxTotalDistanceMm`, `ConsecutiveDecreasesLimit`). Never rely on "the signal will
  eventually decline" or "the stage will eventually hit a hard limit" for termination.
- **Time-bounded I/O.** Every hardware `await` (move, position read, feedback) runs
  under a timeout (`HillClimbConfig.IoTimeoutMs` via `WithTimeoutAsync`). A hung
  controller must surface as a `TimeoutException`, not a stall.
- **Validate sensor inputs.** Reject non-finite feedback (`NaN`/`∞`) at the read
  boundary (`ReadFeedbackChecked`) — never let it reach a peak comparison.
- **Fail safe.** On any abort (cancel, timeout, error), return the stage to the last
  known-good position before surfacing the failure.
- **Check every `Result`.** Don't discard a `Result`/`Result<T>` from a move or read.

## Status of the active-alignment code (2026-06)

`HillClimbEngine` and `CoarseToFineStrategy` meet the bounded-loop, time-bounded-I/O,
and input-validation rules. Their internal error channel is still
exception-based-but-contained (every throw is caught by the run's top-level handler,
which returns the stage to the peak) rather than `Result`-propagating end to end;
converting it to `Result` throughout is a tracked follow-up, not a safety gap.
