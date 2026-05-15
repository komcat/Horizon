using Horizon.Camera;
using Horizon.Common;
using Horizon.CurrentMeasurement;
using Horizon.IO;
using Horizon.LaserController;
using Horizon.Motion;
using Horizon.Pneumatic;

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

    private CameraManager? _cameraManager;
    private CameraFacet? _cameraFacet;

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

    public ICameraFacet Camera =>
        _cameraFacet ?? throw new InvalidOperationException("Engine is not initialized — call InitializeAsync first.");

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
            var count = MotionJsonLoader.LoadDevices(_motionManager, _configDirectory);
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

        if (_cameraFacet != null)
            await _cameraFacet.ConnectAllInternalAsync(progress).ConfigureAwait(false);

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

        return new HardwareStatusSnapshot(state, devices);
    }

    public async Task ShutdownAsync()
    {
        lock (_gate)
        {
            if (_state == EngineState.ShuttingDown) return;
            _state = EngineState.ShuttingDown;
        }

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

    private void Log(LogLevel level, string message)
        => LogEmitted?.Invoke(new LogEntry(level, DateTimeOffset.Now, "Engine", message));
}
