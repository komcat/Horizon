using Horizon.Common;
using Horizon.IO;

namespace Horizon.Engine;

/// <summary>
/// Default in-process implementation of <see cref="IHorizonEngine"/>.
/// Hardware facets are added one bring-up step at a time — currently: IO.
/// </summary>
public sealed class HorizonEngine : IHorizonEngine
{
    private readonly object _gate = new();
    private EngineState _state = EngineState.Created;
    private string _configDirectory = string.Empty;

    private EziIOManager? _ioManager;
    private IoFacet? _ioFacet;

    public EngineState State
    {
        get { lock (_gate) return _state; }
    }

    public IIOFacet IO =>
        _ioFacet ?? throw new InvalidOperationException("Engine is not initialized — call InitializeAsync first.");

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
                var connected = _ioFacet.IsConnected(name);
                var s = _ioFacet.GetState(name);
                var detail = s.Valid
                    ? $"in=0x{s.Inputs:X}  out=0x{s.Outputs:X}"
                    : "no data";
                devices.Add(new DeviceStatus("IO", name, connected, detail));
            }
        }

        return new HardwareStatusSnapshot(state, devices);
    }

    public Task ShutdownAsync()
    {
        lock (_gate)
        {
            if (_state == EngineState.ShuttingDown) return Task.CompletedTask;
            _state = EngineState.ShuttingDown;
        }

        _ioManager?.Dispose();   // stops polling, disconnects all IO devices
        _ioManager = null;
        _ioFacet = null;

        Log(LogLevel.Info, "Engine shut down.");
        return Task.CompletedTask;
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
