# FAAM.UI v0.1

First tagged release of **FAAM.UI**, the FAU-attach operator application built on
the Horizon 3-layer engine stack (Layer 1 `Horizon.Engine` → Layer 2
`Horizon.Workflow` → Layer 3 `FAAM.UI`).

This is an **internal engineering release** for rig deployment, not a general
distribution. It is a snapshot of `master` at the tag, published self-contained
for `win-x64`.

## Assets

| Asset | What it is |
|---|---|
| `FAAM.UI-v0.1.0-win-x64.zip` (102 MB) | The application. Extract anywhere (e.g. `C:\Horizon\FAAM.UI\`) and run `FAAM.UI.exe`. No .NET install needed — the .NET 8 Desktop runtime is bundled. |
| `Horizon.Console-v0.1.0-win-x64.zip` (89 MB) | The headless **engine test rig** — a Spectre.Console menu over the same Layer 1/2 stack. Bring a target up with this *before* running the operator app. See "Bringing up a target" below. |
| `Horizon.ConfigTool-v0.1.0-win-x64.zip` | The **hardware config authoring utility** — a Spectre.Console editor for the device files in `%APPDATA%\Horizon\config\`. This is how a target's hardware is configured after the seed is restored: preset-driven add/edit/remove per domain, plus cross-file validation. See "Configuring the target's hardware" below. |
| `Horizon-appdata.zip` (2.4 MB) | The `%APPDATA%\Horizon` seed — `config\` + `products\`. See "Data / config" below. |
| `restore-appdata.ps1` | Restores the seed on the target; travels standalone so the target doesn't need the repo. |

All three app zips are self-contained `win-x64` folder publishes of the same commit
and read the same `%APPDATA%\Horizon\` tree — restore the seed once and all of them
will use it. Each carries every DLL the build can legally and practically bundle:

| Bundled in the zip | Source |
|---|---|
| .NET 8 runtime (+ WPF natives in `FAAM.UI`) | self-contained publish |
| `PI_GCS2_DLL_x64.dll` (PI motion) | PI Software Suite, copied at build |
| `EziMOTIONPlusE.dll` (Fastech IO) | Ezi-MOTION Plus-E V6, copied at build |
| `ACS.SPiiPlusNET6.dll` (ACS motion, managed) | ACS ADK Suite, copied at build |
| `Basler.Pylon.dll` (managed wrapper) | pylon 8, copied at build |
| **Native Basler pylon runtime** (~26 MB, 26 files) | pylon 8 `Runtime\x64`, staged by `deploy/stage-vendor-runtime.ps1` — **new in this release** |
| `Thorlabs.MotionControl.*` (TEC) | Thorlabs Kinesis, copied at build |
| `MVTec.HalconDotNet.dll` (managed wrapper) | NuGet |
| SkiaSharp / SQLite natives | NuGet |

A `vendor-runtime.txt` manifest in the app folder lists exactly which pylon files
were staged and from which SDK version.

`Horizon.ConfigTool` is the exception: it deliberately does **not** reference
`Horizon.Camera` (it mirrors `camera-config.json` with a local model instead), so it
carries no Basler or HALCON binaries and touches no hardware. It only reads and
writes JSON — it will run on a station with no vendor SDK installed at all, which is
exactly the state a fresh target is in.

## Still required on the target

Bundling DLLs cannot replace kernel-mode drivers or licensed native runtimes.
Install these on the rig before first run — only what the rig actually uses:

- **Basler pylon 8** — the staged runtime lets the app load and enumerate, but
  **USB3 Vision cameras require the Basler USB3 kernel driver**, and GigE needs
  the pylon filter driver for full throughput. Install pylon 8 for real camera work.
- **HALCON runtime + licence file** — the `MVTec.HalconDotNet-Windows` NuGet
  package ships a managed shim only; it does **not** bundle native HALCON
  (~2.3 GB, licensed). Vision degrades gracefully without it, but no vision op runs.
- **NI-VISA runtime** — only if Keithley / Keysight instruments are wired.
  `visa64.dll` is resolved from `System32` by P/Invoke and is never bundled.
- **PI Software Suite / ACS SPiiPlus Runtime Kit** — driver stacks behind the
  bundled managed/native shims.
- **IDS Peak or uEye** — only if an IDS camera is wired. This build was produced
  on a box without the IDS SDK, so `IDSPeakCameraAdapter` is compiled out of it.

Full procedure, rig IPs and the per-machine config checklist: `docs/deployment.md`.

## Data / config

The app reads its runtime data from `%APPDATA%\Horizon\` — device configs,
products, programs, positions, vision profiles. That tree lives outside the repo,
so **the application zip alone will not connect to anything**. Restore the seed
first:

```powershell
powershell -ExecutionPolicy Bypass -File .\restore-appdata.ps1 -Zip .\Horizon-appdata.zip
```

Use `powershell` (Windows PowerShell 5.1, present on every Windows box), not
`pwsh` — PowerShell 7 is usually not installed on a target station.

Any existing `%APPDATA%\Horizon` is moved aside to `Horizon.bak-<timestamp>`,
never overwritten.

`Horizon-appdata.zip` contains 113 entries:

- `config\` — 17 files: `motor_devices.json`, `camera-config.json`, `io_devices.json`,
  `pneumatic_slides.json`, `safety_watch.json`, `io_watch.json`, `jog_transforms.json`,
  `app_settings.json`, `users.json`, `camera_needle_calibration.json`, …
- `products\` — **FAAM**, **SIPHOG**, **SIPHOG-Plus**: 26 `.hwf` programs, 4 decks,
  3 `positions.json`, 34 vision profiles, and `FAAM\scripts\facet_u_correction.moon`.

Deliberately **excluded** (built with `snapshot-appdata.ps1 -CleanHistory`), so the
station starts with clean traceability: `history.db`, every product's `dut\`
records, and `logs\`.

There are no `.hrcp` recipes in this build — every `recipes\` folder is empty.

### Set before first run

| Field | File | Why |
|---|---|---|
| `stationId` | `config\app_settings.json` | Ships **blank**. Stamps DUT traceability — must be unique per machine. |
| `SerialNumber` | `config\camera-config.json` | Per-unit Basler serial; update to the new camera's. |
| `ipAddress` / `port` | `config\motor_devices.json` | Only if the new controllers aren't on the same `192.168.0.x` addresses. |
| operators + PINs | `config\users.json` | Ships as the single default `Operator` entry. |
| `mmPerPixel`, MoveClick, velocity presets | `config\app_settings.json` | Rig-tuned — keep for a true clone, re-calibrate if the camera mount differs. |

Verify `safety_watch.json` / `io_devices.json` pin numbers against the new rig's
harness **before first motion** — confirm E-Stop wiring first.

Edit these with `Horizon.ConfigTool` rather than a text editor — see below.

## Configuring the target's hardware

`Horizon.ConfigTool-v0.1.0-win-x64.zip` → `Horizon.ConfigTool.exe`. It edits the
same `%APPDATA%\Horizon\config\` files the engine consumes, through the domain
models themselves, so a saved file cannot drift from what Layer 1 will deserialize.
Hand-editing that JSON is the error-prone path this tool exists to remove: enum
typos, missing fields, the Ezi-IO input/output pin-offset subtlety, and silent
cross-file mistakes such as a pneumatic slide pointing at an IO pin that does not
exist.

Domains it edits, each with **View / Add (from preset) / Edit / Remove**:

| Domain | File |
|---|---|
| Motion | `motor_devices.json` |
| IO / IO watch | `io_devices.json`, `io_watch.json` (plus **Apply pinout preset**) |
| Pneumatic | `pneumatic_slides.json` |
| Camera | `camera-config.json` |
| Laser / Measurement | `laser_controller_devices.json`, `current_measurement_devices.json` |
| Lighting / TEC / SMU / Power supply | `lighting_devices.json`, `tec_devices.json`, `smu_devices.json`, `power_supply_devices.json` |
| Safety / Interlocks | `safety_watch.json`, `motion_interlocks.json` |
| TCP clients | `tcp_clients.json` |

Plus, from the top menu:

- **Validate all** — the cross-reference pass. Run it after every edit and before
  launching anything that moves.
- **View all** — every domain's current contents in one dump.
- **Export / Import config bundle** — move a whole config set between stations.
- **Change config directory** — point at a folder other than `%APPDATA%\Horizon\config\`
  to draft a config offline, e.g. on a laptop.

New hardware is added from **presets** rather than typed from scratch. The preset
catalogs are `presets\<domain>_presets.json` next to the exe — plain JSON, editable
on the target to add a preset without a rebuild.

## What's in this build

- **Startup → login → shell**: hardware connect, operator + PIN login pushing an
  `OperatorContext` to Layer 2, product + operator identity chip.
- **Run page** — production operator page: camera feed with crosshair/Zoom2×
  inset, MoveClick click-to-jog, process steps panel with Teach/Run toggle,
  serial-bracketed DUT runs with PASS/FAIL, DUT results viewer, and a log column.
- **Alignment**: U-correction + FAU pre-align (chained from one Start), grid
  search, hill-climb active alignment on SiPhOG SAG-V feedback, raster climb, and
  the Auto-W maximiser — surfaced through the utility-app springboard.
- **Engineer page** — Gantt timeline sequence editor compiling to
  `WorkflowProgram` (multi-lane stage → `flow.parallel`), process deck tab,
  dispensing routines, and command help in the palette.
- **Vision Profile page** — per-position vision recipes (NCC / Circle / Line /
  Corner / Edge-regression) over HALCON.
- **Machine page** — hardware status and manual control; **global jog rail**
  with per-device global→local transforms.
- **Repository page** — SQLite-backed program/deck version history with
  git-style save vs commit, checkout and diff.
- **.moon scripting** (MoonSharp) over the engine facets.

## Known limitations

- Run-page process steps are **Phase 1 / hardcoded** in `Views/ProcessStepsPanel`,
  not yet promoted to a Layer 2 `WorkflowDeck`.
- The projects reference the HALCON **24.05** managed wrapper. If the target has a
  different HALCON major installed (the build box has **25.05**), the managed/native
  pairing is a mismatch risk — verify vision ops during the smoke test.
- `Horizon.Camera` hard-references `Basler.Pylon.dll` with no `Exists(...)` guard,
  so **the solution does not build on a machine without pylon 8 installed**. This
  release must be built on a box with the vendor SDKs; CI cannot produce it.
- Utility-app launcher Phase 3 (polish) and the Engineer command-help coverage for
  non-App categories are still outstanding.

## Bringing up a target

Work bottom-up so a driver problem never looks like a UI problem. The three assets
map onto the three stages of a bring-up — **configure** (ConfigTool), **prove**
(Console), **produce** (FAAM.UI) — and steps 1-4 never launch `FAAM.UI.exe`, so a
rig can be proven out before the operator app is trusted with it.

1. **Restore the data** — `Horizon-appdata.zip` + `restore-appdata.ps1` (above).
2. **Configure the hardware** — extract `Horizon.ConfigTool-v0.1.0-win-x64.zip` and
   run `Horizon.ConfigTool.exe`. Set the per-machine fields from the table above
   (station id, camera serial, controller IPs), correct anything the new rig wires
   differently, then run **Validate all** until it is clean. Nothing here touches a
   device, so it is safe to do before drivers are even installed.
3. **Connect the hardware headlessly** — extract `Horizon.Console-v0.1.0-win-x64.zip`
   and run `Horizon.Console.exe`. It runs the same Layer 1 engine the operator app
   does (`InitializeAsync` → `ConnectAllAsync`), so if a device is misconfigured or a
   vendor driver is missing, it surfaces here with the adapter's own error text
   instead of behind a splash screen. Each failure sends you back to step 2.
4. **Exercise each domain from the menu** — `Show status` for the connection
   snapshot, then the per-domain menus: IO devices / IO watch, Motion devices
   (home, move, jog per axis), Pneumatic slides, Current measurement, Laser
   controllers, Power supply, Lighting, TEC, SMU, Cameras, Vision (HALCON), Safety.
   `Reconnect hardware` re-runs the connect pass without restarting. `Scripting
   (.moon)` runs a `.moon` script against the facets. **Confirm E-Stop wiring under
   Safety before commanding any motion.**
5. **Then the operator app** — `FAAM.UI.exe`: jog each axis, confirm a live camera
   feed, trip the safety overlay on E-Stop.
6. **Read the logs** — `%APPDATA%\Horizon\logs\faam-*.log` for adapter warnings.

Only one process may own the hardware SDKs at a time — **close `Horizon.Console`
before starting `FAAM.UI`**, and never run both together. `Horizon.ConfigTool` is
exempt (it opens no device), but the apps read their config at startup, so restart
them after an edit.

Full procedure and rig IPs: `docs/deployment.md`.
