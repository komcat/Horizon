# FAAM.UI v0.6.2

Patch release of **FAAM.UI**, the FAU-attach operator application on the Horizon
3-layer engine stack (Layer 1 `Horizon.Engine` → Layer 2 `Horizon.Workflow` → Layer 3
`FAAM.UI`). Snapshot of `master` at the tag, published self-contained for `win-x64`.
Internal engineering pre-release for rig deployment.

This page lists what changed since
[v0.6.1](https://github.com/komcat/Horizon/releases/tag/v0.6.1); see the
[v0.6](https://github.com/komcat/Horizon/releases/tag/v0.6) notes for the assets and the
self-updating install.

## Fixed

### DUT data saved under the wrong serial
- **Symptom:** the Run page showed the new serial (e.g. `DUT 3483`), but SiPhOG snapshots
  (`siphog_data\`) and DUT entries (`uv1.0`, `uv2.1_final`, `CoilNumber`, …) were filed under
  an older serial.
- **Cause:** a DUT run opened with the header's **Start DUT** stays open until **Pass** /
  **Fail**. When a program then asked for a new serial (`dut.promptSerial`, e.g. in
  GripperOpenHome), only that one program recorded to the new serial — every later program
  fell back to the still-open older run.
- **Fix:** entering a serial for a program run now ends any still-open Start DUT run as
  **Aborted** (logged as a warning naming both serials), and everything after it records to
  the new serial. The header's Start / Pass / Fail buttons update to match.
- Data already misfiled by v0.6.1 is not moved automatically: check `siphog_data\` and `dut\`
  for an old serial that kept collecting entries after a new one was entered.

## Upgrading a station from v0.6.1

- **Home › Version › Update…** (Engineer). No migrations.

## Known limitations

Unchanged from [v0.6](https://github.com/komcat/Horizon/releases/tag/v0.6).
