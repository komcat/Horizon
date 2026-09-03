namespace FAAM.UI.ViewModels;

/// <summary>
/// Which engine facet supplies the device drop-down for a command (drives the
/// Device picker source in the editor). <see cref="None"/> = the op takes no device.
/// </summary>
public enum DeviceKind { None, Motion, Io, Pneumatic, Camera }

/// <summary>
/// The editor widget used for one parameter field. <see cref="PositionName"/>,
/// <see cref="AxisName"/> and <see cref="SetClear"/> render as a combo box whose
/// options are filled by the block (positions from the chosen device, the axis
/// list, or Set/Clear); the rest are a text box / number box / check box.
/// </summary>
public enum ParamKind { Text, Number, Bool, PositionName, AxisName, SetClear, OutputPin, CameraName, SignalName, Operator, MotionDeviceName, DispenseRoutineName, ScriptName }

/// <summary>One parameter of an op — its persisted key, a display label, the editor widget, and a default value.</summary>
public sealed record ParamSpec(string Key, string Label, ParamKind Kind, string Default = "");

/// <summary>
/// A command the operator can drop into a sequence — maps to a Layer 2
/// <see cref="Horizon.Workflow.WorkflowStep.Op"/>. <see cref="Params"/> describes
/// the typed fields shown in the right-hand panel; their <see cref="ParamSpec.Key"/>
/// must match the keys <c>StepDispatcher</c> reads for that op.
/// </summary>
public sealed record OpSpec(
    string OpKey, string Display, string Category, DeviceKind DeviceKind, ParamSpec[] Params);

/// <summary>
/// FAAM-local catalog of the commands the visual editor exposes (v1 focused
/// subset). The op keys and parameter keys mirror
/// <c>Horizon.Workflow.StepDispatcher</c> exactly — that switch is the source of
/// truth, this is just the operator-facing description of it.
///
/// NOTE on Move-to-Position: <c>velocity</c> / <c>camera</c> / <c>exposure</c> are
/// optional PER-MOVE OVERRIDES persisted on the step — 0 / blank means "fall back to
/// the named position's stored value". The position's own stored velocity / camera /
/// exposure remain the default (edited via the "Edit position…" button, with a
/// read-only preview). Both are intentional and complementary: the block field
/// overrides for this one move; the position supplies the shared default.
/// </summary>
public static class OpCatalog
{
    public static readonly OpSpec[] All =
    {
        // ---- Motion ----
        // 'velocity' (mm/s) is an optional per-step override applied before the move;
        // 0 = use the current/position velocity. Capped to the controller type's range.
        new("motion.moveToPosition", "Move to Position", "Motion", DeviceKind.Motion,
            new[]
            {
                new ParamSpec("position", "Position", ParamKind.PositionName),
                new ParamSpec("velocity", "Velocity override (mm/s, 0 = use position)", ParamKind.Number, "0"),
                new ParamSpec("camera", "Camera override (blank = use position)", ParamKind.CameraName),
                new ParamSpec("exposure", "Exposure override (µs, 0 = use position)", ParamKind.Number, "0"),
            }),
        new("motion.moveRelative", "Move Relative", "Motion", DeviceKind.Motion,
            new[]
            {
                new ParamSpec("axis", "Axis", ParamKind.AxisName, "X"),
                new ParamSpec("distance", "Distance (mm)", ParamKind.Number, "0"),
                new ParamSpec("velocity", "Velocity (mm/s, 0=current)", ParamKind.Number, "0"),
                new ParamSpec("camera", "Camera (optional)", ParamKind.CameraName),
                new ParamSpec("exposure", "Exposure (µs, 0=skip)", ParamKind.Number, "0"),
            }),
        new("motion.setVelocity", "Set Velocity", "Motion", DeviceKind.Motion,
            new[] { new ParamSpec("speed", "Speed (mm/s)", ParamKind.Number, "10") }),

        // ---- IO ----
        new("io.setOutputByName", "Set Output", "IO", DeviceKind.Io,
            new[]
            {
                new ParamSpec("pin", "Output pin", ParamKind.OutputPin),
                new ParamSpec("state", "State", ParamKind.SetClear, "true"),
            }),
        // Full pulse cycle: start state → dwell → toggle → dwell → back to start → dwell.
        // 'startState' is the resting state the pin ends in; 'dwellMs' is held after each step.
        new("io.toggleOutput", "Toggle Output", "IO", DeviceKind.Io,
            new[]
            {
                new ParamSpec("pin", "Output pin", ParamKind.OutputPin),
                new ParamSpec("startState", "Start state ON (off = unchecked)", ParamKind.SetClear, "false"),
                new ParamSpec("dwellMs", "Dwell after each step (ms)", ParamKind.Number, "150"),
            }),

        // ---- Pneumatic ----
        new("pneumatic.extend", "Extend Slide", "Pneumatic", DeviceKind.Pneumatic, System.Array.Empty<ParamSpec>()),
        new("pneumatic.retract", "Retract Slide", "Pneumatic", DeviceKind.Pneumatic, System.Array.Empty<ParamSpec>()),
        // Retracts every configured slide and waits until all reach the retracted
        // sensor. Device-less (iterates all slides) — the lane device is ignored.
        new("pneumatic.retractAll", "Retract All Slides", "Pneumatic", DeviceKind.Pneumatic, System.Array.Empty<ParamSpec>()),

        // ---- Flow ----
        new("flow.wait", "Wait", "Flow", DeviceKind.None,
            new[] { new ParamSpec("ms", "Milliseconds", ParamKind.Number, "1000") }),
        // Real-time countdown shown in an always-on-top popup; the operator can
        // Cancel, which fails the program. Handled by WorkflowRunner (flow.waitPopup).
        new("flow.waitPopup", "Wait Popup", "Flow", DeviceKind.None,
            new[]
            {
                new ParamSpec("ms", "Duration (ms)", ParamKind.Number, "5000"),
                new ParamSpec("title", "Popup title", ParamKind.Text, "Please wait"),
                new ParamSpec("message", "Message", ParamKind.Text),
            }),
        // Non-modal always-on-top manual-jog window with a count-up timer; the operator
        // nudges the configured devices/axes then Continues (run resumes) or Cancels
        // (fails the run). Timeout (max 3600 s) resolves as Cancel. Handled by
        // WorkflowRunner (flow.jogPopup) via IOperatorPrompt.JogPopupAsync.
        new("flow.jogPopup", "Jog Popup", "Flow", DeviceKind.None,
            new[]
            {
                new ParamSpec("title", "Popup title", ParamKind.Text, "Manual jog"),
                new ParamSpec("message", "Message", ParamKind.Text),
                new ParamSpec("devices", "Devices (CSV, blank = all motion)", ParamKind.Text),
                new ParamSpec("axes", "Axes to show (e.g. XYZUVW)", ParamKind.Text, "XYZUVW"),
                new ParamSpec("step", "Default step (mm)", ParamKind.Number, "0.01"),
                new ParamSpec("timeoutSec", "Timeout (s, max 3600)", ParamKind.Number, "3600"),
            }),
        new("flow.log", "Log Message", "Flow", DeviceKind.None,
            new[] { new ParamSpec("message", "Message", ParamKind.Text) }),
        new("flow.comment", "Comment", "Flow", DeviceKind.None,
            new[] { new ParamSpec("text", "Note", ParamKind.Text) }),
        new("flow.askYesNo", "Ask Yes/No", "Flow", DeviceKind.None,
            new[]
            {
                new ParamSpec("var", "Result variable", ParamKind.Text),
                new ParamSpec("title", "Dialog title", ParamKind.Text, "Confirm"),
                new ParamSpec("message", "Question", ParamKind.Text),
            }),
        new("flow.askUser", "Ask (confirm / abort)", "Flow", DeviceKind.None,
            new[]
            {
                new ParamSpec("title", "Dialog title", ParamKind.Text, "Confirm"),
                new ParamSpec("message", "Question", ParamKind.Text),
            }),
        new("flow.if", "If (condition)", "Flow", DeviceKind.None,
            new[] { new ParamSpec("condition", "Condition", ParamKind.Text) }),
        // Guided If variants — pick a left-hand value, a comparison, and a number; no
        // expression typing. They open a block closed by the same Else / End If, and
        // read their left-hand value live at run time (no preceding read step needed).
        new("flow.ifSignal", "If Signal", "Flow", DeviceKind.None,
            new[]
            {
                new ParamSpec("signal", "Signal", ParamKind.SignalName),
                new ParamSpec("op", "Comparison", ParamKind.Operator, ">"),
                new ParamSpec("value", "Value", ParamKind.Number, "0"),
            }),
        new("flow.ifPosition", "If Position", "Flow", DeviceKind.None,
            new[]
            {
                new ParamSpec("device", "Motion device", ParamKind.MotionDeviceName),
                new ParamSpec("axis", "Axis", ParamKind.AxisName, "X"),
                new ParamSpec("op", "Comparison", ParamKind.Operator, ">"),
                new ParamSpec("value", "Value (mm)", ParamKind.Number, "0"),
            }),
        new("flow.else", "Else", "Flow", DeviceKind.None, System.Array.Empty<ParamSpec>()),
        new("flow.endif", "End If", "Flow", DeviceKind.None, System.Array.Empty<ParamSpec>()),
        new("var.set", "Set Variable", "Flow", DeviceKind.None,
            new[]
            {
                new ParamSpec("name", "Variable name", ParamKind.Text),
                new ParamSpec("value", "Value", ParamKind.Text),
            }),

        // ---- Signal ----
        // Reads one value from the unified signal catalog (any connected SMU/PSU/
        // Measurement/TEC/Laser channel, or a product source such as SiPhOG) into a
        // context variable. The 'signal' drop-down is filled at edit time from
        // IProcessService.DescribeSignals(); 'storeAs' defaults to the signal key when
        // blank, so 'siphog.sagPwrV' lands in a same-named variable usable by flow.if /
        // var.calculate. Device-less — the signal key carries its own device.
        new("signal.read", "Read Out", "Signal", DeviceKind.None,
            new[]
            {
                new ParamSpec("signal", "Signal", ParamKind.SignalName),
                new ParamSpec("storeAs", "Store in variable (blank = signal key)", ParamKind.Text),
            }),

        // ---- Vision ----
        // Runs a saved vision profile against its camera's latest frame. The match is
        // captured into context vars '{captureAs}_found/_score/_x/_y/_anchorx/_anchory/
        // _angle'; a no-match sets _found=0 (branch on it with flow.if) unless
        // 'requireMatch' is on, which fails the step. Device-less — the profile carries
        // its own camera.
        new("vision.runProfile", "Run Vision Profile", "Vision", DeviceKind.None,
            new[]
            {
                new ParamSpec("profile", "Vision profile name", ParamKind.Text),
                new ParamSpec("captureAs", "Capture prefix", ParamKind.Text, "vision"),
                new ParamSpec("requireMatch", "Fail step if no match", ParamKind.Bool, "false"),
            }),

        // ---- Dispense ----
        // Runs a saved per-product dispense routine (dot / grid / line) at the current
        // camera target. The camera→needle calibration places the needle where the camera
        // is centred, so the program/operator must have the camera centred + focused on the
        // target first. Device-less — the routine carries its own gantry / dispenser.
        new("dispense.runRoutine", "Run Dispense Routine", "Dispense", DeviceKind.None,
            new[] { new ParamSpec("routine", "Dispense routine", ParamKind.DispenseRoutineName) }),
        // Moves the needle onto the camera-pointed location (gantry += camera→needle offset),
        // extends the dispenser head, then approaches the surface by 'approachDistance' (mm) at
        // 'velocity' (mm/s). Leaves the head extended — no shot. Device-less (uses the calibration's
        // gantry / slide). The program/operator should have the camera centred on the target first.
        new("needle.approach", "Needle to Point", "Dispense", DeviceKind.None,
            new[]
            {
                new ParamSpec("approachDistance", "Approach distance (mm)", ParamKind.Number, "10"),
                new ParamSpec("velocity", "Approach velocity (mm/s)", ParamKind.Number, "1"),
            }),

        // ---- Tool (device registration: gripper <-> camera) ----
        // Both use the machine-global registration saved on the Machine page's Registration
        // section. 'tool' is the registered tool device (hex-right = the gripper). 'maxDistance'
        // (mm) bounds the single move — a longer one is REFUSED, never clamped; 0 = the
        // registration's own bound (10 mm by default). Device-less: the registration names
        // the camera device (gantry-main) and the tool device.
        // 'includeZ' also drives Z (camera to the captured focus height / tool to the camera's
        // focus plane); on by default — needs a focus Z captured on the registration, else the step fails.
        new("tool.cameraToTool", "Camera to Tool", "Tool", DeviceKind.None,
            new[]
            {
                new ParamSpec("tool", "Tool device", ParamKind.MotionDeviceName, "hex-right"),
                new ParamSpec("maxDistance", "Max move (mm, 0 = registration bound)", ParamKind.Number, "10"),
                new ParamSpec("includeZ", "Include Z (to focus height)", ParamKind.Bool, "true"),
            }),
        new("tool.toolToCamera", "Tool to Camera", "Tool", DeviceKind.None,
            new[]
            {
                new ParamSpec("tool", "Tool device", ParamKind.MotionDeviceName, "hex-right"),
                new ParamSpec("maxDistance", "Max move (mm, 0 = registration bound)", ParamKind.Number, "10"),
                new ParamSpec("includeZ", "Include Z (to focus plane)", ParamKind.Bool, "true"),
                new ParamSpec("zOffset", "Z offset (mm, blank = registration's; − = higher)", ParamKind.Text),
            }),
        // Sends the tool device back to the pose captured in its registration (all axes).
        new("tool.toPose", "Tool to Pose", "Tool", DeviceKind.None,
            new[] { new ParamSpec("tool", "Tool device", ParamKind.MotionDeviceName, "hex-right") }),

        // ---- Scan ----
        // XY raster over a lattice about the start position: visits EVERY node and
        // returns the device to the best one (no hill-climbing, so it maps the whole
        // field). 'axes' = the two grid axes; 'stepUm' the pitch between nodes;
        // 'extentMm' the half-extent per axis (grid spans the start ± this). 'signal'
        // is the feedback, picked from the unified signal catalog (any connected
        // SMU/PSU/Measurement/TEC/Laser channel, or a product source such as SiPhOG on
        // FAAM.UI — same drop-down as "Read Out"). Results land in '{captureAs}_peak/
        // _baseline/_snr/_significant/_state'. Device = the hexapod/gantry that rasters.
        new("scan.grid", "Grid Scan", "Scan", DeviceKind.Motion,
            new[]
            {
                new ParamSpec("axes", "Grid axes (two, e.g. X,Y)", ParamKind.Text, "X,Y"),
                new ParamSpec("stepUm", "Step / pitch (µm)", ParamKind.Number, "5"),
                new ParamSpec("extentMm", "Half-extent per axis (mm)", ParamKind.Number, "0.05"),
                new ParamSpec("settleMs", "Settle per node (ms)", ParamKind.Number, "400"),
                new ParamSpec("velocity", "Scan velocity (mm/s, 0 = current)", ParamKind.Number, "1"),
                new ParamSpec("averaging", "Samples averaged per node", ParamKind.Number, "3"),
                new ParamSpec("signal", "Feedback signal", ParamKind.SignalName),
                new ParamSpec("captureAs", "Capture prefix", ParamKind.Text, "grid"),
            }),
        // Coarse-to-fine hill-climb scan (multi-pass, progressively finer steps). 'mode'
        // presets the step schedule; 'axes' / 'stepSizesUm' override it. Same 'signal'
        // feedback + capture as Grid Scan. Device = the hexapod/gantry.
        new("scan.hexapod", "Hexapod Scan", "Scan", DeviceKind.Motion,
            new[]
            {
                new ParamSpec("mode", "Mode (coarse / fine / ultrafine)", ParamKind.Text, "coarse"),
                new ParamSpec("axes", "Axes order (blank = mode default)", ParamKind.Text, "Z,Y,X"),
                new ParamSpec("stepSizesUm", "Step sizes µm (CSV, blank = mode)", ParamKind.Text),
                new ParamSpec("settleMs", "Settle per step (ms)", ParamKind.Number, "400"),
                new ParamSpec("velocity", "Scan velocity (mm/s, 0 = current)", ParamKind.Number, "1"),
                new ParamSpec("signal", "Feedback signal", ParamKind.SignalName),
                new ParamSpec("captureAs", "Capture prefix", ParamKind.Text, "scan"),
            }),

        // ---- Align (Hill Climber) ----
        // Anchors / clears the lifetime Z+ budget on the chosen motion device (hex). Once an
        // anchor is set, each Start Hill Climb only gets the travel earlier climbs didn't use,
        // so re-running can't silently spend the budget twice.
        new("hillclimb.setAnchor", "Set Hill Anchor", "Align", DeviceKind.Motion, System.Array.Empty<ParamSpec>()),
        new("hillclimb.clearAnchor", "Clear Hill Anchor", "Align", DeviceKind.Motion, System.Array.Empty<ParamSpec>()),
        // Runs the SiPhOG-fed hill climb to completion on the device. 'distance' is the lifetime
        // ceiling (µm); with an anchor set the run uses what's left of it. 'targetV' > 0 exits
        // early at ≥90% of target. Results land in '{captureAs}_vpeak/_vstart/_ztraveled/_stop'.
        new("hillclimb.start", "Start Hill Climb", "Align", DeviceKind.Motion,
            new[]
            {
                new ParamSpec("distance", "Distance / ceiling (µm)", ParamKind.Number, "98"),
                new ParamSpec("targetV", "Target Sagnac V (0 = disabled)", ParamKind.Number, "0"),
                new ParamSpec("captureAs", "Capture prefix", ParamKind.Text, "hillclimb"),
            }),
        // Aborts a climb started in a parallel lane (Start blocks its own lane). No-op when idle.
        new("hillclimb.stop", "Stop Hill Climb", "Align", DeviceKind.None, System.Array.Empty<ParamSpec>()),

        // ---- App (Run-page utility springboard) ----
        // Drive the Run-page utility apps from a sequence. 'appId' is one of:
        // ucorrection, faupre, hillclimb, siphog. Open/Home are navigation only.
        // Start ACTIVATES an app (foreground + begin its routine / connect) and returns
        // once started — it does NOT await completion (use hillclimb.start for the
        // headless, await-to-completion climb). Requires a head with the springboard.
        new("app.open", "Open App", "App", DeviceKind.None,
            new[] { new ParamSpec("appId", "App (ucorrection / faupre / hillclimb / siphog)", ParamKind.Text, "hillclimb") }),
        new("app.home", "Home (Launcher)", "App", DeviceKind.None, System.Array.Empty<ParamSpec>()),
        new("app.start", "Open & Run App", "App", DeviceKind.None,
            new[] { new ParamSpec("appId", "App (ucorrection / faupre / hillclimb / siphog)", ParamKind.Text, "hillclimb") }),
        new("app.stop", "Stop App", "App", DeviceKind.None,
            new[] { new ParamSpec("appId", "App (ucorrection / faupre / hillclimb / siphog)", ParamKind.Text, "hillclimb") }),

        // ---- SiPhOG -> Google Sheet (V11 UPDATE_SAG) ----
        // Writes the bench row's current SAG-PWR readings into the chosen stage column of
        // the SiPhOG server's Google Sheet. The bench (column-A key) is the run's DUT serial
        // — taken automatically, so there's no field for it; start a DUT run / DUT Entry first.
        // The measured values come from the SiPhOG server's own live stream, so only the stage
        // and completed flag are sent. The step blocks until the server's ACK/ERR (a Google
        // round-trip) and fails the program on ERR. Needs the Run-page SiPhOG client connected.
        new("siphog.updateSheet", "Save SAG to Sheet", "Signal", DeviceKind.None,
            new[]
            {
                new ParamSpec("stage", "Stage (ACTUAL / WET / UV)", ParamKind.Text, "ACTUAL"),
                new ParamSpec("completed", "Mark stage completed", ParamKind.Bool, "true"),
            }),

        // ---- Script ----
        // Runs a saved .moon script from the active product to completion, inline in the
        // sequence. The script drives the same hardware through the 'machine' table and can
        // push a captured still into the Run page's snapshot box (machine.ui.showImage).
        new("script.run", "Run Script", "Flow", DeviceKind.None,
            new[] { new ParamSpec("script", "Script (.moon)", ParamKind.ScriptName) }),

        // ---- DUT ----
        // Prompts the operator (modal) for a serial number and opens the DUT record
        // for this run, so later dut.* steps record into it. Cancel fails the program.
        // Handled by WorkflowRunner (dut.promptSerial).
        new("dut.promptSerial", "DUT Entry", "DUT", DeviceKind.None,
            new[]
            {
                new ParamSpec("title", "Dialog title", ParamKind.Text, "DUT Serial"),
                new ParamSpec("message", "Prompt", ParamKind.Text, "Enter or scan the unit serial number:"),
                new ParamSpec("var", "Store serial in variable (optional)", ParamKind.Text),
            }),
        // Snapshots EVERY signal in the unified catalog (all SiPhOG fields plus every
        // connected SMU / PSU / Measurement / TEC / Laser channel) into one grouped DUT
        // entry, named by 'dataset'. One block = the unit's full telemetry at this point.
        new("dut.readAllSignals", "Read All Signals", "DUT", DeviceKind.None,
            new[]
            {
                new ParamSpec("dataset", "Data set name", ParamKind.Text, "signals"),
            }),
        // Finalises the DUT record this run opened (the bookend to DUT Entry): stamps the
        // verdict, persists the record, and detaches it so later dut.* steps skip and the
        // program's own end-of-run close doesn't double-record. A no-op if no DUT is open.
        // Handled by WorkflowRunner (dut.close).
        new("dut.close", "DUT Close", "DUT", DeviceKind.None,
            new[]
            {
                new ParamSpec("verdict", "Verdict (Pass / Fail / Aborted)", ParamKind.Text, "Pass"),
            }),
    };

    private static readonly Dictionary<string, OpSpec> ByKey =
        All.ToDictionary(s => s.OpKey, StringComparer.OrdinalIgnoreCase);

    /// <summary>The spec for an op key (case-insensitive), or null if not in the catalog.</summary>
    public static OpSpec? Find(string opKey)
        => ByKey.TryGetValue(opKey, out var s) ? s : null;

    /// <summary>
    /// Fallback spec for an op loaded from a <c>.hwf</c> that this catalog doesn't
    /// know (e.g. authored in Horizon.UI). Keeps it editable as a no-param block so
    /// the program still round-trips instead of being dropped.
    /// </summary>
    public static OpSpec ForUnknown(string opKey)
        => new(opKey, opKey, "Other", DeviceKind.None, System.Array.Empty<ParamSpec>());

    /// <summary>Distinct categories in catalog order — drives the palette grouping.</summary>
    private static readonly IReadOnlyList<string> CategoryList =
        All.Select(s => s.Category).Distinct().ToList();

    public static IReadOnlyList<string> Categories => CategoryList;
}
