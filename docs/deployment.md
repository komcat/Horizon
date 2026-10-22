# Deploying FAAM.UI + Console + ConfigTool to a new machine

How to stand up a **clone of an existing rig** (same controllers / camera /
instruments) on a fresh Windows box. Covers the three shipped executables:

- **FAAM.UI** — the FAU-attach operator app (WPF, `net8.0-windows10.0.19041`)
- **Horizon.Console** — Spectre.Console test rig for Layers 1 & 2
- **Horizon.ConfigTool** — hardware-config authoring utility

> The three run on the **same engine stack** and share `%APPDATA%\Horizon\`. Run
> only **one** hardware-owning app at a time (FAAM.UI *or* Console) — a single
> process owns the SDKs.

Deployment splits into three independent concerns:

1. **Machine prerequisites** — vendor runtimes/drivers installed on the target.
2. **The application** — self-contained publish output, copied over.
3. **Data / config** — `%APPDATA%\Horizon\` (device IPs, products, users).

---

## 1. Machine prerequisites (install on the target once)

Because we self-contained-publish (§2), the **.NET runtime is bundled** — you do
*not* need to install the .NET 8 Desktop Runtime. But the P/Invoke wrappers we
ship are thin shims over the **vendor driver stacks**, which must be present on
the target. Install only what this rig actually uses.

| Concern | Install on target | This rig uses it? |
|---|---|---|
| **Vision** | HALCON 24.05 **runtime + license file** (x64) | Yes — vision facet. App runs without it (degrades), but vision ops need the license. |
| **Motion (PI)** | PI Software Suite (GCS2) — provides the driver stack for `PI_GCS2_DLL_x64.dll` | Yes — `hex-right` hexapod |
| **Motion (ACS)** | ACS SPiiPlus Runtime Kit (x64) | Yes — `gantry-main` gantry |
| **Camera (Basler)** | Basler Pylon 8 — **drivers only**; the pylon *runtime DLLs* now ship in the publish output (§2) | Yes — `main_camera` (SdkType 0 = Pylon) |
| **Camera (IDS)** | IDS Peak / IDS uEye | Only if an IDS camera is wired |
| **IO (Fastech)** | Ezi-MOTION Plus-E V6 | Only if Ezi-IO is wired |
| **Instruments (VISA)** | NI-VISA runtime | Only if Keithley/Keysight instruments are wired |
| **TEC (Thorlabs)** | Thorlabs Kinesis | Only if a TED/TEC is wired |

> **Why pylon is still listed** even though we now bundle its runtime: bundling
> covers the *user-mode* DLLs the managed wrapper P/Invokes, so the app loads and
> enumerates. It cannot cover **kernel-mode drivers** — USB3 Vision cameras require
> the Basler USB3 driver, and GigE needs the pylon filter driver for full
> throughput. Install pylon 8 on any rig with a real camera.
>
> Similarly, **native HALCON is never bundled**: the `MVTec.HalconDotNet-Windows`
> NuGet package contains a managed shim only (~72 KB), not the ~2.3 GB licensed
> native runtime. HALCON must be installed on the target for any vision op.

Networking: this rig lives on **`192.168.0.x`**. Give the new PC's NIC a static
address on that subnet (e.g. `192.168.0.10`) so it can reach the controllers:

| Device | Address |
|---|---|
| `hex-right` (PI) | `192.168.0.30:50000` |
| `gantry-main` (ACS) | `192.168.0.50:701` |
| `main_camera` (Basler) | `192.168.0.88` |

## 2. Build & publish the apps (on the dev machine)

> **Why on the dev machine:** the hardware adapter `.csproj` files HintPath the
> vendor managed assemblies (`Basler.Pylon.dll`, `ACS.SPiiPlusNET8.dll`, …) and
> native DLLs from `C:\Program Files\...`, **conditional on `Exists(...)`**. They
> only resolve and copy to output on a machine where the SDKs are installed.
> Build where the SDKs live, then copy the finished output.

Run the publish script (see `deploy/publish.ps1`). It self-contained-publishes
all three to a single staging folder:

```powershell
pwsh deploy/publish.ps1 -Configuration Release -Output C:\Horizon-deploy
```

Result — three self-contained folders (each carries its own .NET runtime + the
native vendor DLLs picked up at build):

```
C:\Horizon-deploy\
  FAAM.UI\          FAAM.UI.exe + runtime + PI/ACS/Fastech/Thorlabs DLLs
                    + the native Basler pylon runtime + vendor-runtime.txt
  Horizon.Console\  Horizon.Console.exe
  Horizon.ConfigTool\  Horizon.ConfigTool.exe + presets\*.json
```

Copy `C:\Horizon-deploy\` to the target (any folder, e.g. `C:\Horizon\`).

### Bundled vendor natives

MSBuild copies most vendor natives itself, via `<None ... CopyToOutputDirectory>`
items (`PI_GCS2_DLL_x64.dll`, `EziMOTIONPlusE.dll`, `Thorlabs.MotionControl.*`) or
`<Private>true</Private>` managed references (`Basler.Pylon.dll`,
`ACS.SPiiPlusNET6.dll`, `ids_peak_*.dll`) — **all `Exists(...)`-guarded**, so they
appear only when the SDK is on the build box.

Basler pylon is the exception: only the *managed* wrapper is referenced, while the
*native* runtime it P/Invokes (`PylonC_v9.dll` → `PylonBase_v9.dll` → GenApi /
GCBase / transport layers) lives in the SDK and was never copied. `publish.ps1`
now calls `deploy/stage-vendor-runtime.ps1`, which copies that runtime (~26 MB,
top-level `Runtime\x64` only — the 172 MB `pylonDataProcessingPlugins\` vTools
tree is excluded) next to the `.exe`, where the Windows loader finds it first. The
exact staged file list lands in `vendor-runtime.txt` beside the app.

Not bundled, by nature: `visa64.dll` (resolved from `System32`), native HALCON
(licensed, ~2.3 GB), and all kernel-mode camera drivers.

### Cutting a versioned release

To publish as a GitHub release instead of a hand-copied folder:

```powershell
pwsh deploy/publish.ps1 -Zip -Snapshot -CleanHistory  # 3 app zips + Horizon-appdata.zip
pwsh deploy/release.ps1 -Version 0.1 -DryRun          # verify the plan
pwsh deploy/release.ps1 -Version 0.1 -PreRelease      # tag v0.1, push, create the release
```

The release attaches **all three apps**, so a target can be brought up from the
release page alone with nothing hand-copied: `Horizon.ConfigTool` to author and
validate the device config, `Horizon.Console` to prove it against real hardware,
`FAAM.UI` to run the process — plus `Horizon-appdata.zip` + `restore-appdata.ps1`
for the data seed. `release.ps1` resolves them from `artifacts\deploy` and **fails
if an app zip is missing** rather than shipping a partial release; pass `-Assets`
to override the set, `-NoAppData` to drop the seed.

`-UploadOnly` adds or replaces assets on an already-published release (and
refreshes its body from the notes file) without tagging anything — the way to
attach an app a release shipped without.

The version comes from `<Version>` in `src\FAAM.UI\FAAM.UI.csproj`; `release.ps1`
refuses to tag if the two disagree. Release body = `docs/release-notes-v<n>.md`.

## 3. Data / config — `%APPDATA%\Horizon\` (bundled with the deploy)

This tree is **runtime user data, not part of the build** — it lives outside the
repo and each store writes it live as you use the app:

```
%APPDATA%\Horizon\
  config\      device configs + app_settings + users + jog transforms
  products\    per-product processes / positions / decks / recipes / vision profiles
  history.db   machine-global program/deck version repository (SQLite)
  logs\        rolling Serilog files
```

We ship the whole thing **together with the apps** as a single seed zip, captured
on the golden box and restored on the target:

```powershell
# On the dev/golden box — snapshot into the same staging folder as the apps:
pwsh deploy/snapshot-appdata.ps1 -Output C:\Horizon-deploy
#   → C:\Horizon-deploy\Horizon-appdata.zip   (excludes logs\)
#   add -CleanHistory to also drop history.db + each product's dut\ (fresh traceability)
```

Or do both — publish the apps *and* bundle the seed — in one command:

```powershell
pwsh deploy/publish.ps1 -Snapshot -Output C:\Horizon-deploy   # add -CleanHistory if wanted
```

So the deploy folder to copy is:

```
C:\Horizon-deploy\
  FAAM.UI\  Horizon.Console\  Horizon.ConfigTool\   (from publish.ps1)
  Horizon-appdata.zip                               (from snapshot-appdata.ps1)
```

On the **target**, restore the seed (existing data is moved aside to
`Horizon.bak-<timestamp>`, never overwritten):

```powershell
powershell -ExecutionPolicy Bypass -File .\restore-appdata.ps1 -Zip .\Horizon-appdata.zip
```

Note `powershell`, not `pwsh` — this one runs on the target, which normally has
only stock Windows PowerShell 5.1. (The DEV-box scripts above do want `pwsh`.)

For a **same-rig clone** the snapshot copies `config\` and `products\` verbatim —
rig geometry is identical. `logs\` is excluded by default. Then set the few
machine-specific fields (below) before first run.

---

## Config bootstrap — fields to set on the clone

After copying `config\`, edit these. Use **Horizon.ConfigTool** for the device
files (it cross-validates, e.g. a pneumatic slide referencing a missing IO pin);
`app_settings.json` and `users.json` you can edit by hand.

### `app_settings.json`
| Field | Action |
|---|---|
| `stationId` | **Set a unique station id** (currently `""`). This stamps DUT traceability — every machine must differ. |
| `mmPerPixel` / `moveClick*` | Keep if the camera/optics are identical; re-calibrate if the camera mount differs. |
| `mainWindow` / `cameraWindows` | Screen coordinates — clear or let them reset if the monitor layout differs (the `-476/-928` values assume a second monitor). |
| `lastProgram` | Cosmetic; leave or blank. |
| velocity/exposure/findFirstLight presets | Rig-tuned — keep for a true clone. |

### `motor_devices.json` / `camera-config.json`
Keep the IPs **only if the new controllers are addressed identically** on
`192.168.0.x`. If the new hardware got different addresses, update
`ipAddress`/`port` (motion) and `IpAddress`/`SerialNumber` (camera). The Basler
`SerialNumber` (`22769965`) is per-unit — **update it to the new camera's serial**.

### `users.json`
Operator directory (machine-global). Default is a single blank-PIN `Operator`.
Add real operators + PINs for the new station, or keep the default if
`autoLoginOperator` is on.

### `safety_watch.json`, `io_devices.json`, `io_watch.json`, `pneumatic_slides.json`
Wiring-dependent. Identical for a true clone — verify pin numbers match the new
rig's harness before first motion. **Confirm E-Stop / safety pins first.**

---

## First-run smoke test on the target

0. **Allow the camera stream through the firewall** — once per install folder
   (rules are per `.exe` path, so every new version folder needs it again):
   `powershell -ExecutionPolicy Bypass -File <folder>\allow-firewall.ps1`
   (the script ships inside the `FAAM.UI\` / `Horizon.Console\` folders). Without
   it a GigE camera connects and "grabs" but shows **no video**.
1. Launch **Horizon.ConfigTool** → confirm every device file loads with no
   cross-reference errors.
2. Launch **Horizon.Console** → `InitializeAsync` → `ConnectAllAsync`; confirm
   each device connects (watch the `IProgress<string>` report). This isolates
   hardware/driver problems from the UI.
3. Launch **FAAM.UI** → StartupWindow connects hardware → LoginWindow → shell.
   Verify jog rail moves each axis, camera feed is live, and E-Stop trips the
   safety overlay.
4. Check `%APPDATA%\Horizon\logs\faam-*.log` for adapter/driver warnings.

> If a vendor SDK is missing on the target, that device fails to connect at step
> 2 (or vision degrades) — install the corresponding runtime from §1 and retry.
