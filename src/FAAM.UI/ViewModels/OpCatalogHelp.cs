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
/// Coverage so far: <b>App</b>, <b>DUT</b>, <b>Lighting</b>, <b>Scan</b> categories plus <c>script.run</c>. Other categories: TODO.
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

        // ===== Motion =====

        ["motion.moveToPosition"] = new()
        {
            Summary = "Move the device to a taught position, optionally slowing down for the last few mm.",
            Usage = "Velocity, camera and exposure fall back to the position's stored values when left 0 / blank.\n\nApproach: set BOTH 'Approach distance' and 'Approach velocity' (> 0) to slow down near the target — e.g. 2 mm at 1 mm/s. The device travels at its normal velocity to a point that far short of the target (on the straight line, measured on X/Y/Z), checks it really stopped there, finishes at the approach velocity, then its velocity is put back to what it was (e.g. 20 → 1 → 20 mm/s) — also after STOP, a fault or a run Stop. A move that starts inside the approach distance runs slow all the way. The step is refused before any motion if the velocity can't be read (it could not be restored) or the slow part would take over 55 s.",
            ParamNotes = new[]
            {
                ("position", "The taught position to move to."),
                ("velocity", "Travel velocity in mm/s; 0 = the position's stored velocity (else the device's current one)."),
                ("approachDistance", "How many mm before the target to slow down. 0 = no approach."),
                ("approachVelocity", "Velocity (mm/s) for the approach. 0 = no approach. Clamped to the controller range (ACS 0.1–50, PI 0.01–10)."),
            },
        },

        ["motion.slowApproach"] = new()
        {
            Summary = "DEPRECATED — do not use in new programs; use Move to Position's approach distance / velocity. Reach a hand-adjusted final position (e.g. the dispense point): teach it by jogging on the first run, then offer a slow automatic move to it on later runs.",
            Usage = "Put it in the device's lane right after the move to the start point (e.g. PreDispense, head down).\n\nFirst run (position not saved yet): the manual-jog popup opens; when the operator presses Continue, the current coordinates are saved under the position name.\n\nLater runs: the operator is asked \"move automatically to the last saved position?\". Yes → the device's current velocity is snapshotted, set to the approach speed, and the stage moves there slowly ONE AXIS AT A TIME — Z first, then Y, then X — while a window with one big STOP button is shown (STOP halts the stage and skips the remaining axes); the velocity is put back afterwards (also after STOP, a fault or a run Stop). Then the manual-jog popup opens for fine adjust. No → manual jog only.\n\nA STOPPED, refused (too far / too slow for the 55 s move limit) or failed approach falls back to the manual jog — jog clear, or Cancel to stop the run. Jog Cancel fails the run. The saved position is only rewritten when 'Re-save position' is on. To re-teach, delete the position on the Machine page.",
            ParamNotes = new[]
            {
                ("position", "Name of the final position. Type a new name — it is created on the first run."),
                ("speed", "Approach speed in mm/s (default 0.2). Clamped to the controller range (ACS ≥ 0.1)."),
                ("axes", "Which of X/Y/Z move to the saved values; the others hold where they are. Default XYZ. They always move in the order Z → Y → X, whatever order you type."),
                ("maxDistance", "Refuse the automatic move if the total travel (Z + Y + X) is more than this (mm) — then adjust manually."),
                ("jogAfterApproach", "On: the jog popup still opens after arriving, for fine adjust. Off: a clean arrival continues straight on."),
                ("updateAfterJog", "On: every manual adjust overwrites the saved position. Off (default): it is only saved the first time."),
                ("message", "Extra line shown in the jog popup."),
                ("jogAxes", "Axes shown on the jog pad (default XYZ)."),
                ("step", "Jog pad default step in mm."),
            },
        },

        // ===== IO =====

        ["io.alertBuzzer"] = new()
        {
            Summary = "Show a yellow alert (e.g. \"UV complete\") and sound a buzzer output in a gentle pattern until the operator presses Acknowledge.",
            Usage = "The sequence waits here until the operator acknowledges; the buzzer stops at once and its output is left OFF. STOP also silences it. The default pattern is a soft double chirp every ~3 s — tune it with the beep fields. A buzzer IO fault is logged as a warning and does not fail the run (the alert still shows).",
            ParamNotes = new[]
            {
                ("title", "Large text on the alert, e.g. UV complete."),
                ("message", "Optional second line, e.g. Remove the part from the fixture."),
                ("pin", "Output pin that drives the buzzer on the selected IO device. Blank = alert only, no buzzer."),
                ("beepCount", "Chirps in each group, 1–5 (default 2)."),
                ("beepMs", "How long each chirp is ON, 20–2000 ms (default 120). Short = softer."),
                ("beepGapMs", "Silence between chirps in a group, 20–2000 ms (default 180)."),
                ("restMs", "Quiet time before the next group, 300–30000 ms (default 2500). Longer = calmer."),
            },
        },

        // ===== Lighting — CCS PD3 LED controller =====
        // Lights come from lighting_devices.json: a channel group — main_camera_light =
        // L1 collimate, L2 red, L3 green, L4 blue; side_camera_light = L5 collimate,
        // L6 red, L7 green, L8 blue — or an ungrouped channel by its name.

        ["lighting.setLightPercent"] = new()
        {
            Summary = "Set EVERY channel of a light to a brightness in percent. 0 % switches it off.",
            Usage = "All channels of the light are driven to the same level — on main_camera_light that is collimate + R + G + B together. To pick one illumination, use \"Set Light Colour\" instead. The controller must be connected (it is at startup) and its front-panel MANU/EXT selector must be on EXT. The step fails if the light name is unknown or the percent is not 0–100.",
            ParamNotes = new[]
            {
                ("light", "The light to drive: a group (e.g. main_camera_light) or a single channel's name."),
                ("percent", "Brightness 0–100 %. 0 turns the output fully off (not just dimmed)."),
            },
        },

        ["lighting.setLightColor"] = new()
        {
            Summary = "Colour call-out for a light — red, green, blue, white (R+G+B mix), collimate or white_collimate — at a brightness in percent.",
            Usage = "Lights ONLY the channels of the call-out and switches the light's other channels off: red / green / blue = that LED; white = red + green + blue mixed at the same level; collimate = the collimated light; white_collimate = collimate + red + green + blue together. Asking for a call-out the light has no channel for fails the step.\n\nTip: Move to Position has the same lighting fields, applied while the stage travels.",
            ParamNotes = new[]
            {
                ("light", "The light to drive — main_camera_light or side_camera_light."),
                ("color", "red, green, blue, white (R+G+B mix), collimate, or white_collimate (collimate + R+G+B)."),
                ("percent", "Brightness 0–100 % for the call-out. 0 = off."),
            },
        },

        ["lighting.connect"] = new()
        {
            Summary = "Open the connection to the lighting controller.",
            Usage = "Normally not needed — the controller connects at startup. Use it after a Disconnect Lighting step or if the controller was power-cycled. The PD3 accepts only ONE client, so close any test tool (Hercules, scripts) first.",
        },

        ["lighting.disconnect"] = new()
        {
            Summary = "Close the connection to the lighting controller (the lights keep their last setting).",
            Usage = "Frees the controller's single TCP slot, e.g. so an external tool can connect. Switch lights off first if they should not stay on.",
        },

        // ===== Power supply =====

        ["psu.powerUp"] = new()
        {
            Summary = "One-block power-up: check the connection, set current limit + voltage, switch the output ON, then verify the reading.",
            Usage = "Runs in this order: 1) verifies the supply is connected (reconnects once if the link dropped); 2) sets the current limit, then the voltage — limit first so the DUT is protected the instant the output turns on; 3) switches the output ON; 4) waits the settle time (150 ms default) and reads the channel back. The step passes only if the output reads ON and the measured voltage is within the tolerance of the setpoint.\n\nIf the check fails (or the run is cancelled) the output is switched back OFF and the step fails. A low voltage with the current at its limit is reported as constant-current mode — usually an overload or short on the DUT. UDP3305S-E ranges: CH1 / CH2 0–32 V / 0–5 A, CH3 0–6 V / 0–3 A.",
            ParamNotes = new[]
            {
                ("channel", "Output channel, 1-based (UDP3305S-E: 1, 2 or 3)."),
                ("volts", "Voltage setpoint in volts."),
                ("amps", "Current limit in amps."),
                ("settleMs", "Wait between output ON and the verifying read-back (0–10000 ms)."),
                ("toleranceV", "Allowed |measured − setpoint| voltage difference."),
                ("offOnFail", "Switch the output back OFF when verification fails (recommended)."),
                ("captureAs", "Prefix for the measured readings: {prefix}_v (volts) and {prefix}_i (amps). Blank = don't store."),
            },
        },

        ["psu.setVoltage"] = new()
        {
            Summary = "Program a power-supply channel's output voltage.",
            Usage = "Sets the voltage setpoint only — the output is not switched on (use \"PSU Output ON\"). Set the current limit first so the load is protected the moment the output turns on. UDP3305S-E ranges: CH1 / CH2 0–32 V, CH3 0–6 V; a value outside the channel's range is refused and fails the step without touching the supply.",
            ParamNotes = new[]
            {
                ("channel", "Output channel, 1-based (UDP3305S-E: 1, 2 or 3)."),
                ("volts", "Voltage setpoint in volts."),
            },
        },

        ["psu.setCurrentLimit"] = new()
        {
            Summary = "Program a power-supply channel's current limit (constant-current ceiling).",
            Usage = "The supply will not source more than this; if the load tries to draw more, the channel drops into constant-current mode and the voltage sags. UDP3305S-E ranges: CH1 / CH2 0–5 A, CH3 0–3 A; out-of-range values fail the step.",
            ParamNotes = new[]
            {
                ("channel", "Output channel, 1-based."),
                ("amps", "Current limit in amps."),
            },
        },

        ["psu.outputOn"] = new()
        {
            Summary = "Switch a power-supply channel's output ON at its programmed voltage / current limit.",
            Usage = "Set the voltage and current limit before this step. Pair it with a \"PSU Output OFF\" later in the program (and in any failure branch) so the DUT is not left powered.",
            ParamNotes = new[] { ("channel", "Output channel, 1-based.") },
        },

        ["psu.outputOff"] = new()
        {
            Summary = "Switch a power-supply channel's output OFF.",
            Usage = "The setpoints are kept, so a later \"PSU Output ON\" returns to the same voltage / limit.",
            ParamNotes = new[] { ("channel", "Output channel, 1-based.") },
        },

        ["psu.readVoltage"] = new()
        {
            Summary = "Measure a channel's actual output voltage and optionally store it in a variable.",
            Usage = "Forces a fresh read of the supply, then logs the value. With a variable name it is stored for later If / Calculate blocks. For a snapshot of every signal into the DUT record use \"Read All Signals\" instead.",
            ParamNotes = new[]
            {
                ("channel", "Output channel, 1-based."),
                ("captureAs", "Variable to receive the reading in volts; blank = log only."),
            },
        },

        ["psu.readCurrent"] = new()
        {
            Summary = "Measure a channel's actual output current and optionally store it in a variable.",
            Usage = "Forces a fresh read of the supply, then logs the value. With a variable name it is stored for later If / Calculate blocks.",
            ParamNotes = new[]
            {
                ("channel", "Output channel, 1-based."),
                ("captureAs", "Variable to receive the reading in amps; blank = log only."),
            },
        },

        ["psu.connect"] = new()
        {
            Summary = "Open the connection to the power supply.",
            Usage = "Normally not needed — the supply connects at startup. Use it after a \"Disconnect PSU\" step or if the supply was power-cycled.",
        },

        ["psu.disconnect"] = new()
        {
            Summary = "Switch every output OFF and close the connection to the power supply.",
            Usage = "All channels are turned off before the link is closed, so a disconnected supply is never left driving the DUT.",
        },

        ["siphog.saveSnapshot"] = new()
        {
            Summary = "Save a labelled snapshot of the six SiPhOG readings (SLED current, photo current, SLED temp, target SAG PWR, SAG PWR, TEC current) for this DUT.",
            Usage = "Drop it at each point you want on record (e.g. \"wet\" after epoxy, \"uv1.0\" after the first cure, \"final\"). Each value is averaged over the window and stored with its min / max / std, the time, the operator and this block's name. Every snapshot is appended to the product folder's siphog_data\\<serial>.json, one file per DUT, so the sets stack up in order and can be compared on the DUT Data page. Re-using a label adds a new set; the newest one with that label is the one shown.\n\nNeeds a DUT serial (a \"DUT Entry\" step or an up-front serial) and the Run-page SiPhOG client connected. If either is missing the block fails softly: with \"Ask operator if it fails\" on (default) a dialog asks Continue or Stop; off, it logs a warning and the run carries on.",
            ParamNotes = new[]
            {
                ("label", "Name of this set, e.g. wet, uv1.0, final. Use var:name to take it from a context variable."),
                ("windowMs", "How long to sample and average, in ms (SiPhOG streams about 200 Hz). 0 takes one frame. Maximum 10000."),
                ("note", "Free text stored with the set (optional)."),
                ("askOnFailure", "On: a failure pops up Continue / Stop. Off: the block is skipped with a warning in the log and the run carries on."),
            },
        },

        // ===== DUT — per-unit traceability records =====

        ["dut.promptText"] = new()
        {
            Summary = "Ask the operator to type text (lot number, remark, rework reason…) and save it in the DUT record.",
            Usage = "A dialog pops up with the prompt; the operator types (or scans) and presses OK or Enter. The text is stored in the unit's DUT record under the key, exactly as typed (always as text, so \"007\" keeps its zeros). Use one block per field you want on record.\n\nNeeds a DUT serial for the run (a \"DUT Entry\" step or an up-front serial). With no DUT open and no variable set, the block is skipped with a warning, since there is nowhere to put the answer.",
            ParamNotes = new[]
            {
                ("key", "Name the text is filed under in the DUT record, e.g. \"lot\", \"remark\". Defaults to \"note\"."),
                ("title", "Dialog window title."),
                ("message", "The question shown to the operator."),
                ("default", "Text already in the box when the dialog opens (optional)."),
                ("var", "Also store the text in this context variable for later blocks (optional)."),
                ("required", "On: Cancel stops the run. Off: Cancel skips the block and the run carries on."),
            },
        },

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
                ("args", "Optional arguments, a Lua table the script reads as 'args' — e.g. { preset = \"fine\" } or { z = false, sets = {{0,0.5},{0,0.25}} }. The outer braces may be left off. Blank = the script's own defaults. Stored on the block, so the program's version history records them."),
            },
        },

        // ===== TODO: other categories (Motion, IO, Pneumatic, Flow, Signal, Vision,
        // Align) — add entries here in the same shape. =====
    };
}
