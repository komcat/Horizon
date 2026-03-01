using Horizon.Camera;
using Horizon.Common;
using Horizon.CurrentMeasurement;
using Horizon.IO;
using Horizon.LaserController;
using Horizon.Motion;
using Horizon.Pneumatic;
using Horizon.Process;
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
    private SafetyEventLog? _safetyEventLog;
    private SafetyWatchManager? _safetyWatchManager;

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

    public SafetyWatchManager? SafetyWatchManager => _safetyWatchManager;

    public MeasurementHub MeasurementHub => _measurementHub;

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
                Log($"  Pylon: {cam.ModelName} ({cam.SerialNumber})");
        }
        catch (Exception ex)
        {
            Log($"Pylon discovery error: {ex.Message}");
        }

        try
        {
            var idsCameras = Horizon.Camera.CameraManager.DiscoverIDSCameras();
            Log($"IDS discovery: {idsCameras.Count} camera(s)");
            foreach (var cam in idsCameras)
                Log($"  IDS: {cam.ModelName} ({cam.SerialNumber}) [{cam.Description}]");
        }
        catch (Exception ex)
        {
            Log($"IDS discovery error: {ex.Message}");
        }

        var registered = _configStore.Load();
        if (registered.Count > 0)
        {
            Log($"Registered cameras: {registered.Count}");
            _cameraManager.DiscoverAndAddRegisteredCameras(registered);
        }
        else
        {
            Log("No registered cameras — use Camera > Config to register cameras.");
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
            Log($"IO config load error: {ex.Message}");
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
            Log($"Motion config load error: {ex.Message}");
        }

        // --- Current Measurement subsystem ---
        _currentMeasurementManager = new CurrentMeasurementManager();
        try
        {
            var cmeasCount = CurrentMeasurementJsonLoader.LoadDevices(_currentMeasurementManager, configDir);
            Log($"Current measurement devices loaded: {cmeasCount}");
        }
        catch (Exception ex)
        {
            Log($"Current measurement config load error: {ex.Message}");
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
            Log($"Laser controller config load error: {ex.Message}");
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
            Log($"Pneumatic config load error: {ex.Message}");
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
            Log($"Safety config load error: {ex.Message}");
        }

        // --- Process engine ---
        _processEngine = new ProcessEngine();
        _processEngine.BuildDeviceRegistry(this);
        Log("Process engine initialized");
    }

    public void Shutdown()
    {
        _safetyWatchManager?.Dispose();
        _safetyWatchManager = null;
        _safetyEventLog = null;

        _processEngine = null;

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

    private void Log(string message)
    {
        LogSink?.Log(message);
    }
}
