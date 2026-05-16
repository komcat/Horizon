using System.Collections.Concurrent;
using Horizon.Common;
using Horizon.Engine.Polling;
using Horizon.LaserController;
using Horizon.LaserController.Models;

namespace Horizon.Engine;

/// <summary>
/// Laser-controller facet — wraps the kept <see cref="LaserControllerManager"/>
/// behind engine-owned types. The manager has no poll loop, so this facet runs
/// one via the engine <see cref="PollLoop"/>. The CLD101x adapter locks
/// internally, so no facet semaphore is needed.
/// </summary>
internal sealed class LaserFacet : ILaserFacet, IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly LaserControllerManager _manager;
    private readonly Action<HardwareStatusChanged> _emitStatus;
    private readonly Action<LogLevel, string> _log;

    private readonly Dictionary<string, LaserDeviceInfo> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LaserState> _state = new(StringComparer.OrdinalIgnoreCase);
    private readonly PollLoop _poll;

    public LaserFacet(
        LaserControllerManager manager,
        Action<HardwareStatusChanged> emitStatus,
        Action<LogLevel, string> log)
    {
        _manager = manager;
        _emitStatus = emitStatus;
        _log = log;

        foreach (var name in _manager.GetAllDeviceNames())
        {
            var dev = _manager.GetDevice(name);
            _devices[name] = new LaserDeviceInfo(name, dev?.ControllerType ?? "?");
            _state[name] = LaserState.Unknown;
        }

        _poll = new PollLoop("Laser", PollInterval, PollTickAsync);
    }

    public IReadOnlyList<string> DeviceNames => _devices.Keys.ToList();

    public LaserDeviceInfo? GetDevice(string device)
        => _devices.TryGetValue(device, out var info) ? info : null;

    public LaserState GetState(string device)
        => _state.TryGetValue(device, out var s) ? s : LaserState.Unknown;

    public bool IsConnected(string device)
        => _manager.GetDevice(device)?.IsConnected ?? false;

    public Task<Result> ConnectAsync(string device, CancellationToken ct = default)
        => Task.Run(() =>
        {
            if (!_devices.ContainsKey(device))
                return Result.Fail(ErrorCode.LaserDeviceNotFound, $"Laser device '{device}' not found.");
            return _manager.ConnectDevice(device)
                ? Result.Ok()
                : Result.Fail(ErrorCode.LaserCommunicationError, $"Laser device '{device}' failed to connect.");
        }, ct);

    public Task<Result> DisconnectAsync(string device)
        => Task.Run(() => _manager.DisconnectDevice(device)
            ? Result.Ok()
            : Result.Fail(ErrorCode.LaserDeviceNotFound, $"Laser device '{device}' not found."));

    public Task<Result> SetTemperatureAsync(string device, double celsius)
        => Task.Run(() => Map(_manager.SetTemperature(device, celsius), $"SetTemperature '{device}' = {celsius}C"));

    public Task<Result> TecOnAsync(string device)
        => Task.Run(() => Map(_manager.TECOn(device), $"TEC on '{device}'"));

    public Task<Result> TecOffAsync(string device)
        => Task.Run(() => Map(_manager.TECOff(device), $"TEC off '{device}'"));

    public Task<Result> SetLaserCurrentAsync(string device, double milliamps)
        => Task.Run(() => Map(_manager.SetLaserCurrent(device, milliamps), $"SetLaserCurrent '{device}' = {milliamps}mA"));

    public Task<Result> LaserOnAsync(string device)
        => Task.Run(() => Map(_manager.LaserOn(device), $"Laser on '{device}'"));

    public Task<Result> LaserOffAsync(string device)
        => Task.Run(() => Map(_manager.LaserOff(device), $"Laser off '{device}'"));

    /// <summary>Connects all devices and starts polling. Called by the engine during ConnectAll.</summary>
    public async Task ConnectAllInternalAsync(IProgress<string>? progress)
    {
        if (_devices.Count == 0)
        {
            progress?.Report("Laser: no devices configured.");
            return;
        }

        progress?.Report($"Laser: connecting {_devices.Count} device(s)…");
        await Task.Run(_manager.ConnectAll).ConfigureAwait(false);
        _poll.Start();

        var connected = _devices.Keys.Count(IsConnected);
        progress?.Report($"Laser: {connected}/{_devices.Count} device(s) connected.");
        _log(connected == _devices.Count ? LogLevel.Info : LogLevel.Warning,
            $"Laser ConnectAll — {connected}/{_devices.Count} device(s) connected.");
    }

    public async ValueTask DisposeAsync()
        => await _poll.DisposeAsync().ConfigureAwait(false);

    public event Action<LaserStateChanged>? StateChanged;

    // --- internals ---

    private Task PollTickAsync(CancellationToken ct)
    {
        foreach (var name in _devices.Keys)
        {
            if (ct.IsCancellationRequested) break;
            if (!IsConnected(name)) continue;

            var status = _manager.GetStatus(name);
            if (status == null) continue;

            var next = new LaserState(
                true,
                status.Connected,
                status.TECOn,
                status.LaserOn,
                status.CurrentTemperature,
                status.SetpointTemperature,
                status.CurrentLaserCurrent,
                status.SetpointLaserCurrent,
                status.LastError);

            var prev = _state.TryGetValue(name, out var p) ? p : LaserState.Unknown;
            if (next == prev) continue;

            _state[name] = next;
            StateChanged?.Invoke(new LaserStateChanged(name, next));
            _emitStatus(new HardwareStatusChanged("Laser", name));
        }
        return Task.CompletedTask;
    }

    private static Result Map(LaserControllerError error, string what)
        => error == LaserControllerError.Success
            ? Result.Ok()
            : Result.Fail(MapCode(error), $"{what} — {error}.");

    private static ErrorCode MapCode(LaserControllerError error) => error switch
    {
        LaserControllerError.DeviceNotFound     => ErrorCode.LaserDeviceNotFound,
        LaserControllerError.Disconnected       => ErrorCode.LaserDisconnected,
        LaserControllerError.CommunicationError => ErrorCode.LaserCommunicationError,
        LaserControllerError.ParseError         => ErrorCode.LaserParseError,
        _                                       => ErrorCode.LaserOperationFailed,
    };
}
