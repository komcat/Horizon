using Horizon.Common;
using Horizon.Engine;
using Horizon.Workflow.Recipe;

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
    private ProgramHistoryStore? _history;   // null when no product is active
    private readonly DeviceLinkStore _deviceLinks = new();   // machine-global, not per-product
    private PauseController? _pause;     // non-null only while a program runs
    private OperatorContext? _operator;  // current operator, stamped onto DUT runs
    private (DutRecord Record, DutRun Run)? _interactiveDut;   // open operator-driven DUT run

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
            _history = new ProgramHistoryStore(dir);
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
            _history = null;
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
        _history = new ProgramHistoryStore(dir);
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
        _history = null;
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

    public async Task<Result> MoveToNamedPositionAsync(string device, string name, CancellationToken ct = default)
    {
        if (_positions == null)
            return Result.Fail(ErrorCode.OperationFailed, "No active product — load one first.");

        var np = _positions.Get(device, name);
        if (np == null)
            return Result.Fail(ErrorCode.FileNotFound, $"Position '{name}' not found for '{device}'.");

        // 1) Apply the position's velocity BEFORE moving (0 = leave unchanged).
        if (np.Velocity > 0)
        {
            var vel = await _engine.Motion.SetDeviceVelocityAsync(device, np.Velocity).ConfigureAwait(false);
            if (!vel.IsSuccess)
                return Result.Fail(vel.Error, $"Move {device} -> '{name}': velocity — {vel.Message}");
            Log(LogLevel.Info, $"Velocity set: {device} = {np.Velocity}");
        }

        // 2) Start the move (do not await yet) so the camera exposure can be
        //    applied AFTER the move has started — i.e. while the stage travels.
        Log(LogLevel.Info, $"Move {device} -> '{name}'");
        var moveTask = _engine.Motion.MoveToPositionAsync(device, np.Coordinates, ct);

        // 3) While moving, retune the camera exposure (leaving gain / frame rate
        //    as-is). A camera failure is logged but never fails the move — and is
        //    caught so the in-flight move is always awaited below.
        if (!string.IsNullOrWhiteSpace(np.Camera) && np.ExposureUs is > 0)
        {
            try
            {
                // A position that pins an exposure means "use exactly this" — so
                // auto-exposure must be turned off, otherwise the camera keeps
                // auto-adjusting and the pinned value is ignored (the adapter does
                // not write a manual exposure while AutoExposure is on).
                var current = _engine.Camera.GetState(np.Camera).Exposure;
                var applied = await _engine.Camera
                    .ApplyExposureAsync(np.Camera, current with { ExposureUs = np.ExposureUs.Value, AutoExposure = false })
                    .ConfigureAwait(false);
                Log(applied.IsSuccess ? LogLevel.Info : LogLevel.Warning,
                    $"Exposure {np.Camera} = {np.ExposureUs}µs: {(applied.IsSuccess ? "ok" : applied.Message)}");
            }
            catch (Exception ex)
            {
                Log(LogLevel.Warning, $"Exposure {np.Camera} = {np.ExposureUs}µs: {ex.Message}");
            }
        }

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
            var version = _history?.Append(program, _operator);
            if (version != null)
                Log(LogLevel.Info,
                    $"History: {program.Name} v{version.Version}" +
                    (string.IsNullOrEmpty(version.OperatorName) ? "" : $" by {version.OperatorName}"));
        }
        catch (Exception ex)
        {
            Log(LogLevel.Warning, $"Program history not recorded for '{program.Name}': {ex.Message}");
        }
        return Result.Ok();
    }

    public ProgramHistory? GetProgramHistory(string name)
        => _history?.Load(name);

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

        var runner = new WorkflowRunner(
            _engine,
            MoveToNamedPositionAsync,
            name => _programs?.Load(name),
            (device, name) => _positions?.Get(device, name),
            SaveNamedPosition,
            _deviceLinks,
            OperatorPrompt,
            pause,
            e => StepEvent?.Invoke(e),
            Log);

        var ctx = new WorkflowContext();
        var dut = BeginDut(serial, program.Name, ctx);

        Log(LogLevel.Info, $"Program started: {program.Name}" + (dut != null ? $" [DUT {dut.Value.Record.Serial}]" : ""));
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
            EndDut(dut, verdict);
        }
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

        var registry = new RecipeMethodRegistry(_engine, MoveToNamedPositionAsync, OperatorPrompt, Log);
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

        // One runner for the whole deck — every program runs through it, so
        // pause / step works across the deck just as it does within a program.
        var runner = new WorkflowRunner(
            _engine,
            MoveToNamedPositionAsync,
            name => _programs?.Load(name),
            (device, name) => _positions?.Get(device, name),
            SaveNamedPosition,
            _deviceLinks,
            OperatorPrompt,
            pause,
            e => StepEvent?.Invoke(e),
            Log);

        var deckRunner = new DeckRunner(
            loadProgram: name => _programs.Load(name),
            runProgram: (program, context, token) => runner.RunAsync(program, context, token),
            onProgress: p => DeckStepEvent?.Invoke(p),
            log: Log);

        // One context for the whole deck — also carries the DUT run, so every
        // program in the deck records into the same per-unit run.
        var ctx = new WorkflowContext();
        var dut = BeginDut(serial, deck.Name, ctx);

        Log(LogLevel.Info, $"Deck started: {deck.Name}" + (dut != null ? $" [DUT {dut.Value.Record.Serial}]" : ""));
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
            EndDut(dut, verdict);
        }
    }

    private void Log(LogLevel level, string message)
        => LogEmitted?.Invoke(new LogEntry(level, DateTimeOffset.Now, "Workflow", message));
}
