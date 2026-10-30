# Horizon.Station.UI — extracting the shared station UI from FAAM.UI

DLAM.UI (`docs/dlam-ui.md`) is built from the same station UI as FAAM.UI. Rather than fork
FAAM.UI's ~64k lines, the generic part moves into a WPF class library,
**`src/Horizon.Station.UI`**, slice by slice; FAAM.UI keeps only what is FAU-specific and
behaves identically after every slice. Layer rule: the library references
`Horizon.Workflow` + `Horizon.Engine` + `Horizon.Common` only, like a head.

## Conventions

- **Namespaces** `FAAM.UI.<folder>` → `Horizon.Station.UI.<folder>`; files keep their folder
  layout (`Mvvm/`, `Telemetry/`, `Process/`, `ViewModels/…`, `Views/…`).
- **XAML**: one namespace for the whole library — `xmlns:st="urn:horizon-station-ui"`
  (`XmlnsDefinition`s in `AssemblyInfo.cs`). A head re-prefixes moved types to `st:`.
- **C#**: a head puts every `Horizon.Station.UI.*` namespace in scope with `<Using Include>`
  in its csproj, so code that used a moved type by its old enclosing-namespace name still
  resolves.
- **Theme**: `Themes/StationTheme.xaml` (converters, the SystemColors palette, fixed status /
  video / prompt / alert colours, `Bevel3D`, `DeviceSelector`). A head merges it:
  `pack://application:,,,/Horizon.Station.UI;component/Themes/StationTheme.xaml`.
- **Per-head identity**: `StationApp` (set by the head at startup) — `Title` for now; the
  shared dialogs bind `{x:Static ui:StationApp.Title}`.
- **Config file names** keep their historical `faam_` prefix (`faam_script_presets.json`,
  `faam_psu_defaults.json`, `faam_position_map.json`, …): one PC runs one head, and renaming
  would orphan every FAAM station's settings.
- A moved `internal` type that a head still uses becomes `public` (it is library API now).

## Dependency picture (2026-10-29, before slice 1)

265 units (a XAML file + its code-behind = one unit). A text scan of type references
(`faam_deps.py`-style: which FAAM types each unit names) gave:

- **42 leaf units** — depend on no other FAAM type, or only on each other → slice 1.
- **One 128-unit cycle** around `EngineHost`, `ShellWindow` and `App`: every view depends on
  `EngineHost`; `ShellWindow` constructs every page (including FAAM's Run page); `App.xaml`
  holds the Machine-section templates.
- **52 units are FAU-specific** (stay in FAAM.UI): `Siphog/*`, the Run page (`RunView`,
  `ProcessStepsPanel`) and its alignment / scan / PSU panels (`AlignmentPanel`,
  `FauPreAlignPanel`, `UCorrectionPanel`, `HillClimbPanel`, `ScanningPanel`, `ScanPanel`,
  `SpiralRunPanel`, `PsuQuickPanel`, deprecated `AutoWPanel` / `RasterClimbPanel`),
  find-first-light, `DutUvData` + the SiPhOG DUT-data views, `ScanPanelStore`, the utility-app
  adapters.

## Slices

| # | Slice | Status |
|---|---|---|
| 1 | Library project + theme + MVVM + telemetry + leaf units | **done** (below) |
| 2 | Cut the back-edges from generic code into FAU-specific code, still inside FAAM.UI (seams below) | next |
| 3 | Move the generic core (the 128-unit cycle minus FAU-specific units) in one step | |
| 4 | DLAM.UI head on the library (`docs/dlam-ui.md` §7 phase 3) | |

### Slice 1 — done

Moved (48 files): `Mvvm/*` (ObservableObject, RelayCommand, converters), `Telemetry/*`
(TelemetryChannel / Store, UiSampler), `Session`, `LogHub`, `FolderOpener`, `JogTransforms`,
`PsuChannelDefaults`, `ScriptPresetStore`, `HiddenProgramStore`, `Process/RunDurationStore` +
`RunProgressTracker`, the Alert / Countdown / SerialEntry / MotionStop / StationState /
ExposureEditor windows, `LogLineViewModel`, `PageViewModel`, `DeviceRowViewModel`,
`IoPinViewModel`, the Placeholder / Status sections, `ProgramListItem`,
`DragInsertionAdorner`, `LuaFolding`, `OeeColors`, `OeeTimelineBar`, `FilterRow`,
`MarkerPlacer`, `SnapshotView`. FAAM.UI: project reference, global usings, `App.xaml` merges
the theme and keeps the section templates, `StationApp.Title = "FAAM"`. Kept in FAAM.UI on
purpose: `AppVersionInfo` (reads the head's own embedded `version_info.json`),
`Process/TeachPointStore` (FAAM's legacy teach points), `DutUvData`, find-first-light,
`ScanPanelStore`.

### Slice 2 — seams to cut first

| Back-edge (generic → FAU-specific) | Seam |
|---|---|
| `ShellWindow` constructs `RunView`, `DutDataView`, … | a page registry: the head contributes its pages (key, title, access level, factory); Home tiles and navigation read it (`AccessPolicy` keeps the minimum per key) |
| `OpCatalog` / `OpCatalogHelp` hold the `fau.*`, `align.*`, `siphog.*`, `hillclimb.*` blocks | catalog contributions: the library catalog has the generic blocks, a head adds its own (FAAM: FAU / SiPhOG; DLAM: `scan.xcan`, `scan.xcanTwoLens`, `scan.climb`, …) |
| `SettingsSectionViewModel` / View edit FAAM settings (scan-panel slots, script presets, find-first-light) | settings contributions: generic settings in the library, head sections appended |
| `EngineHost` wires the SiPhOG client / signal source / hill-climb feedback | a head hook on the host (`OnBuilt`, `OnConnected`) that registers product signal sources and providers |
| `UtilityAppHost` + `Apps/*` adapters | the launcher stays generic; each head registers its apps |
| ~44 hard-coded `"hex-right"` (jog, registration, gripped object, scan panels) | a per-head station profile (`StationApp`): tool hexapod(s), camera device, default PSU |

Then slice 3 moves `EngineHost`, the shell chrome, login / access, the Machine page and its
sections, the jog rail, the Engineer (Gantt) editor, Repository, OEE, Version / update
(`AppUpdater` takes the head's Velopack app id), the vision pages, and the DUT viewer.
