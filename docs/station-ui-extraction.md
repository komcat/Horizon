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
| 2 | Cut the back-edges from generic code into FAU-specific code, still inside FAAM.UI (seams below) | **done** — no generic → head-only reference left (scan below) |
| 3 | Move the generic core (the 128-unit cycle minus FAU-specific units) in one step | next |
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

The head is an `IStationHead` (FAAM.UI for now; moves with `EngineHost` in slice 3), passed to
`new EngineHost(new FaamHead())` and read as `host.Head`.

| Back-edge (generic → FAU-specific) | Seam |
|---|---|
| `ShellWindow` constructs `RunView`, `DutDataView`, … | **done** — the head builds its Run page (`IRunPage`: Activate / Deactivate, progress + edit-program events, process timer) and DUT Data page (`IStationPage`), both interfaces in the library; the DUT process name (`"FAU attach"`) is `IStationHead.DutProcessName`. Every other page is shared, so no general registry yet. |
| `OpCatalog` / `OpCatalogHelp` hold the `fau.*`, `align.*`, `siphog.*`, `hillclimb.*` blocks | **done** — `OpCatalog.Register` / `OpCatalogHelp.Register`; FAAM's 12 blocks + help are `FaamOps` / `FaamOpHelp` (`IStationHead.Ops` / `OpHelp`, registered by `EngineHost`). Palette categories keep a fixed order (`CategoryOrder`); verified identical to before (70 blocks, same order per category, 39 help entries). DLAM adds `scan.xcan`, `scan.xcanTwoLens`, `scan.climb`, … the same way. |
| `SettingsSectionViewModel` / View edit FAAM settings (scan-panel slots, script presets, find-first-light) | **done** — `IHeadSettingsPanel` (library: Load / Validate / Save / SavedFiles), listed after the page's own groups and saved with its Save button (head panels first); FAAM: `StartCorrectionSettingsViewModel` + `ScanPanelSettingsViewModel` with DataTemplates in `App.xaml`. Visible change: "Start correction" now sits next to "Run page scanning" instead of after "Startup". |
| `EngineHost` wires the SiPhOG client / signal source / hill-climb feedback | **not needed** — the wiring lives in FAAM's Run page (`RunView`), which is the head's. What did need a seam in `EngineHost`: the default product (`IStationHead.DefaultProductName`, FAAM "FAAM") and FAAM's legacy `faam_teach_points.json` import (`IStationHead.OnProductReady`). |
| `UtilityAppHost` + `Apps/*` adapters | **not needed** — used only by FAAM's Run page and its panels, so the launcher stays in FAAM.UI (head-only). Promote it to the library when a second head wants one. |
| ~44 hard-coded `"hex-right"` (jog, registration, gripped object, scan panels) | **done** — 16 literals, 10 in head-only files (Run-page panels, scan store). Generic defaults now come from `StationApp.ToolDevice` (FAAM `"hex-right"`): the `tool.*` blocks' Tool device and the Machine › Registration tool picker. The `JogTransforms` seed table lists every device, so it stays. |
| `OperatorActivity`'s `ActivityCategory` enum lists FAAM routines | **done** — a string-backed `ActivityCategory` with the generic `Jog` / `Sequence` / `Other`; FAAM's routines are `FaamActivity` (same names, so the DUT-record keys are unchanged). |
| `EngineerPageView` hosts FAAM's Find First Light and Testing Ground tabs | **done** — `IStationHead.CreateEngineerTabs` (`EngineerTab`, an `IStationPage` content is activated / deactivated); the head's tabs follow the shared ones, so Find First Light moved after Dispensing. |
| `StartupWindow` / `HomeView` / `VersionHistoryWindow` read `AppVersionInfo` (FAAM resource name) | **done** — `AppVersionInfo` reads `<entry assembly>.version_info.json` from the entry assembly and moved to the library. |

Scan after the cuts (comments stripped; `backedges.py`): 108 generic units, 43 head-only, and the only remaining hits are name collisions — the Engineer category "App", the word "Program", `PikaAvatar`'s own `Phase` property. Head-only units: `Siphog/*`, the Run page + its panels + the utility-app launcher, the DUT Data page (UV + SiPhOG), find-first-light, `TestingGroundView`, `ScanPanelStore`, `TeachPointStore`, `DutUvData`, the `Faam*` head classes and FAAM settings panels, `App`, `Program`.

Then slice 3 moves `EngineHost`, the shell chrome, login / access, the Machine page and its
sections, the jog rail, the Engineer (Gantt) editor, Repository, OEE, Version / update
(`AppUpdater` takes the head's Velopack app id), the vision pages, and the DUT viewer.
