using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FAAM.UI.Process;
using Horizon.Common;
using Horizon.Engine;
using Horizon.Workflow;

namespace FAAM.UI.Views;

/// <summary>
/// The Run-page process panel — operator-clicked FAU-attach steps, a process
/// timer, a STOP (halts all motion), and a Teach toggle. With Teach ON, clicking
/// a step records the gantry's current pose for that step (persisted to
/// faam_teach_points.json); with Teach OFF, clicking runs the step's hardcoded
/// sequence (Phase 1 — see <see cref="RunStepAsync"/>).
///
/// Device/pin mapping is hardcoded in the constants below — edit to match the rig.
/// </summary>
public partial class ProcessStepsPanel : UserControl
{
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

    // Named positions the process traverses, in process order, across both devices —
    // drives the Inspect-Motion table (each row is editable via the position editor).
    private static readonly (string Device, string Position)[] ProcessPositions =
    {
        (HexDevice, "ClearGrip"), (HexDevice, "FauGrip"), (HexDevice, "FauHover"),
        (HexDevice, "ClearLoad"), (HexDevice, "FauLoad"), (HexDevice, "Home"),
        (GantryDevice, "ClearToDown"), (GantryDevice, "PreDispense"),
        (GantryDevice, "PIC"), (GantryDevice, "UV"), (GantryDevice, "Home"),
    };

    private readonly DispatcherTimer _timer;
    private EngineHost? _host;
    private DateTime _startUtc;
    private bool _running;
    private bool _alignmentBusy;                // an alignment/scan is driving the hexapod — process steps are blocked
    private CancellationTokenSource? _runCts;   // cancels the in-flight step sequence on STOP
    private WorkflowDeck? _deck;                 // selected deck (null = built-in steps) — drives Auto-chain

    public ProcessStepsPanel()
    {
        InitializeComponent();
        StepList.ItemsSource = DefaultSteps();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => UpdateTimer();
    }

    /// <summary>Supplies the engine host (for the step sequences, teach, STOP, logging).</summary>
    public void Attach(EngineHost host)
    {
        _host = host;
        // While an alignment/scan run holds the hexapod, block the step + DUT-start
        // buttons until it finishes or is stopped (the alignment panels signal via the
        // gate). The process steps themselves mark the gate's Process side so the
        // alignment panels block their Start while a step sequence is executing.
        host.Activity.Changed += OnActivityChanged;
        SetAlignmentBusy(host.Activity.AlignmentBusy);

        // On a safety trip, kill the in-flight step sequence so the runner can't issue
        // the next move once the engine halts the current one (mirrors the STOP button).
        host.SafetyAbort += OnSafetyAbort;

        // Re-point the Run page at the new product's active deck when the product
        // changes, so switching product immediately reloads the right steps.
        host.Process.ProductChanged += OnProductChanged;

        // Populate the deck picker; re-list each time it's opened so decks authored in
        // the Engineer page show up without re-entering the Run page.
        RefreshDecks();
        DeckSelector.DropDownOpened += (_, _) => RefreshDecks();
    }

    /// <summary>
    /// On a product switch, rebuild the deck picker for the new product and select its
    /// active deck (dropping the previous product's selection) — or fall to the
    /// built-in steps + no-deck banner if it has none. Marshalled: <c>ProductChanged</c>
    /// may arrive off the UI thread.
    /// </summary>
    private void OnProductChanged(string? product)
    {
        if (Dispatcher.CheckAccess()) RefreshDecks(productSwitched: true);
        else Dispatcher.BeginInvoke(() => RefreshDecks(productSwitched: true));
    }

    /// <summary>
    /// Refreshes the deck dropdown from the active product, preserving the current
    /// selection (or selecting the product's active deck on first load). Selecting a
    /// deck rebuilds the step buttons from its <c>Run page</c> programs via
    /// <see cref="OnDeckSelected"/>.
    /// </summary>
    private void RefreshDecks(bool productSwitched = false)
    {
        if (_host == null) return;
        var decks = _host.Process.ListDecks();

        // Plain refresh (dropdown opened): skip if nothing changed so the current
        // selection isn't disturbed. A product switch always rebuilds.
        if (!productSwitched && DeckSelector.ItemsSource is IEnumerable<string> existing
            && existing.SequenceEqual(decks))
            return;

        // On a product switch, drop the previous product's selection and fall to the
        // new product's active deck.
        var current = productSwitched ? null : DeckSelector.SelectedItem as string;
        DeckSelector.ItemsSource = decks;
        var target = current ?? _host.Process.ActiveDeck;
        if (target != null && decks.Contains(target))
            DeckSelector.SelectedItem = target;

        UpdateNoDeckBanner();
    }

    /// <summary>
    /// Shows the guidance banner whenever no deck is driving the steps (the built-in
    /// fallback is in use) — i.e. the product has no deck, or none is selected/active.
    /// </summary>
    private void UpdateNoDeckBanner()
        => NoDeckBanner.Visibility = _deck == null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Rebuilds the step buttons from the selected deck's <c>Run page</c> programs (in deck order).</summary>
    private void OnDeckSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_host == null) return;

        if (DeckSelector.SelectedItem is not string name)
        {
            _deck = null;
            StepList.ItemsSource = DefaultSteps();
            TitleText.Text = "FAU ATTACH STEPS";
            UpdateNoDeckBanner();
            return;
        }

        var loaded = _host.Process.LoadDeck(name);   // also makes it the active deck
        if (!loaded.IsSuccess) { Log(LogLevel.Warning, loaded.Message); return; }

        // Build a button per "Run page" entry, but stamp each with its real index in the
        // deck so Auto-chain can walk the following entries (including ones with no button).
        var deck = loaded.Value;
        var steps = new List<ProcessStep>();
        for (int idx = 0; idx < deck.Entries.Count; idx++)
        {
            var entry = deck.Entries[idx];
            if (!entry.ShowInRun || string.IsNullOrWhiteSpace(entry.Program)) continue;
            var n = steps.Count + 1;
            steps.Add(new ProcessStep(n - 1, $"{n}. {entry.Program}", string.Empty, null, entry.Program, idx));
        }

        _deck = deck;
        StepList.ItemsSource = steps;
        TitleText.Text = string.IsNullOrWhiteSpace(deck.Name)
            ? "PROCESS DECK" : deck.Name.ToUpperInvariant();
        UpdateNoDeckBanner();
    }

    /// <summary>
    /// Safety-watchdog trip: cancel the running step sequence and stop the process
    /// timer. The physical motion halt is handled by the engine enforcement + the
    /// EngineHost backstop; here we only tear down the Layer 3 sequence so it stops
    /// advancing. Marshalled to the UI thread (the watchdog fires on a worker thread).
    /// </summary>
    private void OnSafetyAbort(SafetyTrip trip)
    {
        if (Dispatcher.CheckAccess()) AbortForSafety(trip);
        else Dispatcher.BeginInvoke(() => AbortForSafety(trip));
    }

    private void AbortForSafety(SafetyTrip trip)
    {
        _runCts?.Cancel();
        StopTimer();
        Log(LogLevel.Warning, $"SAFETY: {trip.Title} — step sequence aborted.");
    }

    private void OnActivityChanged()
    {
        if (Dispatcher.CheckAccess()) SetAlignmentBusy(_host?.Activity.AlignmentBusy ?? false);
        else Dispatcher.BeginInvoke(() => SetAlignmentBusy(_host?.Activity.AlignmentBusy ?? false));
    }

    /// <summary>Greys out the process step list while a scan/alignment is busy.</summary>
    private void SetAlignmentBusy(bool busy)
    {
        _alignmentBusy = busy;
        StepList.IsEnabled = !busy;
    }

    /// <summary>
    /// The FAU-attach step list. Each step's <c>Program</c> names the authored
    /// <c>.hwf</c> process in the active product's <c>processes\</c> folder that the
    /// button runs (clicking runs <c>processes\{Program}.hwf</c> via Layer 2; see
    /// <see cref="RunStepAsync"/> — the built-in sequence is only a fallback when the
    /// program is missing). <c>Device</c> + <c>Position</c> name the step's primary
    /// teach target — the destination pose Teach mode captures. A null <c>Position</c>
    /// means the step has no teachable pose (the UV dwell steps).
    /// </summary>
    private static List<ProcessStep> DefaultSteps() => new()
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

    private void OnStepClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not ProcessStep step)
            return;

        if (_alignmentBusy)
        {
            Log(LogLevel.Warning, $"Step '{step.Label}' blocked — an alignment/scan is running. Stop it first.");
            return;
        }

        // Block starting a step while a safety flag is raised (e.g. E-Stop pressed and
        // not yet acknowledged) — otherwise the operator could drive straight back into
        // the hazard.
        if (_host?.Engine.Safety.GetState().FlagRaised ?? false)
        {
            Log(LogLevel.Warning, $"Step '{step.Label}' blocked — safety flag raised. Acknowledge first.");
            return;
        }

        if (!_running)
            StartTimer();

        // Record the step into the open DUT run for traceability (no-op if none open).
        _host?.Process.RecordDutValue($"step{step.Index}", step.Label);

        Log(LogLevel.Info, $"Run — step: {step.Label}");

        // Fresh cancellation scope per step click — STOP cancels it (and a new
        // click supersedes any still-running step).
        _runCts?.Cancel();
        _runCts?.Dispose();
        _runCts = new CancellationTokenSource();

        // Mark the Process side of the gate for the step's duration so the manual
        // alignment/scan panels block their Start while this sequence runs.
        if (_host != null)
        {
            var token = _host.Activity.Begin(ActivityKind.Process, $"step {step.Index + 1}");
            _ = RunStepWithGateAsync(step, token, _runCts.Token);
        }
        else
        {
            _ = RunStepAsync(step, _runCts.Token);
        }
    }

    /// <summary>Runs a step, releasing the process-busy gate token when it finishes (or aborts).</summary>
    private async Task RunStepWithGateAsync(ProcessStep step, IDisposable token, CancellationToken ct)
    {
        try { await RunStepAsync(step, ct); }
        finally { token.Dispose(); }
    }

    // --- Process timer (driven by step clicks here and by the DUT run in the shell header) ---

    /// <summary>Starts the process timer if it isn't already running. Called on a step click
    /// and by the shell when a DUT run begins.</summary>
    public void StartProcessTimer()
    {
        if (!_running) StartTimer();
    }

    /// <summary>Stops the process timer. Called by the shell when a DUT run ends.</summary>
    public void StopProcessTimer() => StopTimer();

    /// <summary>
    /// Runs a step by clicking its button. Prefers the step's authored
    /// <see cref="ProcessStep.Program"/> — loaded from the active product's
    /// <c>processes\{Program}.hwf</c> and executed through Layer 2 — so the
    /// operator-edited Gantt/timeline program is the source of truth. The serial is
    /// owned by the panel's Start/Pass/Fail interactive DUT run, so the program runs
    /// with no serial (it creates no DUT record of its own). Falls back to the
    /// built-in <see cref="RunBuiltInStepAsync"/> sequence only when the step has no
    /// program or it fails to load.
    /// </summary>
    private async Task RunStepAsync(ProcessStep step, CancellationToken ct)
    {
        if (_host == null)
            return;

        try
        {
            if (!string.IsNullOrEmpty(step.Program))
            {
                var loaded = _host.Process.LoadProgram(step.Program);
                if (loaded.IsSuccess)
                {
                    Log(LogLevel.Info, $"Running program '{step.Program}' for step '{step.Label}'.");
                    var result = await _host.Process.RunAsync(loaded.Value, serial: null, ct);

                    // Auto-chain (opt-in via the checkbox by STOP): once this deck step's
                    // program succeeds, continue into the following Auto deck entries.
                    if (result.IsSuccess && AutoChainCheck.IsChecked == true
                        && _deck != null && step.DeckIndex >= 0)
                        await AutoChainFromAsync(step.DeckIndex, ct);
                    return;
                }
                Log(LogLevel.Warning,
                    $"Program '{step.Program}' not found ({loaded.Message}) — using built-in sequence.");
            }

            await RunBuiltInStepAsync(step, ct);
        }
        catch (OperationCanceledException)
        {
            Log(LogLevel.Warning, $"Step '{step.Label}' aborted (STOP).");
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Step '{step.Label}' error: {ex.Message}");
        }
    }

    /// <summary>
    /// Auto-chain (opt-in, off by default): after the clicked deck step's program runs,
    /// continue into the following deck entries whose transition is <c>Auto</c>, waiting
    /// each entry's <c>DelaySeconds</c> before running it. The walk stops at the first
    /// entry that is manual (<c>Auto</c> off — the operator clicks it to resume), an
    /// entry that carries a <c>Condition</c> (the Run page can't evaluate it — each
    /// step here runs in its own context, so it's treated as a manual stop rather than
    /// bypassing a gate), a program that fails to load or run, the end of the deck, or
    /// STOP (which cancels <paramref name="ct"/>). Disabled entries are skipped, exactly
    /// as the deck runner skips them. Only the chained programs run — <c>ShowInRun</c>
    /// has no effect here (an entry without a Run-page button still runs in the chain).
    /// </summary>
    private async Task AutoChainFromAsync(int fromIndex, CancellationToken ct)
    {
        if (_host == null || _deck == null) return;
        var entries = _deck.Entries;

        for (int i = fromIndex + 1; i < entries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var entry = entries[i];
            var label = string.IsNullOrEmpty(entry.Program) ? $"entry {i + 1}" : entry.Program;

            if (!entry.Enabled)
            {
                Log(LogLevel.Info, $"Auto-chain: '{label}' disabled — skipped.");
                continue;
            }

            if (!entry.Auto)
            {
                Log(LogLevel.Info, $"Auto-chain stopped before '{label}' — manual transition (Auto off). Click it to continue.");
                return;
            }

            if (!string.IsNullOrWhiteSpace(entry.Condition))
            {
                Log(LogLevel.Info,
                    $"Auto-chain stopped before '{label}' — it has a condition ('{entry.Condition}'), which the Run page can't evaluate. Click it to continue.");
                return;
            }

            if (entry.DelaySeconds > 0)
            {
                Log(LogLevel.Info, $"Auto-chain: waiting {entry.DelaySeconds:0.##}s before '{label}'.");
                await Task.Delay(TimeSpan.FromSeconds(entry.DelaySeconds), ct);
            }

            var loaded = _host.Process.LoadProgram(entry.Program);
            if (!loaded.IsSuccess)
            {
                Log(LogLevel.Warning, $"Auto-chain stopped — program '{label}' not found: {loaded.Message}.");
                return;
            }

            Log(LogLevel.Info, $"Auto-chain: running '{label}'.");
            _host.Process.RecordDutValue($"chain_{i}", label);
            var result = await _host.Process.RunAsync(loaded.Value, serial: null, ct);
            if (!result.IsSuccess)
            {
                Log(LogLevel.Error, $"Auto-chain stopped — '{label}' failed: {result.Message}.");
                return;
            }
        }

        Log(LogLevel.Info, "Auto-chain complete — end of deck.");
    }

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
    private async Task RunBuiltInStepAsync(ProcessStep step, CancellationToken ct)
    {
        switch (step.Index)
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
        await PneuAsync(_host!.Engine.Pneumatic.RetractAsync(DispenserSlide, ct), $"{DispenserSlide} up");
        await PneuAsync(_host.Engine.Pneumatic.RetractAsync(UvSlide, ct), $"{UvSlide} up");

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
    private async Task Step2GripFauAsync(CancellationToken ct)
    {
        Log(LogLevel.Info, "Step 2 — move to grip FAU (InProgress).");

        if (Confirm("Do you want to move to grip FAU?"))
        {
            if (!await MoveNamedAsync(HexDevice, "ClearGrip", ct)) return;
            if (!await MoveNamedAsync(HexDevice, "FauGrip", ct)) return;
        }
        else
        {
            Info("Move to grip FAU cancelled.");
        }

        if (Confirm("Do you want to Grip the FAU?"))
        {
            await GripperAsync(close: true, ct);
            await DwellAsync(5000, "grip settle", ct);
            await VacuumBaseAsync(on: false, ct);
            if (!await MoveNamedAsync(HexDevice, "FauHover", ct)) return;
            _host!.Process.RecordDutValue("step1_grip", "FAU gripped");
            Log(LogLevel.Info, "Step 2 — Completed.");
        }
        else
        {
            Info("Grip FAU cancelled.");
            Log(LogLevel.Info, "Step 2 — Idle (grip cancelled).");
        }
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
            _host!.Process.RecordDutValue("step3_siphog", "at SiPhog");
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
            await PneuAsync(_host!.Engine.Pneumatic.ExtendAsync(DispenserSlide, ct), $"{DispenserSlide} down");
            await DwellAsync(3000, "dispenser head settle", ct);
        }
        else
        {
            Info("Dispenser head lower cancelled.");
        }

        _host!.Process.RecordDutValue("step40_dispense", "at dispense");
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
        await PneuAsync(_host!.Engine.Pneumatic.RetractAsync(DispenserSlide, ct), $"{DispenserSlide} up");
        await DwellAsync(1000, "head lift settle", ct);
        await RelAsync(HexDevice, "Z", gripperMoveBack, "gripper return", ct);
        await DwellAsync(1000, "gripper settle", ct);

        if (!await MoveNamedAsync(GantryDevice, "UV", ct)) return;
        await PneuAsync(_host.Engine.Pneumatic.ExtendAsync(UvSlide, ct), $"{UvSlide} down");
        await DwellAsync(1500, "UV head settle", ct);

        _host.Process.RecordDutValue("step41_uv", "epoxy dispensed, at UV");
        Log(LogLevel.Info, "Step 4.1 — Completed.");
    }

    // --- 4.2 UV step 1 (cure) ---
    private async Task Step42UvStep1Async(CancellationToken ct)
    {
        var seconds = _host!.Settings.Current.UvStep1CuringSeconds;
        Log(LogLevel.Info, "Step 4.2 — UV step1 (InProgress).");

        if (Confirm($"Do you want to start toggling UV PLC1?  UV Step1 Time: {seconds:0.#} seconds"))
        {
            await UvPlcPulseAsync(UvPlc1Pin, ct);
            await DwellAsync((int)(seconds * 1000), "UV step1 cure", ct);
            Info("UV step1 cure complete.");
            _host.Process.RecordDutValue("step42_uv1", $"cured {seconds:0.#}s");
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
        var seconds = _host!.Settings.Current.UvCuringSeconds;
        Log(LogLevel.Info, "Step 4.3 — UV step2 (InProgress).");

        if (Confirm($"Do you want to start toggling UV PLC2?  UV Curing Time: {seconds:0.#} seconds"))
        {
            await UvPlcPulseAsync(UvPlc2Pin, ct);
            await DwellAsync((int)(seconds * 1000), "UV step2 cure", ct);
            Info("UV cure complete — then press (5) to complete and unload.");
            await PneuAsync(_host.Engine.Pneumatic.RetractAsync(UvSlide, ct), $"{UvSlide} up");
            _host.Process.RecordDutValue("step43_uv2", $"cured {seconds:0.#}s");
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
            await PneuAsync(_host!.Engine.Pneumatic.RetractAsync(UvSlide, ct), $"{UvSlide} up");
            await GripperAsync(close: false, ct);     // open / release
            await DwellAsync(2000, "ungrip settle", ct);
            await MoveNamedAsync(HexDevice, "ClearLoad", ct);
            await MoveNamedAsync(HexDevice, "Home", ct);
            _host.Process.RecordDutValue("step5_unload", "unloaded");
            StopTimer();
            Log(LogLevel.Info, "Step 5 — Completed.");
        }
        else
        {
            Info("Unload cancelled.");
            Log(LogLevel.Info, "Step 5 — Idle.");
        }
    }


    /// <summary>
    /// Raised when a step's "Inspect Motion" context-menu item is chosen. Carries
    /// one row per distinct process position; the host view (RunView) renders it as
    /// a grid over the camera column.
    /// </summary>
    public event EventHandler<IReadOnlyList<MotionInspectRow>>? InspectMotionRequested;

    /// <summary>
    /// Builds the motion-inspection table for every named position the process
    /// traverses (<see cref="ProcessPositions"/>, across both the gantry and the
    /// hexapod) from the Layer 2 product: name, device, the position's stored
    /// velocity, its taught X/Y/Z/U/V/W ("—" when not taught), and its camera /
    /// exposure. Each row carries an <see cref="MotionInspectRow.Edit"/> action
    /// that opens the per-position editor.
    /// </summary>
    private void OnInspectMotion(object sender, RoutedEventArgs e)
    {
        if (_host == null)
            return;

        var rows = new List<MotionInspectRow>();
        foreach (var (device, position) in ProcessPositions)
        {
            var np = _host.Process.GetPosition(device, position);
            var c = np?.Coordinates;
            rows.Add(new MotionInspectRow(
                position, device, Fmt(np?.Velocity),
                Fmt(c?.X), Fmt(c?.Y), Fmt(c?.Z), Fmt(c?.U), Fmt(c?.V), Fmt(c?.W),
                np?.Camera ?? "—", Fmt(np?.ExposureUs))
            {
                Edit = () => OpenPositionEditor(device, position, np),
            });
        }

        InspectMotionRequested?.Invoke(this, rows);
    }

    /// <summary>Opens the non-modal per-position editor; on Apply the position is saved to the product via Layer 2.</summary>
    private void OpenPositionEditor(string device, string position, NamedPosition? np)
    {
        if (_host == null)
            return;

        var info = _host.Engine.Motion.GetDevice(device);
        if (info == null)
            return;

        var ctx = new PositionEditContext
        {
            Position = position,
            Device = info.Name,
            ControllerType = info.ControllerType,
            Axes = info.Axes,
            Velocity = np?.Velocity ?? 0,
            Pose = np?.Coordinates ?? MotionPosition.Zero,
            Cameras = _host.Engine.Camera.CameraIds,
            Camera = np?.Camera,
            ExposureUs = np?.ExposureUs,
        };

        new PositionEditorWindow(ctx, ApplyPositionEditAsync)
        {
            Owner = Window.GetWindow(this),
        }.Show();
    }

    /// <summary>Persists an edited position (coords + velocity + camera/exposure) to the product, then refreshes the grid.</summary>
    private Task ApplyPositionEditAsync(PositionEditResult result)
    {
        var r = _host!.Process.SetPositionFull(
            result.Device, result.Position, result.Pose, result.Velocity, result.Camera, result.ExposureUs);
        Log(r.IsSuccess ? LogLevel.Info : LogLevel.Warning,
            r.IsSuccess
                ? $"Position '{result.Position}' saved (velocity {result.Velocity:0.###})."
                : $"Save '{result.Position}' failed: {r.Message}");

        OnInspectMotion(this, new RoutedEventArgs());   // rebuild rows with the new values
        return Task.CompletedTask;
    }

    private static string Fmt(double? v) => v.HasValue ? v.Value.ToString("0.###") : "—";

    /// <summary>Captures the device's current pose under the step's teach-target position name (Teach mode) and persists it.</summary>
    // --- Step-sequence primitives (shared by the RunStepAsync per-step methods) ---

    /// <summary>Operator Yes/No gate — faithful to the legacy confirmation dialogs.</summary>
    private static bool Confirm(string question)
        => MessageBox.Show(question, "FAU Attach", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    /// <summary>Operator acknowledgement popup — faithful to the legacy success / cancel messages.</summary>
    private static void Info(string message)
        => MessageBox.Show(message, "FAU Attach", MessageBoxButton.OK, MessageBoxImage.Information);

    /// <summary>Recalls a named position via Layer 2; returns false (and logs) when the point is untaught / the move fails.</summary>
    private async Task<bool> MoveNamedAsync(string device, string position, CancellationToken ct)
    {
        var r = await _host!.Process.MoveToNamedPositionAsync(device, position, ct);
        Log(r.IsSuccess ? LogLevel.Info : LogLevel.Warning,
            r.IsSuccess ? $"Move {device} → {position}."
                        : $"Move {device} → {position} failed: {r.Message} (teach it via Inspect Motion).");
        return r.IsSuccess;
    }

    /// <summary>Relative single-axis jog on a motion device.</summary>
    private async Task RelAsync(string device, string axis, double distance, string what, CancellationToken ct)
    {
        var r = await _host!.Engine.Motion.MoveRelativeAsync(device, axis, distance, ct);
        Log(r.IsSuccess ? LogLevel.Info : LogLevel.Warning,
            $"{what} ({device} {axis} {distance:+0.###;-0.###} mm): {(r.IsSuccess ? "ok" : r.Message)}");
    }

    /// <summary>Drives a named output pin on the process IO module.</summary>
    private async Task IoAsync(string pin, bool state, string what, CancellationToken ct)
    {
        var r = await _host!.Engine.IO.SetOutputByNameAsync(IoDevice, pin, state, ct);
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
        var target = _host!.Process.GetPosition(HexDevice, position)?.Coordinates;
        if (target == null)
            return false;
        var live = await _host.Engine.Motion.GetPositionAsync(HexDevice);
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

    private async void OnStop(object sender, RoutedEventArgs e)
    {
        StopTimer();
        Log(LogLevel.Warning, "STOP pressed — halting motion.");

        // Cancel the in-flight step sequence FIRST so it cannot issue the next move
        // once the current one is halted below (otherwise the stop is immediately
        // overridden by the step's next MoveNamed/jog).
        _runCts?.Cancel();

        if (_host != null)
        {
            foreach (var device in _host.Engine.Motion.DeviceNames)
            {
                try
                {
                    var r = await _host.Engine.Motion.StopAsync(device);
                    Log(r.IsSuccess ? LogLevel.Info : LogLevel.Error,
                        r.IsSuccess ? $"STOP {device} — halted." : $"STOP {device} FAILED: {r.Message}");
                }
                catch (Exception ex) { Log(LogLevel.Error, $"Stop {device} failed: {ex.Message}"); }
            }
        }
    }

    private void StartTimer()
    {
        _startUtc = DateTime.UtcNow;
        _running = true;
        _timer.Start();
        UpdateTimer();
    }

    private void StopTimer()
    {
        _running = false;
        _timer.Stop();
    }

    private void UpdateTimer()
    {
        var elapsed = DateTime.UtcNow - _startUtc;
        TimerText.Text = $"Process: {elapsed:hh\\:mm\\:ss}";
    }

    private void Log(LogLevel level, string message)
        => _host?.Log.Add(new LogEntry(level, DateTimeOffset.Now, "Process", message));
}

/// <summary>
/// One process step — its fixed index (drives the built-in fallback sequence in
/// <see cref="ProcessStepsPanel.RunStepAsync"/>), display label, the motion
/// <c>Device</c> its teach target lives on, an optional teach-target position name
/// (<c>Position</c>, null = no teachable pose, e.g. the UV dwell steps), and the
/// name of the authored <c>.hwf</c> process the button runs (<c>Program</c>,
/// resolved as <c>processes\{Program}.hwf</c> in the active product), and — for a
/// deck-derived step — its index in the deck's entry list (<c>DeckIndex</c>, -1 for
/// the built-in steps), which Auto-chain uses to walk the following entries.
/// </summary>
public sealed record ProcessStep(int Index, string Label, string Device, string? Position = null, string? Program = null, int DeckIndex = -1);

/// <summary>One row of the motion inspector — a process position and its recall settings (values pre-formatted for display).</summary>
public sealed record MotionInspectRow(
    string Position, string Device, string Velocity,
    string X, string Y, string Z, string U, string V, string W,
    string Camera, string Exposure)
{
    /// <summary>Opens the per-position editor for this row (wired by the inspector builder).</summary>
    public Action? Edit { get; init; }
}
