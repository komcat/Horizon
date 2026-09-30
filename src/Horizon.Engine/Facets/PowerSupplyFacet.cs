using System.Collections.Concurrent;
using Horizon.Common;
using Horizon.Engine.Polling;
using Horizon.PowerSupply;
using Horizon.PowerSupply.Models;

namespace Horizon.Engine;

/// <summary>
/// Power-supply facet — wraps <see cref="PowerSupplyManager"/> behind
/// engine-owned types, polls device status, and raises change events.
/// </summary>
internal sealed class PowerSupplyFacet : IPowerSupplyFacet, IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly PowerSupplyManager _manager;
    private readonly Action<HardwareStatusChanged> _emitStatus;
    private readonly Action<LogLevel, string> _log;

    private readonly Dictionary<string, PowerSupplyDeviceInfo> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PowerSupplyState> _state = new(StringComparer.OrdinalIgnoreCase);
    private readonly PollLoop _poll;

    public PowerSupplyFacet(
        PowerSupplyManager manager,
        Action<HardwareStatusChanged> emitStatus,
        Action<LogLevel, string> log)
    {
        _manager = manager;
        _emitStatus = emitStatus;
        _log = log;

        foreach (var name in _manager.GetAllDeviceNames())
        {
            var dev = _manager.GetDevice(name);
            _devices[name] = new PowerSupplyDeviceInfo(name, dev?.ControllerType ?? "?", dev?.ChannelCount ?? 1);
            _state[name] = PowerSupplyState.Unknown;
        }

        _poll = new PollLoop("PowerSupply", PollInterval, PollTickAsync);
    }

    public IReadOnlyList<string> DeviceNames => _devices.Keys.ToList();

    public PowerSupplyDeviceInfo? GetDevice(string device)
        => _devices.TryGetValue(device, out var info) ? info : null;

    public PowerSupplyState GetState(string device)
        => _state.TryGetValue(device, out var s) ? s : PowerSupplyState.Unknown;

    public bool IsConnected(string device)
        => _manager.GetDevice(device)?.IsConnected ?? false;

    public Task<Result> ConnectAsync(string device, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var d = _manager.GetDevice(device);
            if (d == null)
                return Result.Fail(ErrorCode.OperationFailed, $"Power supply '{device}' not found.");
            if (d.Connect())
                return Result.Ok();

            // Surface the adapter's captured error (VISA status / exception)
            // instead of a blind "failed to connect".
            var detail = d.GetStatus().LastError;
            return Result.Fail(ErrorCode.OperationFailed,
                string.IsNullOrEmpty(detail)
                    ? $"Power supply '{device}' failed to connect."
                    : $"Power supply '{device}' failed to connect: {detail}");
        }, ct);

    public Task<Result> DisconnectAsync(string device)
        => Task.Run(() =>
        {
            var d = _manager.GetDevice(device);
            if (d == null)
                return Result.Fail(ErrorCode.OperationFailed, $"Power supply '{device}' not found.");
            d.Disconnect();
            return Result.Ok();
        });

    public Task<Result> SetVoltageAsync(string device, int channel, double volts)
        => Task.Run(() => Map(device, _manager.SetVoltage(device, channel, volts)));

    public Task<Result> SetCurrentLimitAsync(string device, int channel, double amps)
        => Task.Run(() => Map(device, _manager.SetCurrentLimit(device, channel, amps)));

    public Task<Result> OutputOnAsync(string device, int channel)
        => Task.Run(() => Map(device, _manager.OutputOn(device, channel)));

    public Task<Result> OutputOffAsync(string device, int channel)
        => Task.Run(() => Map(device, _manager.OutputOff(device, channel)));

    public event Action<PowerSupplyStateChanged>? StateChanged;

    /// <summary>Connects all power supplies and starts polling. Called by the engine during ConnectAll.</summary>
    public async Task ConnectAllInternalAsync(IProgress<string>? progress)
    {
        if (_devices.Count == 0)
        {
            progress?.Report("Power supply: no devices configured.");
            return;
        }

        progress?.Report($"Power supply: connecting {_devices.Count} device(s)…");
        await Task.Run(_manager.ConnectAll).ConfigureAwait(false);
        _poll.Start();

        var connected = _devices.Keys.Count(IsConnected);
        progress?.Report($"Power supply: {connected}/{_devices.Count} device(s) connected.");
        _log(connected == _devices.Count ? LogLevel.Info : LogLevel.Warning,
            $"Power supply ConnectAll — {connected}/{_devices.Count} device(s) connected.");
    }

    public async ValueTask DisposeAsync()
        => await _poll.DisposeAsync().ConfigureAwait(false);

    /// <summary>Runs one poll tick now, so <see cref="GetState"/> is current after a command.</summary>
    public Task RefreshAsync() => PollTickAsync(CancellationToken.None);

    private Task PollTickAsync(CancellationToken ct)
    {
        foreach (var name in _devices.Keys)
        {
            if (ct.IsCancellationRequested) break;
            if (!IsConnected(name)) continue;

            var status = _manager.GetStatus(name);
            if (status == null) continue;

            var next = new PowerSupplyState(
                true,
                status.Connected,
                status.Channels
                    .Select(c => new PowerSupplyChannelState(c.Channel, c.Voltage, c.Current, c.OutputOn))
                    .ToList(),
                status.LastError);

            var prev = _state.TryGetValue(name, out var p) ? p : PowerSupplyState.Unknown;
            if (Same(next, prev)) continue;

            _state[name] = next;
            StateChanged?.Invoke(new PowerSupplyStateChanged(name, next));
            _emitStatus(new HardwareStatusChanged("Power supply", name));
        }
        return Task.CompletedTask;
    }

    // PowerSupplyState carries a channel list — record equality would compare it
    // by reference, so compare scalars + the channels element-wise.
    private static bool Same(PowerSupplyState a, PowerSupplyState b)
        => a.Valid == b.Valid
           && a.Connected == b.Connected
           && a.LastError == b.LastError
           && a.Channels.SequenceEqual(b.Channels);

    private static Result Map(string device, PowerSupplyError error)
        => error switch
        {
            PowerSupplyError.Success => Result.Ok(),
            PowerSupplyError.OutOfRange => Result.Fail(ErrorCode.InvalidParameter,
                $"Power supply '{device}': setpoint outside the channel's rated range (nothing sent)."),
            _ => Result.Fail(ErrorCode.OperationFailed, $"Power supply '{device}': {error}"),
        };
}
