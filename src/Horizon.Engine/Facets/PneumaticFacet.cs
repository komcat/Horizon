using System.Collections.Concurrent;
using Horizon.Common;
using Horizon.Engine.Polling;
using Horizon.Pneumatic;
using Horizon.Pneumatic.Models;

namespace Horizon.Engine;

/// <summary>
/// Pneumatic facet — wraps the kept <see cref="PneumaticManager"/> behind
/// engine-owned types. Slides actuate through <see cref="Horizon.IO"/>, which
/// serializes internally, so this facet needs no per-device lock.
/// </summary>
internal sealed class PneumaticFacet : IPneumaticFacet, IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    private readonly PneumaticManager _manager;
    private readonly Action<HardwareStatusChanged> _emitStatus;
    private readonly Action<LogLevel, string> _log;

    private readonly Dictionary<string, PneumaticSlideInfo> _slides = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PneumaticSlideState> _state = new(StringComparer.OrdinalIgnoreCase);
    private readonly PollLoop _poll;

    public PneumaticFacet(
        PneumaticManager manager,
        Action<HardwareStatusChanged> emitStatus,
        Action<LogLevel, string> log)
    {
        _manager = manager;
        _emitStatus = emitStatus;
        _log = log;

        foreach (var name in _manager.GetAllSlideNames())
        {
            _slides[name] = new PneumaticSlideInfo(name);
            _state[name] = PneumaticSlideState.Unknown;
        }

        _poll = new PollLoop("Pneumatic", PollInterval, PollTickAsync);
    }

    public IReadOnlyList<string> SlideNames => _slides.Keys.ToList();

    public PneumaticSlideInfo? GetSlide(string slide)
        => _slides.TryGetValue(slide, out var info) ? info : null;

    public PneumaticSlideState GetState(string slide)
        => _state.TryGetValue(slide, out var s) ? s : PneumaticSlideState.Unknown;

    public Task<Result> ExtendAsync(string slide, CancellationToken ct = default)
        => Task.Run(() => Map(_manager.Extend(slide), $"Extend '{slide}'"), ct);

    public Task<Result> RetractAsync(string slide, CancellationToken ct = default)
        => Task.Run(() => Map(_manager.Retract(slide), $"Retract '{slide}'"), ct);

    public Task<Result> ExtendAndWaitAsync(string slide, CancellationToken ct = default)
        => Task.Run(() => Map(_manager.ExtendAndWait(slide, ct: ct), $"ExtendAndWait '{slide}'"), ct);

    public Task<Result> RetractAndWaitAsync(string slide, CancellationToken ct = default)
        => Task.Run(() => Map(_manager.RetractAndWait(slide, ct: ct), $"RetractAndWait '{slide}'"), ct);

    /// <summary>Starts polling. Called by the engine during ConnectAll (slides need no connection).</summary>
    public Task ConnectAllInternalAsync(IProgress<string>? progress)
    {
        if (_slides.Count == 0)
        {
            progress?.Report("Pneumatic: no slides configured.");
            return Task.CompletedTask;
        }

        _poll.Start();
        progress?.Report($"Pneumatic: {_slides.Count} slide(s) ready.");
        _log(LogLevel.Info, $"Pneumatic ready — {_slides.Count} slide(s).");
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
        => await _poll.DisposeAsync().ConfigureAwait(false);

    public event Action<PneumaticStateChanged>? StateChanged;

    // --- internals ---

    private Task PollTickAsync(CancellationToken ct)
    {
        foreach (var name in _slides.Keys)
        {
            if (ct.IsCancellationRequested) break;

            var slide = _manager.GetSlide(name);
            if (slide == null) continue;

            var s = slide.GetStatus();
            var next = new PneumaticSlideState(
                s.Valid,
                MapPosition(s.Position),
                s.OutputActive,
                s.ExtendedSensorActive,
                s.RetractedSensorActive);

            var prev = _state.TryGetValue(name, out var p) ? p : PneumaticSlideState.Unknown;
            if (next == prev) continue;

            _state[name] = next;
            StateChanged?.Invoke(new PneumaticStateChanged(name, next));
            _emitStatus(new HardwareStatusChanged("Pneumatic", name));
        }
        return Task.CompletedTask;
    }

    private static PneumaticPosition MapPosition(Horizon.Pneumatic.Models.PneumaticPosition p) => p switch
    {
        Horizon.Pneumatic.Models.PneumaticPosition.Extended      => PneumaticPosition.Extended,
        Horizon.Pneumatic.Models.PneumaticPosition.Retracted     => PneumaticPosition.Retracted,
        Horizon.Pneumatic.Models.PneumaticPosition.Extending     => PneumaticPosition.Extending,
        Horizon.Pneumatic.Models.PneumaticPosition.Retracting    => PneumaticPosition.Retracting,
        Horizon.Pneumatic.Models.PneumaticPosition.HardwareError => PneumaticPosition.HardwareError,
        Horizon.Pneumatic.Models.PneumaticPosition.Timeout       => PneumaticPosition.Timeout,
        _                                                        => PneumaticPosition.Unknown,
    };

    private static Result Map(PneumaticError error, string what) => error switch
    {
        PneumaticError.Success           => Result.Ok(),
        PneumaticError.SlideNotFound     => Result.Fail(ErrorCode.PneumaticSlideNotFound, $"Slide not found: {what}."),
        PneumaticError.IOManagerNull     => Result.Fail(ErrorCode.PneumaticIOManagerNull, $"IO manager null: {what}."),
        PneumaticError.DeviceNotFound    => Result.Fail(ErrorCode.PneumaticDeviceNotFound, $"IO device not found: {what}."),
        PneumaticError.PinNotFound       => Result.Fail(ErrorCode.PneumaticPinNotFound, $"IO pin not found: {what}."),
        PneumaticError.Timeout           => Result.Fail(ErrorCode.PneumaticTimeout, $"Timed out: {what}."),
        PneumaticError.AlreadyAtPosition => Result.Fail(ErrorCode.PneumaticAlreadyAtPosition, $"Already at position: {what}."),
        PneumaticError.HardwareError     => Result.Fail(ErrorCode.PneumaticHardwareError, $"Hardware error: {what}."),
        _                                => Result.Fail(ErrorCode.PneumaticOperationFailed, $"Operation failed: {what}."),
    };
}
