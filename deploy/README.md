# Horizon / FAAM deployment

Scripts to package FAAM.UI, Horizon.Console and Horizon.ConfigTool and stand them
up on a new machine as a **clone of an existing rig**. Full reference:
`../docs/deployment.md`.

```
deploy/
  publish.ps1           DEV box  - self-contained publish the 3 apps (+ -Snapshot to bundle data)
  snapshot-appdata.ps1  DEV box  - zip %APPDATA%\Horizon (config + products + history)
  restore-appdata.ps1   TARGET   - unzip the seed into %APPDATA%\Horizon
```

Run scripts with `pwsh` (PowerShell 7+). All three are ASCII-only so they survive
copy to a target with a different default code page.

---

## On the DEV / golden box

Build the apps and bundle the runtime data in one command:

```powershell
pwsh deploy/publish.ps1 -Snapshot -Output C:\Horizon-deploy
```

Add `-CleanHistory` to drop `history.db` + each product's `dut\` so the clone
starts with fresh traceability:

```powershell
pwsh deploy/publish.ps1 -Snapshot -CleanHistory -Output C:\Horizon-deploy
```

Result - the whole package to copy:

```
C:\Horizon-deploy\
  FAAM.UI\             self-contained app (own .NET runtime + vendor DLLs)
  Horizon.Console\
  Horizon.ConfigTool\
  Horizon-appdata.zip  config + products + history
  restore-appdata.ps1  travels with the zip
```

> Build on the box where the vendor SDKs are installed (Basler / PI / ACS / etc.).
> The hardware adapters pick up their native DLLs only when the SDKs are present,
> so a build on a bare box would ship without them.

Equivalent two-step form (same result):

```powershell
pwsh deploy/publish.ps1          -Output C:\Horizon-deploy
pwsh deploy/snapshot-appdata.ps1 -Output C:\Horizon-deploy
```

---

## On the TARGET box

**1. Install vendor drivers** (only for hardware this rig uses):

| Concern | Install |
|---|---|
| Vision  | HALCON 24.05 runtime + license file (x64) |
| Motion  | PI Software Suite (GCS2), ACS SPiiPlus Runtime Kit |
| Camera  | Basler Pylon 8 (and/or IDS Peak / uEye) |
| IO / Instruments | Fastech Ezi-MOTION, NI-VISA, Thorlabs Kinesis as wired |

.NET is bundled (self-contained), so no .NET install is needed.
Set the NIC to a static address on the controller subnet (this rig: `192.168.0.x`).

**2. Copy** `C:\Horizon-deploy\` to the target (e.g. `C:\Horizon\`).

**3. Restore the data** (existing `%APPDATA%\Horizon` is moved to
`Horizon.bak-<timestamp>` first, never overwritten):

```powershell
cd C:\Horizon
pwsh restore-appdata.ps1 -Zip .\Horizon-appdata.zip
```

**4. Set the per-machine fields** (everything else is cloned as-is):

- `%APPDATA%\Horizon\config\app_settings.json` -> `stationId` (unique per station)
- `%APPDATA%\Horizon\config\camera-config.json` -> `SerialNumber` (this camera's serial)
- verify `motor_devices.json` / `camera-config.json` IPs match the rig

**5. Smoke test** in order (isolates driver issues from the UI):

1. `Horizon.ConfigTool\Horizon.ConfigTool.exe` - config loads with no cross-ref errors
2. `Horizon.Console\Horizon.Console.exe` - each device connects
3. `FAAM.UI\FAAM.UI.exe` - jog moves each axis, camera live, E-Stop trips the overlay

> Run only ONE hardware-owning app at a time (FAAM.UI *or* Console) - a single
> process owns the SDKs.

---

## Script options

| Script | Param | Meaning |
|---|---|---|
| publish.ps1 | `-Output <dir>` | staging folder (default `..\artifacts\deploy`) |
| publish.ps1 | `-Configuration` | build config, default `Release` |
| publish.ps1 | `-Snapshot` | also bundle the %APPDATA%\Horizon seed |
| publish.ps1 | `-CleanHistory` | with -Snapshot: drop history.db + dut\ |
| snapshot-appdata.ps1 | `-IncludeLogs` | also capture logs\ (normally skipped) |
| snapshot-appdata.ps1 | `-CleanHistory` | drop history.db + dut\ |
| restore-appdata.ps1 | `-Zip <file>` | seed zip to restore (required) |
