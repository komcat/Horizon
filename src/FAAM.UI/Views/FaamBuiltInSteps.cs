using System.Windows;
using Horizon.Common;
using Horizon.Engine;

namespace FAAM.UI.Views;

/// <summary>
/// FAAM's Run-page fallback steps (<see cref="IRunBuiltInSteps"/>) — the Phase-1 FAU-attach
/// sequence, shown while no deck drives the Run page. Each step first runs its authored
/// program (by the <see cref="RunStepsPanel"/>); this sequence runs only when that program is
/// missing. Device / pin mapping is hardcoded below — edit to match the rig.
/// </summary>
public sealed class FaamBuiltInSteps : IRunBuiltInSteps
{
    private IRunStepContext _ctx = null!;

    public string Title => "FAU ATTACH STEPS";

    private EngineHost Host => _ctx.Host;

    private void Log(LogLevel level, string message) => _ctx.Log(level, message);

    private void StopTimer() => _ctx.StopProcessTimer();

    // --- Device / pin mapping (FAAM rig; mirrors acs_solution_saa PROCESS_BUTTONS.md) ---
    private const string GantryDevice = "gantry-main";       // ACS gantry (X/Y/Z)
    private const string HexDevice = "hex-right";            // PI hexapod (X/Y/Z/U/V/W)
    private const string IoDevice = "IOBottom";              // EziIO module carrying the process outputs
    private const string GripperPin = "R_Gripper";           // set = close, clear = open
    private const string VacuumBasePin = "Vacuum_Base";      // set = base vacuum on, clear = off
    private const string DispenseShotPin = "Dispenser_Shot"; // pulsed during the epoxy shot
    private const string UvPlc1Pin = "UV_PLC1";              // pulsed to trigger UV PLC step 1
    private const string UvPlc2Pin = "UV_PLC2";              // pulsed to trigger UV PLC step 2
    private const string DispenserSlide = "Dispenser_Head";  // pneumatic: extend = head down, retract = up
    private const string UvSlide = "UV_Head";                // pneumatic: extend = head down, retract = up

    // Built-in actuation dwells, mirroring the legacy PneumaticController delays.
    private const int GripperMs = 1000;                      // gripper open/close settle
    private const int VacuumMs = 300;                        // base-vacuum settle
    private const int PulseGapMs = 50;                       // UV-PLC pulse clear→set→clear gap

    /// <summary>
    /// The FAU-attach step list. Each step's <c>Program</c> names the authored
    /// <c>.hwf</c> process in the active product's <c>processes\</c> folder that the
    /// button runs (clicking runs <c>processes\{Program}.hwf</c> via Layer 2; see
    /// <see cref="RunStepsPanel"/> — the built-in sequence is only a fallback when the
    /// program is missing). <c>Device</c> + <c>Position</c> name the step's primary
    /// teach target — the destination pose Teach mode captures. A null <c>Position</c>
    /// means the step has no teachable pose (the UV dwell steps).
    /// </summary>
    public IReadOnlyList<ProcessStep> CreateSteps() => new List<ProcessStep>
    {
        new(0, "(1) Gripper open and Move Home", GantryDevice, "Home", "GripperOpenHome"),
        new(1, "(2) Move to grip FAU and close Gripper", HexDevice, "FauHover", "MoveGripFAU"),
        new(2, "(3) FAU is gripped and move to SiPhog", GantryDevice, "PIC", "MoveFAUtoSiPhog"),
        new(3, "4.0 Move gantry to dispense", GantryDevice, "PreDispense", "MoveToDispense"),
        new(4, "4.1. Dispense and Move to UV", GantryDevice, "UV", "DispenseAndMoveToUV"),
        new(5, "4.2 — UV step1", GantryDevice, null, "UVStep1"),
        new(6, "4.3 — UV step2", GantryDevice, null, "UVStep2"),
        new(7, "(5) Complete UV and unload", HexDevice, "ClearLoad", "CompleteUVUnload"),
    };

    /// <summary>
    /// The built-in (Phase 1) FAU-attach sequence for a step — a faithful migration
    /// of the acs_solution_saa process handlers (see PROCESS_BUTTONS.md), kept as a
    /// fallback for when a step's authored <c>.hwf</c> program is missing. Drives the
    /// gantry + hexapod (named-position recalls and relative jogs), the gripper /
    /// base-vacuum / dispenser-shot / UV-PLC IO pins, and the dispenser / UV pneumatic
    /// heads, gated by the same Yes/No operator confirmations as the legacy. Legacy
    /// modal countdowns become non-blocking awaits (FAAM never blocks the UI thread);
    /// the DUT serial is owned by the panel's Start/Pass/Fail lifecycle, so the legacy
    /// per-step serial-input form is not reproduced.
    /// </summary>
    public async Task RunAsync(ProcessStep processStep, IRunStepContext context, CancellationToken ct)
    {
        _ctx = context;
        switch (processStep.Index)
        {
            case 0: await Step1OpenHomeAsync(ct); break;
            case 1: await Step2GripFauAsync(ct); break;
            case 2: await Step3MoveToSiphogAsync(ct); break;
            case 3: await Step40MoveToDispenseAsync(ct); break;
            case 4: await Step41DispenseAndUvAsync(ct); break;
            case 5: await Step42UvStep1Async(ct); break;
            case 6: await Step43UvStep2Async(ct); break;
            case 7: await Step5CompleteUnloadAsync(ct); break;
        }
    }

    // --- (1) Gripper open & home — no confirmation dialog ---
    private async Task Step1OpenHomeAsync(CancellationToken ct)
    {
        Log(LogLevel.Info, "Step 1 — gripper open & home (InProgress).");

        // Ensure both heads are up before any motion.
        await PneuAsync(Host.Engine.Pneumatic.RetractAsync(DispenserSlide, ct), $"{DispenserSlide} up");
        await PneuAsync(Host.Engine.Pneumatic.RetractAsync(UvSlide, ct), $"{UvSlide} up");

        // Conditional back-off: if the hexapod is sitting at FauGrip, retract X +1.5 mm first.
        if (await HexAtAsync("FauGrip"))
            await RelAsync(HexDevice, "X", 1.5, "FauGrip back-off", ct);

        await GripperAsync(close: false, ct);     // open
        await VacuumBaseAsync(on: true, ct);

        if (!await MoveNamedAsync(HexDevice, "ClearGrip", ct)) return;
        if (!await MoveNamedAsync(HexDevice, "Home", ct)) return;

        if (!await MoveNamedAsync(GantryDevice, "ClearToDown", ct)) return;
        if (!await MoveNamedAsync(GantryDevice, "Home", ct)) return;

        Log(LogLevel.Info, "Step 1 — Completed.");
    }

    // --- (2) Move to grip FAU & close gripper ---
    // Both gates are hard stops: declining either one aborts the step outright — it
    // must never fall through and grip / move anyway. Once the hexapod has been sent
    // to FauGrip there is no safe "carry on from here", so the operator reloads the
    // part and runs step (1).
    private async Task Step2GripFauAsync(CancellationToken ct)
    {
        Log(LogLevel.Info, "Step 2 — move to grip FAU (InProgress).");

        ConfirmOrAbort("Do you want to move to grip FAU?", "move to grip FAU declined");

        if (!await MoveNamedAsync(HexDevice, "ClearGrip", ct)) return;
        if (!await MoveNamedAsync(HexDevice, "FauGrip", ct)) return;

        ConfirmOrAbort("Do you want to Grip the FAU?", "grip the FAU declined");

        await GripperAsync(close: true, ct);
        await DwellAsync(5000, "grip settle", ct);
        await VacuumBaseAsync(on: false, ct);
        if (!await MoveNamedAsync(HexDevice, "FauHover", ct)) return;
        Host.Process.RecordDutValue("step1_grip", "FAU gripped");
        Log(LogLevel.Info, "Step 2 — Completed.");
    }

    // --- (3) FAU gripped → move to SiPhog ---
    private async Task Step3MoveToSiphogAsync(CancellationToken ct)
    {
        Log(LogLevel.Info, "Step 3 — move FAU to SiPhog (InProgress).");

        await IoAsync(VacuumBasePin, false, "Base vacuum off", ct);   // release base vacuum (direct)

        if (Confirm("Do you want to move FAU to Siphog?"))
        {
            // Hexapod sub-moves warn but do not abort on a missing point (legacy gotcha);
            // only the gantry PIC move aborts the step.
            await MoveNamedAsync(HexDevice, "ClearGrip", ct);
            await MoveNamedAsync(HexDevice, "ClearLoad", ct);
            await MoveNamedAsync(HexDevice, "FauLoad", ct);
            if (!await MoveNamedAsync(GantryDevice, "PIC", ct)) return;
            Host.Process.RecordDutValue("step3_siphog", "at SiPhog");
            Log(LogLevel.Info, "Step 3 — Completed.");
        }
        else
        {
            Info("Move FAU to Siphog cancelled.");
            Log(LogLevel.Info, "Step 3 — Idle.");
        }
    }

    // --- 4.0 Move gantry to dispense ---
    private async Task Step40MoveToDispenseAsync(CancellationToken ct)
    {
        Log(LogLevel.Info, "Step 4.0 — move gantry to dispense (InProgress).");

        if (!await MoveNamedAsync(GantryDevice, "ClearToDown", ct)) return;
        if (!await MoveNamedAsync(GantryDevice, "PreDispense", ct)) return;

        if (Confirm($"Do you want to set the output pin '{DispenserSlide}'? (lower dispenser head)"))
        {
            await PneuAsync(Host.Engine.Pneumatic.ExtendAsync(DispenserSlide, ct), $"{DispenserSlide} down");
            await DwellAsync(3000, "dispenser head settle", ct);
        }
        else
        {
            Info("Dispenser head lower cancelled.");
        }

        Host.Process.RecordDutValue("step40_dispense", "at dispense");
        Log(LogLevel.Info, "Step 4.0 — Completed.");
    }

    // --- 4.1 Dispense epoxy & move to UV ---
    private async Task Step41DispenseAndUvAsync(CancellationToken ct)
    {
        const double gantryTravel = 0.38;       // 0.3 + 0.08
        const double gripperMoveBack = 0.08;
        Log(LogLevel.Info, "Step 4.1 — dispense & move to UV (InProgress).");

        await RelAsync(GantryDevice, "X", gantryTravel, "gantry forward", ct);
        await RelAsync(HexDevice, "Z", -gripperMoveBack, "gripper back off", ct);

        if (Confirm("Do you want to dispense epoxy now?"))
        {
            await IoAsync(DispenseShotPin, true, "Dispense shot on", ct);
            await DwellAsync(1800, "dispense shot", ct);
            await IoAsync(DispenseShotPin, false, "Dispense shot off", ct);
            await DwellAsync(300, "shot settle", ct);
        }
        else
        {
            Info("Dispense cancelled.");
        }

        await DwellAsync(1500, "epoxy flow", ct);
        await RelAsync(GantryDevice, "X", -gantryTravel, "gantry return", ct);
        await PneuAsync(Host.Engine.Pneumatic.RetractAsync(DispenserSlide, ct), $"{DispenserSlide} up");
        await DwellAsync(1000, "head lift settle", ct);
        await RelAsync(HexDevice, "Z", gripperMoveBack, "gripper return", ct);
        await DwellAsync(1000, "gripper settle", ct);

        if (!await MoveNamedAsync(GantryDevice, "UV", ct)) return;
        await PneuAsync(Host.Engine.Pneumatic.ExtendAsync(UvSlide, ct), $"{UvSlide} down");
        await DwellAsync(1500, "UV head settle", ct);

        Host.Process.RecordDutValue("step41_uv", "epoxy dispensed, at UV");
        Log(LogLevel.Info, "Step 4.1 — Completed.");
    }

    // --- 4.2 UV step 1 (cure) ---
    private async Task Step42UvStep1Async(CancellationToken ct)
    {
        var seconds = Host.Settings.Current.UvStep1CuringSeconds;
        Log(LogLevel.Info, "Step 4.2 — UV step1 (InProgress).");

        if (Confirm($"Do you want to start toggling UV PLC1?  UV Step1 Time: {seconds:0.#} seconds"))
        {
            await UvPlcPulseAsync(UvPlc1Pin, ct);
            await DwellAsync((int)(seconds * 1000), "UV step1 cure", ct);
            Info("UV step1 cure complete.");
            Host.Process.RecordDutValue("step42_uv1", $"cured {seconds:0.#}s");
            Log(LogLevel.Info, "Step 4.2 — Completed.");
        }
        else
        {
            Log(LogLevel.Info, "Step 4.2 — Idle.");
        }
    }

    // --- 4.3 UV step 2 (final cure) ---
    private async Task Step43UvStep2Async(CancellationToken ct)
    {
        var seconds = Host.Settings.Current.UvCuringSeconds;
        Log(LogLevel.Info, "Step 4.3 — UV step2 (InProgress).");

        if (Confirm($"Do you want to start toggling UV PLC2?  UV Curing Time: {seconds:0.#} seconds"))
        {
            await UvPlcPulseAsync(UvPlc2Pin, ct);
            await DwellAsync((int)(seconds * 1000), "UV step2 cure", ct);
            Info("UV cure complete — then press (5) to complete and unload.");
            await PneuAsync(Host.Engine.Pneumatic.RetractAsync(UvSlide, ct), $"{UvSlide} up");
            Host.Process.RecordDutValue("step43_uv2", $"cured {seconds:0.#}s");
            Log(LogLevel.Info, "Step 4.3 — Completed.");
        }
        else
        {
            Log(LogLevel.Info, "Step 4.3 — Idle.");
        }
    }

    // --- (5) Complete UV & unload ---
    private async Task Step5CompleteUnloadAsync(CancellationToken ct)
    {
        Log(LogLevel.Info, "Step 5 — complete UV & unload (InProgress).");

        if (Confirm("Do you want to unload gripper after UV cure?\nUngrip then move to ClearLoad."))
        {
            await PneuAsync(Host.Engine.Pneumatic.RetractAsync(UvSlide, ct), $"{UvSlide} up");
            await GripperAsync(close: false, ct);     // open / release
            await DwellAsync(2000, "ungrip settle", ct);
            await MoveNamedAsync(HexDevice, "ClearLoad", ct);
            await MoveNamedAsync(HexDevice, "Home", ct);
            Host.Process.RecordDutValue("step5_unload", "unloaded");
            StopTimer();
            Log(LogLevel.Info, "Step 5 — Completed.");
        }
        else
        {
            Info("Unload cancelled.");
            Log(LogLevel.Info, "Step 5 — Idle.");
        }
    }

    // --- Step-sequence primitives (shared by the RunStepAsync per-step methods) ---

    /// <summary>Operator Yes/No gate — faithful to the legacy confirmation dialogs.</summary>
    private static bool Confirm(string question)
        => MessageBox.Show(question, "FAU Attach", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    /// <summary>
    /// Operator gate that <b>stops the step</b> when declined — for confirmations
    /// that guard motion onto the part, where continuing past a No is never safe.
    /// Throws <see cref="StepCancelledException"/>, which <see cref="RunStepAsync"/>
    /// turns into a red step.
    /// </summary>
    private static void ConfirmOrAbort(string question, string reason)
    {
        if (!Confirm(question))
            throw new StepCancelledException(reason);
    }

    /// <summary>Operator acknowledgement popup — faithful to the legacy success / cancel messages.</summary>
    private static void Info(string message)
        => MessageBox.Show(message, "FAU Attach", MessageBoxButton.OK, MessageBoxImage.Information);

    /// <summary>Recalls a named position via Layer 2; returns false (and logs) when the point is untaught / the move fails.</summary>
    private async Task<bool> MoveNamedAsync(string device, string position, CancellationToken ct)
    {
        var r = await Host.Process.MoveToNamedPositionAsync(device, position, ct);
        Log(r.IsSuccess ? LogLevel.Info : LogLevel.Warning,
            r.IsSuccess ? $"Move {device} → {position}."
                        : $"Move {device} → {position} failed: {r.Message} (teach it via Inspect Motion).");
        return r.IsSuccess;
    }

    /// <summary>Relative single-axis jog on a motion device.</summary>
    private async Task RelAsync(string device, string axis, double distance, string what, CancellationToken ct)
    {
        var r = await Host.Engine.Motion.MoveRelativeAsync(device, axis, distance, ct);
        Log(r.IsSuccess ? LogLevel.Info : LogLevel.Warning,
            $"{what} ({device} {axis} {distance:+0.###;-0.###} mm): {(r.IsSuccess ? "ok" : r.Message)}");
    }

    /// <summary>Drives a named output pin on the process IO module.</summary>
    private async Task IoAsync(string pin, bool state, string what, CancellationToken ct)
    {
        var r = await Host.Engine.IO.SetOutputByNameAsync(IoDevice, pin, state, ct);
        Log(r.IsSuccess ? LogLevel.Info : LogLevel.Warning,
            $"{what} ({pin}={(state ? "on" : "off")}): {(r.IsSuccess ? "ok" : r.Message)}");
    }

    /// <summary>Gripper open/close with the legacy 1 s settle dwell.</summary>
    private async Task GripperAsync(bool close, CancellationToken ct)
    {
        await IoAsync(GripperPin, close, $"Gripper {(close ? "close" : "open")}", ct);
        await Task.Delay(GripperMs, ct);
    }

    /// <summary>Base vacuum on/off with the legacy 0.3 s settle dwell.</summary>
    private async Task VacuumBaseAsync(bool on, CancellationToken ct)
    {
        await IoAsync(VacuumBasePin, on, $"Base vacuum {(on ? "on" : "off")}", ct);
        await Task.Delay(VacuumMs, ct);
    }

    /// <summary>UV-PLC trigger pulse — clear → 50 ms → set → 50 ms → clear (legacy UvPlcPulse).</summary>
    private async Task UvPlcPulseAsync(string pin, CancellationToken ct)
    {
        await IoAsync(pin, false, $"UV pulse {pin}", ct);
        await Task.Delay(PulseGapMs, ct);
        await IoAsync(pin, true, $"UV pulse {pin}", ct);
        await Task.Delay(PulseGapMs, ct);
        await IoAsync(pin, false, $"UV pulse {pin}", ct);
    }

    /// <summary>True when the hexapod's live X/Y/Z is within <paramref name="tol"/> mm of the named taught pose.</summary>
    private async Task<bool> HexAtAsync(string position, double tol = 0.5)
    {
        var target = Host.Process.GetPosition(HexDevice, position)?.Coordinates;
        if (target == null)
            return false;
        var live = await Host.Engine.Motion.GetPositionAsync(HexDevice);
        if (!live.IsSuccess)
            return false;
        var p = live.Value;
        return Math.Abs(p.X - target.X) <= tol
            && Math.Abs(p.Y - target.Y) <= tol
            && Math.Abs(p.Z - target.Z) <= tol;
    }

    private async Task PneuAsync(Task<Result> op, string what)
    {
        var r = await op;
        Log(r.IsSuccess ? LogLevel.Info : LogLevel.Warning, $"{what}: {(r.IsSuccess ? "ok" : r.Message)}");
    }

    private async Task DwellAsync(int ms, string what, CancellationToken ct)
    {
        Log(LogLevel.Info, $"{what} ({ms} ms)…");
        await Task.Delay(ms, ct);
    }
}
