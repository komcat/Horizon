# Access control (sign-in gate + machine identity)

Status: **v1 done** — FAAM.UI only. Nothing in the shell works until an operator signs
in with **level + employee number + level password**; every screen declares a minimum
level. There is a **built-in recovery Admin** (`admin` / `admin`) that always signs in.
The station's **machine number + credential string** are stamped onto every DUT
run alongside the operator. Supersedes the "traceability only, no access control" row
in `docs/product-and-traceability-design.md`.

Horizon.UI is **unchanged** — it keeps the original pick-operator-and-PIN login. The
two heads share `users.json`; the fields each ignores are simply left alone.

## Idea — the three fields

```
Level:    [ Engineer      v ]     what you may reach   → ranked, gates every screen
Employee: [ 20449           ]     who you are          → from users.json, stamped on the DUT
Password: [ ******          ]     proof of the LEVEL   → shared per level, not per person
```

The split is deliberate. The employee number is **identity for traceability** and
changes constantly as people join and leave; the password is **entitlement** and
changes rarely. Keeping them separate means adding an employee never involves issuing
or distributing a credential — an Admin types a number and a name, and that person
signs in with the level password their shift already knows.

`UserStore.SignIn(level, employeeId, password)` is the entire check (after the
built-in recovery Admin below, which is tried first):

1. the employee number exists in `users.json`,
2. that record is `active`,
3. its `level` matches the chosen level **exactly** (not "at least" — declaring your
   level is part of the sign-in, and a mismatch usually means a mistyped number), and
4. the level's password verifies.

Failures 1–3 all return **one** message ("Employee number is not registered at that
level"). Which of the three it was is not the operator's business, and distinguishing
them would turn the login screen into a directory-enumeration tool.

## Levels

`AccessLevel` (`Horizon.Common`) is **ranked** — screens compare with `>=`, so the
numeric values *are* the ordering and must not be reshuffled.

| Level | Value | Reaches |
|---|---|---|
| `Operator` | 0 | Home, **Run**, product *switch*, jog rail |
| `Technician` | 1 | + **Machine**, **Vision Profile** |
| `Engineer` | 2 | + **Engineer** (sequence editor), **Repository**, **Script**, **Calibration**, product *create* |
| `Admin` | 3 | + the Machine page's **Settings** section |

Two placements worth stating outright, because neither is obvious:

- **Calibration is Engineer**, not Technician. A camera→needle run rewrites a stored
  offset every later dispense depends on — it is not a hardware exercise, it is a
  change to the process.
- **Product *switch* is Operator, product *create* is Engineer.** The floor runs
  whatever unit arrives next, and re-scoping to an existing product changes nothing
  about it. "Save As…" deep-copies positions, vision profiles and programs into a new
  product — that is authoring a variation.

## What is never gated

**The jog rail's STOP button and the safety overlay's Acknowledge.** Motion must be
stoppable by whoever is standing at the machine, signed in or not. A lock that can
swallow an emergency stop is a worse hazard than the one it prevents.

`JogPanel.SetLocked` disables every direct child of the rail's root stack *except*
`StopButton`, and also disarms keyboard jog — a disabled `ToggleButton` would not
otherwise stop hotkeys that were already armed.

`JogPopupWindow` (the `flow.jogPopup` step) is also ungated: it only ever appears
inside a running program, which a signed-in operator started.

## Horizon.Common

- **`AccessLevel`** — the ranked enum above.
- **`User`** — `id` (**employee number**), `displayName`, `level`, `active`, plus the
  legacy `pin` that only Horizon.UI still reads.
- **`LevelCredential`** — `salt` + `hash`, one per level. PBKDF2-SHA256, 16-byte random
  salt, 100 000 iterations, 32-byte key; verified with
  `CryptographicOperations.FixedTimeEquals`. The password itself is never stored.
  An absent entry — or one with an empty `hash` — means the level has **no** password
  and signs in on the employee number alone.
- **`UserStore`** — `SignIn`, `HasLevelPassword`, `VerifyLevelPassword`,
  `SetLevelPassword`, `AddUser`, `RemoveUser`, `AllUsers`, `Save`, `Reload`. Backed by
  `users.json` in the config directory (machine-global, beside `app_settings.json`).
- **`AppSettings.StationId`** — the station's **machine number**; blank falls back to
  the Windows machine name at sign-in, so a run is never stamped with nothing.
- **`AppSettings.MachineCredential`** — the station's **credential string**: its
  signature on the DUT record (calibration certificate, asset tag, qualification id —
  whatever the line records). Free text; empty means the station is unsigned.

### `users.json`

```jsonc
{
  "schemaVersion": 1,
  "users": [
    { "id": "20449", "displayName": "K. Chaiyaphum", "level": "Engineer", "pin": "", "active": true }
  ],
  "levelPasswords": {
    "Engineer": { "salt": "…base64…", "hash": "…base64…" }
    // a level with no entry here has no password
  }
}
```

### Schema upgrade — the no-lockout rule

A `users.json` written before levels existed has no `schemaVersion`. On first load
**every user in it is promoted to `Admin`**, and the upgraded file is written back
immediately.

This is not generosity, it is the only safe migration: those users had unrestricted
access before the gate arrived, and the screen that manages access is itself
Admin-only. Any other default would let a routine build upgrade lock a station out of
its own Settings with no recovery inside the app. Locking down is therefore an
explicit act — an Admin demotes people — never a silent side effect of installing.

**Consequence on first launch:** with everyone Admin, no level passwords set, and
`AutoLoginOperator` defaulting to `true`, the app signs straight in as Admin and
behaves exactly as before. **Nothing is actually gated until an Admin opens Machine →
Settings → Level passwords and sets some.** That is the intended sequence, but it does
mean "I installed it and nothing changed" is the expected first impression.

If `users.json` is missing entirely, it is seeded with a single `0000 / Administrator`
Admin and no passwords.

## The built-in recovery Admin

```
Level:    Admin
Employee: admin
Password: admin
```

Fixed in source (`UserStore.BuiltInAdminId` / `BuiltInAdminPassword`), checked
**before** the directory, and answering whatever `users.json` contains — including a
file with no Admin in it, a forgotten Admin level password, or a directory edited into
a state nobody can sign in to. That is the point: **a recovery account that
configuration can disable is not a recovery account.**

It is deliberately not a secret:

- It is in the source, in this doc, and on the Settings screen (first row of the user
  directory, in the danger colour, with the credential printed).
- It is identical on every station, and cannot be changed or turned off without a
  rebuild.
- **Physical access to a station is therefore Admin access.** The gate stops the wrong
  person changing the process by accident or convenience; it does not stop anyone who
  knows this page.

What keeps it honest rather than invisible:

- The reserved id cannot be added to the directory (`AddUser` refuses `admin`) or
  removed from it (`RemoveUser` refuses), so nothing can shadow or delete it.
- It is **synthesised, never stored** — it does not appear in `users.json`.
- Every recovery sign-in writes a **warning** to the log naming what happened.
- Its `User.Id` is `admin`, so any DUT run made under it is stamped `admin` in the
  record rather than a person. A unit run on the recovery account says so, permanently,
  in its own file.

The intended use is: sign in as `admin`/`admin` → Machine → Settings → set the Admin
level password and register a real Admin employee → stop using it.

### Lost password

Sign in with the built-in recovery Admin above, then reset the level password in
Settings. Failing that (or to remove a password without the app), delete the level's
entry from `levelPasswords` in `users.json`, or delete the file to re-seed — verifiers
are one-way, so the old password cannot be read back.

This is a shop-floor gate, not a security boundary: the app runs as the logged-in
Windows user and the config file is readable on the machine. It stops the wrong person
changing the process; it does not stop a determined one.

## Layer 2 (`Horizon.Workflow`)

Layer 2 has **no** concept of levels or permissions — it receives identity, it does not
enforce it. The gate is entirely a Layer 3 concern, as it must be: Layer 2 also runs
headless from the consoles, where there is no operator to sign in.

- **`OperatorContext`** gains `Level` and `MachineCredential`, both defaulted to empty
  so Horizon.UI's 3-argument construction still compiles.
- **`DutRun`** gains `OperatorLevel` and `MachineCredential`.
- **`ProcessService.StampOperator`** copies both onto every run, blank → `null` so the
  DUT JSON does not carry empty strings.

A DUT record therefore answers all four traceability questions:

```
operatorId / operatorName   who ran the unit
operatorLevel               under what authority
stationId                   on which machine
machineCredential           under what standing that machine was operating
```

## Layer 3 (`FAAM.UI`)

- **`AccessPolicy`** — the single table of which level each screen needs. Both the Home
  tiles (which are clickable) and `ShellWindow` (which navigations are honoured) read
  it, so a tile can never offer a screen the shell would refuse.
- **`Session`** — `Has(AccessLevel)` is the one question every gated surface asks;
  logged out is always `false`, so the station is locked by default. `LogIn` reads the
  machine number + credential from `AppSettings` **at each login**, so editing the
  station's identity in Settings takes effect on the next sign-in rather than needing a
  restart.
- **`LoginWindow`** — the three fields. The password box is disabled and relabelled
  *"(none set for this level)"* when the chosen level has no password, rather than
  leaving a dead box the operator wonders about.
- **`ShellWindow`**:
  - `Allow(level, screen)` guards every `Show*`; a refusal is logged and lands the
    operator on Home, which is safe at any level.
  - `EvictIfNotAllowed()` runs after every identity change — the navigation gate only
    covers going *to* a page, not still being on one when the level drops (logout, or
    a re-login as someone junior).
  - Identity chip shows **name · level**. Logged out it reads "Not signed in" in the
    danger colour and the button says "Sign In".
  - The DUT header (serial, Start, PASS, FAIL, Results) needs a sign-in — a run must be
    attributable to somebody.
- **`MachinePageViewModel.Sections`** is an `ObservableCollection` rebuilt on
  `Session.Changed`; **Settings** is only in the rail for an Admin. The rebuild
  remembers the current section and restores it, because clearing the collection makes
  the rail's two-way `SelectedItem` binding push `null` back into `CurrentSection`.
- **`DisplayWindow`** (camera pop-out) gates MoveClick itself. It is a separate
  top-level window, so neither the rail lock nor the page eviction reaches it — and it
  auto-opens at startup when `ShowMainCameraOnStartup` is set. Without this a
  logged-out station could still jog the gantry by clicking a pop-out feed. Gated twice
  on purpose: the toggle is disabled *and* `DoMoveClickAsync` re-checks, because that is
  the call that moves metal.
- **Machine → Settings (Admin only)** — machine number + credential, the employee
  directory (add / delete), and the per-level passwords. **Directory and password edits
  write `users.json` immediately**; the Save button covers `app_settings.json` only.
  The `PasswordBox` is read by the view and handed to the view-model (WPF refuses to
  bind a password, by design).
- **Auto sign-in** (`AppSettings.AutoLoginOperator`) only ever picks a user whose level
  has **no** password. Otherwise the setting would hand out a level for free and the
  gate would be decorative. Once every level has a password, startup always asks.

## Flow

```
StartupWindow (connect hardware)
      ↓
LoginWindow   ── cancel ─→ app exits
      ↓                    (not: a shell where every screen is locked)
ShellWindow   ── Logout ─→ station locks, engine stays up, STOP still live
```

## Edge cases

- **Removing the last Admin is refused** — it would lock the station out of Settings.
  Add another Admin first. (The built-in recovery Admin would in fact still get you in,
  but relying on the backdoor for a routine edit is not a workflow worth allowing.)
- **The password box is never disabled**, even for a level with no password set —
  otherwise `admin`/`admin` could not be typed when the Admin level itself is unset.
  The label carries the hint instead.
- **Deleting an employee does not damage history.** A `DutRun` stores the employee
  number as a plain string, so past records keep what they were stamped with.
- **A corrupt verifier** (unparseable base64) fails closed — `VerifyLevelPassword`
  returns `false` rather than treating the level as unprotected.
- **`Session.Changed` subscriptions are unhooked** in `MachinePageViewModel.Dispose`
  and `DisplayWindow.OnClosed`; a pop-out closed and reopened re-wires cleanly.

## Deferred

- Per-level passwords are shared, so they cannot attribute *authority* to an individual
  the way the employee number attributes *identity*. Per-user credentials would, at the
  cost of an issuing process.
- No lockout / retry throttle on failed sign-ins — which matters more than it
  otherwise would, given a known fixed credential.
- The built-in Admin's password cannot be changed or the account disabled without a
  rebuild. Making it settable-once (say, cleared after the first real Admin is
  registered) would trade some recoverability for a lot less exposure.
- No audit log of sign-in, sign-out, or directory edits (the log lines exist; nothing
  persists them separately from the general app log).
- Horizon.UI still uses the old name + PIN picker and is not gated.
