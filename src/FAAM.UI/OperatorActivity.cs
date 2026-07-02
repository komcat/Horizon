namespace FAAM.UI;

/// <summary>
/// The kind of operator action an <see cref="OperatorActivity"/> records. Kept
/// deliberately coarse — one value per "section" the DUT log wants to reason about
/// (manual jog, the correction sequence, and each alignment routine within it).
/// </summary>
public enum ActivityCategory
{
    /// <summary>A manual jog — button click or keyboard jog key.</summary>
    Jog,

    /// <summary>The "Start correction" chain as a whole (its overall start / end).</summary>
    Sequence,

    /// <summary>U-correction routine section.</summary>
    UCorrection,

    /// <summary>FAU pre-align routine section.</summary>
    FauPreAlign,

    /// <summary>Post-FAU grid search section.</summary>
    GridSearch,

    /// <summary>Hill-climb active-alignment section.</summary>
    HillClimb,

    /// <summary>Auto-W maximizer section.</summary>
    AutoW,

    /// <summary>Anything not covered above.</summary>
    Other,
}

/// <summary>
/// One timestamped operator action, published to the <see cref="OperatorActivityHub"/>
/// and mirrored into the active DUT log by <see cref="DutActivityMirror"/>.
///
/// This is the *generic* action record — it deliberately knows nothing about
/// application-specific signals (e.g. the FAAM SiPhOG Sagnac value). Such fields are
/// attached downstream at the FAAM-owned mirror boundary via its signal probe, so the
/// activity layer stays reusable and signal-agnostic.
/// </summary>
/// <param name="Timestamp">When the action happened (local time — matches the log surface).</param>
/// <param name="Category">Which section of operator work this belongs to.</param>
/// <param name="Action">A short verb/tag: a jog direction ("X+"), or a lifecycle marker ("started", "completed", "declined", "cancelled").</param>
/// <param name="Detail">A human-readable one-line summary for the log surface, or null.</param>
/// <param name="Data">Structured fields recorded verbatim into the DUT log (serialized as JSON), or null.</param>
public sealed record OperatorActivity(
    DateTimeOffset Timestamp,
    ActivityCategory Category,
    string Action,
    string? Detail,
    IReadOnlyDictionary<string, object?>? Data)
{
    /// <summary>A manual jog action — records the resolved device-local axis and step actually moved.</summary>
    public static OperatorActivity Jog(string device, string tag, string localAxis, int dir, double stepMm) =>
        new(DateTimeOffset.Now, ActivityCategory.Jog, tag,
            $"{device} {localAxis}{(dir > 0 ? "+" : "-")} {stepMm:0.###} mm",
            new Dictionary<string, object?>
            {
                ["device"] = device,
                ["axis"] = localAxis,
                ["dir"] = dir,
                ["stepMm"] = stepMm,
            });

    /// <summary>A section lifecycle marker (started / completed / declined / cancelled).</summary>
    public static OperatorActivity Section(ActivityCategory category, string action, string? detail = null,
        IReadOnlyDictionary<string, object?>? data = null) =>
        new(DateTimeOffset.Now, category, action, detail, data);
}
