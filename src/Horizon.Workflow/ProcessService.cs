using Horizon.Common;
using Horizon.Engine;
using Horizon.Workflow.Recipe;
using Horizon.Workflow.UCorrection;

namespace Horizon.Workflow;

/// <summary>
/// Default in-process implementation of <see cref="IProcessService"/> — the
/// Layer 2 module on top of <see cref="IHorizonEngine"/>.
/// Phase 1: product management. Phase 2: teach points / named positions.
/// </summary>
public sealed class ProcessService : IProcessService
{
    private readonly IHorizonEngine _engine;
    private readonly ProductStore _products = new();
    private PositionStore? _positions;   // null when no product is active
    private ProgramStore? _programs;     // null when no product is active
    private RecipeStore? _recipes;       // null when no product is active
    private DeckStore? _decks;           // null when no product is active
    private DutStore? _dut;              // null when no product is active
    private readonly ProgramHistoryDb _history = new();   // machine-global audit store (history.db)
    private readonly DeviceLinkStore _deviceLinks = new();   // machine-global, not per-product
    private PauseController? _pause;     // non-null only while a program runs
    private OperatorContext? _operator;  // current operator, stamped onto DUT runs
    private (DutRecord Record, DutRun Run)? _interactiveDut;   // open operator-driven DUT run
    private (DutRecord Record, DutRun Run)? _runDut;   // the running program/deck's DUT (up-front serial, or set mid-run by dut.promptSerial)

    public ProcessService(IHorizonEngine engine)
    {
        _engine = engine;

        // Load stores for a product that was already active from a prior run.
        var dir = _products.ActiveProductDir;
        if (dir != null)
        {
            _positions = new PositionStore(dir);
            _programs = new ProgramStore(dir);
            _recipes = new RecipeStore(dir);
            _decks = new DeckStore(dir);
            _dut = new DutStore(dir, _products.ActiveProduct!);
        }
    }

    public IReadOnlyList<string> Products => _products.GetProductNames();

    public string? ActiveProduct => _products.ActiveProduct;

    public ProductInfo? ActiveProductInfo
    {
        get
        {
            var active = _products.ActiveProduct;
            return active == null ? null : _products.ReadInfo(active);
        }
    }

    public ProductInfo? GetProductInfo(string name) => _products.ReadInfo(name);

    public event Action<WorkflowStepEvent>? StepEvent;
    public event Action<DeckProgress>? DeckStepEvent;
    public event Action<LogEntry>? LogEmitted;
    public event Action<bool>? PausedChanged;
    public event Action<string?>? ProductChanged;
    public event Action<string?>? ActiveSerialChanged;

    public string? ActiveSerial { get; private set; }

    /// <summary>Updates <see cref="ActiveSerial"/> and notifies the front-end (no-op if unchanged).</summary>
    private void SetActiveSerial(string? serial)
    {
        if (string.Equals(ActiveSerial, serial, StringComparison.Ordinal)) return;
        ActiveSerial = serial;
        ActiveSerialChanged?.Invoke(serial);
    }

    public IOperatorPrompt? OperatorPrompt { get; set; }

    public bool IsPaused => _pause?.IsPaused ?? false;

    public void Pause() => _pause?.Pause();
    public void Resume() => _pause?.Resume();
    public void Step() => _pause?.Step();

    // --- Products ---

    public Result CreateProduct(string name)
    {
        if (!_products.IsValidName(name))
            return Result.Fail(ErrorCode.InvalidParameter, $"Invalid product name: '{name}'.");
        if (_products.Exists(name))
            return Result.Fail(ErrorCode.OperationFailed, $"Product '{name}' already exists.");

        _products.Create(name);
        Log(LogLevel.Info, $"Product created: {name}");
        return Result.Ok();
    }

    public Result DeleteProduct(string name)
    {
        if (!_products.Exists(name))
            return Result.Fail(ErrorCode.FileNotFound, $"Product '{name}' not found.");

        var wasActive = string.Equals(_products.ActiveProduct, name, StringComparison.OrdinalIgnoreCase);
        if (wasActive && !CanSwitchProduct(out var why))
            return Result.Fail(ErrorCode.OperationFailed, $"Cannot delete the active product: {why}");

        _products.Delete(name);
        if (wasActive)
        {
            _products.SetActive(null);
            _positions = null;
            _programs = null;
            _recipes = null;
            _decks = null;
            _dut = null;
            SetActiveSerial(null);
            ProductChanged?.Invoke(null);
        }

        Log(LogLevel.Info, $"Product deleted: {name}");
        return Result.Ok();
    }

    public Result LoadProduct(string name)
    {
        if (!_products.Exists(name))
            return Result.Fail(ErrorCode.FileNotFound, $"Product '{name}' not found.");
        if (!CanSwitchProduct(out var why))
            return Result.Fail(ErrorCode.OperationFailed, $"Cannot switch product: {why}");

        _products.SetActive(name);
        var dir = _products.ProductDir(name);
        _positions = new PositionStore(dir);
        _programs = new ProgramStore(dir);
        _recipes = new RecipeStore(dir);
        _decks = new DeckStore(dir);
        _dut = new DutStore(dir, name);
        SetActiveSerial(null);   // a serial belongs to a product — drop it on switch
        Log(LogLevel.Info, $"Product loaded: {name}");
        ProductChanged?.Invoke(name);
        return Result.Ok();
    }

    public void UnloadProduct()
    {
        if (!CanSwitchProduct(out var why))
        {
            Log(LogLevel.Warning, $"Product not unloaded: {why}");
            return;
        }
        _products.SetActive(null);
        _positions = null;
        _programs = null;
        _recipes = null;
        _decks = null;
        _dut = null;
        SetActiveSerial(null);
        Log(LogLevel.Info, "Product unloaded.");
        ProductChanged?.Invoke(null);
    }

    /// <summary>
    /// Guards a product switch: refused while a program / recipe / deck is running
    /// or an interactive DUT run is open, since either would split a unit's data
    /// across two products.
    /// </summary>
    private bool CanSwitchProduct(out string reason)
    {
        if (_pause != null) { reason = "a process is running."; return false; }
        if (_interactiveDut != null) { reason = "a DUT run is open."; return false; }
        reason = string.Empty;
        return true;
    }

    // --- Teach points / named positions ---

    public IReadOnlyList<string> GetPositionNames(string device)
        => _positions?.GetNames(device) ?? Array.Empty<string>();

    public NamedPosition? GetPosition(string device, string name)
        => _positions?.Get(device, name);

    public Result RemovePosition(string device, string name)
    {
        if (_positions == null)
            return Result.Fail(ErrorCode.OperationFailed, "No active product — load one first.");
        if (!_positions.Remove(device, name))
            return Result.Fail(ErrorCode.FileNotFound, $"Position '{name}' not found for '{device}'.");

        Log(LogLevel.Info, $"Position removed: {device}.{name}");
        return Result.Ok();
    }

    public Result SetPosition(string device, string name, MotionPosition position, double velocity)
    {
        if (_positions == null)
            return Result.Fail(ErrorCode.OperationFailed, "No active product — load one first.");
        if (string.IsNullOrWhiteSpace(name))
            return Result.Fail(ErrorCode.InvalidParameter, "Position name is required.");

        _positions.Set(device, name, position, velocity);
        Log(LogLevel.Info, $"Position set (manual): {device}.{name} (velocity {velocity})");
        return Result.Ok();
    }

    public Result SetPositionFull(string device, string name, MotionPosition position, double velocity,
        string? camera, double? exposureUs)
    {
        if (_positions == null)
            return Result.Fail(ErrorCode.OperationFailed, "No active product — load one first.");
        if (string.IsNullOrWhiteSpace(name))
            return Result.Fail(ErrorCode.InvalidParameter, "Position name is required.");

        _positions.SetFull(device, name, position, velocity, camera, exposureUs);
        Log(LogLevel.Info,
            $"Position set: {device}.{name} (velocity {velocity}" +
            (string.IsNullOrWhiteSpace(camera) || exposureUs == null ? ")" : $", {camera} @ {exposureUs}µs)"));
        return Result.Ok();
    }

    public async Task<Result> TeachPositionAsync(string device, string name, double velocity)
    {
        if (_positions == null)
            return Result.Fail(ErrorCode.OperationFailed, "No active product — load one first.");
        if (string.IsNullOrWhiteSpace(name))
            return Result.Fail(ErrorCode.InvalidParameter, "Position name is required.");

        var read = await _engine.Motion.GetPositionAsync(device).ConfigureAwait(false);
        if (!read.IsSuccess)
            return Result.Fail(read.Error, $"Teach '{name}': {read.Message}");

        _positions.Set(device, name, read.Value, velocity);
        Log(LogLevel.Info, $"Position taught: {device}.{name} (velocity {velocity})");
        return Result.Ok();
    }

    public Task<Result> MoveToNamedPositionAsync(string device, string name, CancellationToken ct = default)
        => MoveToNamedPositionCoreAsync(device, name, new MoveOverrides(), ct);

    public Task<Result> MoveToNamedPositionAsync(string device, string name, double velocityOverride, CancellationToken ct = default)
        => MoveToNamedPositionCoreAsync(device, name, new MoveOverrides(velocityOverride), ct);

    /// <summary>
    /// Recalls a named position, applying the step's <see cref="MoveOverrides"/> at the
    /// start of the move: a velocity / camera / exposure given on the step takes
    /// precedence over the position's stored values; anything left default falls back
    /// to the position's. The camera exposure is applied while the stage is already
    /// travelling. This is the back-end for the move ops' velocity / camera fields.
    /// </summary>
    private async Task<Result> MoveToNamedPositionCoreAsync(
        string device, string name, MoveOverrides overrides, CancellationToken ct)
    {
        if (_positions == null)
            return Result.Fail(ErrorCode.OperationFailed, "No active product — load one first.");

        var np = _positions.Get(device, name);
        if (np == null)
            return Result.Fail(ErrorCode.FileNotFound, $"Position '{name}' not found for '{device}'.");

        // 1) Apply velocity BEFORE moving — the step override wins over the position's
        //    stored velocity; 0 leaves the device's current velocity unchanged.
        var velocity = MotionVelocityLimits.Clamp(
            overrides.Velocity > 0 ? overrides.Velocity : np.Velocity,
            _engine.Motion.GetDevice(device)?.ControllerType);
        if (velocity > 0)
        {
            var vel = await _engine.Motion.SetDeviceVelocityAsync(device, velocity).ConfigureAwait(false);
            if (!vel.IsSuccess)
                return Result.Fail(vel.Error, $"Move {device} -> '{name}': velocity — {vel.Message}");
            Log(LogLevel.Info, $"Velocity set: {device} = {velocity}");
        }

        // 2) Start the move (do not await yet) so the camera exposure can be applied
        //    AFTER the move has started — i.e. while the stage travels.
        Log(LogLevel.Info, $"Move {device} -> '{name}'");
        var moveTask = _engine.Motion.MoveToPositionAsync(device, np.Coordinates, ct);

        // 3) While moving, apply the camera exposure — step override wins, else the
        //    position's pinned camera / exposure. Never fails the move.
        var camera = string.IsNullOrWhiteSpace(overrides.Camera) ? np.Camera : overrides.Camera;
        var exposure = overrides.ExposureUs ?? np.ExposureUs;
        await MoveExposure.ApplyAsync(_engine.Camera, camera, exposure, Log).ConfigureAwait(false);

        // 4) Wait for the move to settle and return its result.
        return await moveTask.ConfigureAwait(false);
    }

    // --- Process programs ---

    public IReadOnlyList<string> ListPrograms()
        => _programs?.List() ?? Array.Empty<string>();

    public Result<WorkflowProgram> LoadProgram(string name)
    {
        if (_programs == null)
            return Result<WorkflowProgram>.Fail(ErrorCode.OperationFailed, "No active product — load one first.");

        var program = _programs.Load(name);
        return program == null
            ? Result<WorkflowProgram>.Fail(ErrorCode.FileNotFound, $"Program '{name}' not found.")
            : Result<WorkflowProgram>.Ok(program);
    }

    public Result SaveProgram(WorkflowProgram program)
    {
        if (_programs == null)
            return Result.Fail(ErrorCode.OperationFailed, "No active product — load one first.");

        _programs.Save(program);
        Log(LogLevel.Info, $"Program saved: {program.Name}");
        WarnMissingPositions(program);

        // Record an operator-stamped revision for history / traceability.
        try
        {
            var product = _products.ActiveProduct;
            if (product != null)
            {
                var version = _history.Append(product, program, _operator);
                Log(LogLevel.Info,
                    $"History: {program.Name} v{version.Version}" +
                    (string.IsNullOrEmpty(version.OperatorName) ? "" : $" by {version.OperatorName}"));
            }
        }
        catch (Exception ex)
        {
            Log(LogLevel.Warning, $"Program history not recorded for '{program.Name}': {ex.Message}");
        }
        return Result.Ok();
    }

    public ProgramHistory? GetProgramHistory(string name)
    {
        var product = _products.ActiveProduct;
        return product == null ? null : _history.Load(product, name);
    }

    public IReadOnlyList<HistoryRow> QueryHistory(HistoryQuery query) => _history.Query(query);

    /// <summary>
    /// Logs (non-fatally) any taught-position reference in the program that does not
    /// resolve in the active product. A program is the product's process and is
    /// inseparable from the positions it moves to — they live in the same product
    /// and travel together on switch, so a dangling reference is worth surfacing.
    /// Mirrors ConfigTool's cross-reference checks. Expression-valued names
    /// (containing <c>{</c>) are skipped — they resolve at run time.
    /// </summary>
    private void WarnMissingPositions(WorkflowProgram program)
    {
        if (_positions == null) return;

        void Check(string device, string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Contains('{')) return;
            if (_positions!.Get(device, name) == null)
                Log(LogLevel.Warning,
                    $"Program '{program.Name}' references position '{name}' on '{device}' " +
                    $"not taught in product '{ActiveProduct}'.");
        }

        void Walk(IEnumerable<WorkflowStep> steps)
        {
            foreach (var step in steps)
            {
                if (!step.Enabled) continue;
                switch (step.Op.ToLowerInvariant())
                {
                    case "motion.movetoposition":
                        Check(step.Device, step.Parameters.GetValueOrDefault("position", string.Empty));
                        break;
                    case "motion.parallelmove":
                        for (int i = 1; step.Parameters.TryGetValue($"device{i}", out var dev); i++)
                            Check(dev, step.Parameters.GetValueOrDefault($"position{i}", string.Empty));
                        break;
                    case "flow.parallel":
                        foreach (var branch in step.Branches)
                            Walk(branch.Steps);   // recurse into concurrent branches
                        break;
                }
            }
        }

        Walk(program.Steps);
    }

    public async Task<Result> RunAsync(
        WorkflowProgram program, string? serial = null, CancellationToken ct = default)
    {
        var pause = new PauseController();
        pause.PausedChanged += OnPausedChanged;
        _pause = pause;

        var ctx = new WorkflowContext();

        var runner = new WorkflowRunner(
            _engine,
            MoveToNamedPositionCoreAsync,
            name => _programs?.Load(name),
            (device, name) => _positions?.Get(device, name),
            SaveNamedPosition,
            _deviceLinks,
            OperatorPrompt,
            // dut.promptSerial opens the DUT record mid-run into _runDut, which the
            // finally block persists with the run's verdict.
            (s, c) => SetRunDutSerial(s, program.Name, c),
            pause,
            e => StepEvent?.Invoke(e),
            Log);

        _runDut = BeginDut(serial, program.Name, ctx);

        Log(LogLevel.Info, $"Program started: {program.Name}" + (_runDut != null ? $" [DUT {_runDut.Value.Record.Serial}]" : ""));
        var verdict = DutVerdict.Fail;
        try
        {
            var result = await runner.RunAsync(program, ctx, ct).ConfigureAwait(false);
            verdict = result.IsSuccess ? DutVerdict.Pass : DutVerdict.Fail;
            var summary = $"Program {(result.IsSuccess ? "completed" : "failed")}: {program.Name}";
            if (!string.IsNullOrEmpty(result.Message))
                summary += $" — {result.Message}";
            Log(result.IsSuccess ? LogLevel.Info : LogLevel.Error, summary);
            return result;
        }
        catch (OperationCanceledException)
        {
            verdict = DutVerdict.Aborted;
            Log(LogLevel.Warning, $"Program cancelled: {program.Name}");
            return Result.Fail(ErrorCode.OperationFailed, "Program cancelled.");
        }
        finally
        {
            pause.PausedChanged -= OnPausedChanged;
            _pause = null;
            EndDut(_runDut, verdict);
            _runDut = null;
        }
    }

    /// <summary>
    /// Back-end for the <c>dut.promptSerial</c> step: opens the DUT record for a
    /// serial entered mid-run and attaches it to both the run context and
    /// <see cref="_runDut"/> (so the run's verdict is persisted against it).
    /// Refused if no product is active, the serial is blank, or a serial is
    /// already set for this run.
    /// </summary>
    private Result SetRunDutSerial(string serial, string source, WorkflowContext ctx)
    {
        if (_dut == null)
            return Result.Fail(ErrorCode.OperationFailed, "No active product — cannot set DUT serial.");
        if (string.IsNullOrWhiteSpace(serial))
            return Result.Fail(ErrorCode.InvalidParameter, "Serial number is required.");
        if (ctx.Dut != null)
            return Result.Fail(ErrorCode.OperationFailed, "A DUT serial is already set for this run.");

        var begun = BeginDut(serial, source, ctx);   // creates the run, sets ctx.Dut
        if (begun == null)
            return Result.Fail(ErrorCode.OperationFailed, "Could not open the DUT record.");
        _runDut = begun;
        return Result.Ok();
    }

    private void OnPausedChanged(bool paused) => PausedChanged?.Invoke(paused);

    /// <summary>
    /// Writes a named position into the active product — the back-end for the
    /// <c>motion.updatePosition</c> / <c>updatePositionAxis</c> steps.
    /// </summary>
    private Result SaveNamedPosition(string device, string name, MotionPosition coordinates, double velocity)
    {
        if (_positions == null)
            return Result.Fail(ErrorCode.OperationFailed, "No active product — cannot update positions.");
        _positions.Set(device, name, coordinates, velocity);
        return Result.Ok();
    }

    // --- DUT test records ---

    public IReadOnlyList<string> ListDutRecords() => _dut?.List() ?? Array.Empty<string>();

    public DutRecord? GetDutRecord(string serial)
    {
        if (_dut == null || string.IsNullOrWhiteSpace(serial)) return null;
        // Only return a record that actually exists on disk — never materialise a
        // phantom for a serial that was never run.
        return _dut.List().Contains(serial.Trim(), StringComparer.OrdinalIgnoreCase)
            ? _dut.LoadOrCreate(serial.Trim())
            : null;
    }

    // --- Operator identity (traceability) ---

    public OperatorContext? Operator => _operator;

    public void SetOperator(OperatorContext? op)
    {
        _operator = op;
        Log(LogLevel.Info, op == null
            ? "Operator cleared."
            : $"Operator set: {op.DisplayName} ({op.UserId}) @ station {op.StationId}.");
    }

    // --- Interactive DUT runs (operator-driven, no WorkflowProgram) ---

    public bool HasActiveDutRun => _interactiveDut != null;

    public Result BeginDutRun(string serial, string source)
    {
        if (_dut == null)
            return Result.Fail(ErrorCode.OperationFailed, "No active product — load one first.");
        if (_interactiveDut != null)
            return Result.Fail(ErrorCode.OperationFailed,
                $"A DUT run is already open for '{_interactiveDut.Value.Record.Serial}'.");
        if (string.IsNullOrWhiteSpace(serial))
            return Result.Fail(ErrorCode.InvalidParameter, "Serial number is required.");

        var record = _dut.LoadOrCreate(serial.Trim());
        var run = new DutRun { StartedUtc = DateTime.UtcNow, Source = source };
        StampOperator(run);
        _interactiveDut = (record, run);
        SetActiveSerial(record.Serial);
        Log(LogLevel.Info, $"DUT run started: {record.Serial} [{source}]" +
            (_operator != null ? $" by {_operator.DisplayName}" : ""));
        return Result.Ok();
    }

    public void RecordDutValue(string key, string value, DutValueType type = DutValueType.String)
    {
        if (_interactiveDut == null || string.IsNullOrWhiteSpace(key)) return;
        _interactiveDut.Value.Run.Entries.Add(new DutEntry(DateTime.UtcNow, key, value, type));
    }

    public Result EndDutRun(DutVerdict verdict)
    {
        if (_interactiveDut == null)
            return Result.Fail(ErrorCode.OperationFailed, "No DUT run is open.");

        var (record, run) = _interactiveDut.Value;
        run.EndedUtc = DateTime.UtcNow;
        run.Verdict = verdict;
        record.Runs.Add(run);
        _interactiveDut = null;

        if (_dut == null)
        {
            Log(LogLevel.Warning, $"DUT record for {record.Serial} not saved — product unloaded mid-run.");
            return Result.Fail(ErrorCode.OperationFailed, "Product unloaded mid-run.");
        }
        try
        {
            _dut.Save(record);
            Log(LogLevel.Info,
                $"DUT record saved: {record.Serial} — run {verdict}, {run.Entries.Count} entry(ies).");
            return Result.Ok();
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"DUT record save failed for {record.Serial}: {ex.Message}");
            return Result.Fail(ErrorCode.OperationFailed, ex.Message);
        }
    }

    /// <summary>
    /// Opens (or creates) the DUT record for a run and attaches a fresh
    /// <see cref="DutRun"/> to <paramref name="ctx"/>. Returns null when no
    /// serial was given or no product is active — the run then records nothing.
    /// </summary>
    private (DutRecord Record, DutRun Run)? BeginDut(string? serial, string source, WorkflowContext ctx)
    {
        if (string.IsNullOrWhiteSpace(serial) || _dut == null)
            return null;

        var record = _dut.LoadOrCreate(serial.Trim());
        var run = new DutRun { StartedUtc = DateTime.UtcNow, Source = source };
        StampOperator(run);
        ctx.Dut = run;
        SetActiveSerial(record.Serial);
        return (record, run);
    }

    /// <summary>Copies the current operator + station onto a DUT run for traceability.</summary>
    private void StampOperator(DutRun run)
    {
        if (_operator == null) return;
        run.OperatorId = _operator.UserId;
        run.OperatorName = _operator.DisplayName;
        run.StationId = _operator.StationId;
    }

    /// <summary>Finalises a DUT run with its verdict and persists the record.</summary>
    private void EndDut((DutRecord Record, DutRun Run)? dut, DutVerdict verdict)
    {
        if (dut == null) return;

        var (record, run) = dut.Value;
        run.EndedUtc = DateTime.UtcNow;
        run.Verdict = verdict;
        record.Runs.Add(run);

        if (_dut == null)
        {
            Log(LogLevel.Warning, $"DUT record for {record.Serial} not saved — product unloaded mid-run.");
            return;
        }
        try
        {
            _dut.Save(record);
            Log(LogLevel.Info,
                $"DUT record saved: {record.Serial} — run {verdict}, {run.Entries.Count} entry(ies).");
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"DUT record save failed for {record.Serial}: {ex.Message}");
        }
    }

    // --- U correction ---

    public async Task<Result<UCorrectionResult>> RunUCorrectionAsync(
        UCorrectionOptions options, CancellationToken ct = default)
    {
        if (_positions == null)
            return Result<UCorrectionResult>.Fail(ErrorCode.OperationFailed, "No active product — load one first.");

        // Mirror WorkflowRunner's wiring: the routine owns no stores — it gets the
        // recall-move + position read/save delegates from the service.
        var routine = new UCorrectionRoutine(
            _engine,
            MoveToNamedPositionCoreAsync,
            (device, name) => _positions?.Get(device, name),
            SaveNamedPosition,
            OperatorPrompt,
            Log);

        Log(LogLevel.Info, $"U correction started: gantry '{options.GantryDevice}', hexapod '{options.HexapodDevice}'.");
        return await routine.RunAsync(options, ct).ConfigureAwait(false);
    }

    // --- Recipes ---

    public IReadOnlyList<string> ListRecipes()
        => _recipes?.List() ?? Array.Empty<string>();

    public Result<RecipeProgram> LoadRecipe(string name)
    {
        if (_recipes == null)
            return Result<RecipeProgram>.Fail(ErrorCode.OperationFailed, "No active product — load one first.");

        var source = _recipes.Load(name);
        return source == null
            ? Result<RecipeProgram>.Fail(ErrorCode.FileNotFound, $"Recipe '{name}' not found.")
            : Result<RecipeProgram>.Ok(RecipeParser.Parse(name, source));
    }

    public Result SaveRecipe(string name, string source)
    {
        if (_recipes == null)
            return Result.Fail(ErrorCode.OperationFailed, "No active product — load one first.");
        if (string.IsNullOrWhiteSpace(name))
            return Result.Fail(ErrorCode.InvalidParameter, "Recipe name is required.");

        _recipes.Save(name, source);
        Log(LogLevel.Info, $"Recipe saved: {name}");
        return Result.Ok();
    }

    public async Task<Result> RunRecipeAsync(string name, CancellationToken ct = default)
    {
        if (_recipes == null)
            return Result.Fail(ErrorCode.OperationFailed, "No active product — load one first.");

        var source = _recipes.Load(name);
        if (source == null)
            return Result.Fail(ErrorCode.FileNotFound, $"Recipe '{name}' not found.");

        var program = RecipeParser.Parse(name, source);
        if (program.HasErrors)
        {
            foreach (var error in program.Errors)
                Log(LogLevel.Error, $"Recipe '{name}' parse error — {error}");
            return Result.Fail(ErrorCode.InvalidParameter,
                $"Recipe '{name}' has {program.Errors.Count} parse error(s).");
        }

        var pause = new PauseController();
        pause.PausedChanged += OnPausedChanged;
        _pause = pause;

        var registry = new RecipeMethodRegistry(_engine, MoveToNamedPositionCoreAsync, OperatorPrompt, Log);
        var interpreter = new RecipeInterpreter(registry, pause, Log);

        Log(LogLevel.Info, $"Recipe started: {name}");
        try
        {
            var result = await interpreter.RunAsync(program, ct).ConfigureAwait(false);
            var summary = $"Recipe {(result.IsSuccess ? "completed" : "failed")}: {name}";
            if (!string.IsNullOrEmpty(result.Message))
                summary += $" — {result.Message}";
            Log(result.IsSuccess ? LogLevel.Info : LogLevel.Error, summary);
            return result;
        }
        catch (OperationCanceledException)
        {
            Log(LogLevel.Warning, $"Recipe cancelled: {name}");
            return Result.Fail(ErrorCode.OperationFailed, "Recipe cancelled.");
        }
        finally
        {
            pause.PausedChanged -= OnPausedChanged;
            _pause = null;
        }
    }

    // --- Decks ---

    public IReadOnlyList<string> ListDecks()
        => _decks?.List() ?? Array.Empty<string>();

    public string? ActiveDeck => _decks?.ActiveDeck;

    public Result<WorkflowDeck> LoadDeck(string name)
    {
        if (_decks == null)
            return Result<WorkflowDeck>.Fail(ErrorCode.OperationFailed, "No active product — load one first.");

        var deck = _decks.Load(name);
        if (deck == null)
            return Result<WorkflowDeck>.Fail(ErrorCode.FileNotFound, $"Deck '{name}' not found.");

        _decks.SetActive(name);   // loading a deck makes it the auto-loaded one
        return Result<WorkflowDeck>.Ok(deck);
    }

    public Result SaveDeck(WorkflowDeck deck)
    {
        if (_decks == null)
            return Result.Fail(ErrorCode.OperationFailed, "No active product — load one first.");
        if (string.IsNullOrWhiteSpace(deck.Name))
            return Result.Fail(ErrorCode.InvalidParameter, "Deck name is required.");

        _decks.Save(deck);
        _decks.SetActive(deck.Name);
        Log(LogLevel.Info, $"Deck saved: {deck.Name}");
        return Result.Ok();
    }

    public async Task<Result> RunDeck(
        WorkflowDeck deck, string? serial = null, CancellationToken ct = default)
    {
        if (_programs == null)
            return Result.Fail(ErrorCode.OperationFailed, "No active product — load one first.");

        var pause = new PauseController();
        pause.PausedChanged += OnPausedChanged;
        _pause = pause;

        // One context for the whole deck — also carries the DUT run, so every
        // program in the deck records into the same per-unit run.
        var ctx = new WorkflowContext();

        // One runner for the whole deck — every program runs through it, so
        // pause / step works across the deck just as it does within a program.
        var runner = new WorkflowRunner(
            _engine,
            MoveToNamedPositionCoreAsync,
            name => _programs?.Load(name),
            (device, name) => _positions?.Get(device, name),
            SaveNamedPosition,
            _deviceLinks,
            OperatorPrompt,
            (s, c) => SetRunDutSerial(s, deck.Name, c),
            pause,
            e => StepEvent?.Invoke(e),
            Log);

        var deckRunner = new DeckRunner(
            loadProgram: name => _programs.Load(name),
            runProgram: (program, context, token) => runner.RunAsync(program, context, token),
            onProgress: p => DeckStepEvent?.Invoke(p),
            log: Log);

        _runDut = BeginDut(serial, deck.Name, ctx);

        Log(LogLevel.Info, $"Deck started: {deck.Name}" + (_runDut != null ? $" [DUT {_runDut.Value.Record.Serial}]" : ""));
        var verdict = DutVerdict.Fail;
        try
        {
            var result = await deckRunner.RunAsync(deck, ctx, ct).ConfigureAwait(false);
            verdict = result.IsSuccess ? DutVerdict.Pass : DutVerdict.Fail;
            var summary = $"Deck {(result.IsSuccess ? "completed" : "stopped")}: {deck.Name}";
            if (!string.IsNullOrEmpty(result.Message))
                summary += $" — {result.Message}";
            Log(result.IsSuccess ? LogLevel.Info : LogLevel.Error, summary);
            return result;
        }
        catch (OperationCanceledException)
        {
            verdict = DutVerdict.Aborted;
            Log(LogLevel.Warning, $"Deck cancelled: {deck.Name}");
            return Result.Fail(ErrorCode.OperationFailed, "Deck cancelled.");
        }
        finally
        {
            pause.PausedChanged -= OnPausedChanged;
            _pause = null;
            EndDut(_runDut, verdict);
            _runDut = null;
        }
    }

    private void Log(LogLevel level, string message)
        => LogEmitted?.Invoke(new LogEntry(level, DateTimeOffset.Now, "Workflow", message));
}
