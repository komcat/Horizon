namespace Horizon.Station.UI.ViewModels;

/// <summary>
/// Which engine facet supplies the device drop-down for a command (drives the
/// Device picker source in the editor). <see cref="None"/> = the op takes no device.
/// </summary>
public enum DeviceKind { None, Motion, Io, Pneumatic, Camera, Lighting, PowerSupply }

/// <summary>
/// The editor widget used for one parameter field. <see cref="PositionName"/>,
/// <see cref="AxisName"/> and <see cref="SetClear"/> render as a combo box whose
/// options are filled by the block (positions from the chosen device, the axis
/// list, or Set/Clear); the rest are a text box / number box / check box.
/// </summary>
public enum ParamKind { Text, Number, Bool, PositionName, AxisName, SetClear, OutputPin, CameraName, SignalName, Operator, MotionDeviceName, DispenseRoutineName, ScriptName, LightName, LightColor, Choice }

/// <summary>
/// One parameter of an op — its persisted key, a display label, the editor widget, and a default value.
/// <paramref name="PositionDevice"/> pins a <see cref="ParamKind.PositionName"/> picker to a fixed motion
/// device (for device-less ops, e.g. the align.* blocks whose points live on gantry-main); null = the block's device.
/// <paramref name="Choices"/> is the fixed drop-down list of a <see cref="ParamKind.Choice"/> param.
/// </summary>
public sealed record ParamSpec(string Key, string Label, ParamKind Kind, string Default = "", string? PositionDevice = null,
    string[]? Choices = null);

/// <summary>
/// A command the operator can drop into a sequence — maps to a Layer 2
/// <see cref="Horizon.Workflow.WorkflowStep.Op"/>. <see cref="Params"/> describes
/// the typed fields shown in the right-hand panel; their <see cref="ParamSpec.Key"/>
/// must match the keys <c>StepDispatcher</c> reads for that op.
/// </summary>
public sealed record OpSpec(
    string OpKey, string Display, string Category, DeviceKind DeviceKind, ParamSpec[] Params);

/// <summary>
/// Catalog of the commands the visual editor exposes — the generic blocks here, plus
/// whatever the head adds through <see cref="Register"/>. The op keys and parameter keys mirror
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
    private static readonly OpSpec[] Generic =
    {
        // ---- Motion ----
        // 'velocity' (mm/s) is an optional per-step override applied before the move;
        // 0 = use the current/position velocity. Capped to the controller type's range.
        new("motion.moveToPosition", "Move to Position", "Motion", DeviceKind.Motion,
            new[]
            {
                new ParamSpec("position", "Position", ParamKind.PositionName),
                new ParamSpec("velocity", "Velocity override (mm/s, 0 = use position)", ParamKind.Number, "0"),
                // Approach: the last N mm at a slower velocity, then the velocity is restored.
                // Both must be > 0 to apply (MoveApproach in Layer 2).
                new ParamSpec("approachDistance", "Approach distance (mm before target, 0 = off)", ParamKind.Number, "0"),
                new ParamSpec("approachVelocity", "Approach velocity (mm/s, 0 = off)", ParamKind.Number, "0"),
                new ParamSpec("camera", "Camera override (blank = use position)", ParamKind.CameraName),
                new ParamSpec("exposure", "Exposure override (µs, 0 = use position)", ParamKind.Number, "0"),
                // Lighting call-out applied while travelling; blank light = leave lighting as is.
                new ParamSpec("light", "Lighting (blank = leave as is)", ParamKind.LightName),
                new ParamSpec("lightColor", "Light colour", ParamKind.LightColor, "white"),
                new ParamSpec("lightPercent", "Light intensity (%, 0 = off)", ParamKind.Number, "50"),
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
        // DEPRECATED — do not use in new programs (kept only so existing programs still load
        // and run). For a slow final approach use Move to Position's approach distance / velocity.
        // Teach-or-approach a hand-reached final position: first run = manual jog, saved on
        // Continue; later runs offer a slow move there (velocity snapshot → crawl Z, then Y,
        // then X under a big STOP window → restore), then the manual jog for fine adjust. Handled by
        // WorkflowRunner (motion.slowApproach); type a new name to create the position.
        new("motion.slowApproach", "Slow Approach (DEPRECATED — do not use)", "Motion", DeviceKind.Motion,
            new[]
            {
                new ParamSpec("position", "Final position (saved on first run)", ParamKind.PositionName),
                new ParamSpec("speed", "Approach speed (mm/s)", ParamKind.Number, "0.2"),
                new ParamSpec("axes", "Approach axes", ParamKind.Text, "XYZ"),
                new ParamSpec("maxDistance", "Max approach distance (mm)", ParamKind.Number, "10"),
                new ParamSpec("jogAfterApproach", "Manual adjust after arriving", ParamKind.Bool, "true"),
                new ParamSpec("updateAfterJog", "Re-save position after every adjust", ParamKind.Bool, "false"),
                new ParamSpec("message", "Jog popup message", ParamKind.Text),
                new ParamSpec("jogAxes", "Jog axes", ParamKind.Text, "XYZ"),
                new ParamSpec("step", "Jog default step (mm)", ParamKind.Number, "0.01"),
            }),

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
        // Yellow alert the operator must acknowledge; the buzzer pin pulses a calm pattern
        // (beepCount × beepMs chirps, beepGapMs apart, then restMs quiet) until they do.
        // Blank pin = alert only. Handled by WorkflowRunner (io.alertBuzzer).
        new("io.alertBuzzer", "Alert + Buzzer (acknowledge)", "IO", DeviceKind.Io,
            new[]
            {
                new ParamSpec("title", "Alert title", ParamKind.Text, "UV complete"),
                new ParamSpec("message", "Message", ParamKind.Text),
                new ParamSpec("pin", "Buzzer output pin (blank = no buzzer)", ParamKind.OutputPin),
                new ParamSpec("beepCount", "Beeps per group (1–5)", ParamKind.Number, "2"),
                new ParamSpec("beepMs", "Beep length (ms)", ParamKind.Number, "120"),
                new ParamSpec("beepGapMs", "Gap between beeps (ms)", ParamKind.Number, "180"),
                new ParamSpec("restMs", "Quiet between groups (ms)", ParamKind.Number, "2500"),
            }),

        // ---- Pneumatic ----
        new("pneumatic.extend", "Extend Slide", "Pneumatic", DeviceKind.Pneumatic, System.Array.Empty<ParamSpec>()),
        new("pneumatic.retract", "Retract Slide", "Pneumatic", DeviceKind.Pneumatic, System.Array.Empty<ParamSpec>()),
        // Retracts every configured slide and waits until all reach the retracted
        // sensor. Device-less (iterates all slides) — the lane device is ignored.
        new("pneumatic.retractAll", "Retract All Slides", "Pneumatic", DeviceKind.Pneumatic, System.Array.Empty<ParamSpec>()),

        // ---- Lighting ----
        // 'light' is a channel group (main_camera_light) or an ungrouped channel's
        // name from lighting_devices.json; 'percent' 0..100, 0 = off.
        // Colour call-outs light only their channels (the light's others go off):
        // red / green / blue = that LED, white = R+G+B mixed, collimate = the collimated light,
        // white_collimate = collimate + R+G+B.
        new("lighting.setLightPercent", "Set Light %", "Lighting", DeviceKind.Lighting,
            new[]
            {
                new ParamSpec("light", "Light", ParamKind.LightName),
                new ParamSpec("percent", "Intensity (%, 0 = off)", ParamKind.Number, "50"),
            }),
        new("lighting.setLightColor", "Set Light Colour", "Lighting", DeviceKind.Lighting,
            new[]
            {
                new ParamSpec("light", "Light", ParamKind.LightName, "main_camera_light"),
                new ParamSpec("color", "Colour", ParamKind.LightColor, "white"),
                new ParamSpec("percent", "Intensity (%, 0 = off)", ParamKind.Number, "50"),
            }),
        new("lighting.connect", "Connect Lighting", "Lighting", DeviceKind.Lighting, System.Array.Empty<ParamSpec>()),
        new("lighting.disconnect", "Disconnect Lighting", "Lighting", DeviceKind.Lighting, System.Array.Empty<ParamSpec>()),

        // ---- Power supply ----
        // Device = a supply from power_supply_devices.json (e.g. the UDP3305S-E).
        // 'channel' is 1-based (UDP3305S: CH1/CH2 0–32 V / 0–5 A, CH3 0–6 V / 0–3 A);
        // an out-of-range setpoint is refused by the adapter and fails the step.
        // Power Up = verify link → I-limit → V → ON → settle → verified read-back;
        // fails safe (output back OFF) if the read-back doesn't match.
        new("psu.powerUp", "PSU Power Up (verified)", "Power", DeviceKind.PowerSupply,
            new[]
            {
                new ParamSpec("channel", "Channel", ParamKind.Number, "1"),
                new ParamSpec("volts", "Voltage (V)", ParamKind.Number, "0"),
                new ParamSpec("amps", "Current limit (A)", ParamKind.Number, "0.1"),
                new ParamSpec("settleMs", "Verify after (ms)", ParamKind.Number, "150"),
                new ParamSpec("toleranceV", "Voltage tolerance (± V)", ParamKind.Number, "0.1"),
                new ParamSpec("offOnFail", "Switch output OFF if verify fails", ParamKind.Bool, "true"),
                new ParamSpec("acceptCurrentLimit", "Accept current-limit (CC) mode as ON", ParamKind.Bool, "false"),
                new ParamSpec("captureAs", "Store readings as (→ _v / _i, blank = none)", ParamKind.Text),
            }),
        new("psu.setVoltage", "Set PSU Voltage", "Power", DeviceKind.PowerSupply,
            new[]
            {
                new ParamSpec("channel", "Channel", ParamKind.Number, "1"),
                new ParamSpec("volts", "Voltage (V)", ParamKind.Number, "0"),
            }),
        new("psu.setCurrentLimit", "Set PSU Current Limit", "Power", DeviceKind.PowerSupply,
            new[]
            {
                new ParamSpec("channel", "Channel", ParamKind.Number, "1"),
                new ParamSpec("amps", "Current limit (A)", ParamKind.Number, "0.1"),
            }),
        new("psu.outputOn", "PSU Output ON", "Power", DeviceKind.PowerSupply,
            new[] { new ParamSpec("channel", "Channel", ParamKind.Number, "1") }),
        new("psu.outputOff", "PSU Output OFF", "Power", DeviceKind.PowerSupply,
            new[] { new ParamSpec("channel", "Channel", ParamKind.Number, "1") }),
        // Forces a fresh poll, then stores the measured value in 'captureAs' (blank = log only).
        new("psu.readVoltage", "Read PSU Voltage", "Power", DeviceKind.PowerSupply,
            new[]
            {
                new ParamSpec("channel", "Channel", ParamKind.Number, "1"),
                new ParamSpec("captureAs", "Store in variable (blank = log only)", ParamKind.Text),
            }),
        new("psu.readCurrent", "Read PSU Current", "Power", DeviceKind.PowerSupply,
            new[]
            {
                new ParamSpec("channel", "Channel", ParamKind.Number, "1"),
                new ParamSpec("captureAs", "Store in variable (blank = log only)", ParamKind.Text),
            }),
        new("psu.connect", "Connect PSU", "Power", DeviceKind.PowerSupply, System.Array.Empty<ParamSpec>()),
        new("psu.disconnect", "Disconnect PSU", "Power", DeviceKind.PowerSupply, System.Array.Empty<ParamSpec>()),

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
                new ParamSpec("tool", "Tool device", ParamKind.MotionDeviceName, StationApp.ToolDevice),
                new ParamSpec("maxDistance", "Max move (mm, 0 = registration bound)", ParamKind.Number, "10"),
                new ParamSpec("includeZ", "Include Z (to focus height)", ParamKind.Bool, "true"),
            }),
        new("tool.toolToCamera", "Tool to Camera", "Tool", DeviceKind.None,
            new[]
            {
                new ParamSpec("tool", "Tool device", ParamKind.MotionDeviceName, StationApp.ToolDevice),
                new ParamSpec("maxDistance", "Max move (mm, 0 = registration bound)", ParamKind.Number, "10"),
                new ParamSpec("includeZ", "Include Z (to focus plane)", ParamKind.Bool, "true"),
                new ParamSpec("zOffset", "Z offset (mm, blank = registration's; − = higher)", ParamKind.Text),
            }),
        // Sends the tool device back to the pose captured in its registration (all axes).
        new("tool.toPose", "Tool to Pose", "Tool", DeviceKind.None,
            new[] { new ParamSpec("tool", "Tool device", ParamKind.MotionDeviceName, StationApp.ToolDevice) }),
        // Moves the tool so the named gripped object (taught on the Gripped FAU page, per product)
        // sits under the camera centre; Z brings its focus plane to the camera Z + zOffset.
        // ---- Part alignment (fiducials E1/E2 + local objects, taught on Vision Alignment) ----
        new("part.runAlignment", "Run Part Alignment", "Part", DeviceKind.None,
            new[] { new ParamSpec("part", "Part alignment", ParamKind.Text, "Part") }),
        new("part.cameraToFiducial", "Camera to Fiducial", "Part", DeviceKind.None,
            new[]
            {
                new ParamSpec("part", "Part alignment", ParamKind.Text, "Part"),
                new ParamSpec("fiducial", "Fiducial (1 = E1, 2 = E2)", ParamKind.Number, "1"),
            }),
        new("part.cameraToObject", "Camera to Part Object", "Part", DeviceKind.None,
            new[]
            {
                new ParamSpec("part", "Part alignment", ParamKind.Text, "Part"),
                new ParamSpec("object", "Local object", ParamKind.Text),
                new ParamSpec("applyOffset", "Apply the object's XY offset", ParamKind.Bool, "true"),
            }),

        new("object.toCamera", "Object to Camera", "Tool", DeviceKind.None,
            new[]
            {
                new ParamSpec("object", "Gripped object", ParamKind.Text, "FAU"),
                new ParamSpec("maxDistance", "Max move (mm, 0 = registration bound)", ParamKind.Number, "10"),
                new ParamSpec("includeZ", "Include Z (to focus plane)", ParamKind.Bool, "true"),
                new ParamSpec("zOffset", "Z offset (mm, blank = object's; − = higher)", ParamKind.Text),
            }),

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

        // ---- App (Run-page utility springboard) ----
        // Drive the Run-page utility apps from a sequence. 'appId' is one of:
        // align, ucorrection, faupre (hillclimb / autow are DEPRECATED — removed from the Run page). Open/Home are navigation only.
        // Start ACTIVATES an app (foreground + begin its routine / connect) and returns
        // once started — it does NOT await completion (use hillclimb.start for the
        // headless, await-to-completion climb). Requires a head with the springboard.
        new("app.open", "Open App", "App", DeviceKind.None,
            new[] { new ParamSpec("appId", "App (align / ucorrection / faupre)", ParamKind.Text, "align") }),
        new("app.home", "Home (Launcher)", "App", DeviceKind.None, System.Array.Empty<ParamSpec>()),
        new("app.start", "Open & Run App", "App", DeviceKind.None,
            new[] { new ParamSpec("appId", "App (align / ucorrection / faupre)", ParamKind.Text, "align") }),
        new("app.stop", "Stop App", "App", DeviceKind.None,
            new[] { new ParamSpec("appId", "App (align / ucorrection / faupre)", ParamKind.Text, "align") }),

        // ---- Script ----
        // Runs a saved .moon script from the active product to completion, inline in the
        // sequence. The script drives the same hardware through the 'machine' table and can
        // push a captured still into the Run page's snapshot box (machine.ui.showImage).
        // 'args' (optional Lua table text) reaches the script as its global 'args' table.
        new("script.run", "Run Script", "Flow", DeviceKind.None,
            new[]
            {
                new ParamSpec("script", "Script (.moon)", ParamKind.ScriptName),
                new ParamSpec("args", "Arguments (Lua table, optional)", ParamKind.Text),
            }),

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
                new ParamSpec("newUnit", "New unit — don't reuse the last unit's serial", ParamKind.Bool, "false"),
            }),
        // Prompts the operator (modal) to type free text (lot number, remark, rework
        // reason…) and records it in the open DUT record under 'key'. Cancel fails the
        // program unless 'required' is off. Handled by WorkflowRunner (dut.promptText).
        new("dut.promptText", "DUT Text Entry", "DUT", DeviceKind.None,
            new[]
            {
                new ParamSpec("key", "Record as (DUT key)", ParamKind.Text, "note"),
                new ParamSpec("title", "Dialog title", ParamKind.Text, "DUT Entry"),
                new ParamSpec("message", "Prompt", ParamKind.Text, "Enter text for this unit:"),
                new ParamSpec("default", "Pre-filled text (optional)", ParamKind.Text),
                new ParamSpec("var", "Store text in variable (optional)", ParamKind.Text),
                new ParamSpec("required", "Cancel stops the run", ParamKind.Bool, "true"),
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

    /// <summary>
    /// Palette category order — today's catalog order, so a head's blocks land in their usual
    /// group instead of at the end. A category not listed here sorts after these, in catalog order.
    /// </summary>
    private static readonly string[] CategoryOrder =
    {
        "Motion",
        "IO",
        "Pneumatic",
        "Lighting",
        "Power",
        "Flow",
        "Signal",
        "Vision",
        "Dispense",
        "Tool",
        "Part",
        "Scan",
        "Align",
        "App",
        "DUT",
    };

    private static OpSpec[] _all = Generic;
    private static Dictionary<string, OpSpec> _byKey = Index(_all);
    private static IReadOnlyList<string> _categories = OrderCategories(_all);

    /// <summary>Every block the editor offers: the generic ones, then the head's (<see cref="Register"/>).</summary>
    public static OpSpec[] All => _all;

    /// <summary>
    /// Adds a head's own blocks (FAAM: FAU / SiPhOG; DLAM: XCAN / climb …) after the generic ones.
    /// Called once at startup, before any editor is built; a key already in the catalog is replaced.
    /// </summary>
    public static void Register(IEnumerable<OpSpec> headOps)
    {
        var extra = headOps.ToList();
        var keys = new HashSet<string>(extra.Select(o => o.OpKey), StringComparer.OrdinalIgnoreCase);
        _all = _all.Where(o => !keys.Contains(o.OpKey)).Concat(extra).ToArray();
        _byKey = Index(_all);
        _categories = OrderCategories(_all);
    }

    private static Dictionary<string, OpSpec> Index(IEnumerable<OpSpec> ops)
        => ops.ToDictionary(s => s.OpKey, StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<string> OrderCategories(IEnumerable<OpSpec> ops)
        => ops.Select(o => o.Category).Distinct()
            .OrderBy(c => System.Array.IndexOf(CategoryOrder, c) is var i && i >= 0 ? i : int.MaxValue)
            .ToList();

    /// <summary>The spec for an op key (case-insensitive), or null if not in the catalog.</summary>
    public static OpSpec? Find(string opKey)
        => _byKey.TryGetValue(opKey, out var s) ? s : null;

    /// <summary>
    /// Fallback spec for an op loaded from a <c>.hwf</c> that this catalog doesn't
    /// know (e.g. authored in Horizon.UI). Keeps it editable as a no-param block so
    /// the program still round-trips instead of being dropped.
    /// </summary>
    public static OpSpec ForUnknown(string opKey)
        => new(opKey, opKey, "Other", DeviceKind.None, System.Array.Empty<ParamSpec>());

    /// <summary>Distinct categories in palette order — drives the palette grouping.</summary>
    public static IReadOnlyList<string> Categories => _categories;
}
