using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AvalonDock.Layout;
using AvalonDock.Layout.Serialization;
using Horizon.Camera;
using Horizon.Core;

namespace Horizon.App;

public partial class MainWindow : Window
{
    private readonly IAppContext _context;
    private LiveVideoSubscriber? _liveSubscriber;
    private DispatcherTimer? _liveFeedTimer;
    private WriteableBitmap? _writeableBitmap;
    private string? _liveFeedCameraId;
    private byte[]? _rgbBuffer;

    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Horizon");
    private static readonly string DockLayoutPath = Path.Combine(SettingsDir, "dock-layout.xml");
    private static readonly string WindowStatePath = Path.Combine(SettingsDir, "window-state.txt");

    public MainWindow(IAppContext context)
    {
        _context = context;
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RestoreWindowState();
        RestoreDockLayout();

        // Wire up logging to the output panel
        if (_context is Horizon.Core.AppContext appCtx)
            appCtx.LogSink = new OutputPanelLogSink(this);

        try
        {
            _context.Initialize();
            RefreshCameraTree();
        }
        catch (Exception ex)
        {
            OutputTextBox.Text = $"Initialization error: {ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}";
        }
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        StopLiveFeed();
        SaveWindowState();
        SaveDockLayout();
    }

    private void RefreshCameraTree()
    {
        CamerasTreeItem.Items.Clear();

        if (!_context.IsInitialized)
            return;

        var statuses = _context.CameraManager.GetAllCameraStatus();
        LogOutput($"Found {statuses.Count} camera(s)");

        foreach (var status in statuses)
        {
            var label = $"{status.ModelName} ({status.Id})";
            if (status.Connected)
                label += " [Connected]";

            var item = new TreeViewItem { Header = label, Tag = status.Id };
            item.ContextMenu = CreateCameraContextMenu(status.Id);
            CamerasTreeItem.Items.Add(item);
        }
    }

    private ContextMenu CreateCameraContextMenu(string cameraId)
    {
        var menu = new ContextMenu();

        var connect = new MenuItem { Header = "Connect" };
        connect.Click += (s, e) => CameraConnect_Click(cameraId);
        menu.Items.Add(connect);

        var disconnect = new MenuItem { Header = "Disconnect" };
        disconnect.Click += (s, e) => CameraDisconnect_Click(cameraId);
        menu.Items.Add(disconnect);

        menu.Items.Add(new Separator());

        var capture = new MenuItem { Header = "Capture" };
        capture.Click += (s, e) => CameraCapture_Click(cameraId);
        menu.Items.Add(capture);

        menu.Items.Add(new Separator());

        var liveFeed = new MenuItem { Header = "Live Feed" };
        liveFeed.Click += (s, e) => StartLiveFeed(cameraId);
        menu.Items.Add(liveFeed);

        var stopFeed = new MenuItem { Header = "Stop Feed" };
        stopFeed.Click += (s, e) => StopLiveFeed();
        menu.Items.Add(stopFeed);

        menu.Items.Add(new Separator());

        var status = new MenuItem { Header = "Status" };
        status.Click += (s, e) => CameraStatus_Click(cameraId);
        menu.Items.Add(status);

        var refresh = new MenuItem { Header = "Refresh" };
        refresh.Click += (s, e) => RefreshCameraTree();
        menu.Items.Add(refresh);

        return menu;
    }

    private void CameraConnect_Click(string cameraId)
    {
        if (!_context.IsInitialized) return;
        var result = _context.CameraManager.ConnectCamera(cameraId);
        LogOutput($"Connect '{cameraId}': {(result ? "OK" : "FAILED")}");
        RefreshCameraTree();
    }

    private void CameraDisconnect_Click(string cameraId)
    {
        if (!_context.IsInitialized) return;
        var result = _context.CameraManager.DisconnectCamera(cameraId);
        LogOutput($"Disconnect '{cameraId}': {(result ? "OK" : "FAILED")}");
        RefreshCameraTree();
    }

    private void CameraCapture_Click(string cameraId)
    {
        if (!_context.IsInitialized) return;
        var result = _context.CameraManager.CaptureImage(cameraId);
        if (result)
        {
            var path = _context.CameraManager.GetLastCapturedImagePath();
            LogOutput($"Capture '{cameraId}': saved to {path}");
        }
        else
        {
            LogOutput($"Capture '{cameraId}': FAILED");
        }
    }

    private void CameraStatus_Click(string cameraId)
    {
        if (!_context.IsInitialized) return;
        var s = _context.CameraManager.GetCameraStatus(cameraId);
        LogOutput($"--- Status: {s.ModelName} ({s.Id}) ---");
        LogOutput($"  Type: {s.TypeName}");
        LogOutput($"  Connected: {s.Connected}");
        LogOutput($"  Grabbing: {s.Grabbing}");
        LogOutput($"  Serial: {s.SerialNumber}");
        LogOutput($"  Exposure: {s.ExposureSettings.ExposureTimeUs:F2} us");
        LogOutput($"  Gain: {s.ExposureSettings.Gain:F2}");
        LogOutput($"  FrameRate: {s.ExposureSettings.FrameRate:F2} fps");
    }

    private void StartLiveFeed(string cameraId)
    {
        if (!_context.IsInitialized) return;

        StopLiveFeed();

        // Start grabbing with broadcast
        var result = _context.CameraManager.StartGrabbingWithBroadcast(cameraId);
        if (!result)
        {
            LogOutput($"Live Feed: Failed to start grabbing for '{cameraId}'");
            return;
        }

        // Create subscriber and register
        _liveSubscriber = new LiveVideoSubscriber(cameraId);
        _context.CameraManager.SubscribeToFrames(_liveSubscriber);
        _liveSubscriber.InitializeStatusFromManager(_context.CameraManager);
        _liveFeedCameraId = cameraId;

        // Start polling timer at ~30fps
        _liveFeedTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _liveFeedTimer.Tick += LiveFeedTimer_Tick;
        _liveFeedTimer.Start();

        LiveFeedStatus.Text = $"Live: {cameraId}";
        LogOutput($"Live Feed: Started for '{cameraId}'");
    }

    private void StopLiveFeed()
    {
        if (_liveFeedTimer != null)
        {
            _liveFeedTimer.Stop();
            _liveFeedTimer.Tick -= LiveFeedTimer_Tick;
            _liveFeedTimer = null;
        }

        if (_context.IsInitialized && _liveSubscriber != null)
        {
            _context.CameraManager.UnsubscribeFromFrames(_liveSubscriber.SubscriberId);
        }

        if (_context.IsInitialized && _liveFeedCameraId != null)
        {
            _context.CameraManager.StopGrabbing(_liveFeedCameraId);
            LogOutput($"Live Feed: Stopped for '{_liveFeedCameraId}'");
        }

        _liveSubscriber = null;
        _writeableBitmap = null;
        _rgbBuffer = null;
        _liveFeedCameraId = null;
        LiveFeedStatus.Text = "No feed active";
    }

    private unsafe void LiveFeedTimer_Tick(object? sender, EventArgs e)
    {
        if (_liveSubscriber == null || !_liveSubscriber.HasNewFrame)
            return;

        // Allocate RGB buffer once (max 4096x4096x3 = ~50MB), reuse across frames
        _rgbBuffer ??= new byte[4096 * 4096 * 3];

        if (!_liveSubscriber.GetLatestFrameInto(_rgbBuffer, out int width, out int height, out int channels))
            return;

        // Create or resize WriteableBitmap if needed
        if (_writeableBitmap == null ||
            _writeableBitmap.PixelWidth != width ||
            _writeableBitmap.PixelHeight != height)
        {
            _writeableBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
            LiveFeedImage.Source = _writeableBitmap;
        }

        // Convert RGB8 -> BGR32 directly into WriteableBitmap BackBuffer
        int pixelCount = width * height;

        _writeableBitmap.Lock();
        try
        {
            byte* dst = (byte*)_writeableBitmap.BackBuffer;
            fixed (byte* src = _rgbBuffer)
            {
                int srcIdx = 0;
                int dstIdx = 0;
                for (int i = 0; i < pixelCount; i++)
                {
                    dst[dstIdx + 0] = src[srcIdx + 2]; // B <- R
                    dst[dstIdx + 1] = src[srcIdx + 1]; // G <- G
                    dst[dstIdx + 2] = src[srcIdx + 0]; // R <- B
                    dst[dstIdx + 3] = 255;              // A
                    srcIdx += channels;
                    dstIdx += 4;
                }
            }
            _writeableBitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
        }
        finally
        {
            _writeableBitmap.Unlock();
        }
    }

    // --- Menu event handlers ---

    private void CameraDiscover_Click(object sender, RoutedEventArgs e)
    {
        StopLiveFeed();
        _context.Reinitialize();
        RefreshCameraTree();
    }

    private void CameraConnectAll_Click(object sender, RoutedEventArgs e)
    {
        if (!_context.IsInitialized) return;
        foreach (var id in _context.CameraManager.GetCameraIds())
        {
            var result = _context.CameraManager.ConnectCamera(id);
            LogOutput($"Connect '{id}': {(result ? "OK" : "FAILED")}");
        }
        RefreshCameraTree();
    }

    private void CameraDisconnectAll_Click(object sender, RoutedEventArgs e)
    {
        if (!_context.IsInitialized) return;
        StopLiveFeed();
        foreach (var id in _context.CameraManager.GetCameraIds())
        {
            var result = _context.CameraManager.DisconnectCamera(id);
            LogOutput($"Disconnect '{id}': {(result ? "OK" : "FAILED")}");
        }
        RefreshCameraTree();
    }

    private void ViewCameras_Click(object sender, RoutedEventArgs e)
    {
        ToggleAnchorable("cameras");
    }

    private void ViewOutput_Click(object sender, RoutedEventArgs e)
    {
        ToggleAnchorable("output");
    }

    private void ToggleAnchorable(string contentId)
    {
        var anchorable = DockManager.Layout
            .Descendents()
            .OfType<LayoutAnchorable>()
            .FirstOrDefault(a => a.ContentId == contentId);

        if (anchorable == null) return;

        if (anchorable.IsVisible)
            anchorable.Hide();
        else
            anchorable.Show();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    // --- Dock/Window state persistence ---

    private void SaveDockLayout()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var serializer = new XmlLayoutSerializer(DockManager);
            serializer.Serialize(DockLayoutPath);
        }
        catch { }
    }

    private void RestoreDockLayout()
    {
        try
        {
            if (!File.Exists(DockLayoutPath)) return;
            var serializer = new XmlLayoutSerializer(DockManager);
            serializer.LayoutSerializationCallback += (s, args) =>
            {
                args.Content = args.Content ?? args.Model.Content;
            };
            serializer.Deserialize(DockLayoutPath);
        }
        catch { }
    }

    private void SaveWindowState()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var bounds = RestoreBounds != Rect.Empty ? RestoreBounds : new Rect(Left, Top, Width, Height);
            var lines = new[]
            {
                bounds.Left.ToString(),
                bounds.Top.ToString(),
                bounds.Width.ToString(),
                bounds.Height.ToString(),
                WindowState.ToString()
            };
            File.WriteAllLines(WindowStatePath, lines);
        }
        catch { }
    }

    private void RestoreWindowState()
    {
        try
        {
            if (!File.Exists(WindowStatePath))
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                return;
            }
            var lines = File.ReadAllLines(WindowStatePath);
            if (lines.Length < 5)
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                return;
            }

            Left = double.Parse(lines[0]);
            Top = double.Parse(lines[1]);
            Width = double.Parse(lines[2]);
            Height = double.Parse(lines[3]);
            if (Enum.TryParse<WindowState>(lines[4], out var state))
                WindowState = state;
        }
        catch
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    internal void LogOutput(string message)
    {
        OutputTextBox.Text += $"\n{message}";
        OutputTextBox.ScrollToEnd();
    }

    private sealed class OutputPanelLogSink(MainWindow window) : ILogSink
    {
        public void Log(string message) => window.LogOutput(message);
    }
}
