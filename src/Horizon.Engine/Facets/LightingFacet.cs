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
            var channels = dev?.Channels
                               .Select(c => new LightingChannelInfo(c.Channel, c.Name, c.Group, c.Color))
                               .ToList()
                           ?? new List<LightingChannelInfo>();
            var lights = channels
                .Select(c => string.IsNullOrWhiteSpace(c.Group) ? c.Name : c.Group)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            _devices[id] = new LightingDeviceInfo(
                id, dev?.DisplayName ?? id, dev?.ControllerType ?? "?", channels, dev?.MaxIntensity ?? 255, lights);
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

    public Task<Result> SetNamedChannelAsync(string device, string channelName, int intensity)
        => ApplySelectedAsync(device, $"channel '{channelName}'",
            c => c.Name.Equals(channelName, StringComparison.OrdinalIgnoreCase),
            _ => intensity);

    public Task<Result> SetAllAsync(string device, int intensity)
        => ApplySelectedAsync(device, "all channels", _ => true, _ => intensity);

    public Task<Result> SetGroupAsync(string device, string group, int intensity)
        => ApplySelectedAsync(device, $"group '{group}'",
            c => c.Group.Equals(group, StringComparison.OrdinalIgnoreCase),
            _ => intensity);

    public Task<Result> ConfigureGroupAsync(
        string device, string group, IReadOnlyDictionary<string, int> intensitiesByColor)
    {
        // Channels in the group whose colour is named get that intensity; the
        // rest of the group is turned off (intensity 0).
        var byColor = new Dictionary<string, int>(intensitiesByColor, StringComparer.OrdinalIgnoreCase);
        return ApplySelectedAsync(device, $"group '{group}'",
            c => c.Group.Equals(group, StringComparison.OrdinalIgnoreCase),
            c => byColor.TryGetValue(c.Color, out var v) ? v : 0);
    }

    public Task<Result> SetLightPercentAsync(string device, string light, double percent)
    {
        if (!TryToNative(device, percent, out var native, out var error))
            return Task.FromResult(error);
        return ApplySelectedAsync(device, $"light '{light}'", c => InLight(c, light), _ => native);
    }

    public Task<Result> SetLightColorAsync(string device, string light, LightColor color, double percent)
    {
        if (!TryToNative(device, percent, out var native, out var error))
            return Task.FromResult(error);
        if (color == LightColor.White)
            return ApplySelectedAsync(device, $"light '{light}'", c => InLight(c, light), _ => native);

        var colorName = color.ToString();
        if (_devices.TryGetValue(device, out var info)
            && !info.Channels.Any(c => InLight(c, light) && IsColor(c, colorName)))
            return Task.FromResult(Result.Fail(ErrorCode.InvalidParameter,
                $"Lighting '{device}': light '{light}' has no {colorName.ToLowerInvariant()} channel."));

        // The requested colour at the level; the light's other channels off.
        return ApplySelectedAsync(device, $"light '{light}'",
            c => InLight(c, light),
            c => IsColor(c, colorName) ? native : 0);
    }

    /// <summary>A light is a group name, or the name of an ungrouped channel.</summary>
    private static bool InLight(LightingChannelInfo c, string light)
        => string.IsNullOrWhiteSpace(c.Group)
            ? c.Name.Equals(light, StringComparison.OrdinalIgnoreCase)
            : c.Group.Equals(light, StringComparison.OrdinalIgnoreCase);

    private static bool IsColor(LightingChannelInfo c, string color)
        => c.Color.Equals(color, StringComparison.OrdinalIgnoreCase);

    /// <summary>Validates a 0..100 % request and converts it to controller-native units.</summary>
    private bool TryToNative(string device, double percent, out int native, out Result error)
    {
        native = 0;
        error = Result.Ok();
        if (!_devices.TryGetValue(device, out var info))
        {
            error = Result.Fail(ErrorCode.OperationFailed, $"Lighting controller '{device}' not found.");
            return false;
        }
        if (!double.IsFinite(percent) || percent < 0 || percent > 100)
        {
            error = Result.Fail(ErrorCode.InvalidParameter,
                $"Lighting '{device}': intensity {percent} % is outside 0..100 %.");
            return false;
        }
        native = (int)Math.Round(percent / 100.0 * info.MaxIntensity, MidpointRounding.AwayFromZero);
        return true;
    }

    private static double ToPercent(int native, int max)
        => max > 0 ? Math.Round(native * 100.0 / max, 1) : 0;

    /// <summary>
    /// Applies an intensity to every channel of <paramref name="device"/> that
    /// satisfies <paramref name="match"/>. Best-effort — every matched channel
    /// is attempted; the first failure (if any) is returned.
    /// </summary>
    private Task<Result> ApplySelectedAsync(
        string device, string what,
        Func<LightingChannelInfo, bool> match,
        Func<LightingChannelInfo, int> intensityFor)
        => Task.Run(() =>
        {
            if (!_devices.TryGetValue(device, out var info))
                return Result.Fail(ErrorCode.OperationFailed, $"Lighting controller '{device}' not found.");

            var targets = info.Channels.Where(match).ToList();
            if (targets.Count == 0)
                return Result.Fail(ErrorCode.InvalidParameter,
                    $"Lighting '{device}': {what} matched no channel.");

            Result? firstError = null;
            foreach (var c in targets)
            {
                var error = _manager.SetIntensity(device, c.Channel, intensityFor(c));
                if (error != LightingError.Success)
                    firstError ??= Result.Fail(ErrorCode.OperationFailed,
                        $"Lighting '{device}' channel {c.Channel}: {error}");
            }
            return firstError ?? Result.Ok();
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

    /// <summary>Runs one poll tick now (off the caller's thread), so <see cref="GetState"/> is current after a command.</summary>
    public Task RefreshAsync() => Task.Run(() => PollTickAsync(CancellationToken.None));

    private Task PollTickAsync(CancellationToken ct)
    {
        foreach (var (id, info) in _devices)
        {
            if (ct.IsCancellationRequested) break;
            var device = _manager.GetDevice(id);
            if (device == null) continue;

            // Read back the hardware while connected; a link failure closes the
            // device, and the cached status below then reports it disconnected.
            if (device.IsConnected)
                device.Poll();

            var status = device.GetStatus();
            var next = new LightingState(
                true,
                status.Connected,
                status.Channels
                    .Select(c => new LightingChannelState(
                        c.Channel, c.Name, c.Intensity, c.On, ToPercent(c.Intensity, info.MaxIntensity), c.Mode))
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
