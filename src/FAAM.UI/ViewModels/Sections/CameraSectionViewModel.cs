using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Horizon.Common;
using Horizon.Engine;

namespace FAAM.UI.ViewModels.Sections;

/// <summary>
/// The Machine page's Camera section — a camera selector, connect/grab and
/// exposure controls, a snapshot button, and a live preview. Frames arrive on a
/// background thread via <see cref="ICameraFacet.FrameReady"/>; the newest is
/// coalesced onto the UI thread and blitted into a reused
/// <see cref="WriteableBitmap"/> — a steady stream allocates no bitmaps.
/// </summary>
public sealed class CameraSectionViewModel : ObservableObject, IDisposable
{
    private readonly EngineHost _host;
    private readonly Dispatcher _dispatcher;
    private readonly object _frameLock = new();
    private readonly Stopwatch _fpsWatch = Stopwatch.StartNew();

    private string? _selectedCamera;
    private bool _connected;
    private bool _grabbing;
    private string _info = "—";
    private string _resolution = "—";
    private long _framesReceived;
    private string _fps = "—";
    private double _exposureUs = 10000;
    private double _gain;
    private bool _autoExposure;
    private bool _autoGain;
    private double _frameRate = 10;
    private string _status = "—";

    private WriteableBitmap? _frame;
    private CameraFrame? _latestFrame;
    private bool _renderScheduled;
    private int _fpsCount;
    private bool _disposed;

    private DisplayWindow? _displayWindow;
    private bool _sectionActive;
    private bool _subscribed;

    // Cameras this section holds a grab lease on. Grab is ref-counted in the facet,
    // so the Stop button must release only what this section started — never another
    // consumer's lease (e.g. the startup pop-out window).
    private readonly HashSet<string> _leases = new(StringComparer.Ordinal);

    public CameraSectionViewModel(EngineHost host)
    {
        _host = host;
        _dispatcher = Dispatcher.CurrentDispatcher;
        Cameras = host.Engine.Camera.CameraIds;
        _selectedCamera = Cameras.Count > 0 ? Cameras[0] : null;

        StartCommand = new AsyncRelayCommand(
            StartGrabAsync,
            () => _selectedCamera != null && !_leases.Contains(_selectedCamera));
        StopCommand = new AsyncRelayCommand(
            StopGrabAsync,
            () => _selectedCamera != null && _leases.Contains(_selectedCamera));
        ApplyExposureCommand = new AsyncRelayCommand(ApplyExposureAsync, () => _selectedCamera != null);
        SnapCommand = new AsyncRelayCommand(SnapAsync, () => _selectedCamera != null);
        PopOutCommand = new RelayCommand(PopOut);

        RebuildForCamera();
        // The FrameReady hook is attached lazily — only while the section is
        // visible or the pop-out is open (see UpdateSubscription).
    }

    /// <summary>The engine host — used by the pop-out window's MoveClick jog.</summary>
    public EngineHost Host => _host;

    private LightingPanelViewModel? _lighting;

    /// <summary>
    /// Lighting adjustment shown under the exposure row. Created on first use, so the
    /// other hosts of this view-model (pop-out windows, Gripped FAU page) never build it.
    /// </summary>
    public LightingPanelViewModel Lighting
        => _lighting ??= new LightingPanelViewModel(_host) { Camera = _selectedCamera };

    /// <summary>Label shown in the Machine-page section rail.</summary>
    public string Title => "Camera";

    public IReadOnlyList<string> Cameras { get; }

    public string? SelectedCamera
    {
        get => _selectedCamera;
        set
        {
            if (!SetField(ref _selectedCamera, value)) return;
            RebuildForCamera();
            if (_lighting != null) _lighting.Camera = value;   // lighting rows follow the camera
        }
    }

    public bool Connected { get => _connected; private set => SetField(ref _connected, value); }

    /// <summary>Whether the hardware is grabbing (any consumer) — display only; the
    /// Start/Stop buttons track this section's own lease, not the global state.</summary>
    public bool Grabbing { get => _grabbing; private set => SetField(ref _grabbing, value); }

    /// <summary>Model · type · serial of the selected camera.</summary>
    public string Info { get => _info; private set => SetField(ref _info, value); }
    public string Resolution { get => _resolution; private set => SetField(ref _resolution, value); }
    public long FramesReceived { get => _framesReceived; private set => SetField(ref _framesReceived, value); }
    public string Fps { get => _fps; private set => SetField(ref _fps, value); }

    public double ExposureUs { get => _exposureUs; set => SetField(ref _exposureUs, value); }
    public double Gain { get => _gain; set => SetField(ref _gain, value); }
    public bool AutoExposure { get => _autoExposure; set => SetField(ref _autoExposure, value); }
    public bool AutoGain { get => _autoGain; set => SetField(ref _autoGain, value); }
    public double FrameRate { get => _frameRate; set => SetField(ref _frameRate, value); }
    public string Status { get => _status; private set => SetField(ref _status, value); }

    /// <summary>The live preview image — a reused WriteableBitmap, or null before the first frame.</summary>
    public WriteableBitmap? Frame
    {
        get => _frame;
        private set { if (SetField(ref _frame, value)) OnPropertyChanged(nameof(HasNoVideo)); }
    }

    /// <summary>True until the first frame arrives — drives the "No video" placeholder.</summary>
    public bool HasNoVideo => _frame == null;

    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public AsyncRelayCommand ApplyExposureCommand { get; }
    public AsyncRelayCommand SnapCommand { get; }

    /// <summary>Opens (or re-focuses) the pop-out <see cref="DisplayWindow"/>.</summary>
    public RelayCommand PopOutCommand { get; }

    /// <summary>Refreshes connection / grab state from the engine — called each sampler tick.</summary>
    public void Refresh()
    {
        if (_host.Engine.State == EngineState.ShuttingDown) return;   // facets gone — nothing to read
        _lighting?.Refresh();
        var camera = _selectedCamera;
        Connected = camera != null && _host.Engine.Camera.IsConnected(camera);
        if (camera == null) return;

        var state = _host.Engine.Camera.GetState(camera);
        Grabbing = state.Grabbing;
        Resolution = state.Width > 0 ? $"{state.Width} × {state.Height}" : "—";
        FramesReceived = state.FramesReceived;
    }

    /// <summary>
    /// Sets whether the Camera section is the visible one. The frame pipeline
    /// runs only while the section is shown or the pop-out window is open, so a
    /// grabbing camera costs the UI nothing when nobody is watching.
    /// </summary>
    public void SetActive(bool active)
    {
        _sectionActive = active;
        UpdateSubscription();
    }

    private void UpdateSubscription()
    {
        bool shouldRun = !_disposed && (_sectionActive || _displayWindow != null);
        if (shouldRun == _subscribed) return;
        if (shouldRun)
            _host.Engine.Camera.FrameReady += OnFrameReady;
        else
            _host.Engine.Camera.FrameReady -= OnFrameReady;
        _subscribed = shouldRun;
    }

    private void RebuildForCamera()
    {
        Frame = null;
        var camera = _selectedCamera;
        if (camera == null) { Info = "—"; return; }

        var info = _host.Engine.Camera.GetCamera(camera);
        Info = info != null ? $"{info.Model} · {info.CameraType} · SN {info.SerialNumber}" : "—";

        // Pre-fill the exposure fields from the camera's current settings.
        var exposure = _host.Engine.Camera.GetState(camera).Exposure;
        ExposureUs = exposure.ExposureUs;
        Gain = exposure.Gain;
        AutoExposure = exposure.AutoExposure;
        AutoGain = exposure.AutoGain;
        if (exposure.FrameRate > 0) FrameRate = exposure.FrameRate;

        // Start/Stop reflect this section's lease on the now-selected camera.
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Acquires a grab lease for the selected camera (Start button).</summary>
    private async Task StartGrabAsync()
    {
        var camera = _selectedCamera;
        if (camera == null) return;
        var result = await _host.Engine.Camera.StartGrabbingAsync(camera).ConfigureAwait(true);
        if (result.IsSuccess) _leases.Add(camera);
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        Status = result.IsSuccess ? "Grabbing started" : result.Message;
    }

    /// <summary>Releases this section's grab lease on the selected camera (Stop button).</summary>
    private async Task StopGrabAsync()
    {
        var camera = _selectedCamera;
        if (camera == null) return;
        var result = await _host.Engine.Camera.StopGrabbingAsync(camera).ConfigureAwait(true);
        if (result.IsSuccess) _leases.Remove(camera);
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        Status = result.IsSuccess ? "Grabbing stopped" : result.Message;
    }

    private async Task RunAsync(Func<string, Task<Result>> operation, string okMessage)
    {
        var camera = _selectedCamera;
        if (camera == null) return;
        var result = await operation(camera).ConfigureAwait(true);
        Status = result.IsSuccess ? okMessage : result.Message;
    }

    private Task ApplyExposureAsync()
        => RunAsync(c => _host.Engine.Camera.ApplyExposureAsync(
               c, new CameraExposure(_exposureUs, _gain, _autoExposure, _autoGain, _frameRate)),
               "Exposure applied");

    private async Task SnapAsync()
    {
        var camera = _selectedCamera;
        if (camera == null) return;
        var result = await _host.Engine.Camera.SnapToFileAsync(camera).ConfigureAwait(true);
        Status = result.IsSuccess ? $"Saved {result.Value}" : result.Message;
    }

    // --- live frame pipeline ---

    private void OnFrameReady(CameraFrame frame)
    {
        if (frame.Camera != _selectedCamera) return;

        // Keep only the newest frame; coalesce so at most one render is queued.
        lock (_frameLock)
        {
            _latestFrame = frame;
            if (_renderScheduled) return;
            _renderScheduled = true;
        }
        _dispatcher.BeginInvoke(RenderLatestFrame);
    }

    private void RenderLatestFrame()
    {
        CameraFrame? frame;
        lock (_frameLock)
        {
            _renderScheduled = false;
            frame = _latestFrame;
            _latestFrame = null;
        }
        if (frame == null || _disposed) return;

        PixelFormat? format = frame.Channels switch
        {
            1 => PixelFormats.Gray8,
            3 => PixelFormats.Bgr24,
            4 => PixelFormats.Bgra32,
            _ => null,
        };
        if (format == null) return;

        try
        {
            // Recreate the bitmap only when the geometry/format changes.
            if (_frame == null || _frame.PixelWidth != frame.Width ||
                _frame.PixelHeight != frame.Height || _frame.Format != format.Value)
            {
                Frame = new WriteableBitmap(frame.Width, frame.Height, 96, 96, format.Value, null);
            }
            _frame!.WritePixels(
                new Int32Rect(0, 0, frame.Width, frame.Height),
                frame.Data, frame.Width * frame.Channels, 0);
        }
        catch (Exception ex)
        {
            Status = $"Frame render failed: {ex.Message}";
            return;
        }

        _fpsCount++;
        var elapsed = _fpsWatch.Elapsed.TotalSeconds;
        if (elapsed >= 1.0)
        {
            Fps = $"{_fpsCount / elapsed:F1} fps";
            _fpsCount = 0;
            _fpsWatch.Restart();
        }
    }

    private void PopOut()
    {
        if (_displayWindow != null)
        {
            _displayWindow.Activate();
            return;
        }
        _displayWindow = new DisplayWindow
        {
            Owner = Application.Current.MainWindow,   // closes with the shell window
            DataContext = this,
        };
        _displayWindow.Closed += (_, _) => { _displayWindow = null; UpdateSubscription(); };
        _displayWindow.Show();
        UpdateSubscription();   // pop-out keeps the pipeline running even off-section
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Release every grab lease this section still holds so the count stays
        // balanced (the hardware stops only if no other consumer holds a lease).
        foreach (var cam in _leases)
            _ = _host.Engine.Camera.StopGrabbingAsync(cam);
        _leases.Clear();

        UpdateSubscription();   // _disposed → detaches the frame hook
        _displayWindow?.Close();
    }
}
