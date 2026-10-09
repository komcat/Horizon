using System.Collections.Concurrent;
using Horizon.Common;
using Horizon.Engine.Polling;
using Horizon.Motion;
using Horizon.Motion.Models;

namespace Horizon.Engine;

/// <summary>
/// Motion facet — wraps the kept <see cref="UnifiedMotionManager"/> behind
/// engine-owned types. PI/ACS controllers are not thread-safe, so every
/// controller call (facet ops, poll reads, and <see cref="StopAsync"/>) is
/// serialized through a per-device <see cref="SemaphoreSlim"/>. Moves are issued
/// non-blocking, so the lock is only ever held for the brief command/read — STOP
/// never has to wait for a move to finish, only for the in-flight read to return.
/// </summary>
internal sealed class MotionFacet : IMotionFacet, IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    private const int MoveTimeoutMs = 60_000;
    private const int ReferenceTimeoutMs = 180_000;

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

    public async Task<Result> StopAsync(string device)
    {
        if (!_devices.ContainsKey(device))
            return Result.Fail(ErrorCode.MotionDeviceNotFound, $"Motion device '{device}' not found.");

        // STOP must run serialized with the poll reads: the PI/ACS Api is NOT
        // thread-safe, so firing the stop command on the shared socket while the
        // poll loop is mid-read corrupts/drops it (caught as a silent failure) and
        // motion never halts. Moves are non-blocking, so the lock is only ever held
        // for a single quick read — STOP waits at most ~one poll interval for it.
        return await WithLock(device, CancellationToken.None, () =>
            _manager.StopAll(device)
                ? Result.Ok()
                : Result.Fail(ErrorCode.MotionStopFailed, $"Stop failed for '{device}'."))
            .ConfigureAwait(false);
    }

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

    public async Task<Result> SetDeviceVelocityAsync(string device, double velocity)
    {
        if (!_devices.TryGetValue(device, out var info))
            return Result.Fail(ErrorCode.MotionDeviceNotFound, $"Motion device '{device}' not found.");

        // PI hexapods have a single system (path) velocity; ACS is per-axis.
        if (info.ControllerType.Equals("PI", StringComparison.OrdinalIgnoreCase))
            return await SetSystemVelocityAsync(device, velocity).ConfigureAwait(false);

        foreach (var axis in info.Axes)
        {
            var r = await SetVelocityAsync(device, axis, velocity).ConfigureAwait(false);
            if (!r.IsSuccess) return r;
        }
        return Result.Ok();
    }

    public async Task<Result> SetAxisEnabledAsync(string device, string axis, bool enabled)
    {
        var ctrl = _manager.GetController(device);
        if (ctrl == null)
            return Result.Fail(ErrorCode.MotionDeviceNotFound, $"Motion device '{device}' not found.");
        if (!IsAcs(device))
            return Result.Fail(ErrorCode.NotSupported, $"'{device}' has no per-axis enable.");

        var ok = await WithLock(device, CancellationToken.None, () => ctrl.SetAxisEnabled(axis, enabled))
            .ConfigureAwait(false);
        _log(ok ? LogLevel.Info : LogLevel.Warning,
            $"{(enabled ? "Enable" : "Disable")} {device}.{axis} — {(ok ? "ok" : "failed")}.");
        return ok
            ? Result.Ok()
            : Result.Fail(ErrorCode.MotionAxisError, $"{(enabled ? "Enable" : "Disable")} failed: {device}.{axis}.");
    }

    public async Task<Result<bool>> IsBufferRunningAsync(string device, int buffer)
    {
        var ctrl = _manager.GetController(device);
        if (ctrl == null)
            return Result<bool>.Fail(ErrorCode.MotionDeviceNotFound, $"Motion device '{device}' not found.");
        if (!IsAcs(device))
            return Result<bool>.Fail(ErrorCode.NotSupported, $"'{device}' has no program buffers.");

        var running = await WithLock(device, CancellationToken.None, () => ctrl.IsBufferRunning(buffer))
            .ConfigureAwait(false);
        return running.HasValue
            ? Result<bool>.Ok(running.Value)
            : Result<bool>.Fail(ErrorCode.MotionCommunicationError, $"Buffer {buffer} state unreadable on '{device}'.");
    }

    public async Task<Result<string>> SendTerminalCommandAsync(string device, string command)
    {
        var ctrl = _manager.GetController(device);
        if (ctrl == null)
            return Result<string>.Fail(ErrorCode.MotionDeviceNotFound, $"Motion device '{device}' not found.");
        if (!IsAcs(device))
            return Result<string>.Fail(ErrorCode.NotSupported, $"'{device}' has no command terminal.");
        if (string.IsNullOrWhiteSpace(command))
            return Result<string>.Fail(ErrorCode.InvalidParameter, "Empty terminal command.");

        var (ok, reply) = await WithLock(device, CancellationToken.None, () =>
        {
            var sent = ctrl.SendTerminalCommand(command, out var r);
            return (sent, r);
        }).ConfigureAwait(false);
        _log(ok ? LogLevel.Info : LogLevel.Warning,
            $"Terminal {device}: '{command}' — {(ok ? "ok" : "failed")}{(reply.Length > 0 ? $" ({reply})" : "")}.");
        return ok
            ? Result<string>.Ok(reply)
            : Result<string>.Fail(ErrorCode.OperationFailed, $"'{command}' failed on '{device}': {reply}".TrimEnd(' ', ':'));
    }

    public async Task<Result> ReferenceAsync(string device, CancellationToken ct = default)
    {
        var ctrl = _manager.GetController(device);
        if (ctrl == null)
            return Result.Fail(ErrorCode.MotionDeviceNotFound, $"Motion device '{device}' not found.");
        if (!IsPi(device))
            return Result.Fail(ErrorCode.NotSupported, $"'{device}' is not a PI hexapod — no reference move.");

        var started = await WithLock(device, ct, () => ctrl.StartReference() ? (string?)null : ctrl.GetLastErrorText() ?? "")
            .ConfigureAwait(false);
        if (started != null)
        {
            _log(LogLevel.Warning, $"Reference {device} — failed to start. {started}");
            return Result.Fail(ErrorCode.MotionAxisError, $"Reference {device} — failed to start. {started}".TrimEnd());
        }
        _log(LogLevel.Info, $"Reference {device} — started.");

        // A full hexapod FRF takes tens of seconds; poll until all axes report referenced and still.
        var deadline = Environment.TickCount64 + ReferenceTimeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (ct.IsCancellationRequested)
            {
                await StopAsync(device).ConfigureAwait(false);
                return Result.Fail(ErrorCode.OperationFailed, $"Reference {device} — cancelled.");
            }

            await Task.Delay(250, CancellationToken.None).ConfigureAwait(false);
            var (referenced, moving) = await WithLock(device, CancellationToken.None,
                () => (ctrl.IsReferenced(), _manager.IsMoving(device))).ConfigureAwait(false);
            if (referenced == true && !moving)
            {
                _log(LogLevel.Info, $"Reference {device} — done.");
                return Result.Ok();
            }
        }

        await StopAsync(device).ConfigureAwait(false);
        _log(LogLevel.Warning, $"Reference {device} — timed out.");
        return Result.Fail(ErrorCode.MotionTimeout, $"Reference {device} — timed out.");
    }

    public async Task<Result<PivotPoint>> GetPivotAsync(string device)
    {
        var ctrl = _manager.GetController(device);
        if (ctrl == null)
            return Result<PivotPoint>.Fail(ErrorCode.MotionDeviceNotFound, $"Motion device '{device}' not found.");
        if (!IsPi(device))
            return Result<PivotPoint>.Fail(ErrorCode.NotSupported, $"'{device}' has no pivot point.");

        var p = await WithLock(device, CancellationToken.None, ctrl.GetPivot).ConfigureAwait(false);
        return p.HasValue
            ? Result<PivotPoint>.Ok(new PivotPoint(p.Value.R, p.Value.S, p.Value.T))
            : Result<PivotPoint>.Fail(ErrorCode.MotionCommunicationError, $"Pivot point unreadable on '{device}'.");
    }

    public async Task<Result> SetPivotAsync(string device, PivotPoint pivot)
    {
        var ctrl = _manager.GetController(device);
        if (ctrl == null)
            return Result.Fail(ErrorCode.MotionDeviceNotFound, $"Motion device '{device}' not found.");
        if (!IsPi(device))
            return Result.Fail(ErrorCode.NotSupported, $"'{device}' has no pivot point.");
        if (!double.IsFinite(pivot.R) || !double.IsFinite(pivot.S) || !double.IsFinite(pivot.T))
            return Result.Fail(ErrorCode.InvalidParameter, $"Pivot point {pivot} is not a finite coordinate.");

        var error = await WithLock(device, CancellationToken.None,
            () => ctrl.SetPivot(pivot.R, pivot.S, pivot.T) ? (string?)null : ctrl.GetLastErrorText() ?? "").ConfigureAwait(false);
        if (error != null)
        {
            _log(LogLevel.Warning, $"Set pivot {device} {pivot} — failed. {error}");
            return Result.Fail(ErrorCode.MotionAxisError, $"Set pivot {device} {pivot} failed. {error}".TrimEnd());
        }
        _log(LogLevel.Info, $"Set pivot {device} {pivot}.");
        return Result.Ok();
    }

    private bool IsPi(string device)
        => _devices.TryGetValue(device, out var info)
           && info.ControllerType.Equals("PI", StringComparison.OrdinalIgnoreCase);

    private bool IsAcs(string device)
        => _devices.TryGetValue(device, out var info)
           && info.ControllerType.Equals("ACS", StringComparison.OrdinalIgnoreCase);

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
                    var ctrl = _manager.GetController(name);
                    var enabled = ctrl?.GetEnabledMask();
                    var referenced = ctrl?.IsReferenced();
                    return new MotionDeviceState(p != null, moving,
                        p != null ? ToPosition(p) : MotionPosition.Zero, enabled, referenced);
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
