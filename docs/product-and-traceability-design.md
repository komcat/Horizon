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
| Auth strength | **Traceability only** — operator picks their name + a short PIN. No real security, no roles, no access control. The point is to stamp who ran each DUT. |
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

- **`User`** record + **`UserStore`** (`users.json`, machine-global, same config dir
  as `app_settings.json`): `id`, `displayName`, `pin` (plain — traceability, not
  security), `active`. Seeds a default operator on first run.
- **`AppSettings.StationId`** — machine identity for the station; empty falls back to
  the Windows machine name at login.

### Layer 3 — FAAM.UI (this phase's priority)

- **`LoginWindow`** — pick operator from `UserStore`, enter PIN. On success: build an
  `OperatorContext` (user + station id) and call `Process.SetOperator(...)`.
- **Flow:** `StartupWindow` (connect hardware) → **`LoginWindow`** → `ShellWindow`.
  Login does **not** block engine bring-up. **Logout** returns to `LoginWindow` with
  the engine still up; re-login restamps the operator.
- **Shell chrome:** a persistent header chip showing **current product** + **operator**,
  plus a **Logout** button.
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
