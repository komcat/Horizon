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
/// Coverage so far: <b>App</b>, <b>DUT</b>, <b>Scan</b> categories plus <c>script.run</c>. Other categories: TODO.
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

        // ===== Scan — beam-alignment searches =====

        ["scan.grid"] = new()
        {
            Summary = "Raster an XY grid about the start position, measuring the feedback signal at every node, then return the device to the strongest node.",
            Usage = "Use it to MAP a field rather than climb it — it visits every lattice point, so it can't be trapped by a local maximum, at the cost of more moves than a hill-climb. Centred on wherever the device sits when the block runs; it sweeps the start ± the half-extent on each axis in a serpentine (back-and-forth) pattern.\n\nThe grid is square: (2·floor(extent/step)+1) nodes per axis, capped at 4096 nodes total — if you exceed it, increase the pitch or shrink the extent. On finish (or halt) the device ends at the best node found. A scan that ran is a success even if it only found noise; branch on '{captureAs}_significant' (\"1\"/\"0\"). Only a hardware/setup error fails the step.",
            ParamNotes = new[]
            {
                ("axes", "The two axes that form the grid, e.g. \"X,Y\". Only the first two are used."),
                ("stepUm", "Pitch between adjacent grid nodes, in micrometres. This is the configurable step size."),
                ("extentMm", "Half-extent per axis in mm: the grid spans the start position ± this on each axis."),
                ("settleMs", "Dwell after each move before measuring."),
                ("velocity", "Move velocity during the scan (mm/s); 0 leaves the controller's current velocity."),
                ("averaging", "Feedback samples averaged into each node's reading."),
                ("signal", "The feedback signal to maximise — pick from the catalog (any connected SMU / PSU / Measurement / TEC / Laser channel, or SiPhOG on FAAM.UI, e.g. siphog.sagPwrV). Same list as \"Read Out\"; the chosen signal must be available on this head."),
                ("captureAs", "Prefix for the result variables ({prefix}_peak/_baseline/_snr/_significant/_state)."),
            },
        },

        ["scan.hexapod"] = new()
        {
            Summary = "Coarse-to-fine hill-climb: per axis, step toward the rising feedback signal with progressively finer steps, then settle on the peak.",
            Usage = "Use it to FIND a peak quickly when you expect a single well-behaved maximum near the start — it follows the gradient instead of mapping the whole field, so it is much faster than Grid Scan but can stall on a local maximum. 'mode' picks a built-in step schedule; leave 'axes'/'stepSizesUm' blank to use the mode's defaults, or override them.\n\nSame feedback and capture contract as Grid Scan: ends at the best position, a no-signal result is still a step success (branch on '{captureAs}_significant'), only a hardware/setup error fails it.",
            ParamNotes = new[]
            {
                ("mode", "Preset step schedule: coarse, fine, or ultrafine."),
                ("axes", "Axis order to climb, e.g. \"Z,Y,X\". Blank uses the mode default."),
                ("stepSizesUm", "Comma-separated step sizes in µm, coarse→fine. Blank uses the mode default."),
                ("settleMs", "Dwell after each move before measuring."),
                ("velocity", "Move velocity during the scan (mm/s); 0 leaves the controller's current velocity."),
                ("signal", "The feedback signal to maximise — pick from the catalog (any connected SMU / PSU / Measurement / TEC / Laser channel, or SiPhOG on FAAM.UI, e.g. siphog.sagPwrV). Same list as \"Read Out\"; the chosen signal must be available on this head."),
                ("captureAs", "Prefix for the result variables ({prefix}_peak/_baseline/_snr/_significant/_state)."),
            },
        },

        // ===== Flow — script =====

        // ===== Tool — device registration (gripper <-> camera) =====
        // Both blocks read the machine-global registration saved on the Machine page's
        // Registration section (Engineer level). See docs/device-registration-design.md.

        ["tool.cameraToTool"] = new()
        {
            Summary = "Move the camera device (gantry-main) so the camera is centred on a registered tool — the hex-right gripper. XY, plus Z to the focus height when 'includeZ' is on.",
            Usage = "Needs a saved registration for the tool device (Machine › Registration). The step reads both devices live, works out where the gripper is in the gantry's frame from the hexapod's current pose, and issues ONE coordinated XY move. It is bounded: if the required travel is longer than 'maxDistance' (or the registration's own bound), the step FAILS rather than moving part-way — use it after a Move to Position that already brings the camera near the gripper.",
            ParamNotes = new[]
            {
                ("tool", "The registered tool device (hex-right = the gripper)."),
                ("maxDistance", "Hard bound in mm on this move; longer is refused. 0 = the registration's bound (10 mm by default)."),
                ("includeZ", "Also move the camera Z to the captured focus height (the bound then covers XYZ). Needs a focus Z captured on the Registration section; fails otherwise."),
            },
        },

        ["tool.toolToCamera"] = new()
        {
            Summary = "Move the tool device (hex-right) so the gripper sits where the camera is centred. The hexapod axes mapped to camera X/Y move (and the Z-mapped axis when 'includeZ' is on).",
            Usage = "The mirror of Camera to Tool: the camera stays put and the gripper comes to it. Same registration, same bound rule — a required travel beyond 'maxDistance' fails the step. Without 'includeZ' the hexapod's other axes are untouched, so height / rotation are the program's job before and after.",
            ParamNotes = new[]
            {
                ("tool", "The registered tool device (hex-right = the gripper)."),
                ("maxDistance", "Hard bound in mm on this move; longer is refused. 0 = the registration's bound (10 mm by default)."),
                ("includeZ", "Also move the Z-mapped hexapod axis so the gripper reaches the camera's current focus plane (plus zOffset). Needs a captured focus Z; fails otherwise."),
                ("zOffset", "Z offset (mm, camera-device frame) added to the focus plane when Z is included. Negative = higher on the gantry's Z convention (−1 keeps the gripper 1 mm above focus). Blank = the offset stored on the registration."),
            },
        },

        ["tool.toPose"] = new()
        {
            Summary = "Send the tool device (hex-right) back to the pose captured in its registration — all six axes in one coordinated move.",
            Usage = "A known-good parking spot for the gripper: the pose it stood at when it was registered. Not bounded (the registration pose is a taught, reachable location) — make sure the path is clear, as with any Move to Position.",
            ParamNotes = new[] { ("tool", "The registered tool device (hex-right = the gripper).") },
        },

        // ===== Part — part alignment (fiducials + local objects) =====
        ["part.runAlignment"] = new()
        {
            Summary = "Locate the loaded part: camera to E1 and E2, detect both, rebuild the part frame, then re-measure every local object.",
            Usage = "Run it after the operator loads a part so the program knows exactly where it sits on the fixture. Every point must have a vision profile — manual (click) points cannot be served from a program and fail the step. An object that is not detected keeps its taught position through the new frame (flagged on the overlay) and the step fails with a message, though the frame itself is measured and kept.",
            ParamNotes = new[] { ("part", "The part alignment profile name in the active product (default Part).") },
        },
        ["part.cameraToFiducial"] = new()
        {
            Summary = "Move the camera over fiducial E1 or E2 of a part — its measured position if a run happened, the taught one otherwise.",
            Usage = "Goes to the taught camera Z for that fiducial. Not distance-bounded (it is a taught position, like Move to Position).",
            ParamNotes = new[]
            {
                ("part", "The part alignment profile name."),
                ("fiducial", "1 = E1 (origin), 2 = E2 (X axis)."),
            },
        },
        ["part.cameraToObject"] = new()
        {
            Summary = "Move the camera over a local object of a part (a point, or a line's midpoint), optionally plus the object's XY offset in the part frame.",
            Usage = "Uses the measured object position from the last run (taught through the measured frame if that object was not detected). Z = the mean camera Z the object's points were taught at.",
            ParamNotes = new[]
            {
                ("part", "The part alignment profile name."),
                ("object", "The local object's name on that part."),
                ("applyOffset", "On: go to reference + the object's local-frame offset (the usual work point). Off: the reference itself."),
            },
        },

        ["object.toCamera"] = new()
        {
            Summary = "Move the gripper (hex-right) so the gripped object's centre — the FAU taught on the Gripped FAU page — sits where the camera is centred.",
            Usage = "Same rules as Tool to Camera, but for the object the gripper holds: the object's live position comes from its teach plus the hexapod's move since. With 'includeZ' the object's focus plane is brought to the camera's current Z + 'zOffset'. Bounded — a required travel beyond 'maxDistance' fails the step.",
            ParamNotes = new[]
            {
                ("object", "The gripped object's name in the active product (default FAU)."),
                ("maxDistance", "Hard bound in mm; longer is refused. 0 = the tool registration's bound."),
                ("includeZ", "Also move the Z-mapped hexapod axis so the object's focus plane reaches the camera Z + zOffset."),
                ("zOffset", "Z offset (mm, camera frame). Negative = higher (hex-right X+), positive = lower (hex-right X−). Blank = the offset stored on the object."),
            },
        },

        ["script.run"] = new()
        {
            Summary = "Run a saved .moon script from this product, to completion, as one step in the sequence.",
            Usage = "Use it where a few lines of Lua say what a chain of blocks would: a measurement loop, a computed move, or an operator-facing touch such as capturing a picture into the Run page's snapshot box (machine.camera.snap + machine.ui.showImage). The script drives the same hardware through the 'machine' table and sees the active product's teach points, vision profiles and signals. Its output lines go to the log; a script error (or an operator Stop) fails the step, and so the program. Author and test scripts on the Script page first.",
            ParamNotes = new[]
            {
                ("script", "Which .moon script to run — the drop-down lists this product's saved scripts."),
            },
        },

        // ===== TODO: other categories (Motion, IO, Pneumatic, Flow, Signal, Vision,
        // Align) — add entries here in the same shape. =====
    };
}
