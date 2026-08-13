# FAAM.UI v0.1

First tagged release of **FAAM.UI**, the FAU-attach operator application built on
the Horizon 3-layer engine stack (Layer 1 `Horizon.Engine` → Layer 2
`Horizon.Workflow` → Layer 3 `FAAM.UI`).

This is an **internal engineering release** for rig deployment, not a general
distribution. It is a snapshot of `master` at the tag, published self-contained
for `win-x64`.

## Asset

`FAAM.UI-v0.1-win-x64.zip` — extract anywhere (e.g. `C:\Horizon\FAAM.UI\`) and run
`FAAM.UI.exe`. No .NET install needed: the .NET 8 Desktop runtime is bundled.

The zip carries every DLL the build can legally and practically bundle:

| Bundled in the zip | Source |
|---|---|
| .NET 8 runtime + WPF natives | self-contained publish |
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

`%APPDATA%\Horizon\` (device configs, products, `users.json`, `history.db`) is
**not** part of this release — it is rig-specific runtime data containing operator
PINs and station identity. Seed it separately with `deploy/snapshot-appdata.ps1`
on the golden box and `deploy/restore-appdata.ps1` on the target.

At minimum, set a unique `stationId` in `app_settings.json` before first run —
it stamps DUT traceability and must differ per machine.

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

## Verify after install

Follow the smoke test in `docs/deployment.md`: ConfigTool loads every device file
→ `Horizon.Console` connects each device → FAAM.UI jogs each axis, shows a live
camera feed, and trips the safety overlay on E-Stop → check
`%APPDATA%\Horizon\logs\faam-*.log` for adapter warnings.
