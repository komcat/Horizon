# FAAM.UI v0.2

Second tagged release of **FAAM.UI**, the FAU-attach operator application on the
Horizon 3-layer engine stack (Layer 1 `Horizon.Engine` → Layer 2 `Horizon.Workflow`
→ Layer 3 `FAAM.UI`). Snapshot of `master` at the tag, published self-contained for
`win-x64`. Internal engineering release for rig deployment.

Deployment procedure, bundled/required DLLs, the `%APPDATA%\Horizon` seed and the
bottom-up bring-up (ConfigTool → Console → FAAM.UI) are unchanged from
[v0.1](https://github.com/komcat/Horizon/releases/tag/v0.1) — see those notes and
`docs/deployment.md`. This page lists what changed.

## Assets

| Asset | What it is |
|---|---|
| `FAAM.UI-v0.2.0-win-x64.zip` | The operator application. Extract and run `FAAM.UI.exe`; .NET 8 Desktop runtime bundled. |
| `Horizon.Console-v0.2.0-win-x64.zip` | Headless engine test rig over the same Layer 1/2 stack — prove the hardware before the operator app. |
| `Horizon.ConfigTool-v0.2.0-win-x64.zip` | Hardware config authoring utility for `%APPDATA%\Horizon\config\` (no hardware access). |
| `Horizon-appdata.zip` | The `%APPDATA%\Horizon` seed — `config\` + `products\`, with `history.db`, `dut\` and `logs\` stripped. |
| `restore-appdata.ps1` | Restores the seed on the target (run with Windows PowerShell 5.1: `powershell -ExecutionPolicy Bypass -File .\restore-appdata.ps1 -Zip .\Horizon-appdata.zip`). |

## What's new since v0.1

### Sign-in gate with access levels
- Nothing in the shell works until an operator signs in with **level + employee number
  + that level's password**. `AccessLevel` (Operator < Technician < Engineer < Admin)
  is ranked; every screen's minimum lives in `AccessPolicy` and is enforced by the
  shell on navigation and re-checked on every identity change. **STOP and the safety
  Acknowledge are never gated.**
- Machine › **Settings** (Admin) holds the machine number + credential, the employee
  directory and the per-level passwords (PBKDF2 verifiers in `users.json`).
- Every DUT run is stamped with operator, level, machine number and machine
  credential.
- A **built-in recovery Admin** (`admin` / `admin`) always signs in; it is a known
  credential, shown in the Settings user list, and every use is logged.
- Design: `docs/access-control-design.md`.

### DUT Data page (new Home tile, Operator)
- Views the UV-cure SiPhOG snapshots that `dut.readAllSignals` files per serial
  (`uv1.0` / `uv1.1` / `uv2.0` / `uv2.1_final`): one row per UV run, newest first,
  with the picked signal at each snapshot and the final − initial delta; serial
  filter and range presets (last 7 / 30 days, YTD, last 30 / 100 runs); a per-run
  detail grid with every signal; a trend chart; **Export CSV**.

### Device registration, gripped FAU and part alignment
- **Registration** (Machine page, Engineer): the hex-right gripper registered to the
  gantry camera — bounded `tool.cameraToTool` / `tool.toolToCamera` / `tool.toPose`
  moves that refuse (never clamp) beyond the configured limit, and a live gripper
  overlay on the Run feed, the Zoom2× inset and the camera pop-out.
  `docs/device-registration-design.md`.
- **Vision Alignment** page (tabbed): **Vision Profile** (as before), **Gripped FAU**
  (teach the object held by the gripper from two points + outline, detect procedure
  with settle wait, `object.toCamera` block) and **Part Alignment** (fiducials E1/E2 +
  local objects locating the part on the fixture; `part.runAlignment` /
  `part.cameraToFiducial` / `part.cameraToObject` blocks).
- **Marker placer**: one marker gesture on every teach surface — click to drop,
  arrow keys nudge a pixel, Enter / Esc.
- `.moon` scripting gains `machine.part.*` (run, snapshot, cameraToFiducial,
  cameraToObject, cameraToObjectPoint).

### Run page and alignment
- **Snapshot box** beside the Zoom2× inset in place of the unused SAG chart; a
  `.moon` script pushes a still into it with `machine.ui.showImage`.
- **Hill climb**: after the auto-SLED handoff, the SLED current is stepped back down
  while target SAG PWR sits in saturation (bounded: 10 attempts, 40 mA floor).

### Release tooling
- A release now ships all three apps (FAAM.UI, Horizon.Console, Horizon.ConfigTool)
  plus the appdata seed; `release.ps1 -UploadOnly` tops up an existing release.

## Upgrading a station from v0.1

- **`users.json`**: a file written before access levels existed is upgraded on first
  load by promoting every user to **Admin** (so the upgrade cannot lock the station
  out). Sign in as Admin, set the level passwords and each employee's level in
  Machine › Settings, and set the **machine credential** there.
- New data files appear on first use and need no migration: machine-global
  `tool_registrations.json`; per-product `gripped_objects.json` and
  `part_alignments.json` (both copied by product **Save As…**).
- The seed's products carry the registration / gripped / part data of the golden
  rig. Re-run **Registration** on a rig whose gripper ↔ camera geometry differs.

## Known limitations

- Run-page process steps remain **Phase 1 / hardcoded** in `Views/ProcessStepsPanel`.
- HALCON: the projects reference the **24.05** managed wrapper; verify vision ops on
  a target with a different HALCON major.
- The solution only builds on a box with the vendor SDKs installed (`Horizon.Camera`
  hard-references `Basler.Pylon.dll`).
- The DUT Data page reads the **active product** only and re-reads every record on
  open (hundreds of files: a few seconds, in the background).
