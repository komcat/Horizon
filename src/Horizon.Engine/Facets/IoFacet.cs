using System.Collections.Concurrent;
using Horizon.Common;
using Horizon.IO;
using Horizon.IO.Models;

namespace Horizon.Engine;

/// <summary>
/// IO facet — wraps the kept <see cref="EziIOManager"/> behind engine-owned
/// types. Reuses the manager's built-in 100 ms poll loop; translates its
/// <see cref="EziIOManager.OnIOStatusUpdated"/> ticks into edge-triggered
/// engine events.
/// </summary>
internal sealed class IoFacet : IIOFacet
{
    private readonly EziIOManager _manager;
    private readonly Action<HardwareStatusChanged> _emitStatus;
    private readonly Action<LogLevel, string> _log;

    private readonly Dictionary<string, IoDeviceInfo> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IoState> _state = new(StringComparer.OrdinalIgnoreCase);

    public IoFacet(
        EziIOManager manager,
        Action<HardwareStatusChanged> emitStatus,
        Action<LogLevel, string> log)
    {
        _manager = manager;
        _emitStatus = emitStatus;
        _log = log;

        foreach (var name in _manager.GetAllDeviceNames())
        {
            var dev = _manager.GetDevice(name);
            if (dev == null) continue;

            var inputs = new List<IoPin>(dev.InputCount);
            for (int i = 0; i < dev.InputCount; i++)
                inputs.Add(new IoPin(i, dev.GetPinName(true, i) ?? $"IN{i}"));

            var outputs = new List<IoPin>(dev.OutputCount);
            for (int i = 0; i < dev.OutputCount; i++)
                outputs.Add(new IoPin(i, dev.GetPinName(false, i) ?? $"OUT{i}"));

            _devices[name] = new IoDeviceInfo(name, dev.InputCount, dev.OutputCount, inputs, outputs);
            _state[name] = IoState.Unknown;
        }

        _manager.OnIOStatusUpdated += OnManagerStatus;
    }

    public IReadOnlyList<string> DeviceNames => _devices.Keys.ToList();

    public IoDeviceInfo? GetDevice(string device)
        => _devices.TryGetValue(device, out var info) ? info : null;

    public IoState GetState(string device)
        => _state.TryGetValue(device, out var s) ? s : IoState.Unknown;

    public bool IsConnected(string device)
        => _manager.GetDevice(device)?.IsConnected ?? false;

    public Task<Result> ConnectAsync(string device, CancellationToken ct = default)
        => Task.Run(() =>
        {
            if (!_devices.ContainsKey(device))
                return Result.Fail(ErrorCode.IODeviceNotFound, $"IO device '{device}' not found.");
            return _manager.ConnectDevice(device)
                ? Result.Ok()
                : Result.Fail(ErrorCode.IOConnectionFailed, $"IO device '{device}' failed to connect.");
        }, ct);

    public Task<Result> DisconnectAsync(string device)
        => Task.Run(() =>
        {
            var dev = _manager.GetDevice(device);
            if (dev == null)
                return Result.Fail(ErrorCode.IODeviceNotFound, $"IO device '{device}' not found.");
            dev.Disconnect();
            return Result.Ok();
        });

    public Task<Result> SetOutputAsync(string device, int pin, bool state, CancellationToken ct = default)
        => Task.Run(() => Map(_manager.SetOutput(device, pin, state), $"{device} pin {pin}"), ct);

    public Task<Result> SetOutputByNameAsync(string device, string pinName, bool state, CancellationToken ct = default)
        => Task.Run(() => Map(_manager.SetOutputByName(device, pinName, state), $"{device} pin '{pinName}'"), ct);

    /// <summary>Connects all IO devices and starts polling. Called by the engine during ConnectAll.</summary>
    public async Task ConnectAllInternalAsync(IProgress<string>? progress)
    {
        if (_devices.Count == 0)
        {
            progress?.Report("IO: no devices configured.");
            return;
        }

        progress?.Report($"IO: connecting {_devices.Count} device(s)…");
        var ok = await Task.Run(_manager.ConnectAll).ConfigureAwait(false);
        _manager.StartPolling();

        var connected = _devices.Keys.Count(IsConnected);
        progress?.Report($"IO: {connected}/{_devices.Count} device(s) connected.");
        _log(ok ? LogLevel.Info : LogLevel.Warning,
            $"IO ConnectAll — {connected}/{_devices.Count} device(s) connected.");
    }

    private void OnManagerStatus(string device, IOStatus status)
    {
        var next = new IoState(status.Valid, status.Inputs, status.Outputs);
        var prev = _state.TryGetValue(device, out var s) ? s : IoState.Unknown;
        if (next == prev) return;

        _state[device] = next;
        InputChanged?.Invoke(new IoInputChanged(device, next));
        _emitStatus(new HardwareStatusChanged("IO", device));
    }

    private static Result Map(IOError error, string what) => error switch
    {
        IOError.Success        => Result.Ok(),
        IOError.DeviceNotFound => Result.Fail(ErrorCode.IODeviceNotFound, $"IO device not found: {what}."),
        IOError.Disconnected   => Result.Fail(ErrorCode.IODisconnected, $"IO device disconnected: {what}."),
        IOError.InvalidPin     => Result.Fail(ErrorCode.IOInvalidPin, $"Invalid IO pin: {what}."),
        IOError.NotInitialized => Result.Fail(ErrorCode.IONotInitialized, $"IO not initialized: {what}."),
        _                      => Result.Fail(ErrorCode.IOOperationFailed, $"IO operation failed: {what}."),
    };

    public event Action<IoInputChanged>? InputChanged;
}
