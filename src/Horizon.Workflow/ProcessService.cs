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
    private PauseController? _pause;     // non-null only while a program runs

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
        }
    }

    public IReadOnlyList<string> Products => _products.GetProductNames();

    public string? ActiveProduct => _products.ActiveProduct;

    public event Action<WorkflowStepEvent>? StepEvent;
    public event Action<DeckProgress>? DeckStepEvent;
    public event Action<LogEntry>? LogEmitted;
    public event Action<bool>? PausedChanged;

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
        _products.Delete(name);
        if (wasActive)
        {
            _products.SetActive(null);
            _positions = null;
            _programs = null;
            _recipes = null;
            _decks = null;
        }

        Log(LogLevel.Info, $"Product deleted: {name}");
        return Result.Ok();
    }

    public Result LoadProduct(string name)
    {
        if (!_products.Exists(name))
            return Result.Fail(ErrorCode.FileNotFound, $"Product '{name}' not found.");

        _products.SetActive(name);
        var dir = _products.ProductDir(name);
        _positions = new PositionStore(dir);
        _programs = new ProgramStore(dir);
        _recipes = new RecipeStore(dir);
        _decks = new DeckStore(dir);
        Log(LogLevel.Info, $"Product loaded: {name}");
        return Result.Ok();
    }

    public void UnloadProduct()
    {
        _products.SetActive(null);
        _positions = null;
        _programs = null;
        _recipes = null;
        _decks = null;
        Log(LogLevel.Info, "Product unloaded.");
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

        // Apply the position's velocity before moving (0 = leave unchanged).
        if (np.Velocity > 0)
        {
            var vel = await _engine.Motion.SetDeviceVelocityAsync(device, np.Velocity).ConfigureAwait(false);
            if (!vel.IsSuccess)
                return Result.Fail(vel.Error, $"Move {device} -> '{name}': velocity — {vel.Message}");
            Log(LogLevel.Info, $"Velocity set: {device} = {np.Velocity}");
        }

        Log(LogLevel.Info, $"Move {device} -> '{name}'");
        return await _engine.Motion.MoveToPositionAsync(device, np.Coordinates, ct).ConfigureAwait(false);
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
        return Result.Ok();
    }

    public async Task<Result> RunAsync(WorkflowProgram program, CancellationToken ct = default)
    {
        var pause = new PauseController();
        pause.PausedChanged += OnPausedChanged;
        _pause = pause;

        var runner = new WorkflowRunner(
            _engine,
            MoveToNamedPositionAsync,
            OperatorPrompt,
            pause,
            e => StepEvent?.Invoke(e),
            Log);

        Log(LogLevel.Info, $"Program started: {program.Name}");
        try
        {
            var result = await runner.RunAsync(program, ct).ConfigureAwait(false);
            var summary = $"Program {(result.IsSuccess ? "completed" : "failed")}: {program.Name}";
            if (!string.IsNullOrEmpty(result.Message))
                summary += $" — {result.Message}";
            Log(result.IsSuccess ? LogLevel.Info : LogLevel.Error, summary);
            return result;
        }
        catch (OperationCanceledException)
        {
            Log(LogLevel.Warning, $"Program cancelled: {program.Name}");
            return Result.Fail(ErrorCode.OperationFailed, "Program cancelled.");
        }
        finally
        {
            pause.PausedChanged -= OnPausedChanged;
            _pause = null;
        }
    }

    private void OnPausedChanged(bool paused) => PausedChanged?.Invoke(paused);

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

    public Result<WorkflowDeck> LoadDeck(string name)
    {
        if (_decks == null)
            return Result<WorkflowDeck>.Fail(ErrorCode.OperationFailed, "No active product — load one first.");

        var deck = _decks.Load(name);
        return deck == null
            ? Result<WorkflowDeck>.Fail(ErrorCode.FileNotFound, $"Deck '{name}' not found.")
            : Result<WorkflowDeck>.Ok(deck);
    }

    public Result SaveDeck(WorkflowDeck deck)
    {
        if (_decks == null)
            return Result.Fail(ErrorCode.OperationFailed, "No active product — load one first.");
        if (string.IsNullOrWhiteSpace(deck.Name))
            return Result.Fail(ErrorCode.InvalidParameter, "Deck name is required.");

        _decks.Save(deck);
        Log(LogLevel.Info, $"Deck saved: {deck.Name}");
        return Result.Ok();
    }

    public async Task<Result> RunDeck(WorkflowDeck deck, CancellationToken ct = default)
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
            OperatorPrompt,
            pause,
            e => StepEvent?.Invoke(e),
            Log);

        var deckRunner = new DeckRunner(
            loadProgram: name => _programs.Load(name),
            runProgram: (program, context, token) => runner.RunAsync(program, context, token),
            onProgress: p => DeckStepEvent?.Invoke(p),
            log: Log);

        Log(LogLevel.Info, $"Deck started: {deck.Name}");
        try
        {
            var result = await deckRunner.RunAsync(deck, ct).ConfigureAwait(false);
            var summary = $"Deck {(result.IsSuccess ? "completed" : "stopped")}: {deck.Name}";
            if (!string.IsNullOrEmpty(result.Message))
                summary += $" — {result.Message}";
            Log(result.IsSuccess ? LogLevel.Info : LogLevel.Error, summary);
            return result;
        }
        catch (OperationCanceledException)
        {
            Log(LogLevel.Warning, $"Deck cancelled: {deck.Name}");
            return Result.Fail(ErrorCode.OperationFailed, "Deck cancelled.");
        }
        finally
        {
            pause.PausedChanged -= OnPausedChanged;
            _pause = null;
        }
    }

    private void Log(LogLevel level, string message)
        => LogEmitted?.Invoke(new LogEntry(level, DateTimeOffset.Now, "Workflow", message));
}
