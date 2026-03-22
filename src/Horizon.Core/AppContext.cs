using Horizon.Camera;
using Horizon.Camera.Models;
using Horizon.Common;
using Horizon.CurrentMeasurement;
using Horizon.IO;
using Horizon.LaserController;
using Horizon.Motion;
using Horizon.Pneumatic;
using Horizon.Motion.Models;
using Horizon.Process;
using Horizon.Process.Recipe;
using Horizon.Safety;

namespace Horizon.Core;

public sealed class AppContext : IAppContext, IDisposable
{
    private CameraManager? _cameraManager;
    private UnifiedMotionManager? _motionManager;
    private EziIOManager? _ioManager;
    private PneumaticManager? _pneumaticManager;
    private CurrentMeasurementManager? _currentMeasurementManager;
    private LaserControllerManager? _laserControllerManager;
    private ProcessEngine? _processEngine;
    private ProcessPluginRegistry? _pluginRegistry;
    private RecipeEngine? _recipeEngine;
    private readonly HardwareExecutionLock _executionLock = new();
    private SafetyEventLog? _safetyEventLog;
    private SafetyWatchManager? _safetyWatchManager;
    private TeachPointManager? _teachPointManager;
    private AxisTransformService? _axisTransformService;

    private readonly CameraConfigStore _configStore = new();
    private readonly MeasurementHub _measurementHub = new();
    private bool _disposed;

    public CameraManager CameraManager =>
        _cameraManager ?? throw new InvalidOperationException("AppContext is not initialized.");

    public UnifiedMotionManager MotionManager =>
        _motionManager ?? throw new InvalidOperationException("AppContext is not initialized.");

    public EziIOManager IOManager =>
        _ioManager ?? throw new InvalidOperationException("AppContext is not initialized.");

    public PneumaticManager PneumaticManager =>
        _pneumaticManager ?? throw new InvalidOperationException("AppContext is not initialized.");

    public CurrentMeasurementManager CurrentMeasurementManager =>
        _currentMeasurementManager ?? throw new InvalidOperationException("AppContext is not initialized.");

    public LaserControllerManager LaserControllerManager =>
        _laserControllerManager ?? throw new InvalidOperationException("AppContext is not initialized.");

    public ProcessEngine ProcessEngine =>
        _processEngine ?? throw new InvalidOperationException("AppContext is not initialized.");

    public ProcessPluginRegistry PluginRegistry =>
        _pluginRegistry ?? throw new InvalidOperationException("AppContext is not initialized.");

    public RecipeEngine RecipeEngine =>
        _recipeEngine ?? throw new InvalidOperationException("AppContext is not initialized.");

    public HardwareExecutionLock ExecutionLock => _executionLock;

    public SafetyWatchManager? SafetyWatchManager => _safetyWatchManager;

    public MeasurementHub MeasurementHub => _measurementHub;

    public TeachPointManager TeachPointManager =>
        _teachPointManager ?? throw new InvalidOperationException("AppContext is not initialized.");

    public AxisTransformService AxisTransformService => _axisTransformService ?? new();

    public CameraConfigStore ConfigStore => _configStore;

    public bool IsInitialized => _cameraManager != null;

    public bool SimulationMode { get; set; }

    public ILogSink? LogSink { get; set; }

    public void Initialize()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AppContext));

        Shutdown();

        var configDir = ConfigLoader.GetConfigDirectory();

        // --- Camera subsystem (existing) ---
        _cameraManager = new CameraManager();
        Log("Camera manager created");

        try
        {
            var pylonCameras = Horizon.Camera.CameraManager.DiscoverPylonCameras();
            Log($"Pylon discovery: {pylonCameras.Count} camera(s)");
            foreach (var cam in pylonCameras)
                LogDebug($"  Pylon: {cam.ModelName} ({cam.SerialNumber})");
        }
        catch (Exception ex)
        {
            LogError($"Pylon discovery error: {ex.Message}");
        }

        try
        {
            var idsCameras = Horizon.Camera.CameraManager.DiscoverIDSCameras();
            Log($"IDS discovery: {idsCameras.Count} camera(s)");
            foreach (var cam in idsCameras)
                LogDebug($"  IDS: {cam.ModelName} ({cam.SerialNumber}) [{cam.Description}]");
        }
        catch (Exception ex)
        {
            LogError($"IDS discovery error: {ex.Message}");
        }

        var registered = _configStore.Load();
        if (registered.Count > 0)
        {
            Log($"Registered cameras: {registered.Count}");
            _cameraManager.DiscoverAndAddRegisteredCameras(registered);
        }
        else
        {
            LogWarn("No registered cameras — use Camera > Config to register cameras.");
        }
        Log($"Total cameras added: {_cameraManager.CameraCount}");

        // --- IO subsystem ---
        _ioManager = new EziIOManager();
        try
        {
            var ioCount = EziIOJsonLoader.LoadDevices(_ioManager, configDir);
            Log($"IO devices loaded: {ioCount}");
        }
        catch (Exception ex)
        {
            LogError($"IO config load error: {ex.Message}");
        }

        // --- Motion subsystem ---
        _motionManager = new UnifiedMotionManager();
        try
        {
            var motionCount = MotionJsonLoader.LoadDevices(_motionManager, configDir);
            Log($"Motion devices loaded: {motionCount}");
        }
        catch (Exception ex)
        {
            LogError($"Motion config load error: {ex.Message}");
        }

        // --- Axis transform service ---
        _axisTransformService = new AxisTransformService();
        try
        {
            var transformCount = _axisTransformService.LoadConfig(configDir);
            Log($"Axis transforms loaded: {transformCount}");
        }
        catch (Exception ex)
        {
            LogError($"Axis transform config load error: {ex.Message}");
        }

        // --- Current Measurement subsystem ---
        _currentMeasurementManager = new CurrentMeasurementManager();
        _currentMeasurementManager.OnStatusUpdated += (deviceName, status) =>
        {
            if (!status.Valid) return;
            var now = DateTimeOffset.UtcNow;
            for (int i = 0; i < status.ChannelReadings.Length; i++)
                _measurementHub.Push($"{deviceName}.CH{i + 1}", status.ChannelReadings[i], now);
        };
        try
        {
            var cmeasCount = CurrentMeasurementJsonLoader.LoadDevices(_currentMeasurementManager, configDir);
            Log($"Current measurement devices loaded: {cmeasCount}");
        }
        catch (Exception ex)
        {
            LogError($"Current measurement config load error: {ex.Message}");
        }

        // --- Laser Controller subsystem ---
        _laserControllerManager = new LaserControllerManager();
        try
        {
            var laserCount = LaserControllerJsonLoader.LoadDevices(_laserControllerManager, configDir);
            Log($"Laser controller devices loaded: {laserCount}");
        }
        catch (Exception ex)
        {
            LogError($"Laser controller config load error: {ex.Message}");
        }

        // --- Pneumatic subsystem (depends on IO) ---
        _pneumaticManager = new PneumaticManager();
        try
        {
            var pneumaticCount = PneumaticJsonLoader.LoadSlides(_pneumaticManager, _ioManager, configDir);
            Log($"Pneumatic slides loaded: {pneumaticCount}");
        }
        catch (Exception ex)
        {
            LogError($"Pneumatic config load error: {ex.Message}");
        }

        // --- Safety subsystem (depends on IO) ---
        _safetyEventLog = new SafetyEventLog();
        _safetyWatchManager = new SafetyWatchManager(_ioManager, _safetyEventLog);
        try
        {
            var watchCount = _safetyWatchManager.LoadConfig(configDir);
            Log($"Safety watch inputs loaded: {watchCount}");
        }
        catch (Exception ex)
        {
            LogError($"Safety config load error: {ex.Message}");
        }

        // --- Process engine ---
        _processEngine = new ProcessEngine();
        _processEngine.BuildDeviceRegistry(this);
        _processEngine.RegisterHandler(new Scanning.ScanningCommandHandler(_motionManager, _measurementHub, LogSink));
        _processEngine.RegisterDevice("scanning", null, "scanning");
        Log("Process engine initialized");

        // --- Recipe engine ---
        _recipeEngine = new RecipeEngine();
        _recipeEngine.RegisterBuiltins();
        BuildRecipeMethodRegistry();
        Log("Recipe engine initialized");

        // --- Plugin registry ---
        _pluginRegistry = new ProcessPluginRegistry();
        _pluginRegistry.DiscoverPlugins(typeof(ProcessPluginRegistry).Assembly);
        Log($"Process plugins discovered: {_pluginRegistry.GetAll().Count}");

        // --- Teach point system ---
        _teachPointManager = new TeachPointManager(_motionManager);
        try
        {
            _teachPointManager.Load();
            var activeProduct = _teachPointManager.ActiveProductName;
            if (!string.IsNullOrEmpty(activeProduct))
            {
                _teachPointManager.ApplyActiveProduct();
                Log($"Teach points loaded, active product: {activeProduct}");
            }
            else
            {
                Log("Teach points loaded, no active product");
            }
        }
        catch (Exception ex)
        {
            LogError($"Teach point load error: {ex.Message}");
        }
    }

    public void Shutdown()
    {
        _safetyWatchManager?.Dispose();
        _safetyWatchManager = null;
        _safetyEventLog = null;

        _teachPointManager = null;
        _axisTransformService = null;
        _processEngine = null;
        _pluginRegistry = null;
        _recipeEngine = null;

        _pneumaticManager = null;

        _laserControllerManager?.Dispose();
        _laserControllerManager = null;

        _currentMeasurementManager?.Dispose();
        _currentMeasurementManager = null;

        _motionManager?.DisconnectAll();
        _motionManager = null;

        _ioManager?.Dispose();
        _ioManager = null;

        if (_cameraManager != null)
        {
            foreach (var id in _cameraManager.GetCameraIds())
            {
                try { _cameraManager.StopGrabbing(id); } catch { }
            }
            _cameraManager.Dispose();
            _cameraManager = null;
        }
    }

    public void Reinitialize()
    {
        Initialize();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            Shutdown();
            _disposed = true;
        }
    }

    private void BuildRecipeMethodRegistry()
    {
        if (_recipeEngine == null) return;
        var reg = _recipeEngine.Methods;

        // --- Motion ---
        if (_motionManager != null)
        {
            foreach (var name in _motionManager.GetAllDeviceNames())
            {
                var dev = name;
                reg.RegisterDeviceType(dev, "motion");

                reg.Register(dev, "movetoposition", (args, ctx, ct) =>
                {
                    var pos = args.GetValueOrDefault("position")?.ToString() ?? "";
                    return Task.FromResult(_motionManager.MoveToNamedPosition(dev, pos, true));
                });

                reg.Register(dev, "movetoabs", (args, ctx, ct) =>
                {
                    var axis = args.GetValueOrDefault("axis")?.ToString() ?? "";
                    var position = Convert.ToDouble(args.GetValueOrDefault("position", 0.0));
                    return Task.FromResult(_motionManager.MoveToAbs(dev, axis, position, true));
                });

                reg.Register(dev, "moverelative", (args, ctx, ct) =>
                {
                    var axis = args.GetValueOrDefault("axis")?.ToString() ?? "";
                    var distance = Convert.ToDouble(args.GetValueOrDefault("distance", 0.0));
                    return Task.FromResult(_motionManager.MoveRelative(dev, axis, distance, true));
                });

                reg.Register(dev, "moverelativemulti", (args, ctx, ct) =>
                {
                    var axisNames = new[] { "x", "y", "z", "u", "v", "w" };
                    // Start all non-blocking
                    foreach (var axisKey in axisNames)
                    {
                        if (args.TryGetValue(axisKey, out var val))
                        {
                            var dist = Convert.ToDouble(val);
                            if (dist != 0.0)
                                _motionManager.MoveRelative(dev, axisKey.ToUpperInvariant(), dist, false);
                        }
                    }
                    // Wait for completion
                    var deadline = Environment.TickCount64 + 30000;
                    while (_motionManager.IsMoving(dev) && Environment.TickCount64 < deadline)
                    {
                        ct.ThrowIfCancellationRequested();
                        Thread.Sleep(50);
                    }
                    return Task.FromResult(!_motionManager.IsMoving(dev));
                });

                reg.Register(dev, "movetoabs6", (args, ctx, ct) =>
                {
                    var pos = new PositionStruct
                    {
                        X = args.TryGetValue("x", out var xv) ? Convert.ToDouble(xv) : 0.0,
                        Y = args.TryGetValue("y", out var yv) ? Convert.ToDouble(yv) : 0.0,
                        Z = args.TryGetValue("z", out var zv) ? Convert.ToDouble(zv) : 0.0,
                        U = args.TryGetValue("u", out var uv) ? Convert.ToDouble(uv) : 0.0,
                        V = args.TryGetValue("v", out var vv) ? Convert.ToDouble(vv) : 0.0,
                        W = args.TryGetValue("w", out var wv) ? Convert.ToDouble(wv) : 0.0,
                    };
                    return Task.FromResult(_motionManager.MoveToAbs6(dev, pos, true));
                });

                reg.Register(dev, "getposition", (args, ctx, ct) =>
                {
                    var pos = _motionManager.GetCurrentPosition(dev);
                    if (pos != null)
                    {
                        ctx.Set($"{dev}.X", pos.X);
                        ctx.Set($"{dev}.Y", pos.Y);
                        ctx.Set($"{dev}.Z", pos.Z);
                        ctx.Set($"{dev}.U", pos.U);
                        ctx.Set($"{dev}.V", pos.V);
                        ctx.Set($"{dev}.W", pos.W);
                    }
                    return Task.FromResult(true);
                });

                reg.Register(dev, "stopall", (args, ctx, ct) =>
                    Task.FromResult(_motionManager.StopAll(dev)));

                reg.Register(dev, "ismoving", (args, ctx, ct) =>
                {
                    var moving = _motionManager.IsMoving(dev);
                    var key = args.GetValueOrDefault("store")?.ToString() ?? "isMoving";
                    ctx.Set(key, moving);
                    return Task.FromResult(true);
                });

                reg.Register(dev, "setvelocity", (args, ctx, ct) =>
                {
                    var speed = Convert.ToDouble(args.GetValueOrDefault("speed", 0.0));
                    var controller = _motionManager.GetController(dev);
                    if (controller == null) return Task.FromResult(false);
                    var setVelMethod = controller.GetType().GetMethod("SetVelocity");
                    if (setVelMethod != null)
                    {
                        var result = setVelMethod.Invoke(controller, new object[] { speed });
                        return Task.FromResult(result is not false);
                    }
                    ctx.Set($"{dev}.velocity", speed);
                    return Task.FromResult(true);
                });

                reg.Register(dev, "waitforposition", (args, ctx, ct) =>
                {
                    var axis = args.GetValueOrDefault("axis")?.ToString() ?? "";
                    var timeoutMs = Convert.ToInt32(args.GetValueOrDefault("timeoutMs", 30000.0));
                    var pollMs = Convert.ToInt32(args.GetValueOrDefault("pollMs", 50.0));
                    var controller = _motionManager.GetController(dev);
                    if (controller == null) return Task.FromResult(false);
                    var deadline = Environment.TickCount64 + timeoutMs;
                    while (Environment.TickCount64 < deadline)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (controller.IsOnTarget(axis)) return Task.FromResult(true);
                        Thread.Sleep(pollMs);
                    }
                    return Task.FromResult(false);
                });
            }
        }

        // --- IO ---
        if (_ioManager != null)
        {
            foreach (var name in _ioManager.GetAllDeviceNames())
            {
                var dev = name;
                reg.RegisterDeviceType(dev, "io");

                reg.Register(dev, "setoutput", (args, ctx, ct) =>
                {
                    var pin = Convert.ToInt32(args.GetValueOrDefault("pin", 0.0));
                    var state = Convert.ToBoolean(args.GetValueOrDefault("state", false));
                    var result = _ioManager.SetOutput(dev, pin, state);
                    return Task.FromResult(result.ToString() == "Success");
                });

                reg.Register(dev, "setoutputbyname", (args, ctx, ct) =>
                {
                    var pinName = args.GetValueOrDefault("pinName")?.ToString() ?? "";
                    var state = Convert.ToBoolean(args.GetValueOrDefault("state", false));
                    var result = _ioManager.SetOutputByName(dev, pinName, state);
                    return Task.FromResult(result.ToString() == "Success");
                });

                reg.Register(dev, "readinput", (args, ctx, ct) =>
                {
                    var pin = Convert.ToInt32(args.GetValueOrDefault("pin", 0.0));
                    var readResult = _ioManager.ReadInputs(dev, out uint inputs);
                    if (readResult.ToString() != "Success") return Task.FromResult(false);
                    var state = (inputs & (1u << pin)) != 0;
                    var key = args.GetValueOrDefault("store")?.ToString() ?? $"{dev}.input.{pin}";
                    ctx.Set(key, state);
                    return Task.FromResult(true);
                });
            }
        }

        // --- Pneumatic ---
        if (_pneumaticManager != null)
        {
            foreach (var name in _pneumaticManager.GetAllSlideNames())
            {
                var dev = name;
                reg.RegisterDeviceType(dev, "pneumatic");

                reg.Register(dev, "extend", (args, ctx, ct) =>
                    Task.FromResult(_pneumaticManager.Extend(dev).ToString() == "Success"));

                reg.Register(dev, "retract", (args, ctx, ct) =>
                    Task.FromResult(_pneumaticManager.Retract(dev).ToString() == "Success"));

                reg.Register(dev, "extendandwait", (args, ctx, ct) =>
                    Task.FromResult(_pneumaticManager.ExtendAndWait(dev).ToString() == "Success"));

                reg.Register(dev, "retractandwait", (args, ctx, ct) =>
                    Task.FromResult(_pneumaticManager.RetractAndWait(dev).ToString() == "Success"));
            }
        }

        // --- Current Measurement ---
        if (_currentMeasurementManager != null)
        {
            foreach (var name in _currentMeasurementManager.GetAllDeviceNames())
            {
                var dev = name;
                reg.RegisterDeviceType(dev, "currentmeasurement");

                reg.Register(dev, "measure", (args, ctx, ct) =>
                {
                    var channel = Convert.ToInt32(args.GetValueOrDefault("channel", 1.0));
                    var result = _currentMeasurementManager.ReadCurrent(dev, channel, out double current);
                    if (result.ToString() != "Success") return Task.FromResult(false);
                    var key = args.GetValueOrDefault("store")?.ToString() ?? $"{dev}.CH{channel}";
                    ctx.Set(key, current);
                    ctx.Set($"{key}.unit", "A");
                    _measurementHub.Push($"{dev}.CH{channel}", current, DateTimeOffset.UtcNow);
                    return Task.FromResult(true);
                });

                reg.Register(dev, "readall", (args, ctx, ct) =>
                {
                    var result = _currentMeasurementManager.ReadAllChannels(dev, out double[] currents);
                    if (result.ToString() != "Success") return Task.FromResult(false);
                    for (int i = 0; i < currents.Length; i++)
                    {
                        ctx.Set($"{dev}.CH{i + 1}", currents[i]);
                        ctx.Set($"{dev}.CH{i + 1}.unit", "A");
                        _measurementHub.Push($"{dev}.CH{i + 1}", currents[i], DateTimeOffset.UtcNow);
                    }
                    return Task.FromResult(true);
                });

                reg.Register(dev, "reset", (args, ctx, ct) =>
                    Task.FromResult(_currentMeasurementManager.ResetDevice(dev).ToString() == "Success"));

                reg.Register(dev, "connect", (args, ctx, ct) =>
                    Task.FromResult(_currentMeasurementManager.ConnectDevice(dev)));

                reg.Register(dev, "disconnect", (args, ctx, ct) =>
                {
                    _currentMeasurementManager.DisconnectDevice(dev);
                    return Task.FromResult(true);
                });

                reg.Register(dev, "setvoltage", (args, ctx, ct) =>
                {
                    var volts = Convert.ToDouble(args.GetValueOrDefault("volts", 0.0));
                    return Task.FromResult(_currentMeasurementManager.SetVoltage(dev, volts).ToString() == "Success");
                });

                reg.Register(dev, "outputon", (args, ctx, ct) =>
                    Task.FromResult(_currentMeasurementManager.SetVoltageEnabled(dev, true).ToString() == "Success"));

                reg.Register(dev, "outputoff", (args, ctx, ct) =>
                    Task.FromResult(_currentMeasurementManager.SetVoltageEnabled(dev, false).ToString() == "Success"));

                reg.Register(dev, "startpolling", (args, ctx, ct) =>
                {
                    var interval = Convert.ToInt32(args.GetValueOrDefault("intervalMs", 500.0));
                    _currentMeasurementManager.StartPolling(interval);
                    return Task.FromResult(true);
                });

                reg.Register(dev, "stoppolling", (args, ctx, ct) =>
                {
                    _currentMeasurementManager.StopPolling();
                    return Task.FromResult(true);
                });

                reg.Register(dev, "startfastpolling", (args, ctx, ct) =>
                    Task.FromResult(_currentMeasurementManager.StartFastPolling(dev)));

                reg.Register(dev, "stopfastpolling", (args, ctx, ct) =>
                {
                    _currentMeasurementManager.StopFastPolling(dev);
                    return Task.FromResult(true);
                });

                reg.Register(dev, "initialize", (args, ctx, ct) =>
                {
                    // Connect → set voltage → output on → start fast polling
                    if (!_currentMeasurementManager.ConnectDevice(dev))
                        return Task.FromResult(false);
                    var volts = Convert.ToDouble(args.GetValueOrDefault("volts", 1.0));
                    if (_currentMeasurementManager.SetVoltage(dev, volts).ToString() != "Success")
                        return Task.FromResult(false);
                    if (_currentMeasurementManager.SetVoltageEnabled(dev, true).ToString() != "Success")
                        return Task.FromResult(false);
                    return Task.FromResult(_currentMeasurementManager.StartFastPolling(dev));
                });
            }
        }

        // --- Laser Controller ---
        if (_laserControllerManager != null)
        {
            foreach (var name in _laserControllerManager.GetAllDeviceNames())
            {
                var dev = name;
                reg.RegisterDeviceType(dev, "lasercontroller");

                reg.Register(dev, "settemperature", (args, ctx, ct) =>
                {
                    var temp = Convert.ToDouble(args.GetValueOrDefault("celsius", 25.0));
                    return Task.FromResult(_laserControllerManager.SetTemperature(dev, temp).ToString() == "Success");
                });

                reg.Register(dev, "readtemperature", (args, ctx, ct) =>
                {
                    var result = _laserControllerManager.ReadTemperature(dev, out double temp);
                    if (result.ToString() != "Success") return Task.FromResult(false);
                    var key = args.GetValueOrDefault("store")?.ToString() ?? $"{dev}.temperature";
                    ctx.Set(key, temp);
                    ctx.Set($"{key}.unit", "C");
                    return Task.FromResult(true);
                });

                reg.Register(dev, "tecon", (args, ctx, ct) =>
                    Task.FromResult(_laserControllerManager.TECOn(dev).ToString() == "Success"));

                reg.Register(dev, "tecoff", (args, ctx, ct) =>
                    Task.FromResult(_laserControllerManager.TECOff(dev).ToString() == "Success"));

                reg.Register(dev, "setlasercurrent", (args, ctx, ct) =>
                {
                    var ma = Convert.ToDouble(args.GetValueOrDefault("milliamps", 0.0));
                    return Task.FromResult(_laserControllerManager.SetLaserCurrent(dev, ma).ToString() == "Success");
                });

                reg.Register(dev, "readlasercurrent", (args, ctx, ct) =>
                {
                    var result = _laserControllerManager.ReadLaserCurrent(dev, out double ma);
                    if (result.ToString() != "Success") return Task.FromResult(false);
                    var key = args.GetValueOrDefault("store")?.ToString() ?? $"{dev}.lasercurrent";
                    ctx.Set(key, ma);
                    ctx.Set($"{key}.unit", "mA");
                    return Task.FromResult(true);
                });

                reg.Register(dev, "laseron", (args, ctx, ct) =>
                    Task.FromResult(_laserControllerManager.LaserOn(dev).ToString() == "Success"));

                reg.Register(dev, "laseroff", (args, ctx, ct) =>
                    Task.FromResult(_laserControllerManager.LaserOff(dev).ToString() == "Success"));

                reg.Register(dev, "connect", (args, ctx, ct) =>
                    Task.FromResult(_laserControllerManager.ConnectDevice(dev)));

                reg.Register(dev, "disconnect", (args, ctx, ct) =>
                {
                    _laserControllerManager.DisconnectDevice(dev);
                    return Task.FromResult(true);
                });

                reg.Register(dev, "initialize", (args, ctx, ct) =>
                {
                    // Connect → set temperature → TEC on
                    if (!_laserControllerManager.ConnectDevice(dev))
                        return Task.FromResult(false);
                    var temp = Convert.ToDouble(args.GetValueOrDefault("celsius", 25.0));
                    if (_laserControllerManager.SetTemperature(dev, temp).ToString() != "Success")
                        return Task.FromResult(false);
                    if (_laserControllerManager.TECOn(dev).ToString() != "Success")
                        return Task.FromResult(false);
                    return Task.FromResult(true);
                });
            }
        }

        // --- Camera ---
        if (_cameraManager != null)
        {
            foreach (var id in _cameraManager.GetCameraIds())
            {
                var dev = id;
                reg.RegisterDeviceType(dev, "camera");

                reg.Register(dev, "capture", (args, ctx, ct) =>
                {
                    var filename = args.GetValueOrDefault("filename")?.ToString();
                    return Task.FromResult(filename != null
                        ? _cameraManager.CaptureImage(dev, filename)
                        : _cameraManager.CaptureImage(dev));
                });

                reg.Register(dev, "startgrabbing", (args, ctx, ct) =>
                    Task.FromResult(_cameraManager.StartGrabbing(dev)));

                reg.Register(dev, "stopgrabbing", (args, ctx, ct) =>
                    Task.FromResult(_cameraManager.StopGrabbing(dev)));

                reg.Register(dev, "setexposure", (args, ctx, ct) =>
                {
                    var exposure = Convert.ToDouble(args.GetValueOrDefault("exposure", 10000.0));
                    var current = _cameraManager.GetExposureSettings(dev);
                    var settings = current with { ExposureTimeUs = exposure };
                    return Task.FromResult(_cameraManager.ApplyExposureSettings(dev, settings));
                });
            }
        }
    }

    private void Log(string message)
    {
        LogSink?.Info(message);
    }

    private void LogDebug(string message)
    {
        LogSink?.Debug(message);
    }

    private void LogWarn(string message)
    {
        LogSink?.Warn(message);
    }

    private void LogError(string message)
    {
        LogSink?.Error(message);
    }
}
