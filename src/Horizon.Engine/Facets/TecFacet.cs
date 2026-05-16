using System.Collections.Concurrent;
using Horizon.Common;
using Horizon.Engine.Polling;
using Horizon.Tec;
using Horizon.Tec.Models;

namespace Horizon.Engine;

/// <summary>
/// TEC facet — wraps <see cref="TecManager"/> behind engine-owned types, polls
/// controller status, and raises change events.
/// </summary>
internal sealed class TecFacet : ITecFacet, IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly TecManager _manager;
    private readonly Action<HardwareStatusChanged> _emitStatus;
    private readonly Action<LogLevel, string> _log;

    private readonly Dictionary<string, TecDeviceInfo> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TecState> _state = new(StringComparer.OrdinalIgnoreCase);
    private readonly PollLoop _poll;

    public TecFacet(
        TecManager manager,
        Action<HardwareStatusChanged> emitStatus,
        Action<LogLevel, string> log)
    {
        _manager = manager;
        _emitStatus = emitStatus;
        _log = log;

        foreach (var name in _manager.GetAllDeviceNames())
        {
            var dev = _manager.GetDevice(name);
            _devices[name] = new TecDeviceInfo(name, dev?.ControllerType ?? "?");
            _state[name] = TecState.Unknown;
        }

        _poll = new PollLoop("TEC", PollInterval, PollTickAsync);
    }

    public IReadOnlyList<string> DeviceNames => _devices.Keys.ToList();

    public TecDeviceInfo? GetDevice(string device)
        => _devices.TryGetValue(device, out var info) ? info : null;

    public TecState GetState(string device)
        => _state.TryGetValue(device, out var s) ? s : TecState.Unknown;

    public bool IsConnected(string device)
        => _manager.GetDevice(device)?.IsConnected ?? false;

    public Task<Result> ConnectAsync(string device, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var d = _manager.GetDevice(device);
            if (d == null)
                return Result.Fail(ErrorCode.OperationFailed, $"TEC '{device}' not found.");
            return d.Connect()
                ? Result.Ok()
                : Result.Fail(ErrorCode.OperationFailed, $"TEC '{device}' failed to connect.");
        }, ct);

    public Task<Result> DisconnectAsync(string device)
        => Task.Run(() =>
        {
            var d = _manager.GetDevice(device);
            if (d == null)
                return Result.Fail(ErrorCode.OperationFailed, $"TEC '{device}' not found.");
            d.Disconnect();
            return Result.Ok();
        });

    public Task<Result> SetTemperatureAsync(string device, double celsius)
        => Task.Run(() => Map(device, _manager.SetTemperature(device, celsius)));

    public Task<Result> TecOnAsync(string device)
        => Task.Run(() => Map(device, _manager.TecOn(device)));

    public Task<Result> TecOffAsync(string device)
        => Task.Run(() => Map(device, _manager.TecOff(device)));

    public event Action<TecStateChanged>? StateChanged;

    /// <summary>Connects all TEC controllers and starts polling.</summary>
    public async Task ConnectAllInternalAsync(IProgress<string>? progress)
    {
        if (_devices.Count == 0)
        {
            progress?.Report("TEC: no devices configured.");
            return;
        }

        progress?.Report($"TEC: connecting {_devices.Count} device(s)…");
        await Task.Run(_manager.ConnectAll).ConfigureAwait(false);
        _poll.Start();

        var connected = _devices.Keys.Count(IsConnected);
        progress?.Report($"TEC: {connected}/{_devices.Count} device(s) connected.");
        _log(connected == _devices.Count ? LogLevel.Info : LogLevel.Warning,
            $"TEC ConnectAll — {connected}/{_devices.Count} device(s) connected.");
    }

    public async ValueTask DisposeAsync()
        => await _poll.DisposeAsync().ConfigureAwait(false);

    private Task PollTickAsync(CancellationToken ct)
    {
        foreach (var name in _devices.Keys)
        {
            if (ct.IsCancellationRequested) break;
            if (!IsConnected(name)) continue;

            var status = _manager.GetStatus(name);
            if (status == null) continue;

            var next = new TecState(
                true,
                status.Connected,
                status.CurrentTemperature,
                status.SetpointTemperature,
                status.TecOn,
                status.LastError);

            var prev = _state.TryGetValue(name, out var p) ? p : TecState.Unknown;
            if (next == prev) continue;

            _state[name] = next;
            StateChanged?.Invoke(new TecStateChanged(name, next));
            _emitStatus(new HardwareStatusChanged("TEC", name));
        }
        return Task.CompletedTask;
    }

    private static Result Map(string device, TecError error)
        => error == TecError.Success
            ? Result.Ok()
            : Result.Fail(ErrorCode.OperationFailed, $"TEC '{device}': {error}");
}
