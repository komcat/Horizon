using Horizon.Common;
using Horizon.Engine;

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
        }
    }

    public IReadOnlyList<string> Products => _products.GetProductNames();

    public string? ActiveProduct => _products.ActiveProduct;

    public event Action<WorkflowStepEvent>? StepEvent;
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
        Log(LogLevel.Info, $"Product loaded: {name}");
        return Result.Ok();
    }

    public void UnloadProduct()
    {
        _products.SetActive(null);
        _positions = null;
        _programs = null;
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

    private void Log(LogLevel level, string message)
        => LogEmitted?.Invoke(new LogEntry(level, DateTimeOffset.Now, "Workflow", message));
}
