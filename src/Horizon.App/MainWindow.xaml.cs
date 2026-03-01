using System.IO;
using System.Windows;
using System.Windows.Controls;
using AvalonDock.Layout;
using AvalonDock.Layout.Serialization;
using Horizon.Camera;
using Horizon.Camera.Models;
using System.Windows.Input;
using Horizon.Core;
using Horizon.CurrentMeasurement;
using Horizon.CurrentMeasurement.Interfaces;
using Horizon.Motion;
using Horizon.Pneumatic;

namespace Horizon.App;

public partial class MainWindow : Window
{
    private readonly IAppContext _context;

    private List<RegisteredCamera> _registeredCameras = new();
    private OverlayWindow? _overlayWindow;

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

        // Wire up logging to the output panel
        if (_context is Horizon.Core.AppContext appCtx)
            appCtx.LogSink = new OutputPanelLogSink(this);

        // Initialize and auto-connect are handled by SplashWindow before MainWindow is shown
        LoadRegisteredCameras();
        RefreshHardwareTree();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        StopLiveFeed();
        StopAllIOPanels();
        StopAllMotionPanels();
        StopAllPneumaticPanels();
        StopAllCurrentMeasPanels();
        StopAllCurrentChartPanels();
        _overlayWindow?.Close();
        SaveWindowState();
        SaveDockLayout();
    }

    private void StopAllIOPanels()
    {
        var ioDocs = DockManager.Layout
            .Descendents()
            .OfType<LayoutDocument>()
            .Where(d => d.ContentId != null && d.ContentId.StartsWith("io_"))
            .ToList();

        foreach (var doc in ioDocs)
        {
            if (doc.Content is IOPanel panel)
                panel.StopPolling();
        }
    }

    private void StopAllMotionPanels()
    {
        var motionDocs = DockManager.Layout
            .Descendents()
            .OfType<LayoutDocument>()
            .Where(d => d.ContentId != null && d.ContentId.StartsWith("motion_"))
            .ToList();

        foreach (var doc in motionDocs)
        {
            if (doc.Content is MotionPanel panel)
                panel.StopPolling();
        }
    }

    private void StopAllPneumaticPanels()
    {
        var pneuDocs = DockManager.Layout
            .Descendents()
            .OfType<LayoutDocument>()
            .Where(d => d.ContentId != null && d.ContentId.StartsWith("pneu_"))
            .ToList();

        foreach (var doc in pneuDocs)
        {
            if (doc.Content is PneumaticPanel panel)
                panel.StopPolling();
        }
    }

    private void StopAllCurrentMeasPanels()
    {
        var cmeasDocs = DockManager.Layout
            .Descendents()
            .OfType<LayoutDocument>()
            .Where(d => d.ContentId != null && d.ContentId.StartsWith("cmeas_"))
            .ToList();

        foreach (var doc in cmeasDocs)
        {
            if (doc.Content is CurrentMeasurementPanel panel)
                panel.StopPolling();
        }
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

        // If a tab for this camera already exists, activate it
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
        var panel = new CameraInfoPanel(status, _context.CameraManager);
        panel.ConnectionChanged += () => { RefreshCameraTree(); RefreshConfigGrid(); };
        panel.StartFeedRequested += id => StartLiveFeed(id);
        panel.StopFeedRequested += id => StopLiveFeed(id);

        var doc = new LayoutDocument
        {
            Title = $"Camera: {status.ModelName} ({cameraId})",
            ContentId = contentId,
            Content = panel
        };

        // Add to the first document pane
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
            LogOutput($"Connect '{cameraId}': OK");
        }
        else
        {
            var hw = _context.CameraManager.GetCameraHardware(cameraId);
            var error = hw?.LastError ?? "unknown error";
            LogOutput($"Connect '{cameraId}': FAILED — {error}");
        }
        RefreshCameraTree();
        RefreshConfigGrid();
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
            LogOutput($"Live Feed: Failed to start grabbing for '{cameraId}'");
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
                    _context.CurrentMeasurementManager);
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

        LogOutput($"Live Feed: Started for '{cameraId}'");
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
                LogOutput($"Live Feed: Stopped for '{panel.CameraId}'");
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
            LogOutput($"Live Feed: Stopped for '{cameraId}'");
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

        LogOutput($"Auto-connecting {ids.Count} camera(s)...");
        foreach (var id in ids)
        {
            var result = _context.CameraManager.ConnectCamera(id);
            if (result)
            {
                LogOutput($"Auto-connect '{id}': OK");
            }
            else
            {
                var hw = _context.CameraManager.GetCameraHardware(id);
                var error = hw?.LastError ?? "unknown error";
                LogOutput($"Auto-connect '{id}': FAILED — {error}");
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
            LogOutput("Mount: Serial number is required.");
            return;
        }

        if (connType == "IP" && string.IsNullOrEmpty(ipAddress))
        {
            LogOutput("Mount: IP address is required for IP connection.");
            return;
        }

        if (_registeredCameras.Any(r => r.SerialNumber.Equals(serial, StringComparison.OrdinalIgnoreCase)))
        {
            LogOutput($"Mount: Serial '{serial}' is already registered.");
            return;
        }

        var sdkType = MountSdkCombo.SelectedItem is CameraType ct ? ct : CameraType.Pylon;

        if (string.IsNullOrEmpty(name))
            name = $"{sdkType} {serial}";

        var entry = new RegisteredCamera
        {
            Name = name,
            SerialNumber = serial,
            SdkType = sdkType,
            ConnectionType = connType,
            IpAddress = connType == "IP" ? ipAddress : string.Empty
        };

        _registeredCameras.Add(entry);
        GetConfigStore()?.Save(_registeredCameras);

        MountNameBox.Clear();
        MountSerialBox.Clear();
        MountIpBox.Clear();

        LogOutput($"Mounted camera: {entry.Name} ({entry.SerialNumber}) [{entry.SdkType}] {connType}{(connType == "IP" ? $" {ipAddress}" : "")}");
        RefreshConfigGrid();

        // Re-discover with updated list
        PerformRediscovery();
    }

    private void UnmountCamera_Click(object sender, RoutedEventArgs e)
    {
        var selected = ConfigDataGrid.SelectedItem;
        if (selected == null)
        {
            LogOutput("Unmount: No camera selected.");
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

        LogOutput($"Unmounted camera: {entry.Name} ({entry.SerialNumber})");
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

    private void ViewHardware_Click(object sender, RoutedEventArgs e)
    {
        ToggleAnchorable("hardware");
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
    }

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
            var item = new TreeViewItem { Header = label, Tag = name };
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

        var existing = DockManager.Layout
            .Descendents()
            .OfType<LayoutDocument>()
            .FirstOrDefault(d => d.ContentId == contentId);
        if (existing != null)
        {
            existing.IsActive = true;
            return;
        }

        var ctrl = _context.MotionManager.GetController(deviceName);
        if (ctrl == null) return;

        var panel = new MotionPanel(deviceName, _context.MotionManager);
        panel.ConnectionChanged += () => RefreshMotionTree();
        panel.LogMessage += msg => LogOutput(msg);

        var doc = new LayoutDocument
        {
            Title = $"Motion: {deviceName}",
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
            var item = new TreeViewItem { Header = label, Tag = name };
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

        var existing = DockManager.Layout
            .Descendents()
            .OfType<LayoutDocument>()
            .FirstOrDefault(d => d.ContentId == contentId);
        if (existing != null)
        {
            existing.IsActive = true;
            return;
        }

        var device = _context.IOManager.GetDevice(deviceName);
        if (device == null) return;

        var panel = new IOPanel(deviceName, _context.IOManager);
        panel.ConnectionChanged += () => RefreshIOTree();
        panel.LogMessage += msg => LogOutput(msg);

        var doc = new LayoutDocument
        {
            Title = $"IO: {deviceName}",
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
            var item = new TreeViewItem { Header = label, Tag = name };
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

        var existing = DockManager.Layout
            .Descendents()
            .OfType<LayoutDocument>()
            .FirstOrDefault(d => d.ContentId == contentId);
        if (existing != null)
        {
            existing.IsActive = true;
            return;
        }

        var slide = _context.PneumaticManager.GetSlide(slideName);
        if (slide == null) return;

        var panel = new PneumaticPanel(slideName, _context.PneumaticManager);
        panel.StatusChanged += () => RefreshPneumaticTree();
        panel.LogMessage += msg => LogOutput(msg);

        var doc = new LayoutDocument
        {
            Title = $"Pneumatic: {slideName}",
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
            var item = new TreeViewItem { Header = label, Tag = name };
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

        var existing = DockManager.Layout
            .Descendents()
            .OfType<LayoutDocument>()
            .FirstOrDefault(d => d.ContentId == contentId);
        if (existing != null)
        {
            existing.IsActive = true;
            return;
        }

        var device = _context.CurrentMeasurementManager.GetDevice(deviceName);
        if (device == null) return;

        var panel = new CurrentMeasurementPanel(deviceName, _context.CurrentMeasurementManager, _context.MeasurementHub);
        panel.ConnectionChanged += () => RefreshCurrentMeasTree();
        panel.LogMessage += msg => LogOutput(msg);
        panel.PopOutChartRequested += OpenCurrentChartPanel;

        var doc = new LayoutDocument
        {
            Title = $"CMeas: {deviceName}",
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

    private void TopViewChart_Click(object sender, RoutedEventArgs e)
    {
        if (_overlayWindow != null)
        {
            // Toggle: if already open, bring to front or close
            if (_overlayWindow.IsVisible)
            {
                _overlayWindow.Close();
                _overlayWindow = null;
                return;
            }
            _overlayWindow = null;
        }

        _overlayWindow = new OverlayWindow(
            _context.CameraManager,
            _context.CurrentMeasurementManager,
            _context.MeasurementHub);

        _overlayWindow.Closed += (_, _) => _overlayWindow = null;
        _overlayWindow.Show();
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
            _context.CurrentMeasurementManager);

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

            // If the saved layout is from before the unified Hardware panel,
            // discard it so the XAML-defined layout takes effect.
            var xml = File.ReadAllText(DockLayoutPath);
            if (!xml.Contains("\"hardware\""))
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
                    || (id != null && id.StartsWith("chart_")))
                {
                    args.Cancel = true;
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
