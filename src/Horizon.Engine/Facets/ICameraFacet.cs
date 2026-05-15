using Horizon.Common;

namespace Horizon.Engine;

/// <summary>
/// Camera domain of the engine — Basler Pylon, IDS Peak, and uEye cameras.
/// Exposes engine-owned types only; frames are delivered as raw bytes.
/// </summary>
public interface ICameraFacet
{
    /// <summary>Ids of all configured cameras.</summary>
    IReadOnlyList<string> CameraIds { get; }

    /// <summary>Static camera description, or null if the id is unknown.</summary>
    CameraDeviceInfo? GetCamera(string id);

    /// <summary>Last polled state for a camera. <see cref="CameraDeviceState.Unknown"/> if unknown.</summary>
    CameraDeviceState GetState(string id);

    /// <summary>Whether the camera connection is currently open.</summary>
    bool IsConnected(string id);

    Task<Result> ConnectAsync(string id, CancellationToken ct = default);
    Task<Result> DisconnectAsync(string id);

    /// <summary>Starts continuous grabbing; frames flow on <see cref="FrameReady"/>.</summary>
    Task<Result> StartGrabbingAsync(string id);
    Task<Result> StopGrabbingAsync(string id);

    /// <summary>Applies exposure / gain settings.</summary>
    Task<Result> ApplyExposureAsync(string id, CameraExposure exposure);

    /// <summary>Captures a single image to a file; returns the saved path.</summary>
    Task<Result<string>> SnapToFileAsync(string id);

    /// <summary>Raised for every grabbed frame — raw pixel bytes, no UI types.</summary>
    event Action<CameraFrame>? FrameReady;

    /// <summary>Raised (edge-triggered) when a camera's polled state changes.</summary>
    event Action<CameraStateChanged>? StateChanged;
}
