namespace FAAM.UI.ViewModels;

/// <summary>
/// Help text for one command (op) shown in the Engineer page: a one-line
/// <see cref="Summary"/>, a longer <see cref="Usage"/> ("how to use"), and per-parameter
/// notes (<see cref="ParamNotes"/>, keyed by <c>ParamSpec.Key</c>).
/// </summary>
public sealed class OpHelp
{
    public required string Summary { get; init; }
    public string Usage { get; init; } = "";
    /// <summary>Per-parameter notes, keyed by the parameter's <c>ParamSpec.Key</c>.</summary>
    public (string Key, string Note)[] ParamNotes { get; init; } = System.Array.Empty<(string, string)>();
}

/// <summary>
/// The CONSOLIDATED, human-authored help for the Engineer command palette — one entry
/// per op key (the keys mirror <see cref="OpCatalog"/>). This is the single place to
/// write command help: add/extend an entry here and it shows automatically in the
/// right-hand panel (when a palette command is clicked) and as a one-line summary atop
/// the parameter editor (when a placed block is selected). Ops with no entry fall back
/// to their parameter labels — no help is not an error.
///
/// Authoring guide + structure: <c>docs/engineer-command-help.md</c>.
/// Coverage so far: <b>App</b> category. Other categories: TODO.
/// </summary>
public static class OpCatalogHelp
{
    /// <summary>Help for an op key, or null when none is authored yet.</summary>
    public static OpHelp? For(string opKey)
        => opKey != null && ByKey.TryGetValue(opKey, out var h) ? h : null;

    private static readonly Dictionary<string, OpHelp> ByKey = new(StringComparer.OrdinalIgnoreCase)
    {
        // ===== App — the Run-page utility springboard =====
        // The four apps are: ucorrection, faupre, hillclimb, siphog.
        // Two axes: Open/Home move the FOREGROUND (which screen shows); Open&Run/Stop
        // change the RUN-STATE (the routine), which keeps going in the background.

        ["app.open"] = new()
        {
            Summary = "Bring a utility app's screen to the front of the Run-page springboard. Navigation only — it does NOT start the app's routine.",
            Usage = "Use it to show the operator the right app at the right moment (e.g. open FAU Pre-align just before a manual step). Returns immediately. To also start the routine, use \"Open & Run App\". Only works on a head that has the Run-page springboard (FAAM.UI); elsewhere the step fails with a clear message.",
            ParamNotes = new[]
            {
                ("appId", "Which app to show: ucorrection, faupre, hillclimb, or siphog."),
            },
        },

        ["app.home"] = new()
        {
            Summary = "Return the springboard to the home launcher (the app grid).",
            Usage = "Apps keep running in the BACKGROUND — a running climb or live SiPhOG stream is not stopped; its tile keeps its badge (yellow = running, green = connected). Use it to step back to the launcher between phases.",
        },

        ["app.start"] = new()
        {
            Summary = "Activate a utility app: bring it to the front AND begin its routine / connection — like opening the app and pressing its Start button.",
            Usage = "Per app: siphog → connect (tile turns green); hillclimb → start a climb using the panel's current Distance/Target (tile turns yellow); ucorrection / faupre → begin the guided routine that the operator then drives (Capture/Apply).\n\nIMPORTANT: this returns ONCE STARTED — it does NOT wait for the routine to finish. For a climb that must block the sequence until done (with distance/target and captured results), use \"Start Hill Climb\" (Align) instead.",
            ParamNotes = new[]
            {
                ("appId", "Which app to activate: ucorrection, faupre, hillclimb, or siphog."),
            },
        },

        ["app.stop"] = new()
        {
            Summary = "Stop a utility app's routine — like pressing its Stop button (Disconnect for SiPhOG). Leaves the current screen as-is.",
            Usage = "siphog → disconnect; hillclimb → cancel the climb; ucorrection / faupre → abort the in-flight run. No-op if the app isn't running.",
            ParamNotes = new[]
            {
                ("appId", "Which app to stop: ucorrection, faupre, hillclimb, or siphog."),
            },
        },

        // ===== DUT — per-unit traceability records =====

        ["dut.readAllSignals"] = new()
        {
            Summary = "Snapshot EVERY available signal into the DUT record as one named data set — all SiPhOG readings plus every connected SMU / PSU / Measurement / TEC / Laser channel.",
            Usage = "Drop it wherever you want a full telemetry snapshot of the unit (e.g. right after alignment, or at end-of-test). It reads each signal live and stores them together as one grouped entry under the data-set name, so a unit can carry several snapshots (e.g. \"before\" and \"after\") across the run.\n\nNeeds a DUT serial for the run (a \"DUT Entry\" step or an up-front serial); with no serial it is skipped with a warning. Signals whose device is offline are skipped (logged), not fatal. The set is read from the same catalog as \"Read Out\" (Signal).",
            ParamNotes = new[]
            {
                ("dataset", "Name this snapshot is filed under in the DUT record (e.g. \"post-align\"). Defaults to \"signals\"."),
            },
        },

        // ===== TODO: other categories (Motion, IO, Pneumatic, Flow, Signal, Vision,
        // Align) — add entries here in the same shape. =====
    };
}
