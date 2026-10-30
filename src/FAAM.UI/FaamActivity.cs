namespace FAAM.UI;

/// <summary>
/// FAAM's alignment-routine sections for the operator-activity log (<see cref="ActivityCategory"/>).
/// The names are the DUT-record key prefixes FAAM has always written (e.g. <c>"UCorrection:started"</c>).
/// </summary>
public static class FaamActivity
{
    /// <summary>U-correction routine section.</summary>
    public static readonly ActivityCategory UCorrection = new("UCorrection");

    /// <summary>FAU pre-align routine section.</summary>
    public static readonly ActivityCategory FauPreAlign = new("FauPreAlign");

    /// <summary>Post-FAU grid search section.</summary>
    public static readonly ActivityCategory GridSearch = new("GridSearch");

    /// <summary>Hill-climb active-alignment section.</summary>
    public static readonly ActivityCategory HillClimb = new("HillClimb");

    /// <summary>Auto-W maximizer section (deprecated panel).</summary>
    public static readonly ActivityCategory AutoW = new("AutoW");
}
