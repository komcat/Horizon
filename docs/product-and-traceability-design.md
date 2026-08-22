# Product encapsulation & DUT traceability

Status: **Phase 1 in progress** (FAAM.UI first). This doc covers two related pieces
of operator-facing work that sit on top of the already-stable hardware/system layer:

1. **Product** — clearly encapsulate a product and make load / save / switch a
   first-class, demonstrable operator action.
2. **User + DUT traceability** — a lightweight operator login so every DUT result
   records *who* ran it and *on which station*.

The "System" layer (config, motion/other devices, transformation matrices,
`mm/pixel`) is settled and out of scope here — except to note that those are
**station-scoped** calibration, deliberately *not* moved into the product.

## Decisions (locked)

| Topic | Decision |
|---|---|
| Auth strength | ~~Traceability only~~ — **superseded** by `docs/access-control-design.md`. FAAM.UI now gates access by ranked level; Horizon.UI still uses the original name + PIN picker. |
| Access levels (FAAM.UI) | **Level + employee number + level password.** Four ranked levels (`Operator` < `Technician` < `Engineer` < `Admin`); every screen declares a minimum. The employee number identifies the person and comes from `users.json`; the password is **shared per level**, not per person, so adding an employee never means issuing a credential. Still a shop-floor gate, not a security boundary — but nothing in the shell opens until somebody signs in. |
| Machine identity | **Machine number + credential string**, both machine-global (`app_settings.json`), both stamped onto every `DutRun` alongside the operator. The credential is free text — the machine's signature on the record (calibration cert, asset tag, qualification id). |
| Per-product settings | **Process params only.** Station calibration (`mm/pixel`, jog transforms, device links) stays machine-global. A product-settings store is stubbed but only populated when a concrete process param needs a home. |
| FAAM.UI products | **Default + visible switch.** FAAM keeps defaulting to the `FAAM` product but gains a visible selector so load/switch is demonstrable. |
| "Save product" | **Per-file saves** (already exist), made visible. Programs are the product's *process* and are inseparable from the positions they reference — both already live under the product directory and travel together on switch. No export/import bundle this round. |

## Current state (what already exists)

Layer 2 (`Horizon.Workflow`) already scopes everything to a product:

- A **product is a directory** `%APPDATA%\Horizon\products\{name}\` holding
  `processes\*.hwf`, `recipes\*.hrec`, `decks\*.hwd`, `positions.json`, `dut\*.json`.
  Active product persisted in `products\active.json` (`ProductStore`).
- `ProcessService.LoadProduct / CreateProduct / UnloadProduct` rebuild all
  sub-stores against the selected product. **Switching is pure file re-scoping —
  zero hardware impact.**
- DUT capture already exists: `DutRecord → DutRun → DutEntry` (`Dut/DutModels.cs`),
  written automatically by `ProcessService.RunAsync(program, serial)` /
  `RunDeck(deck, serial)` via `BeginDut`/`EndDut`. A `DutRun` records
  `StartedUtc`, `EndedUtc`, `Source`, `Verdict` (Pass/Fail/Aborted) and timestamped
  key/value entries.
- Horizon.UI already has a product picker (`ProgramEditorPageViewModel`,
  `DeckPageViewModel`). FAAM.UI hardcodes the `FAAM` product in `EngineHost`.

### The two gaps

- **Product has no identity** beyond a folder name, the switch is invisible/unguarded
  in the UI, and FAAM doesn't expose it.
- **Nothing user-related exists.** No login, no operator on a `DutRun`, no station id.
  So "who ran serial X, on which machine" is unanswerable today.

A second structural fact drives the FAAM design: **FAAM's Run page does not run a
`WorkflowProgram`.** Its steps are hardcoded sequences executed directly on the
engine facets (`ProcessStepsPanel.RunStepAsync`). So the existing program-driven
DUT path never fires for FAAM. Phase 1 therefore adds an **interactive DUT session**
to Layer 2 that brackets an operator-driven process (serial in → steps → verdict),
independent of `WorkflowProgram` execution.

## Architecture

Boundaries are unchanged: **Layer 1 untouched.** Layer 2 owns Product + DUT and just
*receives* an operator context. Layer 3 owns the login UI and pushes identity down —
mirroring the existing `IOperatorPrompt` seam (Layer 2 interface, Layer 3 impl).

### Layer 2 — `Horizon.Workflow`

- **`ProductInfo`** (new public record) + `product.json` per product directory:
  `displayName`, `partNumber`, `description`, `schemaVersion`, `createdUtc`,
  `modifiedUtc`. `ProductStore` reads/writes it; `Create` seeds it.
- **`IProcessService` additions**
  - `ProductInfo? ActiveProductInfo { get; }`
  - `event Action<string?>? ProductChanged` (fires on load/create/unload/delete).
  - `LoadProduct` **refuses to switch** while a program run or interactive DUT run
    is active.
  - `SaveProgram` cross-validates position references and logs any that are missing
    from the active product (non-fatal, mirrors ConfigTool's pin cross-check).
- **Operator traceability**
  - **`OperatorContext(UserId, DisplayName, StationId)`** (new public record).
  - `void SetOperator(OperatorContext?)` + `OperatorContext? Operator { get; }`.
  - `DutRun` gains `OperatorId`, `OperatorName`, `StationId` (nullable).
  - Both `EndDut` (program path) and the interactive session stamp the current
    operator onto the run.
- **Interactive DUT session** (operator-driven, no `WorkflowProgram`)
  - `Result BeginDutRun(string serial, string source)` — opens/loads the DUT record
    and starts an in-memory run; refuses if one is already open.
  - `void RecordDutValue(string key, string value, DutValueType type)` — appends to
    the open run.
  - `Result EndDutRun(DutVerdict verdict)` — stamps operator, finalises, persists.
  - `bool HasActiveDutRun { get; }`
  - `DutRecord? GetDutRecord(string serial)` — read-back for the results viewer.

### Horizon.Common

- **`AccessLevel`** enum — `Operator` < `Technician` < `Engineer` < `Admin`, ranked;
  screens compare with `>=`. The numeric values *are* the ordering.
- **`User`** record + **`UserStore`** (`users.json`, machine-global, same config dir
  as `app_settings.json`): `id` (employee number), `displayName`, `level`, `active`,
  plus the legacy `pin` that only Horizon.UI still reads.
- **Level passwords** live in the same file (`levelPasswords`: level name → PBKDF2
  verifier, salted, 100k iterations — never the password itself). An absent or empty
  entry means that level has **no** password and signs in on the employee number
  alone. `UserStore.SignIn(level, employeeId, password)` is the whole check: the
  employee must exist, be active, be registered at *exactly* that level, and the
  level password must match. Its failure message deliberately does not distinguish
  "no such employee" from "wrong level", so the login screen cannot be used to
  enumerate the directory.
- **Schema upgrade.** A `users.json` written before levels existed has no
  `schemaVersion`; every user in it is promoted to `Admin` on first load, because
  those users had unrestricted access before the gate arrived. Locking the station
  down is then an explicit act (demote people in Settings) rather than a silent
  side effect of installing a build that would otherwise lock everyone out of the
  very screen that manages access.
- **`AppSettings.StationId`** — the station's **machine number**; empty falls back to
  the Windows machine name at sign-in.
- **`AppSettings.MachineCredential`** — the station's **credential string**, its
  signature on the DUT record. Free text, empty = unsigned.

### Layer 3 — FAAM.UI (this phase's priority)

- **`LoginWindow`** — three fields: **level**, **employee number**, **level password**
  (the box says so when the chosen level has no password set). On success: build an
  `OperatorContext` (employee + level + machine number + credential) and call
  `Process.SetOperator(...)`.
- **Flow:** `StartupWindow` (connect hardware) → **`LoginWindow`** → `ShellWindow`.
  Sign-in does **not** block engine bring-up, but it does gate the shell: cancelling
  the first sign-in exits rather than opening a shell where every screen is locked.
  **Logout** locks the station with the engine still up; re-login restamps.
- **`AccessPolicy`** — the single table of which level each screen needs. Both the
  Home tiles (which are clickable) and `ShellWindow` (which navigations are honoured)
  read it, so a tile can never offer a screen the shell would refuse. The shell
  re-checks on every navigation *and* after every identity change: a re-login at a
  lower level evicts the operator from a page they can no longer see.
- **Deliberately ungated:** the jog rail's **STOP** and the safety overlay's
  **Acknowledge**. Motion must be stoppable by whoever is at the machine, signed in
  or not — a lock that can swallow an emergency stop is worse than what it prevents.
  `JogPanel.SetLocked` disables the rail *except* that button.
- **Auto sign-in at startup** (`AppSettings.AutoLoginOperator`) only ever picks a user
  whose **level has no password**. Once every level has one, startup always asks —
  otherwise the setting would hand out a level for free and the gate would be
  decorative.
- **Shell chrome:** a persistent header chip showing **current product** +
  **operator · level**, plus a **Logout / Sign In** button.
- **Machine Settings (Admin only):** machine number + credential string, the employee
  directory (add / delete), and the per-level passwords. Directory and password edits
  write `users.json` immediately; the Save button covers `app_settings.json` only.
- **Home:** a product selector (list + load/create), defaulting to `FAAM`.
- **Run page:** a **serial** input that brackets a DUT run — `BeginDutRun` on start,
  **Pass/Fail** buttons calling `EndDutRun`. A few `RecordDutValue` calls capture
  which steps ran.
- **Results viewer:** a simple list of the active product's DUT serials → runs
  (operator / verdict / time).

## Phasing

- **Phase 1 (this doc):** everything above — a demonstrable vertical slice across both
  bullets, FAAM.UI first. Horizon.UI also gets the login gate + identity chip (operator
  stamped on its `WorkflowProgram`/deck runs via the shared Layer 2 path); its existing
  Program-Editor / Deck product pickers now drive the same shell product chip. A
  serial-bracketed Run page / DUT results viewer for Horizon.UI is deferred (its Run
  page is still a Phase-5 placeholder).
- **Phase 2 (deferred):** per-product settings populated with real params; product
  export/import bundle; audit log (login/logout, product switch, edits, run lifecycle);
  roles + access control; idle auto-logout; route FAAM's hardcoded steps through a
  Layer 2 `WorkflowDeck`/`PositionStore`.

## Files touched (Phase 1)

- `Horizon.Common`: `User.cs` (new), `AppSettings.cs` (+`StationId`).
- `Horizon.Workflow`: `OperatorContext.cs` (new), `ProductStore.cs`,
  `Dut/DutModels.cs`, `IProcessService.cs`, `ProcessService.cs`.
- `FAAM.UI`: `LoginWindow.*` (new), `Session.cs` (new), `App`/`StartupWindow`
  (flow), `ShellWindow.*` (chrome + logout), `Views/HomeView.*` (product selector),
  `Views/ProcessStepsPanel.*` + `Views/RunView.*` (serial + DUT), results viewer.
</content>
</invoke>
