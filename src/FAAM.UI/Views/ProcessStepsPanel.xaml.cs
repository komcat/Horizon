using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
public partial class ProcessStepsPanel : UserControl, IMotionInspectHost
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

    private readonly DispatcherTimer _timer;
    private EngineHost? _host;
    private DateTime _startUtc;
    private bool _running;
    private bool _alignmentBusy;                // an alignment/scan is driving the hexapod — process steps are blocked
    private CancellationTokenSource? _runCts;   // cancels the in-flight step sequence on STOP
    private WorkflowDeck? _deck;                 // selected deck (null = built-in steps) — drives Auto-chain

    private string _title = "FAU ATTACH STEPS";   // deck title shown while idle
    private int _executing;                       // steps in flight (a new click can overlap a superseded one)
    private readonly RunProgressTracker _progress = new();   // drives the progress bar under the title
    private readonly RunDurationStore _durations = new();    // earlier-run block timings → % estimate
    private readonly DispatcherTimer _progressTick;          // advances the time estimate while running

    /// <summary>Raised on the UI thread whenever the run progress changes (and once with
    /// <see cref="RunProgressSnapshot.Running"/> false when execution ends) — the shell
    /// mirrors it onto the big RUN header.</summary>
    public event Action<RunProgressSnapshot>? ProgressChanged;

    public ProcessStepsPanel()
    {
        InitializeComponent();
        StepList.ItemsSource = DefaultSteps();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => UpdateTimer();
        _progressTick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _progressTick.Tick += (_, _) => { _progress.Tick(); ShowProgress(); };
    }

    /// <summary>Shows the active DUT serial large above the run title; null / blank hides it. UI thread.</summary>
    public void SetDutSerial(string? serial)
    {
        bool has = !string.IsNullOrWhiteSpace(serial);
        DutBannerText.Text = has ? serial!.Trim() : string.Empty;
        DutBanner.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
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

        // Progress bar feed — both events arrive off the UI thread.
        host.Process.StepEvent += e => Dispatcher.BeginInvoke(() => { if (_executing > 0) { _progress.OnStep(e); ShowProgress(); } });
        host.Process.ScriptProgressChanged += p => Dispatcher.BeginInvoke(() => { if (_executing > 0) { _progress.OnScript(p); ShowProgress(); } });

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

    /// <summary>
    /// Re-syncs the step buttons with the on-disk deck — called when the Run page is
    /// shown, so a deck edited and saved in the Engineer page (same product, same name,
    /// which <see cref="RefreshDecks"/> leaves untouched because the deck-name list is
    /// unchanged) is picked up without re-selecting it in the dropdown. Re-lists the
    /// deck names first (one may have been added/renamed/deleted), then reloads the
    /// selected deck's content and rebuilds the buttons from its current Run-page entries.
    /// </summary>
    public void SyncDeck()
    {
        if (_host == null) return;
        RefreshDecks();
        if (DeckSelector.SelectedItem is string name)
            LoadDeckIntoSteps(name);
    }

    /// <summary>Rebuilds the step buttons from the selected deck's <c>Run page</c> programs (in deck order).</summary>
    private void OnDeckSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_host == null) return;

        if (DeckSelector.SelectedItem is not string name)
        {
            _deck = null;
            StepList.ItemsSource = DefaultSteps();
            SetTitle("FAU ATTACH STEPS");
            UpdateNoDeckBanner();
            return;
        }

        LoadDeckIntoSteps(name);
    }

    /// <summary>Loads the named deck (making it active) and rebuilds the Run-page buttons from its <c>Run page</c> entries.</summary>
    private void LoadDeckIntoSteps(string name)
    {
        if (_host == null) return;

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
        SetTitle(string.IsNullOrWhiteSpace(deck.Name)
            ? "PROCESS DECK" : deck.Name.ToUpperInvariant());
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
        UpdateStepListEnabled();
    }

    /// <summary>
    /// The step buttons and deck picker are usable only when nothing is running: no step of
    /// this panel executing (one program at a time — STOP is the only way to abort it) and
    /// no alignment/scan holding the hexapod. STOP and Auto-chain stay live.
    /// </summary>
    private void UpdateStepListEnabled()
    {
        bool idle = !_alignmentBusy && _executing == 0;
        StepList.IsEnabled = idle;
        DeckSelector.IsEnabled = idle;
    }

    /// <summary>
    /// Why a new run can't start right now (a step of this panel executing, or a Layer 2
    /// program / deck / recipe running from another page), or null when it may.
    /// </summary>
    private string? RunBlockedReason()
    {
        if (_executing > 0) return "a step is already running — wait for it to finish or press STOP";
        if (_host?.Process.IsRunning ?? false) return "a program is already running — wait for it to finish or press STOP";
        return null;
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

        // One program at a time: a click while something runs is refused, never a
        // silent cancel-and-restart of the running step.
        if (RunBlockedReason() is { } busy)
        {
            Log(LogLevel.Warning, $"Step '{step.Label}' blocked — {busy}.");
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

        var isFirstStep = ReferenceEquals(step, (StepList.ItemsSource as IEnumerable<ProcessStep>)?.FirstOrDefault());

        // Clicking the first step starts a fresh run — clear every button back to idle
        // (default colour). Re-clicking any later step leaves the prior colours intact.
        if (isFirstStep)
            ResetStepStatuses();

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

        step.Status = StepStatus.Running;   // yellow while executing
        SetExecuting(+1);
        try
        {
            if (!string.IsNullOrEmpty(step.Program))
            {
                var loaded = _host.Process.LoadProgram(step.Program);
                if (loaded.IsSuccess)
                {
                    Log(LogLevel.Info, $"Running program '{step.Program}' for step '{step.Label}'.");
                    BeginProgress(step.Program);
                    var result = await _host.Process.RunAsync(loaded.Value, serial: null, ct);
                    CommitProgress(result.IsSuccess);
                    if (!result.IsSuccess)
                    {
                        step.Status = StepStatus.Failed;   // program reported failure → red
                        if (result.Error == ErrorCode.ProcessCancelled)
                            Log(LogLevel.Warning, $"Step '{step.Label}' cancelled by the operator.");
                        else
                            Log(LogLevel.Error, $"Step '{step.Label}' failed: {result.Message}");
                        return;
                    }

                    step.Status = StepStatus.Success;      // ran to the end → green

                    // Auto-chain (opt-in via the checkbox by STOP): once this deck step's
                    // program succeeds, continue into the following Auto deck entries.
                    if (AutoChainCheck.IsChecked == true && _deck != null && step.DeckIndex >= 0)
                        await AutoChainFromAsync(step.DeckIndex, ct);
                    return;
                }
                Log(LogLevel.Warning,
                    $"Program '{step.Program}' not found ({loaded.Message}) — using built-in sequence.");
            }

            await RunBuiltInStepAsync(step, ct);
            step.Status = StepStatus.Success;              // built-in sequence completed → green
        }
        catch (StepCancelledException ex)
        {
            step.Status = StepStatus.Failed;              // operator declined a gate → red
            Log(LogLevel.Warning, $"Step '{step.Label}' cancelled — {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            // STOP / safety / a superseding step click.
            step.Status = StepStatus.Failed;              // interrupted → red
            Log(LogLevel.Warning, $"Step '{step.Label}' aborted (STOP).");
        }
        catch (Exception ex)
        {
            step.Status = StepStatus.Failed;              // errored → red
            Log(LogLevel.Error, $"Step '{step.Label}' error: {ex.Message}");
        }
        finally
        {
            SetExecuting(-1);
        }
    }

    /// <summary>Restarts the progress bar for the program about to run.</summary>
    private void BeginProgress(string program)
    {
        _progress.Reset(_durations, RunDurationStore.Key(_host?.Process.ActiveProduct, program));
        ShowProgress();
    }

    /// <summary>Saves the finished program's block timings for the next run's estimate.</summary>
    private void CommitProgress(bool completed)
        => _progress.Commit(completed, err => Log(LogLevel.Warning, $"Could not save run timings: {err}"));

    /// <summary>Pushes the tracker's % and current block onto the progress bar (+ the shell header).</summary>
    private void ShowProgress()
    {
        var snap = RunProgressSnapshot.From(_progress, running: _executing > 0);
        RunProgress.IsIndeterminate = !snap.HasProgress;
        RunProgress.Value = snap.Percent;
        RunProgressText.Text = snap.Text;
        ProgressChanged?.Invoke(snap);
    }

    /// <summary>Sets the idle (deck) title; while executing the title bar keeps showing "RUNNING…".</summary>
    private void SetTitle(string title)
    {
        _title = title;
        if (_executing == 0) TitleText.Text = title;
    }

    /// <summary>
    /// Tracks in-flight steps and switches the panel's executing look: a darker panel
    /// fill and a green "RUNNING…" title bar while any step runs; restored when idle.
    /// </summary>
    private void SetExecuting(int delta)
    {
        _executing = Math.Max(0, _executing + delta);
        bool busy = _executing > 0;
        UpdateStepListEnabled();

        PanelRoot.SetResourceReference(Border.BackgroundProperty,
            busy ? SystemColors.ControlDarkBrushKey : "Panel");

        if (busy && delta > 0)
        {
            _progress.Reset();          // built-in step: stays indeterminate (no program events)
            _progressTick.Start();
            ShowProgress();
        }
        else if (!busy)
        {
            _progressTick.Stop();
            ProgressChanged?.Invoke(RunProgressSnapshot.Idle);
        }
        ProgressArea.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy)
        {
            TitleBar.Background = (Brush)FindResource("StatusSuccess");
            TitleText.Foreground = Brushes.Black;
            TitleText.Text = "RUNNING…";
        }
        else
        {
            TitleBar.Background = Brushes.Transparent;
            TitleText.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            TitleText.Text = _title;
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

            // Mirror the chained entry's run onto its Run-page button colour (if it has one).
            var chained = FindStepByDeckIndex(i);
            if (chained != null) chained.Status = StepStatus.Running;

            Log(LogLevel.Info, $"Auto-chain: running '{label}'.");
            _host.Process.RecordDutValue($"chain_{i}", label);
            BeginProgress(entry.Program);
            try
            {
                var result = await _host.Process.RunAsync(loaded.Value, serial: null, ct);
                CommitProgress(result.IsSuccess);
                if (!result.IsSuccess)
                {
                    if (chained != null) chained.Status = StepStatus.Failed;
                    if (result.Error == ErrorCode.ProcessCancelled)
                        Log(LogLevel.Warning, $"Auto-chain stopped — '{label}' cancelled by the operator.");
                    else
                        Log(LogLevel.Error, $"Auto-chain stopped — '{label}' failed: {result.Message}.");
                    return;
                }
                if (chained != null) chained.Status = StepStatus.Success;
            }
            catch
            {
                if (chained != null) chained.Status = StepStatus.Failed;
                throw;   // let RunStepAsync's handler log the cancel/error
            }
        }

        Log(LogLevel.Info, "Auto-chain complete — end of deck.");
    }

    /// <summary>Finds the Run-page step button backing a given deck-entry index (null if that entry has no button).</summary>
    private ProcessStep? FindStepByDeckIndex(int deckIndex)
        => (StepList.ItemsSource as IEnumerable<ProcessStep>)?.FirstOrDefault(s => s.DeckIndex == deckIndex);

    /// <summary>Resets every step button back to <see cref="StepStatus.Idle"/> (default colour).</summary>
    private void ResetStepStatuses()
    {
        if (StepList.ItemsSource is IEnumerable<ProcessStep> steps)
            foreach (var s in steps)
                s.Status = StepStatus.Idle;
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
        _host!.Process.RecordDutValue("step1_grip", "FAU gripped");
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
    /// Raised when a step's "Edit" context-menu item is chosen, with that step's program
    /// name. The shell switches to the Engineer page and opens it in the timeline editor.
    /// </summary>
    public event Action<string>? EditProgramRequested;

    private void OnEditProgram(object sender, RoutedEventArgs e)
    {
        var step = StepOf(sender);
        if (step == null)
            return;

        if (string.IsNullOrWhiteSpace(step.Program))
        {
            Log(LogLevel.Warning, $"Edit: step '{step.Label}' has no program — nothing to edit.");
            return;
        }

        EditProgramRequested?.Invoke(step.Program);
    }

    /// <summary>
    /// Raised when a step's "Inspect Motion" context-menu item is chosen. Carries the
    /// inspector session for <b>that step's program</b>; the host view (RunView) binds
    /// it as a grid plus step-cursor controls over the camera column.
    /// </summary>
    public event EventHandler<MotionInspectSession>? InspectMotionRequested;

    /// <summary>The inspector currently on screen, so a position edit can refresh its rows.</summary>
    private MotionInspectSession? _inspect;

    /// <summary>
    /// Builds the motion-inspection table for the right-clicked step: the named
    /// positions <b>that step's program actually moves to</b>, in program order (see
    /// <see cref="MotionInspectSession.Build"/>) — not every position the process
    /// knows. Each row shows the point's velocity, taught X/Y/Z/U/V/W ("—" when not
    /// taught) and camera / exposure, and can be stepped to and re-taught.
    /// </summary>
    private void OnInspectMotion(object sender, RoutedEventArgs e)
    {
        if (_host == null)
            return;

        var step = StepOf(sender);
        if (step == null)
            return;

        if (string.IsNullOrWhiteSpace(step.Program))
        {
            Log(LogLevel.Warning,
                $"Inspect Motion: step '{step.Label}' has no program — nothing to inspect.");
            return;
        }

        var session = MotionInspectSession.Build(this, step.Program, step.Label);
        if (session == null)
            return;

        _inspect = session;
        InspectMotionRequested?.Invoke(this, session);

        // Find out where the machine already is, so the cursor opens on the right row.
        session.SyncCommand.Execute(null);
    }

    /// <summary>
    /// Resolves the step a context-menu click belongs to. The menu item's own
    /// DataContext is inherited from the step button, but the button's
    /// <c>Tag</c> (bound to the step) reached through the menu's
    /// <c>PlacementTarget</c> is the reliable route, so try that first.
    /// </summary>
    private static ProcessStep? StepOf(object sender)
    {
        if (sender is MenuItem { Parent: ContextMenu { PlacementTarget: FrameworkElement { Tag: ProcessStep tagged } } })
            return tagged;
        return (sender as FrameworkElement)?.DataContext as ProcessStep;
    }

    // --- IMotionInspectHost (services the motion inspector borrows from this panel) ---

    EngineHost IMotionInspectHost.Host => _host!;

    void IMotionInspectHost.InspectorLog(LogLevel level, string message) => Log(level, message);

    void IMotionInspectHost.OpenPositionEditor(string device, string position)
        => OpenPositionEditor(device, position, _host?.Process.GetPosition(device, position));

    /// <summary>
    /// Runs one inspector move under the same guards as a process step: blocked while
    /// an alignment/scan holds the hexapod or a safety flag is raised, marked on the
    /// process side of the activity gate, and cancelled by STOP / a safety trip
    /// (it shares the panel's <c>_runCts</c>).
    /// </summary>
    async Task<Result> IMotionInspectHost.RunInspectorMoveAsync(
        string what, Func<CancellationToken, Task<Result>> body)
    {
        if (_host == null)
            return Result.Fail(ErrorCode.OperationFailed, "Engine not attached.");

        if (_alignmentBusy)
            return Result.Fail(ErrorCode.OperationFailed,
                "an alignment/scan is running — stop it first");

        if (_host.Engine.Safety.GetState().FlagRaised)
            return Result.Fail(ErrorCode.OperationFailed,
                "a safety flag is raised — acknowledge it first");

        if (RunBlockedReason() is { } busy)
            return Result.Fail(ErrorCode.OperationFailed, busy);

        _runCts?.Cancel();
        _runCts?.Dispose();
        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;

        using var gate = _host.Activity.Begin(ActivityKind.Process, what);
        try
        {
            return await body(ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return Result.Fail(ErrorCode.ProcessCancelled, "move cancelled");
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCode.OperationFailed, ex.Message);
        }
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

        _inspect?.RefreshRows();   // re-read the open inspector's rows with the new values
        return Task.CompletedTask;
    }

    /// <summary>Captures the device's current pose under the step's teach-target position name (Teach mode) and persists it.</summary>
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
    {
        _host?.Log.Add(new LogEntry(level, DateTimeOffset.Now, "Process", message));
        // Mirror every process-step log line into the open DUT run (no-op if none is
        // open) so each unit's serial carries its full, timestamped step history.
        _host?.Process.RecordDutValue("log", level == LogLevel.Info ? message : $"[{level}] {message}");
    }
}

/// <summary>
/// Thrown by a built-in step sequence when the operator declines a gate that guards
/// motion onto the part. Distinct from <see cref="OperationCanceledException"/> (STOP
/// / safety / a superseding click) so it is reported as an operator cancel.
/// </summary>
public sealed class StepCancelledException : Exception
{
    public StepCancelledException(string reason) : base(reason) { }
}

/// <summary>Run state of a process step button — drives its fill colour.</summary>
public enum StepStatus
{
    /// <summary>Never run (or reset) — default button colour.</summary>
    Idle,
    /// <summary>Currently executing — yellow.</summary>
    Running,
    /// <summary>Ran to completion successfully — green.</summary>
    Success,
    /// <summary>Interrupted, cancelled (STOP/safety), failed, or errored — red.</summary>
    Failed,
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
/// <see cref="Status"/> is mutable and observable so the button colour tracks the
/// run live.
/// </summary>
public sealed class ProcessStep : Mvvm.ObservableObject
{
    public ProcessStep(int index, string label, string device, string? position = null, string? program = null, int deckIndex = -1)
    {
        Index = index;
        Label = label;
        Device = device;
        Position = position;
        Program = program;
        DeckIndex = deckIndex;
    }

    public int Index { get; }
    public string Label { get; }
    public string Device { get; }
    public string? Position { get; }
    public string? Program { get; }
    public int DeckIndex { get; }

    private StepStatus _status = StepStatus.Idle;
    /// <summary>Live run state — the button's fill colour binds to this.</summary>
    public StepStatus Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }
}
