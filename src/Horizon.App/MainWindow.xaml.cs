using System.IO;
using System.Windows;
using System.Windows.Controls;
using AvalonDock.Layout;
using AvalonDock.Layout.Serialization;
using Horizon.Camera;
using Horizon.Camera.Models;
using System.Collections.Specialized;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Shapes;
using Horizon.Common;
using Path = System.IO.Path;
using Horizon.Core;
using Horizon.CurrentMeasurement;
using Horizon.CurrentMeasurement.Interfaces;
using Horizon.LaserController;
using Horizon.Motion;
using Horizon.Pneumatic;
using Serilog;

namespace Horizon.App;

public partial class MainWindow : Window
{
    private readonly IAppContext _context;

    private List<RegisteredCamera> _registeredCameras = new();
    private OverlayWindow? _overlayWindow;
    private UserPreferences _prefs = new();
    private string? _currentHardwareDetailId;
    private LayoutDocument? _hardwareDoc;
    private DispatcherTimer? _hwStatusTimer;

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

        // Populate SDK and Connection combos
        MountSdkCombo.ItemsSource = new[] { CameraType.Pylon, CameraType.IDS };
        MountSdkCombo.SelectedIndex = 0;
        MountConnCombo.ItemsSource = new[] { "USB", "IP" };
        MountConnCombo.SelectedIndex = 0;

        // Wire WPF output sink to the log ListBox
        App.UiSink.Initialize(Dispatcher);
        OutputListBox.ItemsSource = App.UiSink.ViewSource.View;

        // Auto-scroll to newest entry
        ((INotifyCollectionChanged)App.UiSink.ViewSource.View).CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Add && OutputListBox.Items.Count > 0)
                OutputListBox.ScrollIntoView(OutputListBox.Items[^1]);
        };

        // Cache the hardware document reference for toggle logic
        _hardwareDoc = DockManager.Layout.Descendents()
            .OfType<LayoutDocument>()
            .FirstOrDefault(d => d.ContentId == "hardware");

        // Initialize and auto-connect are handled by SplashWindow before MainWindow is shown
        LoadRegisteredCameras();
        RefreshHardwareTree();

        // Load user preferences and apply auto-start behaviors
        _prefs = UserPreferences.Load();

        if (_prefs.AutoStartMeasurementPolling && _context.IsInitialized
            && _context.CurrentMeasurementManager.GetAllDeviceNames().Count > 0)
        {
            _context.CurrentMeasurementManager.StartPolling();
        }

        if (_prefs.AutoOpenTopView)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, OpenOverlayWithPrefs);
        }

        // Hardware status bar — poll TEC/Laser/Keithley every 1s
        _hwStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _hwStatusTimer.Tick += HwStatusTimer_Tick;
        _hwStatusTimer.Start();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _hwStatusTimer?.Stop();
        StopLiveFeed();
        StopAllIOPanels();
        StopAllMotionPanels();
        StopAllPneumaticPanels();
        StopAllCurrentMeasPanels();
        StopAllCurrentChartPanels();
        StopAllLaserControllerPanels();
        StopAllProcessPanels();
        StopAllRunPanels();
        StopAllRecipePanels();
        StopAllJogPanels();
        StopAllTeachPanels();
        StopAllHexScanPanels();
        StopAllTrainerPanels();
        if (_overlayWindow is { IsVisible: true })
            _overlayWindow.SavePreferences(_prefs);
        _prefs.Save();
        _overlayWindow?.Close();
        SaveWindowState();
        SaveDockLayout();
    }

    private void StopAllIOPanels()
    {
        if (HardwareDetailContent.Content is IOPanel ioPanel)
            ioPanel.StopPolling();
    }

    private void StopAllMotionPanels()
    {
        if (HardwareDetailContent.Content is MotionPanel motionPanel)
            motionPanel.StopPolling();
    }

    private void StopAllPneumaticPanels()
    {
        if (HardwareDetailContent.Content is PneumaticPanel pneuPanel)
            pneuPanel.StopPolling();
    }

    private void StopAllCurrentMeasPanels()
    {
        if (HardwareDetailContent.Content is CurrentMeasurementPanel cmeasPanel)
            cmeasPanel.StopPolling();
    }

    private static TreeViewItem CreateStatusTreeItem(string label, string tag, Brush statusColor)
    {
        var dot = new Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = statusColor,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(dot);
        panel.Children.Add(text);

        return new TreeViewItem { Header = panel, Tag = tag };
    }

    private static readonly Brush StatusGreen = new SolidColorBrush(Color.FromRgb(76, 175, 80));
    private static readonly Brush StatusYellow = new SolidColorBrush(Color.FromRgb(255, 193, 7));
    private static readonly Brush StatusRed = new SolidColorBrush(Color.FromRgb(244, 67, 54));
    private static readonly Brush StatusGray = new SolidColorBrush(Color.FromRgb(158, 158, 158));

    private void RefreshCameraTree()
    {
        CamerasTreeItem.Items.Clear();

        if (!_context.IsInitialized)
            return;

        var statuses = _context.CameraManager.GetAllCameraStatus();
        Log.Information("Found {Count} camera(s)", statuses.Count);

        foreach (var status in statuses)
        {
            var label = $"{status.ModelName} ({status.Id})";
            if (status.Connected)
                label += " [Connected]";

            var color = status.Connected ? StatusGreen : StatusRed;
            var item = CreateStatusTreeItem(label, status.Id, color);
            item.ContextMenu = CreateCameraContextMenu(status.Id);
            item.MouseDoubleClick += CameraItem_MouseDoubleClick;
            CamerasTreeItem.Items.Add(item);
        }
    }

    private void CameraItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TreeViewItem item || item.Tag is not string cameraId)
            return;
        if (!_context.IsInitialized) return;

        e.Handled = true;

        var contentId = $"camInfo_{cameraId}";
        if (_currentHardwareDetailId == contentId) return;

        var status = _context.CameraManager.GetCameraStatus(cameraId);
        var panel = new CameraInfoPanel(status, _context.CameraManager);
        panel.ConnectionChanged += () => { RefreshCameraTree(); RefreshConfigGrid(); };
        panel.StartFeedRequested += id => StartLiveFeed(id);
        panel.StopFeedRequested += id => StopLiveFeed(id);
        panel.SensorConfigSaved += OnSensorConfigSaved;

        SetHardwareDetail(panel, $"Camera: {status.ModelName} ({cameraId})", contentId);
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
        stopFeed.Click += (s, e) => StopLiveFeed(cameraId);
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
        if (result)
        {
            Log.Information("Connect '{CameraId}': OK", cameraId);
        }
        else
        {
            var hw = _context.CameraManager.GetCameraHardware(cameraId);
            var error = hw?.LastError ?? "unknown error";
            Log.Error("Connect '{CameraId}': FAILED — {Error}", cameraId, error);
        }
        RefreshCameraTree();
        RefreshConfigGrid();
    }

    private void CameraDisconnect_Click(string cameraId)
    {
        if (!_context.IsInitialized) return;
        var result = _context.CameraManager.DisconnectCamera(cameraId);
        Log.Information("Disconnect '{CameraId}': {Result}", cameraId, result ? "OK" : "FAILED");
        RefreshCameraTree();
    }

    private void CameraCapture_Click(string cameraId)
    {
        if (!_context.IsInitialized) return;
        var result = _context.CameraManager.CaptureImage(cameraId);
        if (result)
        {
            var path = _context.CameraManager.GetLastCapturedImagePath();
            Log.Information("Capture '{CameraId}': saved to {Path}", cameraId, path);
        }
        else
        {
            Log.Error("Capture '{CameraId}': FAILED", cameraId);
        }
    }

    private void CameraStatus_Click(string cameraId)
    {
        if (!_context.IsInitialized) return;
        var s = _context.CameraManager.GetCameraStatus(cameraId);
        Log.Information("--- Status: {ModelName} ({Id}) ---", s.ModelName, s.Id);
        Log.Information("  Type: {TypeName}", s.TypeName);
        Log.Information("  Connected: {Connected}", s.Connected);
        Log.Information("  Grabbing: {Grabbing}", s.Grabbing);
        Log.Information("  Serial: {Serial}", s.SerialNumber);
        Log.Information("  Exposure: {Exposure:F2} us", s.ExposureSettings.ExposureTimeUs);
        Log.Information("  Gain: {Gain:F2}", s.ExposureSettings.Gain);
        Log.Information("  FrameRate: {FrameRate:F2} fps", s.ExposureSettings.FrameRate);
    }

    private void StartLiveFeed(string cameraId)
    {
        if (!_context.IsInitialized) return;

        var contentId = $"liveFeed_{cameraId}";

        // If a feed tab for this camera already exists, just activate it
        var existing = DockManager.Layout
            .Descendents()
            .OfType<LayoutDocument>()
            .FirstOrDefault(d => d.ContentId == contentId);
        if (existing != null)
        {
            existing.IsActive = true;
            return;
        }

        var status = _context.CameraManager.GetCameraStatus(cameraId);
        var panel = new LiveFeedPanel(cameraId, _context.CameraManager);

        if (!panel.Start())
        {
            Log.Error("Live Feed: Failed to start grabbing for '{CameraId}'", cameraId);
            return;
        }

        // Attach first available current measurement device channels for overlay
        var cmeasNamesForFeed = _context.CurrentMeasurementManager.GetAllDeviceNames();
        if (cmeasNamesForFeed.Count > 0)
        {
            var cmeasDevice = _context.CurrentMeasurementManager.GetDevice(cmeasNamesForFeed[0]);
            if (cmeasDevice != null)
            {
                var keys = BuildChannelKeys(cmeasNamesForFeed[0], cmeasDevice);
                panel.AttachCurrentChart(_context.MeasurementHub, keys,
                    _context.CurrentMeasurementManager, _context.LaserControllerManager);
            }
        }

        var doc = new LayoutDocument
        {
            Title = $"Feed: {status.ModelName} ({cameraId})",
            ContentId = contentId,
            Content = panel
        };

        var docPane = DockManager.Layout
            .Descendents()
            .OfType<LayoutDocumentPane>()
            .FirstOrDefault();
        if (docPane != null)
        {
            docPane.Children.Add(doc);
            doc.IsActive = true;
        }

        Log.Information("Live Feed: Started for '{CameraId}'", cameraId);
    }

    private void StopLiveFeed()
    {
        // Stop ALL live feed tabs
        var feedDocs = DockManager.Layout
            .Descendents()
            .OfType<LayoutDocument>()
            .Where(d => d.ContentId != null && d.ContentId.StartsWith("liveFeed_"))
            .ToList();

        foreach (var doc in feedDocs)
        {
            if (doc.Content is LiveFeedPanel panel)
            {
                panel.Stop();
                Log.Information("Live Feed: Stopped for '{CameraId}'", panel.CameraId);
            }
            doc.Close();
        }
    }

    private void StopLiveFeed(string cameraId)
    {
        var contentId = $"liveFeed_{cameraId}";
        var doc = DockManager.Layout
            .Descendents()
            .OfType<LayoutDocument>()
            .FirstOrDefault(d => d.ContentId == contentId);

        if (doc == null) return;

        if (doc.Content is LiveFeedPanel panel)
        {
            panel.Stop();
            Log.Information("Live Feed: Stopped for '{CameraId}'", cameraId);
        }
        doc.Close();
    }

    // --- Camera Config helpers ---

    private CameraConfigStore? GetConfigStore()
    {
        return (_context as Horizon.Core.AppContext)?.ConfigStore;
    }

    private void LoadRegisteredCameras()
    {
        var store = GetConfigStore();
        if (store == null) return;
        _registeredCameras = store.Load();
        RefreshConfigGrid();
    }

    private void OnSensorConfigSaved(string cameraId, RegisteredCamera updatedConfig)
    {
        // Update the registered camera entry in the list
        var existing = _registeredCameras.FirstOrDefault(
            r => r.SerialNumber.Equals(cameraId, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.MmPerPixel = updatedConfig.MmPerPixel;
            existing.Magnification = updatedConfig.Magnification;
            existing.Width = updatedConfig.Width;
            existing.Height = updatedConfig.Height;
        }

        // Persist to disk
        GetConfigStore()?.Save(_registeredCameras);

        RefreshConfigGrid();
        Log.Information("Sensor config saved for camera '{CameraId}': mm/px={MmPx}, Mag={Mag}",
            cameraId, updatedConfig.MmPerPixel, updatedConfig.Magnification);
    }

    private void RefreshConfigGrid()
    {
        var connectedSerials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_context.IsInitialized)
        {
            foreach (var status in _context.CameraManager.GetAllCameraStatus())
            {
                if (status.Connected)
                    connectedSerials.Add(status.SerialNumber);
            }
        }

        // Check which serials are present in CameraManager (discovered/added)
        var knownSerials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_context.IsInitialized)
        {
            foreach (var status in _context.CameraManager.GetAllCameraStatus())
                knownSerials.Add(status.SerialNumber);
        }

        var rows = _registeredCameras.Select(r => new
        {
            r.Name,
            r.SerialNumber,
            r.SdkType,
            r.ConnectionType,
            r.IpAddress,
            Resolution = (r.Width > 0 && r.Height > 0) ? $"{r.Width}x{r.Height}" : "Auto",
            MmPx = r.MmPerPixel > 0 ? r.MmPerPixel.ToString("G4") : "-",
            Mag = r.Magnification.ToString("G4"),
            Status = connectedSerials.Contains(r.SerialNumber) ? "Online"
                   : knownSerials.Contains(r.SerialNumber) ? "Found"
                   : "Offline"
        }).ToList();

        ConfigDataGrid.ItemsSource = rows;
    }

    private void AutoConnectCameras()
    {
        if (!_context.IsInitialized) return;

        var ids = _context.CameraManager.GetCameraIds();
        if (ids.Count == 0) return;

        Log.Information("Auto-connecting {Count} camera(s)...", ids.Count);
        foreach (var id in ids)
        {
            var result = _context.CameraManager.ConnectCamera(id);
            if (result)
            {
                Log.Information("Auto-connect '{CameraId}': OK", id);
            }
            else
            {
                var hw = _context.CameraManager.GetCameraHardware(id);
                var error = hw?.LastError ?? "unknown error";
                Log.Error("Auto-connect '{CameraId}': FAILED — {Error}", id, error);
            }
        }
        RefreshCameraTree();
        RefreshConfigGrid();
    }

    private void CameraConfig_Click(object sender, RoutedEventArgs e)
    {
        ToggleAnchorable("cameraConfig");
    }

    private void MountCamera_Click(object sender, RoutedEventArgs e)
    {
        var name = MountNameBox.Text.Trim();
        var serial = MountSerialBox.Text.Trim();
        var connType = MountConnCombo.SelectedItem as string ?? "USB";
        var ipAddress = MountIpBox.Text.Trim();

        if (string.IsNullOrEmpty(serial))
        {
            Log.Warning("Mount: Serial number is required.");
            return;
        }

        if (connType == "IP" && string.IsNullOrEmpty(ipAddress))
        {
            Log.Warning("Mount: IP address is required for IP connection.");
            return;
        }

        if (_registeredCameras.Any(r => r.SerialNumber.Equals(serial, StringComparison.OrdinalIgnoreCase)))
        {
            Log.Warning("Mount: Serial '{Serial}' is already registered.", serial);
            return;
        }

        var sdkType = MountSdkCombo.SelectedItem is CameraType ct ? ct : CameraType.Pylon;

        if (string.IsNullOrEmpty(name))
            name = $"{sdkType} {serial}";

        int.TryParse(MountWidthBox.Text.Trim(), out var width);
        int.TryParse(MountHeightBox.Text.Trim(), out var height);
        double.TryParse(MountMmPerPixelBox.Text.Trim(), out var mmPerPixel);
        if (!double.TryParse(MountMagBox.Text.Trim(), out var magnification))
            magnification = 1.0;

        var entry = new RegisteredCamera
        {
            Name = name,
            SerialNumber = serial,
            SdkType = sdkType,
            ConnectionType = connType,
            IpAddress = connType == "IP" ? ipAddress : string.Empty,
            Width = width,
            Height = height,
            MmPerPixel = mmPerPixel,
            Magnification = magnification
        };

        _registeredCameras.Add(entry);
        GetConfigStore()?.Save(_registeredCameras);

        MountNameBox.Clear();
        MountSerialBox.Clear();
        MountIpBox.Clear();
        MountWidthBox.Text = "0";
        MountHeightBox.Text = "0";
        MountMmPerPixelBox.Text = "0";
        MountMagBox.Text = "1";

        Log.Information("Mounted camera: {Name} ({Serial}) [{SdkType}] {ConnType}{IpSuffix}",
            entry.Name, entry.SerialNumber, entry.SdkType, connType,
            connType == "IP" ? $" {ipAddress}" : "");
        RefreshConfigGrid();

        // Re-discover with updated list
        PerformRediscovery();
    }

    private void UnmountCamera_Click(object sender, RoutedEventArgs e)
    {
        var selected = ConfigDataGrid.SelectedItem;
        if (selected == null)
        {
            Log.Warning("Unmount: No camera selected.");
            return;
        }

        // Get serial from the anonymous type via reflection
        var serialProp = selected.GetType().GetProperty("SerialNumber");
        var serial = serialProp?.GetValue(selected) as string;
        if (string.IsNullOrEmpty(serial)) return;

        var entry = _registeredCameras.FirstOrDefault(
            r => r.SerialNumber.Equals(serial, StringComparison.OrdinalIgnoreCase));
        if (entry == null) return;

        _registeredCameras.Remove(entry);
        GetConfigStore()?.Save(_registeredCameras);

        // Disconnect and remove from CameraManager if present
        if (_context.IsInitialized)
        {
            var cam = _context.CameraManager.GetAllCameraStatus()
                .FirstOrDefault(s => s.SerialNumber.Equals(serial, StringComparison.OrdinalIgnoreCase));
            if (cam != null)
            {
                StopLiveFeed(cam.Id);
                _context.CameraManager.DisconnectCamera(cam.Id);
                _context.CameraManager.RemoveCamera(cam.Id);
            }
        }

        Log.Information("Unmounted camera: {Name} ({Serial})", entry.Name, entry.SerialNumber);
        RefreshConfigGrid();
        RefreshCameraTree();
    }

    private void RediscoverCameras_Click(object sender, RoutedEventArgs e)
    {
        PerformRediscovery();
    }

    private void PerformRediscovery()
    {
        StopLiveFeed();
        _context.Reinitialize();
        LoadRegisteredCameras();
        RefreshCameraTree();
    }

    // --- Menu event handlers ---

    private void CameraDiscover_Click(object sender, RoutedEventArgs e)
    {
        PerformRediscovery();
    }

    private void CameraConnectAll_Click(object sender, RoutedEventArgs e)
    {
        if (!_context.IsInitialized) return;
        foreach (var id in _context.CameraManager.GetCameraIds())
        {
            var result = _context.CameraManager.ConnectCamera(id);
            Log.Information("Connect '{CameraId}': {Result}", id, result ? "OK" : "FAILED");
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
            Log.Information("Disconnect '{CameraId}': {Result}", id, result ? "OK" : "FAILED");
        }
        RefreshCameraTree();
    }

    private void DockManager_DocumentClosing(object? sender, AvalonDock.DocumentClosingEventArgs e)
    {
        // Intercept Hardware tab close — hide instead of destroy so it can be re-shown
        if (e.Document.ContentId == "hardware")
        {
            e.Cancel = true;
            var parent = e.Document.Parent as LayoutDocumentPane;
            parent?.RemoveChild(e.Document);
        }
    }

    private void HwStatusTimer_Tick(object? sender, EventArgs e)
    {
        bool anyVisible = false;

        // TEC temperature
        try
        {
            var laserMgr = _context.LaserControllerManager;
            var names = laserMgr.GetAllDeviceNames();
            if (names.Count > 0)
            {
                var status = laserMgr.GetStatus(names[0]);
                if (status.Connected)
                {
                    anyVisible = true;
                    HwStatusTecTemp.Text = status.TECOn
                        ? $"{status.CurrentTemperature:F1} C"
                        : "OFF";
                    HwStatusLaserCurrent.Text = status.LaserOn
                        ? $"{status.CurrentLaserCurrent:F1} mA"
                        : "OFF";
                }
                else
                {
                    HwStatusTecTemp.Text = "--";
                    HwStatusLaserCurrent.Text = "--";
                }
            }
        }
        catch { HwStatusTecTemp.Text = "--"; HwStatusLaserCurrent.Text = "--"; }

        // Keithley latest reading from MeasurementHub
        try
        {
            var cmeasMgr = _context.CurrentMeasurementManager;
            var names = cmeasMgr.GetAllDeviceNames();
            if (names.Count > 0)
            {
                var hub = _context.MeasurementHub;
                var key = $"{names[0]}.CH1";
                var channel = hub.GetChannel(key);
                if (channel != null && channel.LatestValue.HasValue)
                {
                    anyVisible = true;
                    HwStatusKeithley.Text = FormatCurrentSI(channel.LatestValue.Value);
                }
                else
                {
                    HwStatusKeithley.Text = "--";
                }
            }
        }
        catch { HwStatusKeithley.Text = "--"; }

        HwStatusBar.Visibility = anyVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string FormatCurrentSI(double amps)
    {
        var abs = Math.Abs(amps);
        if (abs >= 1) return $"{amps:F3} A";
        if (abs >= 1e-3) return $"{amps * 1e3:F3} mA";
        if (abs >= 1e-6) return $"{amps * 1e6:F3} uA";
        if (abs >= 1e-9) return $"{amps * 1e9:F3} nA";
        return $"{amps * 1e12:F3} pA";
    }

    private void ViewHardware_Click(object sender, RoutedEventArgs e)
    {
        if (_hardwareDoc == null) return;

        if (_hardwareDoc.Parent != null && _hardwareDoc.IsVisible)
        {
            // Hide by removing from parent pane (preserves Content)
            var parent = _hardwareDoc.Parent as LayoutDocumentPane;
            parent?.RemoveChild(_hardwareDoc);
        }
        else if (_hardwareDoc.Parent == null)
        {
            // Re-add to the first document pane
            var pane = DockManager.Layout.Descendents()
                .OfType<LayoutDocumentPane>()
                .FirstOrDefault();
            if (pane != null)
            {
                pane.Children.Add(_hardwareDoc);
                _hardwareDoc.IsActive = true;
            }
        }
        else
        {
            _hardwareDoc.IsActive = true;
        }
    }

    private void ViewOutput_Click(object sender, RoutedEventArgs e)
    {
        ToggleAnchorable("output");
    }

    private void RefreshHardwareTree()
    {
        RefreshCameraTree();
        RefreshMotionTree();
        RefreshIOTree();
        RefreshPneumaticTree();
        RefreshCurrentMeasTree();
        RefreshLaserControllerTree();
    }

    private void SetHardwareDetail(UIElement panel, string title, string contentId)
    {
        CleanupCurrentHardwareDetail();
        HardwareDetailTitle.Text = title;
        HardwareDetailContent.Content = panel;
        _currentHardwareDetailId = contentId;

        // Activate the Hardware document tab
        var hwDoc = DockManager.Layout.Descendents()
            .OfType<LayoutDocument>()
            .FirstOrDefault(d => d.ContentId == "hardware");
        if (hwDoc != null) hwDoc.IsActive = true;
    }

    private void CleanupCurrentHardwareDetail()
    {
        var current = HardwareDetailContent.Content;
        if (current == null) return;

        // Stop polling on the previous panel
        switch (current)
        {
            case MotionPanel mp: mp.StopPolling(); break;
            case IOPanel ip: ip.StopPolling(); break;
            case PneumaticPanel pp: pp.StopPolling(); break;
            case CurrentMeasurementPanel cmp: cmp.StopPolling(); break;
            case LaserControllerPanel lcp: lcp.StopPolling(); break;
            case MotionOverviewPanel mop: mop.StopPolling(); break;
            case IOOverviewPanel iop: iop.StopPolling(); break;
            case PneumaticOverviewPanel pop: pop.StopPolling(); break;
            case CameraOverviewPanel cop: cop.StopPolling(); break;
        }

        HardwareDetailContent.Content = null;
        _currentHardwareDetailId = null;
    }

    // ── Category overview double-click handlers ──────────────────────

    /// <summary>
    /// Returns true if the double-click originated from a child TreeViewItem
    /// (i.e. NOT directly on the parent header). Walks the visual tree from
    /// OriginalSource up to the handler's TreeViewItem looking for an
    /// intermediate TreeViewItem.
    /// </summary>
    private static bool IsChildTreeItemClick(MouseButtonEventArgs e, TreeViewItem parent)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source != null && source != parent)
        {
            if (source is TreeViewItem)
                return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private void MotionCategory_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled) return;
        if (sender is not TreeViewItem item || item != MotionTreeItem) return;
        if (IsChildTreeItemClick(e, MotionTreeItem)) return;
        if (!_context.IsInitialized) return;
        e.Handled = true;

        var contentId = "overview_motion";
        if (_currentHardwareDetailId == contentId) return;

        var panel = new MotionOverviewPanel(_context.MotionManager);
        panel.DeviceSelected += name =>
        {
            var ctrl = _context.MotionManager.GetController(name);
            if (ctrl == null) return;
            var mp = new MotionPanel(name, _context.MotionManager);
            mp.ConnectionChanged += () => RefreshMotionTree();
            SetHardwareDetail(mp, $"Motion: {name}", $"motion_{name}");
        };
        SetHardwareDetail(panel, "Motion — All Controllers", contentId);
    }

    private void IOCategory_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled) return;
        if (sender is not TreeViewItem item || item != IOTreeItem) return;
        if (IsChildTreeItemClick(e, IOTreeItem)) return;
        if (!_context.IsInitialized) return;
        e.Handled = true;

        var contentId = "overview_io";
        if (_currentHardwareDetailId == contentId) return;

        var panel = new IOOverviewPanel(_context.IOManager);
        panel.DeviceSelected += name =>
        {
            var device = _context.IOManager.GetDevice(name);
            if (device == null) return;
            var ip = new IOPanel(name, _context.IOManager);
            ip.ConnectionChanged += () => RefreshIOTree();
            SetHardwareDetail(ip, $"IO: {name}", $"io_{name}");
        };
        SetHardwareDetail(panel, "IO — All Devices", contentId);
    }

    private void PneumaticCategory_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled) return;
        if (sender is not TreeViewItem item || item != PneumaticTreeItem) return;
        if (IsChildTreeItemClick(e, PneumaticTreeItem)) return;
        if (!_context.IsInitialized) return;
        e.Handled = true;

        var contentId = "overview_pneumatic";
        if (_currentHardwareDetailId == contentId) return;

        var panel = new PneumaticOverviewPanel(_context.PneumaticManager);
        panel.DeviceSelected += name =>
        {
            var slide = _context.PneumaticManager.GetSlide(name);
            if (slide == null) return;
            var pp = new PneumaticPanel(name, _context.PneumaticManager);
            pp.StatusChanged += () => RefreshPneumaticTree();
            SetHardwareDetail(pp, $"Pneumatic: {name}", $"pneu_{name}");
        };
        SetHardwareDetail(panel, "Pneumatic — All Slides", contentId);
    }

    private void CamerasCategory_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled) return;
        if (sender is not TreeViewItem item || item != CamerasTreeItem) return;
        if (IsChildTreeItemClick(e, CamerasTreeItem)) return;
        if (!_context.IsInitialized) return;
        e.Handled = true;

        var contentId = "overview_cameras";
        if (_currentHardwareDetailId == contentId) return;

        var panel = new CameraOverviewPanel(_context.CameraManager);
        panel.DeviceSelected += id =>
        {
            var status = _context.CameraManager.GetCameraStatus(id);
            var cp = new CameraInfoPanel(status, _context.CameraManager);
            cp.ConnectionChanged += () => { RefreshCameraTree(); RefreshConfigGrid(); };
            cp.StartFeedRequested += camId => StartLiveFeed(camId);
            cp.StopFeedRequested += camId => StopLiveFeed(camId);
            cp.SensorConfigSaved += OnSensorConfigSaved;
            SetHardwareDetail(cp, $"Camera: {status.ModelName} ({id})", $"camInfo_{id}");
        };
        SetHardwareDetail(panel, "Cameras — All Devices", contentId);
    }

    // ── Tree refresh + item handlers ──────────────────────────────

    private void RefreshMotionTree()
    {
        MotionTreeItem.Items.Clear();
        if (!_context.IsInitialized) return;

        foreach (var name in _context.MotionManager.GetAllDeviceNames())
        {
            var ctrl = _context.MotionManager.GetController(name);
            if (ctrl == null) continue;

            var status = ctrl.IsConnected ? "Connected" : "Disconnected";
            var label = $"{name} [{status}] {ctrl.ControllerType}";
            var color = ctrl.IsConnected ? StatusGreen : StatusRed;
            var item = CreateStatusTreeItem(label, name, color);
            item.MouseDoubleClick += MotionItem_MouseDoubleClick;
            MotionTreeItem.Items.Add(item);
        }
    }

    private void MotionItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TreeViewItem item || item.Tag is not string deviceName)
            return;
        if (!_context.IsInitialized) return;

        e.Handled = true;

        var contentId = $"motion_{deviceName}";
        if (_currentHardwareDetailId == contentId) return;

        var ctrl = _context.MotionManager.GetController(deviceName);
        if (ctrl == null) return;

        var panel = new MotionPanel(deviceName, _context.MotionManager);
        panel.ConnectionChanged += () => RefreshMotionTree();

        SetHardwareDetail(panel, $"Motion: {deviceName}", contentId);
    }

    private void RefreshIOTree()
    {
        IOTreeItem.Items.Clear();
        if (!_context.IsInitialized) return;

        foreach (var name in _context.IOManager.GetAllDeviceNames())
        {
            var device = _context.IOManager.GetDevice(name);
            if (device == null) continue;

            var status = device.IsConnected ? "Connected" : "Disconnected";
            var label = $"{name} [{status}] {device.InputCount}in/{device.OutputCount}out";
            var color = device.IsConnected ? StatusGreen : StatusRed;
            var item = CreateStatusTreeItem(label, name, color);
            item.MouseDoubleClick += IOItem_MouseDoubleClick;
            IOTreeItem.Items.Add(item);
        }
    }

    private void IOItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TreeViewItem item || item.Tag is not string deviceName)
            return;
        if (!_context.IsInitialized) return;

        e.Handled = true;

        var contentId = $"io_{deviceName}";
        if (_currentHardwareDetailId == contentId) return;

        var device = _context.IOManager.GetDevice(deviceName);
        if (device == null) return;

        var panel = new IOPanel(deviceName, _context.IOManager);
        panel.ConnectionChanged += () => RefreshIOTree();

        SetHardwareDetail(panel, $"IO: {deviceName}", contentId);
    }

    private void RefreshPneumaticTree()
    {
        PneumaticTreeItem.Items.Clear();
        if (!_context.IsInitialized) return;

        foreach (var name in _context.PneumaticManager.GetAllSlideNames())
        {
            var slide = _context.PneumaticManager.GetSlide(name);
            if (slide == null) continue;

            var status = slide.GetStatus();
            var label = $"{name} — {status.Position}";
            var color = status.Position switch
            {
                Horizon.Pneumatic.Models.PneumaticPosition.Extended => StatusGreen,
                Horizon.Pneumatic.Models.PneumaticPosition.Retracted => StatusYellow,
                _ => StatusGray
            };
            var item = CreateStatusTreeItem(label, name, color);
            item.MouseDoubleClick += PneumaticItem_MouseDoubleClick;
            PneumaticTreeItem.Items.Add(item);
        }
    }

    private void PneumaticItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TreeViewItem item || item.Tag is not string slideName)
            return;
        if (!_context.IsInitialized) return;

        e.Handled = true;

        var contentId = $"pneu_{slideName}";
        if (_currentHardwareDetailId == contentId) return;

        var slide = _context.PneumaticManager.GetSlide(slideName);
        if (slide == null) return;

        var panel = new PneumaticPanel(slideName, _context.PneumaticManager);
        panel.StatusChanged += () => RefreshPneumaticTree();

        SetHardwareDetail(panel, $"Pneumatic: {slideName}", contentId);
    }

    private void RefreshCurrentMeasTree()
    {
        CurrentMeasTreeItem.Items.Clear();
        if (!_context.IsInitialized) return;

        foreach (var name in _context.CurrentMeasurementManager.GetAllDeviceNames())
        {
            var device = _context.CurrentMeasurementManager.GetDevice(name);
            if (device == null) continue;

            var status = device.IsConnected ? "Connected" : "Disconnected";
            var channels = string.Join(",", device.EnabledChannels.Select(ch => $"CH{ch}"));
            var label = $"{name} [{status}] {channels}";
            var color = device.IsConnected ? StatusGreen : StatusRed;
            var item = CreateStatusTreeItem(label, name, color);
            item.MouseDoubleClick += CurrentMeasItem_MouseDoubleClick;
            CurrentMeasTreeItem.Items.Add(item);
        }
    }

    private void CurrentMeasItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TreeViewItem item || item.Tag is not string deviceName)
            return;
        if (!_context.IsInitialized) return;

        e.Handled = true;

        var contentId = $"cmeas_{deviceName}";
        if (_currentHardwareDetailId == contentId) return;

        var device = _context.CurrentMeasurementManager.GetDevice(deviceName);
        if (device == null) return;

        var panel = new CurrentMeasurementPanel(deviceName, _context.CurrentMeasurementManager, _context.MeasurementHub);
        panel.ConnectionChanged += () => RefreshCurrentMeasTree();
        panel.PopOutChartRequested += OpenCurrentChartPanel;

        SetHardwareDetail(panel, $"CMeas: {deviceName}", contentId);
    }

    private void RefreshLaserControllerTree()
    {
        LaserControllerTreeItem.Items.Clear();
        if (!_context.IsInitialized) return;

        foreach (var name in _context.LaserControllerManager.GetAllDeviceNames())
        {
            var device = _context.LaserControllerManager.GetDevice(name);
            if (device == null) continue;

            var status = device.IsConnected ? "Connected" : "Disconnected";
            var label = $"{name} [{status}] {device.ControllerType}";
            var color = device.IsConnected ? StatusGreen : StatusRed;
            var item = CreateStatusTreeItem(label, name, color);
            item.MouseDoubleClick += LaserControllerItem_MouseDoubleClick;
            LaserControllerTreeItem.Items.Add(item);
        }
    }

    private void LaserControllerItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TreeViewItem item || item.Tag is not string deviceName)
            return;
        if (!_context.IsInitialized) return;

        e.Handled = true;

        var contentId = $"laser_{deviceName}";
        if (_currentHardwareDetailId == contentId) return;

        var device = _context.LaserControllerManager.GetDevice(deviceName);
        if (device == null) return;

        var panel = new LaserControllerPanel(deviceName, _context.LaserControllerManager, _context.MeasurementHub);
        panel.ConnectionChanged += () => RefreshLaserControllerTree();

        SetHardwareDetail(panel, $"Laser: {deviceName}", contentId);
    }

    private void StopAllLaserControllerPanels()
    {
        if (HardwareDetailContent.Content is LaserControllerPanel laserPanel)
            laserPanel.StopPolling();
    }

    private void TopViewChart_Click(object sender, RoutedEventArgs e)
    {
        if (_overlayWindow != null)
        {
            if (_overlayWindow.IsVisible)
            {
                _overlayWindow.SavePreferences(_prefs);
                _prefs.Save();
                _overlayWindow.Close();
                _overlayWindow = null;
                return;
            }
            _overlayWindow = null;
        }

        OpenOverlayWithPrefs();
    }

    private void OpenOverlayWithPrefs()
    {
        _overlayWindow = new OverlayWindow(
            _context.CameraManager,
            _context.CurrentMeasurementManager,
            _context.MeasurementHub,
            _context.MotionManager,
            _context.LaserControllerManager);

        _overlayWindow.ApplyPreferences(_prefs);
        _overlayWindow.Closed += (_, _) =>
        {
            if (_overlayWindow != null)
            {
                _overlayWindow.SavePreferences(_prefs);
                _prefs.Save();
            }
            _overlayWindow = null;
        };
        _overlayWindow.Show();
    }

    private void EditSettings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(_prefs) { Owner = this };
        dlg.ShowDialog();
    }

    private void OpenCurrentChartPanel(string deviceName, List<string> channelKeys)
    {
        var contentId = $"chart_{deviceName}";

        // If already open, just activate
        var existing = DockManager.Layout
            .Descendents()
            .OfType<LayoutDocument>()
            .FirstOrDefault(d => d.ContentId == contentId);
        if (existing != null)
        {
            existing.IsActive = true;
            return;
        }

        var panel = new CurrentChartPanel(deviceName, _context.MeasurementHub, channelKeys,
            _context.CurrentMeasurementManager, _context.LaserControllerManager);

        var doc = new LayoutDocument
        {
            Title = $"Chart: {deviceName}",
            ContentId = contentId,
            Content = panel,
            CanFloat = true
        };

        var docPane = DockManager.Layout
            .Descendents()
            .OfType<LayoutDocumentPane>()
            .FirstOrDefault();
        if (docPane != null)
        {
            docPane.Children.Add(doc);
            doc.IsActive = true;

            // Float it immediately so it pops out as a window
            doc.Float();
        }
    }

    private void ProcessEditor_Click(object sender, RoutedEventArgs e)
    {
        var contentId = "process_editor";

        var existing = DockManager.Layout
            .Descendents()
            .OfType<LayoutDocument>()
            .FirstOrDefault(d => d.ContentId == contentId);
        if (existing != null)
        {
            existing.IsActive = true;
            return;
        }

        var panel = new ProcessPanel(_context);

        var doc = new LayoutDocument
        {
            Title = "Process Editor",
            ContentId = contentId,
            Content = panel
        };

        var docPane = DockManager.Layout
            .Descendents()
            .OfType<LayoutDocumentPane>()
            .FirstOrDefault();
        if (docPane != null)
        {
            docPane.Children.Add(doc);
            doc.IsActive = true;
        }
    }

    private void RunPanel_Click(object sender, RoutedEventArgs e)
    {
        var contentId = "run_panel";

        var existing = DockManager.Layout
            .Descendents()
            .OfType<LayoutDocument>()
            .FirstOrDefault(d => d.ContentId == contentId);
        if (existing != null)
        {
            existing.IsActive = true;
            return;
        }

        var panel = new RunPanel(_context);

        var doc = new LayoutDocument
        {
            Title = "Run",
            ContentId = contentId,
            Content = panel
        };

        var docPane = DockManager.Layout
            .Descendents()
            .OfType<LayoutDocumentPane>()
            .FirstOrDefault();
        if (docPane != null)
        {
            docPane.Children.Add(doc);
            doc.IsActive = true;
        }
    }

    private void RecipeEditor_Click(object sender, RoutedEventArgs e)
    {
        var contentId = "recipe_editor";

        var existing = DockManager.Layout
            .Descendents()
            .OfType<LayoutDocument>()
            .FirstOrDefault(d => d.ContentId == contentId);
        if (existing != null)
        {
            existing.IsActive = true;
            return;
        }

        var panel = new RecipeEditorPanel(_context);

        var doc = new LayoutDocument
        {
            Title = "Recipe Editor",
            ContentId = contentId,
            Content = panel
        };

        var docPane = DockManager.Layout
            .Descendents()
            .OfType<LayoutDocumentPane>()
            .FirstOrDefault();
        if (docPane != null)
        {
            docPane.Children.Add(doc);
            doc.IsActive = true;
        }
    }

    private void StopAllRecipePanels()
    {
        var recipeDocs = DockManager.Layout
            .Descendents()
            .OfType<LayoutDocument>()
            .Where(d => d.ContentId != null && d.ContentId.StartsWith("recipe_"))
            .ToList();

        foreach (var doc in recipeDocs)
        {
            if (doc.Content is RecipeEditorPanel panel)
                panel.StopExecution();
        }
    }

    private void StopAllProcessPanels()
    {
        var processDocs = DockManager.Layout
            .Descendents()
            .OfType<LayoutDocument>()
            .Where(d => d.ContentId != null && d.ContentId.StartsWith("process_"))
            .ToList();

        foreach (var doc in processDocs)
        {
            if (doc.Content is ProcessPanel panel)
                panel.StopExecution();
        }
    }

    private void StopAllRunPanels()
    {
        var runDocs = DockManager.Layout
            .Descendents()
            .OfType<LayoutDocument>()
            .Where(d => d.ContentId != null && d.ContentId.StartsWith("run_"))
            .ToList();

        foreach (var doc in runDocs)
        {
            if (doc.Content is RunPanel panel)
                panel.StopExecution();
        }
    }

    private void StopAllJogPanels()
    {
        var jogDocs = DockManager.Layout
            .Descendents()
            .OfType<LayoutDocument>()
            .Where(d => d.ContentId != null && d.ContentId.StartsWith("jog_"))
            .ToList();

        foreach (var doc in jogDocs)
        {
            if (doc.Content is JogPanel panel)
                panel.StopPolling();
        }
    }

    private void StopAllTeachPanels()
    {
        var teachDocs = DockManager.Layout
            .Descendents()
            .OfType<LayoutDocument>()
            .Where(d => d.ContentId != null && d.ContentId.StartsWith("teach_"))
            .ToList();

        foreach (var doc in teachDocs)
        {
            if (doc.Content is TeachPointPanel panel)
                panel.StopPolling();
        }
    }

    private void StopAllHexScanPanels()
    {
        var hexDocs = DockManager.Layout
            .Descendents()
            .OfType<LayoutDocument>()
            .Where(d => d.ContentId != null && d.ContentId.StartsWith("hex_"))
            .ToList();

        foreach (var doc in hexDocs)
        {
            if (doc.Content is HexScanPanel panel)
                panel.StopPolling();
        }
    }

    private void StopAllTrainerPanels()
    {
        var trainerDocs = DockManager.Layout
            .Descendents()
            .OfType<LayoutDocument>()
            .Where(d => d.ContentId == "trainer")
            .ToList();

        foreach (var doc in trainerDocs)
        {
            if (doc.Content is TrainerPanel panel)
                panel.StopPolling();
        }
    }

    private void StopAllCurrentChartPanels()
    {
        var chartDocs = DockManager.Layout
            .Descendents()
            .OfType<LayoutDocument>()
            .Where(d => d.ContentId != null && d.ContentId.StartsWith("chart_"))
            .ToList();

        foreach (var doc in chartDocs)
        {
            if (doc.Content is CurrentChartPanel panel)
                panel.StopRendering();
        }
    }

    private static List<string> BuildChannelKeys(string deviceName, ICurrentMeasurementDevice device)
    {
        var keys = new List<string>(device.ChannelCount);
        foreach (var ch in device.EnabledChannels)
            keys.Add($"{deviceName}.CH{ch}");
        return keys;
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

    // --- Log filter/clear handlers ---

    private void FilterInfo_Click(object sender, RoutedEventArgs e)
    {
        App.UiSink.ShowInfo = FilterInfoToggle.IsChecked == true;
    }

    private void FilterWarn_Click(object sender, RoutedEventArgs e)
    {
        App.UiSink.ShowWarning = FilterWarnToggle.IsChecked == true;
    }

    private void FilterError_Click(object sender, RoutedEventArgs e)
    {
        App.UiSink.ShowError = FilterErrorToggle.IsChecked == true;
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        App.UiSink.Clear();
    }

    private void HelpExpressionSyntax_Click(object sender, RoutedEventArgs e)
    {
        var win = new ExpressionSyntaxWindow { Owner = this };
        win.Show();
    }

    private void QuickButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        var index = btn.Tag?.ToString() ?? "?";
        Log.Information("Quick button {Index} clicked", index);
    }

    private void JogPanel_Click(object sender, RoutedEventArgs e)
    {
        var contentId = "jog_panel";

        var existing = DockManager.Layout
            .Descendents()
            .OfType<LayoutDocument>()
            .FirstOrDefault(d => d.ContentId == contentId);
        if (existing != null)
        {
            existing.IsActive = true;
            return;
        }

        var panel = new JogPanel(_context);

        var doc = new LayoutDocument
        {
            Title = "Jog Panel",
            ContentId = contentId,
            Content = panel
        };

        var docPane = DockManager.Layout
            .Descendents()
            .OfType<LayoutDocumentPane>()
            .FirstOrDefault();
        if (docPane != null)
        {
            docPane.Children.Add(doc);
            doc.IsActive = true;
        }
    }

    private void TeachPointPanel_Click(object sender, RoutedEventArgs e)
    {
        var contentId = "teach_points";

        var existing = DockManager.Layout
            .Descendents()
            .OfType<LayoutDocument>()
            .FirstOrDefault(d => d.ContentId == contentId);
        if (existing != null)
        {
            existing.IsActive = true;
            return;
        }

        var panel = new TeachPointPanel(_context);

        var doc = new LayoutDocument
        {
            Title = "Teach Points",
            ContentId = contentId,
            Content = panel
        };

        var docPane = DockManager.Layout
            .Descendents()
            .OfType<LayoutDocumentPane>()
            .FirstOrDefault();
        if (docPane != null)
        {
            docPane.Children.Add(doc);
            doc.IsActive = true;
        }
    }

    private void HexScanPanel_Click(object sender, RoutedEventArgs e)
    {
        var contentId = "hex_scan";

        var existing = DockManager.Layout
            .Descendents()
            .OfType<LayoutDocument>()
            .FirstOrDefault(d => d.ContentId == contentId);
        if (existing != null)
        {
            existing.IsActive = true;
            return;
        }

        var panel = new HexScanPanel(_context);

        var doc = new LayoutDocument
        {
            Title = "Hex Scan",
            ContentId = contentId,
            Content = panel
        };

        var docPane = DockManager.Layout
            .Descendents()
            .OfType<LayoutDocumentPane>()
            .FirstOrDefault();
        if (docPane != null)
        {
            docPane.Children.Add(doc);
            doc.IsActive = true;
        }
    }

    private void TrainerPanel_Click(object sender, RoutedEventArgs e)
    {
        var contentId = "trainer";

        var existing = DockManager.Layout
            .Descendents()
            .OfType<LayoutDocument>()
            .FirstOrDefault(d => d.ContentId == contentId);
        if (existing != null)
        {
            existing.IsActive = true;
            return;
        }

        var panel = new TrainerPanel(_context);

        var doc = new LayoutDocument
        {
            Title = "Trainer",
            ContentId = contentId,
            Content = panel
        };

        var docPane = DockManager.Layout
            .Descendents()
            .OfType<LayoutDocumentPane>()
            .FirstOrDefault();
        if (docPane != null)
        {
            docPane.Children.Add(doc);
            doc.IsActive = true;
        }
    }

    private void VisionPanel_Click(object sender, RoutedEventArgs e)
    {
        var contentId = "vision";

        var existing = DockManager.Layout
            .Descendents()
            .OfType<LayoutDocument>()
            .FirstOrDefault(d => d.ContentId == contentId);
        if (existing != null)
        {
            existing.IsActive = true;
            return;
        }

        var placeholder = new TextBlock
        {
            Text = "Vision — Coming Soon",
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 16,
            Foreground = System.Windows.Media.Brushes.Gray,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var doc = new LayoutDocument
        {
            Title = "Vision",
            ContentId = contentId,
            Content = placeholder
        };

        var docPane = DockManager.Layout
            .Descendents()
            .OfType<LayoutDocumentPane>()
            .FirstOrDefault();
        if (docPane != null)
        {
            docPane.Children.Add(doc);
            doc.IsActive = true;
        }
    }

    private void OpenConfigFolder_Click(object sender, RoutedEventArgs e)
    {
        var configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Horizon", "config");
        if (!Directory.Exists(configDir))
            Directory.CreateDirectory(configDir);
        System.Diagnostics.Process.Start("explorer.exe", configDir);
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

            // If the saved layout uses the old design (hardware as anchorable or missing),
            // discard it so the XAML-defined layout takes effect.
            var xml = File.ReadAllText(DockLayoutPath);
            if (!xml.Contains("\"hardware\"") || !xml.Contains("LayoutDocument"))
            {
                File.Delete(DockLayoutPath);
                return;
            }

            // Snapshot existing anchorable content by ContentId before deserialization
            var contentMap = new Dictionary<string, object>();
            foreach (var anc in DockManager.Layout.Descendents().OfType<LayoutAnchorable>())
            {
                if (anc.ContentId != null && anc.Content != null)
                    contentMap[anc.ContentId] = anc.Content;
            }
            foreach (var doc in DockManager.Layout.Descendents().OfType<LayoutDocument>())
            {
                if (doc.ContentId != null && doc.Content != null)
                    contentMap[doc.ContentId] = doc.Content;
            }

            var serializer = new XmlLayoutSerializer(DockManager);
            serializer.LayoutSerializationCallback += (s, args) =>
            {
                var id = args.Model.ContentId;

                // Discard saved feed/IO/motion/pneumatic tabs — they need runtime state
                if (id == "liveFeed"
                    || (id != null && id.StartsWith("liveFeed_"))
                    || (id != null && id.StartsWith("io_"))
                    || (id != null && id.StartsWith("motion_"))
                    || (id != null && id.StartsWith("pneu_"))
                    || (id != null && id.StartsWith("cmeas_"))
                    || (id != null && id.StartsWith("chart_"))
                    || (id != null && id.StartsWith("laser_"))
                    || (id != null && id.StartsWith("camInfo_"))
                    || (id != null && id.StartsWith("jog_"))
                    || (id != null && id.StartsWith("overview_")))
                {
                    args.Cancel = true;
                    return;
                }

                // Recreate panels that need fresh runtime context
                if (id == "process_editor")
                {
                    args.Content = new ProcessPanel(_context);
                    return;
                }

                if (id == "recipe_editor")
                {
                    args.Content = new RecipeEditorPanel(_context);
                    return;
                }

                if (id == "teach_points")
                {
                    args.Content = new TeachPointPanel(_context);
                    return;
                }

                if (id == "hex_scan")
                {
                    args.Content = new HexScanPanel(_context);
                    return;
                }

                if (args.Content != null) return;

                // Restore content from pre-deserialization snapshot
                if (id != null && contentMap.TryGetValue(id, out var content))
                    args.Content = content;
                else
                    args.Content = args.Model.Content;
            };
            serializer.Deserialize(DockLayoutPath);

            // Ensure the hardware document is visible after layout restore
            var hwDoc = DockManager.Layout.Descendents()
                .OfType<LayoutDocument>()
                .FirstOrDefault(d => d.ContentId == "hardware");
            if (hwDoc == null)
            {
                // Hardware document was lost during restore — delete stale layout and reload from XAML
                File.Delete(DockLayoutPath);
            }
        }
        catch
        {
            // If restore fails, delete stale layout so next launch uses XAML defaults
            try { File.Delete(DockLayoutPath); } catch { }
        }
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
}
