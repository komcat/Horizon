# Horizon.ConfigTool — Hardware Config Authoring Utility

> Design doc. A console utility that drafts and edits the hardware
> configuration JSON files Horizon consumes — preset-driven, validated.
> Status: **DRAFT for review**.

## 1. Purpose

The hardware config files in `%APPDATA%\Horizon\config\` are hand-edited JSON
today. That is error-prone: enum typos, missing fields, the Ezi-IO input/output
pin-offset subtlety, and silent **cross-file** mistakes (a pneumatic slide that
references an IO pin which does not exist). `Horizon.ConfigTool` is a console
utility that starts fresh or loads an existing config set, adds hardware from
**presets**, allows edit/remove, and **validates** — including cross-references
— before writing the files back.

It is authoring-side tooling. It pairs with Layer 1 (`Horizon.Engine`), which
*consumes* these files, but is not part of the runtime stack.

## 2. Codebase audit — the model map

Each config file already has a **public model record** in its domain project.
The engine and the legacy app deserialize into the **same** types — one source
of truth, so the tool reusing them cannot drift.

| File | Model type | Project / namespace |
|---|---|---|
| `io_devices.json` | `IODeviceConfig` (+ `IOPinConfig`) | `Horizon.IO` / `Horizon.IO.Models` |
| `motor_devices.json` | `MotionDevice` (+ `PositionStruct`) | `Horizon.Motion` / `Horizon.Motion.Models` |
| `pneumatic_slides.json` | `PneumaticConfig` (+ `IOPinReference`) | `Horizon.Pneumatic` / `Horizon.Pneumatic.Models` |
| `camera-config.json` | `RegisteredCamera` | `Horizon.Camera` / `Horizon.Camera.Models` |
| `laser_controller_devices.json` | `LaserControllerDeviceConfig` | `Horizon.LaserController` / `…Models` |
| `current_measurement_devices.json` | `CurrentMeasurementDeviceConfig` | `Horizon.CurrentMeasurement` / `…Models` |
| `safety_watch.json` | `SafetyWatchConfig` | `Horizon.Safety` / `Horizon.Safety.Models` |
| `motion_interlocks.json` | `InterlockConfig` | `Horizon.Interlock` / `Horizon.Core.Interlock` |

Shared reader: `Horizon.Common.ConfigLoader.LoadJson<T>` — `System.Text.Json`,
case-insensitive, comments + trailing commas allowed, camelCase
`JsonStringEnumConverter`, `.local.json` overrides `.json`.

**The gap:** only `SafetyWatchManager`, `InterlockService`, and
`CameraConfigStore` expose a *save* path. IO / Motion / Pneumatic / Laser /
Measurement are **load-only** — and their JSON file-envelope types
(`IODevicesFile`, etc.) are private to the loaders.

## 3. Architecture decision

**The tool must not redefine device schemas.** It reuses the public model
records above. The only thing it may own is the trivial *file envelope*
(`{ "devices": [ … ] }`).

To avoid the tool replicating private envelope types — and to give the schema's
owner control of round-tripping — **Phase 0 adds a symmetric `Save` to each
domain loader**: every domain that owns a config schema owns both `Load` and
`Save` for it. The tool then stays thin and correct-by-construction; a future
in-app config editor calls the same methods.

```
Horizon.ConfigTool  ──Load/Save──▶  domain loaders (own the schema)
                                      EziIOJsonLoader, MotionJsonLoader, …
                    ──validate──▶  shared validation (cross-references)
```

Rejected alternative: the tool defines its own envelope records and serializes
directly. Works, but duplicates the JSON-facing shape and drifts.

## 4. Project setup

`src\Horizon.ConfigTool\` — console app, sibling to `Horizon.Console` /
`Horizon.Workflow.Console`.

- `net8.0-windows`, `x64`, nullable, implicit usings (matches the console hosts).
- `PackageReference`: `Spectre.Console` 0.49.1.
- `ProjectReference`: `Horizon.Common` + each domain project, **added per phase**
  as that domain is implemented (keeps early builds light).
- Operates on a config directory — default `%APPDATA%\Horizon\config\`,
  overridable, so a fresh set can be drafted to an empty folder.

## 5. UX flow

```
start → choose config directory (default %APPDATA%\Horizon\config)
        → load existing files, or start fresh (empty set)

main menu — one entry per domain:
   Cameras · Motion · IO · Pneumatic · Laser · Measurement · Safety · Interlocks

per domain:  list devices · add (from preset) · edit · remove

save → validate the whole set → write the JSON files
       (offer a dry-run / diff before writing)
```

**Add-device flow:** domain → pick a **preset** → fill 2–4 instance fields →
the preset supplies every other field (controller type, ports, defaults, the
Ezi-IO register layout). The user never types an enum or remembers an offset.

## 6. Preset catalog (initial)

| Domain | Presets | Instance fields prompted |
|---|---|---|
| Motion | ACS gantry (SPiiPlus), PI hexapod (GCS2) | name, IP, installed axes |
| Camera | Basler IP (Pylon), IDS USB (Peak) | name, serial / IP |
| IO | Fastech Ezi-IO 16-in/16-out, 8/8 | name, IP, pin names |
| Laser | Thorlabs CLD101x | name, VISA resource |
| Measurement | Keithley 6482 | name, GPIB address, channels |
| Pneumatic | Slide with 2 sensors, slide output-only | name, output pin, sensor pins |
| Safety | Watched input (E-stop, door) | device, pin, edge, severity |
| Interlock | Slide-retracted-before-motion rule | target device, axis block, slide |

Presets live in a small data structure, not scattered in UI code — easy to add.

## 7. Validation — the feature that justifies the tool

On save, beyond per-field checks, validate **cross-references** that hand-editing
cannot catch:

- pneumatic slides → referenced IO device + pin names exist
- `safety_watch` watched inputs → referenced IO device + pin exist
- `motion_interlocks` rules → referenced slides / devices exist
- no duplicate device names within a domain
- IP / serial / VISA-resource format sanity

Validation logic is a **shared library** (`Horizon.ConfigTool.Validation` or a
small `Horizon.Config` project) so a future in-app editor reuses it.

## 8. Phasing

| Phase | Deliverable |
|---|---|
| 0 | Symmetric `Save` on each domain loader; expose the file-envelope types needed. Foundation — no tool yet. |
| 1 | `Horizon.ConfigTool` project; choose directory; load + **list + view** all domains (read-only). Proves the model references. |
| 2 | Add device from preset → save (one domain end-to-end first, e.g. Measurement — simplest schema). |
| 3 | Edit / remove; roll out add/edit to all 8 domains. |
| 4 | Cross-reference validation + save-time report; optional dry-run diff. |

Each phase = compilable, runnable, reviewed — the established per-step rhythm.

## 9. Risks / open questions

- **Heavy references.** `RegisteredCamera` lives in `Horizon.Camera`, which
  drags the Pylon/IDS/HALCON native deps. Acceptable for a dev utility, but
  consider moving pure config model records to `Horizon.Common` (or a new
  `Horizon.Config.Models`) so neither the tool nor a future editor pulls SDKs.
- **`.local.json` overrides & comments.** Decide whether the tool preserves a
  hand-written `.local.json` / comments or normalizes on write. Safest v1:
  only ever write the base `.json`, never touch `.local.json`.
- **`connection_config.json`** has no model type in the codebase — out of scope
  until one exists.
- Don't build the editor twice — Phase 0's `Save` + the validation library are
  the shared seam the WPF app would reuse.
