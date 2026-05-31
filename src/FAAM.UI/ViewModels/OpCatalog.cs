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
public enum ParamKind { Text, Number, Bool, PositionName, AxisName, SetClear }

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
/// NOTE on Move-to-Position: the only persisted parameter is <c>position</c>.
/// Velocity, camera and exposure are NOT step parameters — they live on the named
/// position (<c>NamedPosition</c>) and are applied on recall. The editor surfaces
/// them as a read-only preview with an "Edit position…" button; do not add fake
/// velocity / camera params to this op.
/// </summary>
public static class OpCatalog
{
    public static readonly OpSpec[] All =
    {
        // ---- Motion ----
        new("motion.moveToPosition", "Move to Position", "Motion", DeviceKind.Motion,
            new[] { new ParamSpec("position", "Position", ParamKind.PositionName) }),
        new("motion.moveRelative", "Move Relative", "Motion", DeviceKind.Motion,
            new[]
            {
                new ParamSpec("axis", "Axis", ParamKind.AxisName, "X"),
                new ParamSpec("distance", "Distance (mm)", ParamKind.Number, "0"),
            }),
        new("motion.setVelocity", "Set Velocity", "Motion", DeviceKind.Motion,
            new[] { new ParamSpec("speed", "Speed (mm/s)", ParamKind.Number, "10") }),

        // ---- IO ----
        new("io.setOutputByName", "Set Output", "IO", DeviceKind.Io,
            new[]
            {
                new ParamSpec("pin", "Pin name", ParamKind.Text),
                new ParamSpec("state", "State", ParamKind.SetClear, "true"),
            }),

        // ---- Pneumatic ----
        new("pneumatic.extend", "Extend Slide", "Pneumatic", DeviceKind.Pneumatic, System.Array.Empty<ParamSpec>()),
        new("pneumatic.retract", "Retract Slide", "Pneumatic", DeviceKind.Pneumatic, System.Array.Empty<ParamSpec>()),

        // ---- Flow ----
        new("flow.wait", "Wait", "Flow", DeviceKind.None,
            new[] { new ParamSpec("ms", "Milliseconds", ParamKind.Number, "1000") }),
        new("flow.log", "Log Message", "Flow", DeviceKind.None,
            new[] { new ParamSpec("message", "Message", ParamKind.Text) }),
        new("flow.comment", "Comment", "Flow", DeviceKind.None,
            new[] { new ParamSpec("text", "Note", ParamKind.Text) }),
        new("flow.if", "If (condition)", "Flow", DeviceKind.None,
            new[] { new ParamSpec("condition", "Condition", ParamKind.Text) }),
        new("flow.else", "Else", "Flow", DeviceKind.None, System.Array.Empty<ParamSpec>()),
        new("flow.endif", "End If", "Flow", DeviceKind.None, System.Array.Empty<ParamSpec>()),
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
    public static IReadOnlyList<string> Categories =>
        All.Select(s => s.Category).Distinct().ToList();
}
