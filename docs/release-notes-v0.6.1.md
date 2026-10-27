# FAAM.UI v0.6.1

Patch release of **FAAM.UI**, the FAU-attach operator application on the Horizon
3-layer engine stack (Layer 1 `Horizon.Engine` → Layer 2 `Horizon.Workflow` → Layer 3
`FAAM.UI`). Snapshot of `master` at the tag, published self-contained for `win-x64`.
Internal engineering pre-release for rig deployment — and the first release delivered
through the in-app software update.

This page lists what changed since
[v0.6](https://github.com/komcat/Horizon/releases/tag/v0.6); see those notes for the
assets and the one-time switch to the self-updating install.

## What's new since v0.6

### Startup screen
- The title reads **Fiber Active Alignment Machine**, with the version and git commit
  (`v0.6.1 · abc1234`) under the subtitle.
- **Pika** sits beside the title: hops and zaps while the hardware connects, turns red if
  startup fails.
- Larger text for the connection status (18 pt) and the connection log (15 pt); the window
  is a little bigger to fit it.

### Deployment
- `README.txt` beside `allow-firewall.ps1` in the install folder: camera connected but
  "no video" = firewall, and the command to fix it (PowerShell and cmd).

## Upgrading a station from v0.6

- Installed from `FAAM.UI-win-Setup.exe` / `-Portable.zip`: **Home › Version › Update…**
  (Engineer). No migrations; the firewall rule stays valid.
- A plain-zip v0.6 copy: switch to the self-updating install as described in the v0.6 notes.

## Known limitations

Unchanged from [v0.6](https://github.com/komcat/Horizon/releases/tag/v0.6).
