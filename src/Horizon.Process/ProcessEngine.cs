using Horizon.Process.Handlers;
using Horizon.Process.Interfaces;
using Horizon.Process.Models;

namespace Horizon.Process;

public sealed class ProcessEngine
{
    private readonly Dictionary<string, (object? Manager, string ManagerType)> _deviceRegistry = new(StringComparer.OrdinalIgnoreCase);
    private readonly CommandDispatcher _dispatcher = new();

    public Action<int, string, string>? OnStepStarted;
    public Action<int, bool>? OnStepCompleted;
    public Action<int, int, int>? OnLoopIteration; // (stepIndex, current 1-based, total 0=infinite)

    // Execution lock — only one caller (ProcessPanel or RunPanel) at a time
    private int _executionLock;

    /// <summary>True when ExecuteAsync is actively running.</summary>
    public bool IsRunning => _executionLock != 0;

    /// <summary>Name of the current owner (for diagnostics/UI).</summary>
    public string? RunningOwner { get; private set; }

    /// <summary>
    /// Attempt to acquire exclusive execution rights.
    /// Returns true if acquired, false if another caller already holds the lock.
    /// </summary>
    public bool TryAcquire(string owner)
    {
        if (Interlocked.CompareExchange(ref _executionLock, 1, 0) != 0)
            return false;
        RunningOwner = owner;
        return true;
    }

    /// <summary>Release execution rights.</summary>
    public void Release()
    {
        RunningOwner = null;
        Interlocked.Exchange(ref _executionLock, 0);
    }

    // Step-by-step mode
    private ManualResetEventSlim? _stepGate;
    public bool StepByStepMode { get; set; }
    public HashSet<int> Breakpoints { get; } = new();

    public ProcessEngine()
    {
        // Register built-in handlers
        _dispatcher.RegisterHandler(new MotionCommandHandler());
        _dispatcher.RegisterHandler(new IOCommandHandler());
        _dispatcher.RegisterHandler(new PneumaticCommandHandler());
        _dispatcher.RegisterHandler(new CameraCommandHandler());
        _dispatcher.RegisterHandler(new SystemCommandHandler());
        _dispatcher.RegisterHandler(new ContextCommandHandler());
        _dispatcher.RegisterHandler(new CurrentMeasurementCommandHandler());
        _dispatcher.RegisterHandler(new LaserControllerCommandHandler());
    }

    public void RegisterHandler(ICommandHandler handler)
    {
        _dispatcher.RegisterHandler(handler);
    }

    public void RegisterDevice(string deviceName, object? manager, string managerType)
    {
        _deviceRegistry[deviceName] = (manager, managerType);
    }

    /// <summary>
    /// Registers all devices from an AppContext-like object using reflection.
    /// Looks for MotionManager, IOManager, PneumaticManager, CameraManager properties.
    /// </summary>
    public void BuildDeviceRegistry(object context)
    {
        var contextType = context.GetType();

        // Register motion devices
        var motionMgr = contextType.GetProperty("MotionManager")?.GetValue(context);
        if (motionMgr != null)
        {
            var getNames = motionMgr.GetType().GetMethod("GetAllDeviceNames");
            if (getNames?.Invoke(motionMgr, null) is List<string> names)
            {
                foreach (var name in names)
                    RegisterDevice(name, motionMgr, "motion");
            }
        }

        // Register IO devices
        var ioMgr = contextType.GetProperty("IOManager")?.GetValue(context);
        if (ioMgr != null)
        {
            var getNames = ioMgr.GetType().GetMethod("GetAllDeviceNames");
            if (getNames?.Invoke(ioMgr, null) is List<string> names)
            {
                foreach (var name in names)
                    RegisterDevice(name, ioMgr, "io");
            }
        }

        // Register pneumatic slides
        var pneumaticMgr = contextType.GetProperty("PneumaticManager")?.GetValue(context);
        if (pneumaticMgr != null)
        {
            var getNames = pneumaticMgr.GetType().GetMethod("GetAllSlideNames");
            if (getNames?.Invoke(pneumaticMgr, null) is List<string> names)
            {
                foreach (var name in names)
                    RegisterDevice(name, pneumaticMgr, "pneumatic");
            }
        }

        // Register cameras
        var cameraMgr = contextType.GetProperty("CameraManager")?.GetValue(context);
        if (cameraMgr != null)
        {
            var getIds = cameraMgr.GetType().GetMethod("GetCameraIds");
            if (getIds?.Invoke(cameraMgr, null) is List<string> ids)
            {
                foreach (var id in ids)
                    RegisterDevice(id, cameraMgr, "camera");
            }
        }

        // Register current measurement devices
        var cmeasMgr = contextType.GetProperty("CurrentMeasurementManager")?.GetValue(context);
        if (cmeasMgr != null)
        {
            var getNames = cmeasMgr.GetType().GetMethod("GetAllDeviceNames");
            if (getNames?.Invoke(cmeasMgr, null) is List<string> names)
            {
                foreach (var name in names)
                    RegisterDevice(name, cmeasMgr, "currentmeasurement");
            }
        }

        // Register laser controller devices
        var laserMgr = contextType.GetProperty("LaserControllerManager")?.GetValue(context);
        if (laserMgr != null)
        {
            var getNames = laserMgr.GetType().GetMethod("GetAllDeviceNames");
            if (getNames?.Invoke(laserMgr, null) is List<string> names)
            {
                foreach (var name in names)
                    RegisterDevice(name, laserMgr, "lasercontroller");
            }
        }

        // Always register system and context handlers (no device needed)
        RegisterDevice("system", null, "system");
        RegisterDevice("context", null, "context");
    }

    public async Task<bool> ExecuteAsync(List<ProcessStep> steps, ProcessContext context, CancellationToken ct)
    {
        context.StartTime = DateTimeOffset.UtcNow;
        context.CancellationToken = ct;
        context.StepCount = CountSteps(steps);

        _stepGate = new ManualResetEventSlim(false);

        try
        {
            return await Task.Run(() => ExecuteSteps(steps, context, ct, 0), ct);
        }
        finally
        {
            context.EndTime = DateTimeOffset.UtcNow;
            _stepGate.Dispose();
            _stepGate = null;
        }
    }

    /// <summary>
    /// Resume from step-by-step pause.
    /// </summary>
    public void ContinueStep()
    {
        _stepGate?.Set();
    }

    private bool ExecuteSteps(List<ProcessStep> steps, ProcessContext context, CancellationToken ct, int baseIndex)
    {
        int stepIndex = baseIndex;

        foreach (var step in steps)
        {
            if (ct.IsCancellationRequested) return false;

            // Step-by-step / breakpoint check
            if (StepByStepMode || Breakpoints.Contains(stepIndex))
            {
                _stepGate?.Reset();
                _stepGate?.Wait(ct);
            }

            switch (step)
            {
                case ActionStep action:
                {
                    OnStepStarted?.Invoke(stepIndex, action.DeviceName, action.MethodName);

                    bool success = ExecuteAction(action, context, ct);
                    OnStepCompleted?.Invoke(stepIndex, success);

                    if (!success) return false;
                    stepIndex++;
                    break;
                }

                case LoopStep loop:
                {
                    bool infinite = loop.Count <= 0;
                    int iteration = 0;
                    while (infinite || iteration < loop.Count)
                    {
                        if (ct.IsCancellationRequested) return false;
                        context.SetValue("loop.iteration", iteration);
                        OnLoopIteration?.Invoke(stepIndex, iteration + 1, loop.Count);

                        if (!ExecuteSteps(loop.Steps, context, ct, stepIndex))
                            return false;
                        iteration++;
                    }
                    stepIndex += CountSteps(loop.Steps);
                    break;
                }

                case ConditionalStep cond:
                {
                    bool conditionMet = EvaluateCondition(cond, context);
                    var branch = conditionMet ? cond.ThenSteps : cond.ElseSteps;

                    if (branch.Count > 0)
                    {
                        if (!ExecuteSteps(branch, context, ct, stepIndex))
                            return false;
                    }
                    stepIndex += CountSteps(cond.ThenSteps) + CountSteps(cond.ElseSteps);
                    break;
                }

                case StepMarkerStep marker:
                {
                    OnStepStarted?.Invoke(marker.StepIndex, "marker", marker.StepName);
                    stepIndex++;
                    break;
                }
            }
        }

        return true;
    }

    private bool ExecuteAction(ActionStep action, ProcessContext context, CancellationToken ct)
    {
        // Resolve device to manager
        if (!_deviceRegistry.TryGetValue(action.DeviceName, out var reg))
        {
            // Try "system" and "context" as fallback managerType
            var managerType = action.DeviceName.ToLowerInvariant();
            if (managerType is "system" or "context")
            {
                reg = (null, managerType);
            }
            else
            {
                OnStepCompleted?.Invoke(-1, false);
                return false;
            }
        }

        // Execute with timeout
        var timeoutMs = action.Params.TryGetValue("timeout", out var t) && int.TryParse(t, out var ms) ? ms : 60000;

        var task = Task.Run(() =>
            _dispatcher.Dispatch(reg.ManagerType, reg.Manager, action.DeviceName, action.MethodName, action.Params, out _, context),
            ct);

        var delayTask = Task.Delay(timeoutMs, ct);
        var completed = Task.WhenAny(task, delayTask).Result;

        if (completed == delayTask)
            return false; // timeout

        return task.Result;
    }

    private static bool EvaluateCondition(ConditionalStep cond, ProcessContext context)
    {
        return cond.Comparison switch
        {
            ComparisonType.IsTrue => context.GetBoolean(cond.ContextKey),
            ComparisonType.IsFalse => !context.GetBoolean(cond.ContextKey),
            ComparisonType.Exists => context.HasValue(cond.ContextKey),
            ComparisonType.NotExists => !context.HasValue(cond.ContextKey),
            ComparisonType.GreaterThan => context.GetNumeric(cond.ContextKey) > cond.Threshold,
            ComparisonType.LessThan => context.GetNumeric(cond.ContextKey) < cond.Threshold,
            ComparisonType.Equal => Math.Abs(context.GetNumeric(cond.ContextKey) - cond.Threshold) < 0.001f,
            ComparisonType.NotEqual => Math.Abs(context.GetNumeric(cond.ContextKey) - cond.Threshold) >= 0.001f,
            ComparisonType.GreaterOrEqual => context.GetNumeric(cond.ContextKey) >= cond.Threshold,
            ComparisonType.LessOrEqual => context.GetNumeric(cond.ContextKey) <= cond.Threshold,
            _ => false
        };
    }

    private static int CountSteps(List<ProcessStep> steps)
    {
        int count = 0;
        foreach (var step in steps)
        {
            count++;
            if (step is LoopStep loop)
                count += CountSteps(loop.Steps);
            else if (step is ConditionalStep cond)
                count += CountSteps(cond.ThenSteps) + CountSteps(cond.ElseSteps);
        }
        return count;
    }
}
