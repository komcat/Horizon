using System.Collections.Concurrent;
using Horizon.Common;
using Horizon.Engine.Polling;
using Horizon.Smu;
using Horizon.Smu.Models;

namespace Horizon.Engine;

/// <summary>
/// SMU facet — wraps <see cref="SmuManager"/> behind engine-owned types, polls
/// unit status, and raises change events.
/// </summary>
internal sealed class SmuFacet : ISmuFacet, IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly SmuManager _manager;
    private readonly Action<HardwareStatusChanged> _emitStatus;
    private readonly Action<LogLevel, string> _log;

    private readonly Dictionary<string, SmuDeviceInfo> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SmuState> _state = new(StringComparer.OrdinalIgnoreCase);
    private readonly PollLoop _poll;

    public SmuFacet(
        SmuManager manager,
        Action<HardwareStatusChanged> emitStatus,
        Action<LogLevel, string> log)
    {
        _manager = manager;
        _emitStatus = emitStatus;
        _log = log;

        foreach (var name in _manager.GetAllDeviceNames())
        {
            var dev = _manager.GetDevice(name);
            _devices[name] = new SmuDeviceInfo(name, dev?.ControllerType ?? "?");
            _state[name] = SmuState.Unknown;
        }

        _poll = new PollLoop("SMU", PollInterval, PollTickAsync);
    }

    public IReadOnlyList<string> DeviceNames => _devices.Keys.ToList();

    public SmuDeviceInfo? GetDevice(string device)
        => _devices.TryGetValue(device, out var info) ? info : null;

    public SmuState GetState(string device)
        => _state.TryGetValue(device, out var s) ? s : SmuState.Unknown;

    public bool IsConnected(string device)
        => _manager.GetDevice(device)?.IsConnected ?? false;

    public Task<Result> ConnectAsync(string device, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var d = _manager.GetDevice(device);
            if (d == null)
                return Result.Fail(ErrorCode.OperationFailed, $"SMU '{device}' not found.");
            return d.Connect()
                ? Result.Ok()
                : Result.Fail(ErrorCode.OperationFailed, $"SMU '{device}' failed to connect.");
        }, ct);

    public Task<Result> DisconnectAsync(string device)
        => Task.Run(() =>
        {
            var d = _manager.GetDevice(device);
            if (d == null)
                return Result.Fail(ErrorCode.OperationFailed, $"SMU '{device}' not found.");
            d.Disconnect();
            return Result.Ok();
        });

    public Task<Result> SetSourceVoltageAsync(string device, double volts)
        => Task.Run(() => Map(device, _manager.SetSourceVoltage(device, volts)));

    public Task<Result> SetSourceCurrentAsync(string device, double amps)
        => Task.Run(() => Map(device, _manager.SetSourceCurrent(device, amps)));

    public Task<Result> SetComplianceAsync(string device, double value)
        => Task.Run(() => Map(device, _manager.SetCompliance(device, value)));

    public Task<Result> OutputOnAsync(string device)
        => Task.Run(() => Map(device, _manager.OutputOn(device)));

    public Task<Result> OutputOffAsync(string device)
        => Task.Run(() => Map(device, _manager.OutputOff(device)));

    public Task<Result<SmuReading>> ReadMeasurementAsync(string device, int channel)
        => Task.Run(() =>
        {
            var d = _manager.GetDevice(device);
            if (d == null)
                return Result<SmuReading>.Fail(ErrorCode.OperationFailed, $"SMU '{device}' not found.");
            if (!d.IsConnected)
                return Result<SmuReading>.Fail(ErrorCode.OperationFailed, $"SMU '{device}' is not connected.");

            var r = _manager.ReadMeasurement(device, channel);
            return r == null
                ? Result<SmuReading>.Fail(ErrorCode.OperationFailed, $"SMU '{device}' read failed.")
                : Result<SmuReading>.Ok(new SmuReading(r.Channel, r.Current, r.Voltage, r.Timestamp));
        });

    public Task<Result> SetFastModeAsync(string device, bool fast)
        => Task.Run(() => Map(device, _manager.SetFastMode(device, fast)));

    public event Action<SmuStateChanged>? StateChanged;

    /// <summary>Connects all SMUs and starts polling.</summary>
    public async Task ConnectAllInternalAsync(IProgress<string>? progress)
    {
        if (_devices.Count == 0)
        {
            progress?.Report("SMU: no devices configured.");
            return;
        }

        progress?.Report($"SMU: connecting {_devices.Count} device(s)…");
        await Task.Run(_manager.ConnectAll).ConfigureAwait(false);
        _poll.Start();

        var connected = _devices.Keys.Count(IsConnected);
        progress?.Report($"SMU: {connected}/{_devices.Count} device(s) connected.");
        _log(connected == _devices.Count ? LogLevel.Info : LogLevel.Warning,
            $"SMU ConnectAll — {connected}/{_devices.Count} device(s) connected.");
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

            var next = new SmuState(
                true,
                status.Connected,
                status.OutputOn,
                status.SourceMode == SmuSourceMode.Current ? "current" : "voltage",
                status.Voltage,
                status.Current,
                status.LastError);

            var prev = _state.TryGetValue(name, out var p) ? p : SmuState.Unknown;
            if (next == prev) continue;

            _state[name] = next;
            StateChanged?.Invoke(new SmuStateChanged(name, next));
            _emitStatus(new HardwareStatusChanged("SMU", name));
        }
        return Task.CompletedTask;
    }

    private static Result Map(string device, SmuError error)
        => error == SmuError.Success
            ? Result.Ok()
            : Result.Fail(ErrorCode.OperationFailed, $"SMU '{device}': {error}");
}
