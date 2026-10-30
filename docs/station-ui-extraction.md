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
| 3 | Move the generic core (the 128-unit cycle minus FAU-specific units) in one step | **done** (below) |
| 3b | Run-page kit: FAAM's Run page split into shared pieces | **done** (below) |
| 4 | DLAM.UI head on the library (`docs/dlam-ui.md` §8) | **first cut built** — not yet run on the rig |

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

### Slice 3 — done

Moved in one step (`tools/station-ui/move_slice3.py`: `git mv` of every unit not in
`backedges.py`'s `HEAD_ONLY`, then `namespace` / `using` / `x:Class` / `clr-namespace`
`FAAM.UI` → `Horizon.Station.UI`): 161 files — `EngineHost`, `IStationHead`, `ShellWindow`,
startup / login / shutdown windows, `AccessPolicy`, `ActivityGate`, operator activity / prompt /
attention, `TowerLight`, `HexapodPivotDefaults`, `AppUpdater`, Version window, the history / picker
windows, Home, the Machine page and every section, jog rail / pad / popup, the Engineer page
(timeline, deck, dispensing, script editor, `OpCatalog` + help), Repository, OEE + downtime +
maintenance history, the vision pages (Vision Profile, Gripped FAU, Part Alignment, calibration,
needle calibration), the DUT results viewer, and the toolbar images (`assets/*.png`). FAAM.UI keeps
66 files, all head-only (`backedges.py` now reports 0 generic units).

What else changed:

- **Section templates** left FAAM's `App.xaml` for `Themes/StationSections.xaml`; a head's
  `App.xaml` merges `StationTheme.xaml` then `StationSections.xaml`, and keeps only DataTemplates for
  its own settings panels.
- **Library XAML cannot use `st:`** — a `urn:` `XmlnsDefinition` doesn't resolve inside its own
  assembly (MC3074). Library XAML uses `clr-namespace:` prefixes
  (`tools/station-ui/unprefix_lib_xaml.py` rewrote the five files that used `st:`); heads keep `st:`.
- **Identity**: the shell, jog popup, operator prompt, startup / login / shutdown titles and the
  shared log source read `StationApp.Title`; the OEE session's app name and the Version window's
  product line read the new `StationApp.AppName` (entry assembly name, so FAAM still records
  `"FAAM.UI"`).
- **Images**: library XAML keeps `/assets/x.png` (resolves to the library); FAAM's Run page points
  at `/Horizon.Station.UI;component/assets/stop.png`.
- **Packages**: the library carries AvalonEdit, SkiaSharp.Views.WPF, Serilog and Velopack (same
  versions); FAAM.UI drops AvalonEdit and keeps SkiaSharp (its own charts) and Velopack (`Program.Main`).
  The library also gets the same `<Using>` list as the head.
- No type needed to go `internal` → `public` — the moved code was already public where a head uses it.
- Verified: solution builds x64 Release with no errors; FAAM.UI's analyzer warnings
  (`--no-incremental`) are the same set as before the move (268 distinct, none new).

Left for slice 4 — **DLAM publishes on its own Velopack channel** in the same repo
(`komcat/Horizon`). No `AppUpdater` change: an installed copy updates from the channel it was
packed with (`releases.<channel>.json`), and `GithubSource` skips releases that don't carry that
feed, so FAAM (default channel `win`, `releases.win.json`) and DLAM never see each other's packages.
The DLAM release pipeline must pass `--channel dlam` to `vpk pack`, `vpk download github` (delta
base) and `vpk upload github`, use `--packId DLAM.UI`, and tag differently from FAAM's `v*`
(e.g. `dlam-v0.1.0`). `UpdateOptions.ExplicitChannel` is only for switching channels — not needed.

### Slice 3b — the Run-page kit (done, with slice 4)

FAAM's Run page mixed generic parts with FAU panels; DLAM needs the generic parts, so they moved
into the library as building blocks a head composes (`Views/`):

- **`RunStepsPanel`** — `git mv` of FAAM's `ProcessStepsPanel`: deck picker → one button per
  `Run page` program, run / Auto-chain, STOP, process timer, progress bar, DUT banner, Edit /
  Inspect Motion. A head's fallback steps (shown while no deck drives the list) plug in through
  **`IRunBuiltInSteps`** (`Title`, `CreateSteps`, `RunAsync(step, IRunStepContext, ct)`); FAAM's
  Phase-1 FAU-attach sequence is now `FAAM.UI/Views/FaamBuiltInSteps.cs` (moved verbatim). Without
  built-ins the list is empty and the banner says to author a deck. STOP also raises the new
  **`EngineHost.StopPressed`**, so other Run-page sequences (DLAM's alignment band) cancel too.
- **`RunCameraPanel`** — the camera column from FAAM's `RunView` (main feed, crosshair, ROI,
  MoveClick, detection flash, tool overlays, DUT banner, capture prompts, Zoom 2×, snapshot box,
  thumbnails, motion inspector — `ShowInspector`). `Attach` registers the prompts on
  `host.CapturePrompts` and makes the snapshot box the image sink.
- **`RunLogView`** (log box) and **`RunPageLayout`** (camera column width per head:
  `faam_run_layout.json`, `dlam_run_layout.json`).
- **`DutRecordsView`** — a generic DUT Data page (serials → runs → values, CSV export) for a head
  without its own data views (DLAM).

FAAM's `RunView` is now the kit + its PSU panel + utility apps + SiPhOG wiring; behaviour is meant
to be unchanged (FAU steps, prompts, inspector, layout file). Verified by build + analyzer diff
only — needs a FAAM.UI run.

`build/version-info.ps1` moved from `src/FAAM.UI/build/` to the repo root and takes `-TagPrefix`
(FAAM `v`, DLAM `dlam-v`), so each head's Version window lists only its own releases.
