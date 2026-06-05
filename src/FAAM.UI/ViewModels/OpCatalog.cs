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
public enum ParamKind { Text, Number, Bool, PositionName, AxisName, SetClear, OutputPin, CameraName, SignalName }

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
