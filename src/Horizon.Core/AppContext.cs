using Horizon.Camera;

namespace Horizon.Core;

public sealed class AppContext : IAppContext, IDisposable
{
    private CameraManager? _cameraManager;
    private readonly CameraConfigStore _configStore = new();
    private bool _disposed;

    public CameraManager CameraManager =>
        _cameraManager ?? throw new InvalidOperationException("AppContext is not initialized.");

    public CameraConfigStore ConfigStore => _configStore;

    public bool IsInitialized => _cameraManager != null;

    public ILogSink? LogSink { get; set; }

    public void Initialize()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AppContext));

        Shutdown();

        _cameraManager = new CameraManager();
        Log("Camera manager created");

        // Discover Pylon cameras
        try
        {
            var pylonCameras = Horizon.Camera.CameraManager.DiscoverPylonCameras();
            Log($"Pylon discovery: {pylonCameras.Count} camera(s)");
            foreach (var cam in pylonCameras)
                Log($"  Pylon: {cam.ModelName} ({cam.SerialNumber})");
        }
        catch (Exception ex)
        {
            Log($"Pylon discovery error: {ex.Message}");
        }

        // Discover IDS cameras
        try
        {
            var idsCameras = Horizon.Camera.CameraManager.DiscoverIDSCameras();
            Log($"IDS discovery: {idsCameras.Count} camera(s)");
            foreach (var cam in idsCameras)
                Log($"  IDS: {cam.ModelName} ({cam.SerialNumber}) [{cam.Description}]");
        }
        catch (Exception ex)
        {
            Log($"IDS discovery error: {ex.Message}");
        }

        // Load registered cameras and filter discovery
        var registered = _configStore.Load();
        if (registered.Count > 0)
        {
            Log($"Registered cameras: {registered.Count}");
            _cameraManager.DiscoverAndAddRegisteredCameras(registered);
        }
        else
        {
            Log("No registered cameras — discovery will add nothing. Use Camera > Config to register cameras.");
        }
        Log($"Total cameras added: {_cameraManager.CameraCount}");
    }

    public void Shutdown()
    {
        if (_cameraManager != null)
        {
            // Stop all grabbing before disposing
            foreach (var id in _cameraManager.GetCameraIds())
            {
                try { _cameraManager.StopGrabbing(id); } catch { }
            }
            _cameraManager.Dispose();
            _cameraManager = null;
        }
    }

    public void Reinitialize()
    {
        Initialize();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            Shutdown();
            _disposed = true;
        }
    }

    private void Log(string message)
    {
        LogSink?.Log(message);
    }
}
