using System.Collections.Concurrent;
using Horizon.Camera;
using Horizon.Camera.Interfaces;
using Horizon.Camera.Models;
using Horizon.Common;
using Horizon.Engine.Polling;

namespace Horizon.Engine;

/// <summary>
/// Camera facet — wraps the kept <see cref="CameraManager"/> behind engine-owned
/// types. Registers itself as the camera broadcast subscriber, turning grabbed
/// frames into the engine's <see cref="FrameReady"/> stream (raw bytes only).
/// </summary>
internal sealed class CameraFacet : ICameraFacet, ICameraFrameSubscriber, IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly CameraManager _manager;
    private readonly Action<HardwareStatusChanged> _emitStatus;
    private readonly Action<LogLevel, string> _log;

    private readonly Dictionary<string, CameraDeviceInfo> _cameras = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CameraDeviceState> _state = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _frameCounts = new(StringComparer.Ordinal);
    private readonly PollLoop _poll;

    public CameraFacet(
        CameraManager manager,
        Action<HardwareStatusChanged> emitStatus,
        Action<LogLevel, string> log)
    {
        _manager = manager;
        _emitStatus = emitStatus;
        _log = log;

        foreach (var id in _manager.GetCameraIds())
        {
            var status = _manager.GetCameraStatus(id);
            _cameras[id] = new CameraDeviceInfo(
                id,
                _manager.GetCameraDisplayName(id),
                status.ModelName,
                status.Type.ToString(),
                status.SerialNumber);
            _state[id] = CameraDeviceState.Unknown;
            _frameCounts[id] = 0;
        }

        _manager.SubscribeToFrames(this);
        _poll = new PollLoop("Camera", PollInterval, PollTickAsync);
    }

    public IReadOnlyList<string> CameraIds => _cameras.Keys.ToList();

    public CameraDeviceInfo? GetCamera(string id)
        => _cameras.TryGetValue(id, out var info) ? info : null;

    public CameraDeviceState GetState(string id)
        => _state.TryGetValue(id, out var s) ? s : CameraDeviceState.Unknown;

    public bool IsConnected(string id)
        => _manager.GetCameraHardware(id)?.IsConnected ?? false;

    public Task<Result> ConnectAsync(string id, CancellationToken ct = default)
        => Task.Run(() =>
        {
            if (!_cameras.ContainsKey(id))
                return Result.Fail(ErrorCode.CameraNotFound, $"Camera '{id}' not found.");
            return _manager.ConnectCamera(id)
                ? Result.Ok()
                : Result.Fail(ErrorCode.CameraConnectionFailed, $"Camera '{id}' failed to connect.");
        }, ct);

    public Task<Result> DisconnectAsync(string id)
        => Task.Run(() => _manager.DisconnectCamera(id)
            ? Result.Ok()
            : Result.Fail(ErrorCode.CameraNotFound, $"Camera '{id}' not found."));

    public Task<Result> StartGrabbingAsync(string id)
        => Task.Run(() => _manager.StartGrabbingWithBroadcast(id)
            ? Result.Ok()
            : Result.Fail(ErrorCode.CameraGrabFailed, $"Camera '{id}' failed to start grabbing."));

    public Task<Result> StopGrabbingAsync(string id)
        => Task.Run(() => _manager.StopGrabbing(id)
            ? Result.Ok()
            : Result.Fail(ErrorCode.CameraGrabFailed, $"Camera '{id}' failed to stop grabbing."));

    public Task<Result> ApplyExposureAsync(string id, CameraExposure exposure)
        => Task.Run(() =>
        {
            var settings = new ExposureSettings
            {
                ExposureTimeUs = exposure.ExposureUs,
                Gain = exposure.Gain,
                AutoExposure = exposure.AutoExposure,
                AutoGain = exposure.AutoGain,
                FrameRate = exposure.FrameRate,
            };
            return _manager.ApplyExposureSettings(id, settings)
                ? Result.Ok()
                : Result.Fail(ErrorCode.CameraNotConnected, $"Camera '{id}' — exposure not applied.");
        });

    public Task<Result<string>> SnapToFileAsync(string id)
        => Task.Run(() =>
        {
            if (!_cameras.ContainsKey(id))
                return Result<string>.Fail(ErrorCode.CameraNotFound, $"Camera '{id}' not found.");
            return _manager.CaptureImage(id)
                ? Result<string>.Ok(_manager.GetLastCapturedImagePath())
                : Result<string>.Fail(ErrorCode.CameraCaptureFailed, $"Camera '{id}' — capture failed.");
        });

    /// <summary>Connects all cameras and starts polling. Called by the engine during ConnectAll.</summary>
    public async Task ConnectAllInternalAsync(IProgress<string>? progress)
    {
        if (_cameras.Count == 0)
        {
            progress?.Report("Camera: no cameras configured.");
            return;
        }

        progress?.Report($"Camera: connecting {_cameras.Count} camera(s)…");
        await Task.Run(() =>
        {
            foreach (var id in _cameras.Keys)
                _manager.ConnectCamera(id);
        }).ConfigureAwait(false);
        _poll.Start();

        var connected = _cameras.Keys.Count(IsConnected);
        progress?.Report($"Camera: {connected}/{_cameras.Count} camera(s) connected.");
        _log(connected == _cameras.Count ? LogLevel.Info : LogLevel.Warning,
            $"Camera ConnectAll — {connected}/{_cameras.Count} camera(s) connected.");
    }

    public async ValueTask DisposeAsync()
    {
        _manager.UnsubscribeFromFrames(SubscriberId);
        await _poll.DisposeAsync().ConfigureAwait(false);
    }

    public event Action<CameraFrame>? FrameReady;
    public event Action<CameraStateChanged>? StateChanged;

    // --- ICameraFrameSubscriber ---

    public string SubscriberId => "engine-camera-facet";
    public int MinFrameIntervalMs => 0;
    public bool WantsFramesFromCamera(string cameraId) => true;

    public void OnNewFrame(CameraFrameData frame)
    {
        _frameCounts.AddOrUpdate(frame.CameraId, 1, (_, c) => c + 1);
        FrameReady?.Invoke(new CameraFrame(
            frame.CameraId, frame.Width, frame.Height, frame.Channels, frame.ImageData, frame.FrameNumber));
    }

    public void OnCameraStatusChanged(string cameraId, bool connected, bool grabbing)
    {
        // Connection/grab state is owned by the poll loop — nothing to do here.
    }

    // --- internals ---

    private Task PollTickAsync(CancellationToken ct)
    {
        foreach (var id in _cameras.Keys)
        {
            if (ct.IsCancellationRequested) break;

            var status = _manager.GetCameraStatus(id);
            var frames = _frameCounts.TryGetValue(id, out var c) ? c : 0;
            var next = new CameraDeviceState(
                status.Connected,
                status.Grabbing,
                status.LiveImageWidth,
                status.LiveImageHeight,
                ToExposure(status.ExposureSettings),
                frames);

            var prev = _state.TryGetValue(id, out var p) ? p : CameraDeviceState.Unknown;
            if (next == prev) continue;

            _state[id] = next;
            StateChanged?.Invoke(new CameraStateChanged(id, next));
            _emitStatus(new HardwareStatusChanged("Camera", id));
        }
        return Task.CompletedTask;
    }

    private static CameraExposure ToExposure(ExposureSettings e)
        => new(e.ExposureTimeUs, e.Gain, e.AutoExposure, e.AutoGain, e.FrameRate);
}
