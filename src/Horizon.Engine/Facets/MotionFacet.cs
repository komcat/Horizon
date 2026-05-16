using System.Collections.Concurrent;
using Horizon.Common;
using Horizon.Engine.Polling;
using Horizon.Motion;
using Horizon.Motion.Models;

namespace Horizon.Engine;

/// <summary>
/// Motion facet — wraps the kept <see cref="UnifiedMotionManager"/> behind
/// engine-owned types. PI/ACS controllers are not thread-safe, so every
/// controller call (facet ops and poll reads) is serialized through a
/// per-device <see cref="SemaphoreSlim"/>. <see cref="StopAsync"/> is the only
/// exception — it must never queue behind a move.
/// </summary>
internal sealed class MotionFacet : IMotionFacet, IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    private const int MoveTimeoutMs = 60_000;

    private readonly UnifiedMotionManager _manager;
    private readonly Action<HardwareStatusChanged> _emitStatus;
    private readonly Action<LogLevel, string> _log;

    private readonly Dictionary<string, MotionDeviceInfo> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, MotionDeviceState> _state = new(StringComparer.OrdinalIgnoreCase);
    private readonly PollLoop _poll;

    public MotionFacet(
        UnifiedMotionManager manager,
        Action<HardwareStatusChanged> emitStatus,
        Action<LogLevel, string> log)
    {
        _manager = manager;
        _emitStatus = emitStatus;
        _log = log;

        foreach (var name in _manager.GetAllDeviceNames())
        {
            var ctrl = _manager.GetController(name);
            var type = ctrl?.ControllerType ?? "?";
            var axes = ctrl?.AxisNames?.ToList() ?? new List<string>();
            _devices[name] = new MotionDeviceInfo(name, type, axes);
            _locks[name] = new SemaphoreSlim(1, 1);
            _state[name] = MotionDeviceState.Unknown;
        }

        _poll = new PollLoop("Motion", PollInterval, PollTickAsync);
    }

    public IReadOnlyList<string> DeviceNames => _devices.Keys.ToList();

    public MotionDeviceInfo? GetDevice(string device)
        => _devices.TryGetValue(device, out var info) ? info : null;

    public MotionDeviceState GetState(string device)
        => _state.TryGetValue(device, out var s) ? s : MotionDeviceState.Unknown;

    public bool IsConnected(string device)
        => _manager.GetController(device)?.IsConnected ?? false;

    public async Task<Result> ConnectAsync(string device, CancellationToken ct = default)
    {
        if (!_devices.ContainsKey(device))
            return Result.Fail(ErrorCode.MotionDeviceNotFound, $"Motion device '{device}' not found.");
        return await WithLock(device, ct, () =>
            _manager.ConnectDevice(device)
                ? Result.Ok()
                : Result.Fail(ErrorCode.MotionCommunicationError, $"Motion device '{device}' failed to connect."))
            .ConfigureAwait(false);
    }

    public async Task<Result> DisconnectAsync(string device)
    {
        var ctrl = _manager.GetController(device);
        if (ctrl == null)
            return Result.Fail(ErrorCode.MotionDeviceNotFound, $"Motion device '{device}' not found.");
        return await WithLock(device, CancellationToken.None, () =>
        {
            ctrl.Disconnect();
            return Result.Ok();
        }).ConfigureAwait(false);
    }

    public Task<Result> MoveAbsoluteAsync(string device, string axis, double position, CancellationToken ct = default)
        => MoveAsync(device, ct,
            () => _manager.MoveToAbs(device, axis, position, blocking: false),
            $"MoveAbs {device}.{axis} -> {position}");

    public Task<Result> MoveRelativeAsync(string device, string axis, double distance, CancellationToken ct = default)
        => MoveAsync(device, ct,
            () => _manager.MoveRelative(device, axis, distance, blocking: false),
            $"MoveRel {device}.{axis} {distance:+0.####;-0.####}");

    public Task<Result> MoveToPositionAsync(string device, MotionPosition target, CancellationToken ct = default)
        => MoveAsync(device, ct,
            () => _manager.MoveToAbs6(device, ToPositionStruct(target), blocking: false),
            $"MoveToPosition {device}");

    public Task<Result> StopAsync(string device)
        => Task.Run(() => _manager.StopAll(device)
            ? Result.Ok()
            : Result.Fail(ErrorCode.MotionStopFailed, $"Stop failed for '{device}'."));

    public async Task<Result<MotionPosition>> GetPositionAsync(string device)
    {
        if (!_devices.ContainsKey(device))
            return Result<MotionPosition>.Fail(ErrorCode.MotionDeviceNotFound, $"Motion device '{device}' not found.");
        return await WithLock(device, CancellationToken.None, () =>
        {
            var p = _manager.GetCurrentPosition(device);
            return p == null
                ? Result<MotionPosition>.Fail(ErrorCode.MotionCommunicationError, $"No position from '{device}'.")
                : Result<MotionPosition>.Ok(ToPosition(p));
        }).ConfigureAwait(false);
    }

    public async Task<Result> SetVelocityAsync(string device, string axis, double velocity)
    {
        if (!_devices.ContainsKey(device))
            return Result.Fail(ErrorCode.MotionDeviceNotFound, $"Motion device '{device}' not found.");
        return await WithLock(device, CancellationToken.None, () =>
            _manager.SetVelocity(device, axis, velocity)
                ? Result.Ok()
                : Result.Fail(ErrorCode.MotionAxisError, $"SetVelocity failed: {device}.{axis}."))
            .ConfigureAwait(false);
    }

    public async Task<Result<double>> GetVelocityAsync(string device, string axis)
    {
        if (!_devices.ContainsKey(device))
            return Result<double>.Fail(ErrorCode.MotionDeviceNotFound, $"Motion device '{device}' not found.");
        return await WithLock(device, CancellationToken.None, () =>
            Result<double>.Ok(_manager.GetVelocity(device, axis))).ConfigureAwait(false);
    }

    public async Task<Result> SetSystemVelocityAsync(string device, double velocity)
    {
        if (!_devices.ContainsKey(device))
            return Result.Fail(ErrorCode.MotionDeviceNotFound, $"Motion device '{device}' not found.");
        return await WithLock(device, CancellationToken.None, () =>
            _manager.SetSystemVelocity(device, velocity)
                ? Result.Ok()
                : Result.Fail(ErrorCode.MotionAxisError, $"SetSystemVelocity failed: {device}."))
            .ConfigureAwait(false);
    }

    public async Task<Result<double>> GetSystemVelocityAsync(string device)
    {
        if (!_devices.ContainsKey(device))
            return Result<double>.Fail(ErrorCode.MotionDeviceNotFound, $"Motion device '{device}' not found.");
        return await WithLock(device, CancellationToken.None, () =>
            Result<double>.Ok(_manager.GetSystemVelocity(device))).ConfigureAwait(false);
    }

    /// <summary>Connects all motion devices and starts polling. Called by the engine during ConnectAll.</summary>
    public async Task ConnectAllInternalAsync(IProgress<string>? progress)
    {
        if (_devices.Count == 0)
        {
            progress?.Report("Motion: no devices configured.");
            return;
        }

        progress?.Report($"Motion: connecting {_devices.Count} device(s)…");
        await Task.Run(_manager.ConnectAll).ConfigureAwait(false);
        _poll.Start();

        var connected = _devices.Keys.Count(IsConnected);
        progress?.Report($"Motion: {connected}/{_devices.Count} device(s) connected.");
        _log(connected == _devices.Count ? LogLevel.Info : LogLevel.Warning,
            $"Motion ConnectAll — {connected}/{_devices.Count} device(s) connected.");
    }

    public async ValueTask DisposeAsync()
        => await _poll.DisposeAsync().ConfigureAwait(false);

    public event Action<MotionStateChanged>? StateChanged;

    // --- internals ---

    private async Task<Result> MoveAsync(string device, CancellationToken ct, Func<bool> startMove, string what)
    {
        if (!_devices.ContainsKey(device))
            return Result.Fail(ErrorCode.MotionDeviceNotFound, $"Motion device '{device}' not found.");

        var started = await WithLock(device, ct, startMove).ConfigureAwait(false);
        if (!started)
            return Result.Fail(ErrorCode.MotionAxisError, $"{what} — failed to start.");

        var deadline = Environment.TickCount64 + MoveTimeoutMs;
        while (true)
        {
            if (ct.IsCancellationRequested)
            {
                await StopAsync(device).ConfigureAwait(false);
                return Result.Fail(ErrorCode.OperationFailed, $"{what} — cancelled.");
            }
            if (Environment.TickCount64 > deadline)
            {
                await StopAsync(device).ConfigureAwait(false);
                return Result.Fail(ErrorCode.MotionTimeout, $"{what} — timed out.");
            }

            var moving = await WithLock(device, CancellationToken.None, () => _manager.IsMoving(device))
                .ConfigureAwait(false);
            if (!moving) break;

            await Task.Delay(50).ConfigureAwait(false);
        }
        return Result.Ok();
    }

    private async Task PollTickAsync(CancellationToken ct)
    {
        foreach (var name in _devices.Keys)
        {
            if (ct.IsCancellationRequested) break;
            if (!IsConnected(name)) continue;

            MotionDeviceState next;
            try
            {
                next = await WithLock(name, ct, () =>
                {
                    var p = _manager.GetCurrentPosition(name);
                    var moving = _manager.IsMoving(name);
                    return new MotionDeviceState(p != null, moving,
                        p != null ? ToPosition(p) : MotionPosition.Zero);
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var prev = _state.TryGetValue(name, out var s) ? s : MotionDeviceState.Unknown;
            if (next == prev) continue;

            _state[name] = next;
            StateChanged?.Invoke(new MotionStateChanged(name, next));
            _emitStatus(new HardwareStatusChanged("Motion", name));
        }
    }

    private async Task<T> WithLock<T>(string device, CancellationToken ct, Func<T> action)
    {
        var sem = _locks[device];
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(action).ConfigureAwait(false);
        }
        finally
        {
            sem.Release();
        }
    }

    private static MotionPosition ToPosition(PositionStruct p)
        => new(p.X, p.Y, p.Z, p.U, p.V, p.W);

    private static PositionStruct ToPositionStruct(MotionPosition p)
        => new() { X = p.X, Y = p.Y, Z = p.Z, U = p.U, V = p.V, W = p.W };
}
