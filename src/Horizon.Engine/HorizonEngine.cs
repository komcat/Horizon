using Horizon.Camera;
using Horizon.Common;
using Horizon.Config;
using Horizon.Core.Interlock;
using Horizon.CurrentMeasurement;
using Horizon.IO;
using Horizon.LaserController;
using Horizon.Lighting;
using Horizon.Motion;
using Horizon.Motion.Models;
using Horizon.Pneumatic;
using Horizon.PowerSupply;
using Horizon.Safety;
using Horizon.Safety.Models;

namespace Horizon.Engine;

/// <summary>
/// Default in-process implementation of <see cref="IHorizonEngine"/>.
/// Hardware facets are added one bring-up step at a time — currently: IO, Motion.
/// </summary>
public sealed class HorizonEngine : IHorizonEngine
{
    private readonly object _gate = new();
    private EngineState _state = EngineState.Created;
    private string _configDirectory = string.Empty;

    private EziIOManager? _ioManager;
    private IoFacet? _ioFacet;

    private UnifiedMotionManager? _motionManager;
    private MotionFacet? _motionFacet;

    private PneumaticFacet? _pneumaticFacet;

    private CurrentMeasurementManager? _measurementManager;
    private MeasurementFacet? _measurementFacet;

    private LaserControllerManager? _laserManager;
    private LaserFacet? _laserFacet;

    private PowerSupplyManager? _powerSupplyManager;
    private PowerSupplyFacet? _powerSupplyFacet;

    private LightingManager? _lightingManager;
    private LightingFacet? _lightingFacet;

    private CameraManager? _cameraManager;
    private CameraFacet? _cameraFacet;

    private VisionFacet? _visionFacet;

    private SafetyWatchManager? _safetyWatch;
    private SafetyFacet? _safetyFacet;

    public EngineState State
    {
        get { lock (_gate) return _state; }
    }

    public IIOFacet IO =>
        _ioFacet ?? throw new InvalidOperationException("Engine is not initialized — call InitializeAsync first.");

    public IMotionFacet Motion =>
        _motionFacet ?? throw new InvalidOperationException("Engine is not initialized — call InitializeAsync first.");

    public IPneumaticFacet Pneumatic =>
        _pneumaticFacet ?? throw new InvalidOperationException("Engine is not initialized — call InitializeAsync first.");

    public IMeasurementFacet Measurement =>
        _measurementFacet ?? throw new InvalidOperationException("Engine is not initialized — call InitializeAsync first.");

    public ILaserFacet Laser =>
        _laserFacet ?? throw new InvalidOperationException("Engine is not initialized — call InitializeAsync first.");

    public IPowerSupplyFacet PowerSupply =>
        _powerSupplyFacet ?? throw new InvalidOperationException("Engine is not initialized — call InitializeAsync first.");

    public ILightingFacet Lighting =>
        _lightingFacet ?? throw new InvalidOperationException("Engine is not initialized — call InitializeAsync first.");

    public ICameraFacet Camera =>
        _cameraFacet ?? throw new InvalidOperationException("Engine is not initialized — call InitializeAsync first.");

    public IVisionFacet Vision =>
        _visionFacet ?? throw new InvalidOperationException("Engine is not initialized — call InitializeAsync first.");

    public ISafetyFacet Safety =>
        _safetyFacet ?? throw new InvalidOperationException("Engine is not initialized — call InitializeAsync first.");

    public event Action<HardwareStatusChanged>? StatusChanged;
    public event Action<LogEntry>? LogEmitted;

    public Task InitializeAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_state is not (EngineState.Created or EngineState.Initialized))
            {
                Log(LogLevel.Warning, $"InitializeAsync ignored — engine is {_state}.");
                return Task.CompletedTask;
            }
            _configDirectory = ConfigLoader.GetConfigDirectory();
        }

        Log(LogLevel.Info, $"Engine initializing. Config directory: {_configDirectory}");

        // --- IO domain ---
        _ioManager = new EziIOManager();
        try
        {
            var count = EziIOJsonLoader.LoadDevices(_ioManager, _configDirectory);
            Log(LogLevel.Info, $"IO devices loaded: {count}");
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"IO config load failed: {ex.Message}");
        }
        _ioFacet = new IoFacet(_ioManager, RaiseStatusChanged, Log);

        // --- Motion domain ---
        _motionManager = new UnifiedMotionManager();
        try
        {
            var count = LoadMotionDevices(_motionManager, _configDirectory);
            Log(LogLevel.Info, $"Motion devices loaded: {count}");
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Motion config load failed: {ex.Message}");
        }
        _motionFacet = new MotionFacet(_motionManager, RaiseStatusChanged, Log);

        // --- Pneumatic domain (depends on IO) ---
        var pneumaticManager = new PneumaticManager();
        try
        {
            var count = PneumaticJsonLoader.LoadSlides(pneumaticManager, _ioManager, _configDirectory);
            Log(LogLevel.Info, $"Pneumatic slides loaded: {count}");
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Pneumatic config load failed: {ex.Message}");
        }
        _pneumaticFacet = new PneumaticFacet(pneumaticManager, RaiseStatusChanged, Log);

        // --- Current measurement domain ---
        _measurementManager = new CurrentMeasurementManager();
        try
        {
            var count = CurrentMeasurementJsonLoader.LoadDevices(_measurementManager, _configDirectory);
            Log(LogLevel.Info, $"Current measurement devices loaded: {count}");
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Current measurement config load failed: {ex.Message}");
        }
        _measurementFacet = new MeasurementFacet(_measurementManager, RaiseStatusChanged, Log);

        // --- Laser controller domain ---
        _laserManager = new LaserControllerManager();
        try
        {
            var count = LaserControllerJsonLoader.LoadDevices(_laserManager, _configDirectory);
            Log(LogLevel.Info, $"Laser controller devices loaded: {count}");
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Laser controller config load failed: {ex.Message}");
        }
        _laserFacet = new LaserFacet(_laserManager, RaiseStatusChanged, Log);

        // --- Power supply domain ---
        _powerSupplyManager = new PowerSupplyManager();
        try
        {
            var devices = PowerSupplyJsonLoader.Load(_configDirectory);
            foreach (var device in devices)
                _powerSupplyManager.AddDevice(device);
            Log(LogLevel.Info, $"Power supply devices loaded: {devices.Count}");
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Power supply config load failed: {ex.Message}");
        }
        _powerSupplyFacet = new PowerSupplyFacet(_powerSupplyManager, RaiseStatusChanged, Log);

        // --- Lighting domain ---
        _lightingManager = new LightingManager();
        try
        {
            var devices = LightingJsonLoader.Load(_configDirectory);
            foreach (var device in devices)
                _lightingManager.AddDevice(device);
            Log(LogLevel.Info, $"Lighting controllers loaded: {devices.Count}");
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Lighting config load failed: {ex.Message}");
        }
        _lightingFacet = new LightingFacet(_lightingManager, RaiseStatusChanged, Log);

        // --- Camera domain ---
        _cameraManager = new CameraManager();
        try
        {
            var registered = new CameraConfigStore().Load();
            if (registered.Count > 0)
                _cameraManager.DiscoverAndAddRegisteredCameras(registered);
            Log(LogLevel.Info, $"Cameras added: {_cameraManager.CameraCount}");
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Camera config load failed: {ex.Message}");
        }
        _cameraFacet = new CameraFacet(_cameraManager, RaiseStatusChanged, Log);

        // --- Vision domain (HALCON over camera frames) ---
        _visionFacet = new VisionFacet(_cameraFacet, Log);

        // --- Safety domain (IO watchdog + motion interlocks) ---
        var eventLog = new SafetyEventLog();
        _safetyWatch = new SafetyWatchManager(_ioManager, eventLog);
        try
        {
            var count = _safetyWatch.LoadConfig(_configDirectory);
            Log(LogLevel.Info, $"Safety watched inputs loaded: {count}");
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Safety config load failed: {ex.Message}");
        }

        var interlock = new InterlockService(pneumaticManager, _ioManager);
        try
        {
            var count = interlock.LoadConfig(_configDirectory);
            Log(LogLevel.Info, $"Motion interlock rules loaded: {count}");
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Interlock config load failed: {ex.Message}");
        }

        // Enforcement: the watchdog reaches into Motion / Pneumatic / IO on a trip.
        _safetyWatch.SetEnforcementActions(
            stopAllMotion: () => _motionManager?.StopAllDevices(),
            retractAllPneumatics: () => pneumaticManager.StopAll(),
            killIO: KillIoOutputs,
            releaseStepGates: null);   // no process/recipe engines in Layer 1

        // Interlock check: gate every motion command (left null since Step 3).
        _motionManager.InterlockCheck = (device, axis, source) =>
            interlock.CheckMotionAllowed(device, axis, (MotionSource)source);

        _safetyFacet = new SafetyFacet(_safetyWatch, interlock, RaiseStatusChanged, Log);

        lock (_gate) _state = EngineState.Initialized;
        Log(LogLevel.Info, "Engine initialized.");
        return Task.CompletedTask;
    }

    public async Task ConnectAllAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_state is not (EngineState.Initialized or EngineState.Connected))
            {
                Log(LogLevel.Warning,
                    $"ConnectAllAsync ignored — engine is {_state} (call InitializeAsync first).");
                return;
            }
            _state = EngineState.Connecting;
        }

        progress?.Report("Connecting hardware…");
        ct.ThrowIfCancellationRequested();

        if (_ioFacet != null)
            await _ioFacet.ConnectAllInternalAsync(progress).ConfigureAwait(false);

        if (_motionFacet != null)
            await _motionFacet.ConnectAllInternalAsync(progress).ConfigureAwait(false);

        if (_pneumaticFacet != null)
            await _pneumaticFacet.ConnectAllInternalAsync(progress).ConfigureAwait(false);

        if (_measurementFacet != null)
            await _measurementFacet.ConnectAllInternalAsync(progress).ConfigureAwait(false);

        if (_laserFacet != null)
            await _laserFacet.ConnectAllInternalAsync(progress).ConfigureAwait(false);

        if (_powerSupplyFacet != null)
            await _powerSupplyFacet.ConnectAllInternalAsync(progress).ConfigureAwait(false);

        if (_lightingFacet != null)
            await _lightingFacet.ConnectAllInternalAsync(progress).ConfigureAwait(false);

        if (_cameraFacet != null)
            await _cameraFacet.ConnectAllInternalAsync(progress).ConfigureAwait(false);

        if (_visionFacet != null)
            progress?.Report($"Vision: HALCON {_visionFacet.HalconVersion}");

        if (_safetyWatch != null)
        {
            _safetyWatch.StartWatching();
            progress?.Report($"Safety: watching {_safetyWatch.WatchedInputs.Count} input(s).");
        }

        lock (_gate) _state = EngineState.Connected;
        Log(LogLevel.Info, "ConnectAll complete.");
    }

    public HardwareStatusSnapshot GetStatusSnapshot()
    {
        EngineState state;
        lock (_gate) state = _state;

        var devices = new List<DeviceStatus>();

        if (_ioFacet != null)
        {
            foreach (var name in _ioFacet.DeviceNames)
            {
                var s = _ioFacet.GetState(name);
                var detail = s.Valid ? $"in=0x{s.Inputs:X}  out=0x{s.Outputs:X}" : "no data";
                devices.Add(new DeviceStatus("IO", name, _ioFacet.IsConnected(name), detail));
            }
        }

        if (_motionFacet != null)
        {
            foreach (var name in _motionFacet.DeviceNames)
            {
                var s = _motionFacet.GetState(name);
                var detail = s.Valid
                    ? $"X={s.Position.X:F3} Y={s.Position.Y:F3} Z={s.Position.Z:F3}{(s.IsMoving ? " [moving]" : "")}"
                    : "no data";
                devices.Add(new DeviceStatus("Motion", name, _motionFacet.IsConnected(name), detail));
            }
        }

        if (_pneumaticFacet != null)
        {
            foreach (var name in _pneumaticFacet.SlideNames)
            {
                var s = _pneumaticFacet.GetState(name);
                var detail = s.Valid
                    ? $"{s.Position}{(s.OutputActive ? " out=on" : "")}"
                    : "no data";
                devices.Add(new DeviceStatus("Pneumatic", name, true, detail));
            }
        }

        if (_measurementFacet != null)
        {
            foreach (var name in _measurementFacet.DeviceNames)
            {
                var s = _measurementFacet.GetState(name);
                var detail = s.Valid && s.Channels.Count > 0
                    ? string.Join("  ", s.Channels.Select((v, i) => $"CH{i + 1}={v:E2}{s.Unit}"))
                    : "no data";
                devices.Add(new DeviceStatus("Measurement", name, _measurementFacet.IsConnected(name), detail));
            }
        }

        if (_laserFacet != null)
        {
            foreach (var name in _laserFacet.DeviceNames)
            {
                var s = _laserFacet.GetState(name);
                var detail = s.Valid
                    ? $"T={s.Temperature:F2}C  I={s.LaserCurrent:F2}mA{(s.TecOn ? " TEC" : "")}{(s.LaserOn ? " LASER" : "")}"
                    : "no data";
                devices.Add(new DeviceStatus("Laser", name, _laserFacet.IsConnected(name), detail));
            }
        }

        if (_powerSupplyFacet != null)
        {
            foreach (var name in _powerSupplyFacet.DeviceNames)
            {
                var s = _powerSupplyFacet.GetState(name);
                var detail = s.Valid && s.Channels.Count > 0
                    ? string.Join("  ", s.Channels.Select(c =>
                        $"CH{c.Channel}={c.Voltage:F2}V/{c.Current:F3}A{(c.OutputOn ? " ON" : "")}"))
                    : "no data";
                devices.Add(new DeviceStatus("Power supply", name, _powerSupplyFacet.IsConnected(name), detail));
            }
        }

        if (_lightingFacet != null)
        {
            foreach (var name in _lightingFacet.DeviceNames)
            {
                var s = _lightingFacet.GetState(name);
                var detail = s.Valid && s.Channels.Count > 0
                    ? string.Join("  ", s.Channels.Select(c => $"{c.Name}={c.Intensity}"))
                    : "no data";
                devices.Add(new DeviceStatus("Lighting", name, _lightingFacet.IsConnected(name), detail));
            }
        }

        if (_cameraFacet != null)
        {
            foreach (var id in _cameraFacet.CameraIds)
            {
                var s = _cameraFacet.GetState(id);
                var detail = s.Grabbing
                    ? $"{s.Width}x{s.Height} grabbing frames={s.FramesReceived}"
                    : (s.Connected ? "idle" : "no data");
                devices.Add(new DeviceStatus("Camera", id, _cameraFacet.IsConnected(id), detail));
            }
        }

        if (_visionFacet != null)
        {
            devices.Add(new DeviceStatus("Vision", "HALCON",
                _visionFacet.HalconAvailable, _visionFacet.HalconVersion));
        }

        if (_safetyFacet != null)
        {
            var ss = _safetyFacet.GetState();
            var detail = ss.FlagRaised
                ? (ss.CriticalRaised ? "CRITICAL FLAG RAISED" : "FLAG RAISED")
                : $"watching {_safetyFacet.WatchedInputs.Count} input(s)";
            devices.Add(new DeviceStatus("Safety", "Watchdog", ss.IsWatching, detail));
        }

        return new HardwareStatusSnapshot(state, devices);
    }

    public async Task ShutdownAsync()
    {
        lock (_gate)
        {
            if (_state == EngineState.ShuttingDown) return;
            _state = EngineState.ShuttingDown;
        }

        _safetyFacet?.Dispose();   // unsubscribes from watchdog events
        _safetyFacet = null;
        _safetyWatch?.Dispose();   // stops the 50 ms watch loop before motion/IO teardown
        _safetyWatch = null;

        _visionFacet?.Dispose();   // unsubscribes from camera frames
        _visionFacet = null;

        if (_cameraFacet != null)
            await _cameraFacet.DisposeAsync().ConfigureAwait(false);   // stops the camera poll loop, unsubscribes
        _cameraManager?.Dispose();
        _cameraManager = null;
        _cameraFacet = null;

        if (_laserFacet != null)
            await _laserFacet.DisposeAsync().ConfigureAwait(false);   // stops the laser poll loop
        _laserManager?.Dispose();
        _laserManager = null;
        _laserFacet = null;

        if (_powerSupplyFacet != null)
            await _powerSupplyFacet.DisposeAsync().ConfigureAwait(false);   // stops the power-supply poll loop
        _powerSupplyManager?.Dispose();
        _powerSupplyManager = null;
        _powerSupplyFacet = null;

        if (_lightingFacet != null)
            await _lightingFacet.DisposeAsync().ConfigureAwait(false);   // stops the lighting poll loop
        _lightingManager?.Dispose();
        _lightingManager = null;
        _lightingFacet = null;

        _measurementManager?.Dispose();   // stops its poll loop, disconnects devices
        _measurementManager = null;
        _measurementFacet = null;

        if (_pneumaticFacet != null)
            await _pneumaticFacet.DisposeAsync().ConfigureAwait(false);   // stops the pneumatic poll loop
        _pneumaticFacet = null;

        if (_motionFacet != null)
            await _motionFacet.DisposeAsync().ConfigureAwait(false);   // stops the motion poll loop
        _motionManager?.DisconnectAll();
        _motionManager = null;
        _motionFacet = null;

        _ioManager?.Dispose();   // stops IO polling, disconnects all IO devices
        _ioManager = null;
        _ioFacet = null;

        Log(LogLevel.Info, "Engine shut down.");
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync().ConfigureAwait(false);
    }

    /// <summary>Raises <see cref="StatusChanged"/>. Used by hardware facets.</summary>
    internal void RaiseStatusChanged(HardwareStatusChanged change)
        => StatusChanged?.Invoke(change);

    /// <summary>
    /// Loads the engine's motion hardware config from <c>motor_devices_config.json</c>
    /// — hardware only (controller type, IP, axes). Named positions are not a
    /// Layer 1 concern; the legacy <c>motor_devices.json</c> is not read by the engine.
    /// </summary>
    private static int LoadMotionDevices(UnifiedMotionManager manager, string configDir)
    {
        var file = ConfigLoader.LoadJson<MotionDevicesConfigFile>(configDir, "motor_devices_config");
        if (file?.Devices == null) return 0;
        foreach (var device in file.Devices)
            manager.AddDevice(device);
        return file.Devices.Count;
    }

    private sealed record MotionDevicesConfigFile
    {
        public List<MotionDevice> Devices { get; init; } = new();
    }

    /// <summary>Safety enforcement: drives configured IO outputs off on a watchdog trip.</summary>
    private void KillIoOutputs(IOKillAction action)
    {
        if (_ioManager == null) return;

        if (action.KillAll)
        {
            foreach (var name in _ioManager.GetAllDeviceNames())
            {
                var device = _ioManager.GetDevice(name);
                if (device == null || !device.IsConnected) continue;
                for (int pin = 0; pin < device.OutputCount; pin++)
                    device.SetOutput(pin, false);
            }
        }
        else
        {
            foreach (var target in action.Pins)
                _ioManager.SetOutputByName(target.DeviceName, target.PinName, false);
        }
    }

    private void Log(LogLevel level, string message)
        => LogEmitted?.Invoke(new LogEntry(level, DateTimeOffset.Now, "Engine", message));
}
