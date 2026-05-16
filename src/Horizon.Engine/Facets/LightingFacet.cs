using System.Collections.Concurrent;
using Horizon.Common;
using Horizon.Engine.Polling;
using Horizon.Lighting;
using Horizon.Lighting.Models;

namespace Horizon.Engine;

/// <summary>
/// Lighting facet — wraps <see cref="LightingManager"/> behind engine-owned
/// types, polls controller state, and raises change events.
/// </summary>
internal sealed class LightingFacet : ILightingFacet, IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly LightingManager _manager;
    private readonly Action<HardwareStatusChanged> _emitStatus;
    private readonly Action<LogLevel, string> _log;

    private readonly Dictionary<string, LightingDeviceInfo> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LightingState> _state = new(StringComparer.OrdinalIgnoreCase);
    private readonly PollLoop _poll;

    public LightingFacet(
        LightingManager manager,
        Action<HardwareStatusChanged> emitStatus,
        Action<LogLevel, string> log)
    {
        _manager = manager;
        _emitStatus = emitStatus;
        _log = log;

        foreach (var id in _manager.GetAllDeviceIds())
        {
            var dev = _manager.GetDevice(id);
            var channels = dev?.Channels.Select(c => new LightingChannelInfo(c.Channel, c.Name)).ToList()
                           ?? new List<LightingChannelInfo>();
            _devices[id] = new LightingDeviceInfo(id, dev?.DisplayName ?? id, dev?.ControllerType ?? "?", channels);
            _state[id] = LightingState.Unknown;
        }

        _poll = new PollLoop("Lighting", PollInterval, PollTickAsync);
    }

    public IReadOnlyList<string> DeviceNames => _devices.Keys.ToList();

    public LightingDeviceInfo? GetDevice(string device)
        => _devices.TryGetValue(device, out var info) ? info : null;

    public LightingState GetState(string device)
        => _state.TryGetValue(device, out var s) ? s : LightingState.Unknown;

    public bool IsConnected(string device)
        => _manager.GetDevice(device)?.IsConnected ?? false;

    public Task<Result> ConnectAsync(string device, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var d = _manager.GetDevice(device);
            if (d == null)
                return Result.Fail(ErrorCode.OperationFailed, $"Lighting controller '{device}' not found.");
            return d.Connect()
                ? Result.Ok()
                : Result.Fail(ErrorCode.OperationFailed, $"Lighting controller '{device}' failed to connect.");
        }, ct);

    public Task<Result> DisconnectAsync(string device)
        => Task.Run(() =>
        {
            var d = _manager.GetDevice(device);
            if (d == null)
                return Result.Fail(ErrorCode.OperationFailed, $"Lighting controller '{device}' not found.");
            d.Disconnect();
            return Result.Ok();
        });

    public Task<Result> SetChannelIntensityAsync(string device, int channel, int intensity)
        => Task.Run(() =>
        {
            var error = _manager.SetIntensity(device, channel, intensity);
            return error == LightingError.Success
                ? Result.Ok()
                : Result.Fail(ErrorCode.OperationFailed, $"Lighting '{device}' channel {channel}: {error}");
        });

    public event Action<LightingStateChanged>? StateChanged;

    /// <summary>Connects all lighting controllers and starts polling.</summary>
    public async Task ConnectAllInternalAsync(IProgress<string>? progress)
    {
        if (_devices.Count == 0)
        {
            progress?.Report("Lighting: no controllers configured.");
            return;
        }

        progress?.Report($"Lighting: connecting {_devices.Count} controller(s)…");
        await Task.Run(_manager.ConnectAll).ConfigureAwait(false);
        _poll.Start();

        var connected = _devices.Keys.Count(IsConnected);
        progress?.Report($"Lighting: {connected}/{_devices.Count} controller(s) connected.");
        _log(connected == _devices.Count ? LogLevel.Info : LogLevel.Warning,
            $"Lighting ConnectAll — {connected}/{_devices.Count} controller(s) connected.");
    }

    public async ValueTask DisposeAsync()
        => await _poll.DisposeAsync().ConfigureAwait(false);

    /// <summary>Runs one poll tick now, so <see cref="GetState"/> is current after a command.</summary>
    public Task RefreshAsync() => PollTickAsync(CancellationToken.None);

    private Task PollTickAsync(CancellationToken ct)
    {
        foreach (var id in _devices.Keys)
        {
            if (ct.IsCancellationRequested) break;
            if (!IsConnected(id)) continue;

            var status = _manager.GetStatus(id);
            if (status == null) continue;

            var next = new LightingState(
                true,
                status.Connected,
                status.Channels
                    .Select(c => new LightingChannelState(c.Channel, c.Name, c.Intensity))
                    .ToList(),
                status.LastError);

            var prev = _state.TryGetValue(id, out var p) ? p : LightingState.Unknown;
            if (Same(next, prev)) continue;

            _state[id] = next;
            StateChanged?.Invoke(new LightingStateChanged(id, next));
            _emitStatus(new HardwareStatusChanged("Lighting", id));
        }
        return Task.CompletedTask;
    }

    // LightingState carries a channel list — record equality would compare it
    // by reference, so compare scalars + the channels element-wise.
    private static bool Same(LightingState a, LightingState b)
        => a.Valid == b.Valid
           && a.Connected == b.Connected
           && a.LastError == b.LastError
           && a.Channels.SequenceEqual(b.Channels);
}
