using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using FAAM.UI.ViewModels;
using FAAM.UI.ViewModels.Sections;
using FAAM.UI.Views;
using Horizon.Common;
using Horizon.Engine;
using Horizon.Workflow;

namespace FAAM.UI;

/// <summary>
/// The application shell — fixed chrome (Home button, left rail, headers) around
/// a swappable page host. Home shows the launcher; the Machine tile navigates to
/// the Machine page. Pages are created once and reused.
///
/// Closing is intercepted: the operator confirms, then a <see cref="ShutdownWindow"/>
/// runs the ordered hardware teardown with progress. This window stays alive
/// (hidden) until that completes, so the process never ends mid-disconnect.
/// </summary>
public partial class ShellWindow : Window
{
    private readonly EngineHost _host;
    private readonly HomeView _home = new();
    private readonly MachinePageViewModel _machineVm;
    private readonly MachinePageView _machine;
    private readonly RunView _run;
    private readonly EngineerPageView _engineer;
    private readonly VisionProfileView _visionProfile;
    private readonly ScriptEditorView _scriptEditor;
    private readonly CalibrationView _calibration;
    private readonly CameraNeedleCalibrationView _cameraNeedle;
    private readonly RepositoryView _repository;

    /// <summary>Camera pop-out windows this shell owns (auto-opened + restored).</summary>
    private readonly List<DisplayWindow> _cameraWindows = new();
    private bool _exiting;

    public ShellWindow(EngineHost host)
    {
        _host = host;
        _machineVm = new MachinePageViewModel(host);
        _machine = new MachinePageView { DataContext = _machineVm };
        _run = new RunView(host);
        _engineer = new EngineerPageView(host);
        _visionProfile = new VisionProfileView(host);
        _scriptEditor = new ScriptEditorView(host);
        _calibration = new CalibrationView();
        _cameraNeedle = new CameraNeedleCalibrationView(host);
        _repository = new RepositoryView();
        _repository.Attach(host);
        InitializeComponent();

        RestoreMainWindowPlacement();

        // The shell top-bar engine-status panel binds to the Machine page VM
        // (whose sampler supplies EngineState / DeviceSummary / Reconnect).
        EngineHeader.DataContext = _machineVm;

        // The global jog panel (left rail) drives the connected motion devices.
        JogPanelControl.Attach(host);

        // The Home product bar loads / creates the active product.
        _home.Attach(host);

        // Register the operator-prompt handler — a workflow flow.askUser step
        // blocks on this until the operator answers a modal dialog.
        host.Process.OperatorPrompt = new OperatorPrompt(host);

        _home.MachineRequested += ShowMachine;
        _home.RunRequested += ShowRun;
        _home.EngineerRequested += ShowEngineer;
        _home.VisionProfileRequested += ShowVisionProfile;
        _home.ScriptRequested += ShowScript;
        _home.CalibrationRequested += ShowCalibration;
        _home.RepositoryRequested += ShowRepository;
        _calibration.CameraNeedleRequested += ShowCameraNeedle;
        _cameraNeedle.BackRequested += ShowCalibration;

        // Identity chip — refresh on login/logout and on product switch.
        _host.Session.Changed += UpdateIdentity;
        _host.Process.ProductChanged += OnProductChanged;
        _host.Process.ActiveSerialChanged += OnActiveSerialChanged;
        _host.Activity.Changed += OnDutActivityChanged;   // gate Start DUT while an alignment/scan holds the hexapod
        UpdateIdentity();
        OnActiveSerialChanged(_host.Process.ActiveSerial);
        RefreshDutButtons();

        ShowHome();

        Closing += OnClosing;

        // Pop up the live main_camera window as soon as the app is up (the engine
        // is already connected by the time the shell is shown), if enabled.
        Loaded += OnShellLoaded;
    }

    private void OnShellLoaded(object? sender, RoutedEventArgs e)
    {
        Loaded -= OnShellLoaded;   // once

        // Reopen the camera windows that were open at last exit, at their saved
        // positions. The engine is already connected by the time the shell loads.
        foreach (var p in _host.Settings.Current.CameraWindows)
        {
            if (!_host.Engine.Camera.CameraIds.Contains(p.Camera, System.StringComparer.OrdinalIgnoreCase))
                continue;
            OpenCameraWindow(p.Camera, new WindowPlacement(p.Left, p.Top, p.Width, p.Height, false));
        }

        // Auto-open main_camera unless a restored window already shows it.
        if (_host.Settings.Current.ShowMainCameraOnStartup && !IsCameraWindowOpen("main_camera"))
            ShowMainCameraWindow();
    }

    /// <summary>Stops a page's background work (camera pipeline, status sampler) when leaving it.</summary>
    private void LeaveCurrent()
    {
        if (ReferenceEquals(PageHost.Content, _run))
            _run.Deactivate();
        else if (ReferenceEquals(PageHost.Content, _machine))
            _machineVm.OnDeactivated();
        else if (ReferenceEquals(PageHost.Content, _engineer))
            _engineer.Deactivate();
        else if (ReferenceEquals(PageHost.Content, _visionProfile))
            _visionProfile.Deactivate();
        else if (ReferenceEquals(PageHost.Content, _scriptEditor))
            _scriptEditor.Deactivate();
        else if (ReferenceEquals(PageHost.Content, _cameraNeedle))
            _cameraNeedle.Deactivate();
        else if (ReferenceEquals(PageHost.Content, _repository))
            _repository.Deactivate();
    }

    private void ShowHome()
    {
        LeaveCurrent();
        Header1Text.Text = "HOME";
        EngineHeader.Visibility = Visibility.Collapsed;
        DutHeader.Visibility = Visibility.Collapsed;
        PageHost.Content = _home;
    }

    private void ShowMachine()
    {
        LeaveCurrent();
        Header1Text.Text = "MACHINE";
        _machineVm.OnActivated();   // starts the ~5 Hz status sampler
        EngineHeader.Visibility = Visibility.Visible;
        DutHeader.Visibility = Visibility.Collapsed;
        PageHost.Content = _machine;
    }

    private void ShowRun()
    {
        LeaveCurrent();
        Header1Text.Text = "RUN";
        EngineHeader.Visibility = Visibility.Collapsed;
        DutHeader.Visibility = Visibility.Visible;   // serial-bracketed DUT run lives in the header here
        RefreshDutButtons();
        _run.Activate();   // starts grabbing + frame routing
        PageHost.Content = _run;
    }

    private void ShowEngineer()
    {
        LeaveCurrent();
        Header1Text.Text = "ENGINEER";
        EngineHeader.Visibility = Visibility.Collapsed;
        DutHeader.Visibility = Visibility.Collapsed;
        _engineer.Activate();
        PageHost.Content = _engineer;
    }

    private void ShowVisionProfile()
    {
        LeaveCurrent();
        Header1Text.Text = "VISION PROFILE";
        EngineHeader.Visibility = Visibility.Collapsed;
        DutHeader.Visibility = Visibility.Collapsed;
        _visionProfile.Activate();
        PageHost.Content = _visionProfile;
    }

    private void ShowScript()
    {
        LeaveCurrent();
        Header1Text.Text = "SCRIPT";
        EngineHeader.Visibility = Visibility.Collapsed;
        DutHeader.Visibility = Visibility.Collapsed;
        _scriptEditor.Activate();
        PageHost.Content = _scriptEditor;
    }

    private void ShowCalibration()
    {
        LeaveCurrent();
        Header1Text.Text = "CALIBRATION";
        EngineHeader.Visibility = Visibility.Collapsed;
        DutHeader.Visibility = Visibility.Collapsed;
        PageHost.Content = _calibration;
    }

    private void ShowRepository()
    {
        LeaveCurrent();
        Header1Text.Text = "REPOSITORY";
        EngineHeader.Visibility = Visibility.Collapsed;
        DutHeader.Visibility = Visibility.Collapsed;
        _repository.Activate();
        PageHost.Content = _repository;
    }

    private void ShowCameraNeedle()
    {
        LeaveCurrent();
        Header1Text.Text = "CALIBRATION — CAMERA → NEEDLE";
        EngineHeader.Visibility = Visibility.Collapsed;
        DutHeader.Visibility = Visibility.Collapsed;
        _cameraNeedle.Activate();
        PageHost.Content = _cameraNeedle;
    }

    /// <summary>
    /// Opens the <c>main_camera</c> pop-out (or focuses it if already open). Used by
    /// the startup auto-show.
    /// </summary>
    public void ShowMainCameraWindow()
    {
        const string cam = "main_camera";
        var existing = FindCameraWindow(cam);
        if (existing != null) { existing.Activate(); return; }

        if (!_host.Engine.Camera.CameraIds.Contains(cam, System.StringComparer.OrdinalIgnoreCase))
        {
            _host.Log.Add(new LogEntry(LogLevel.Warning, System.DateTimeOffset.Now, "FAAM",
                $"Auto-show camera: '{cam}' is not among the configured cameras " +
                $"[{string.Join(", ", _host.Engine.Camera.CameraIds)}]."));
            return;
        }
        OpenCameraWindow(cam, null);
    }

    /// <summary>
    /// Opens a live pop-out window for <paramref name="camera"/> at the optional
    /// saved <paramref name="placement"/>. The window holds one grab lease
    /// (ref-counted in the camera facet) for its lifetime and releases it on close,
    /// so it shares the stream with the Run page / Machine camera without blacking
    /// out the others. Tracked in <see cref="_cameraWindows"/> for save-on-exit.
    /// </summary>
    private void OpenCameraWindow(string camera, WindowPlacement? placement)
    {
        _ = _host.Engine.Camera.StartGrabbingAsync(camera);   // acquire this window's lease

        var vm = new CameraSectionViewModel(_host) { SelectedCamera = camera };
        vm.SetActive(true);

        var win = new DisplayWindow { Owner = this, DataContext = vm };
        if (placement != null && placement.Width > 80 && placement.Height > 80 && IsOnScreen(placement))
        {
            win.WindowStartupLocation = WindowStartupLocation.Manual;
            win.Left = placement.Left;
            win.Top = placement.Top;
            win.Width = placement.Width;
            win.Height = placement.Height;
        }
        win.Closed += (_, _) =>
        {
            _cameraWindows.Remove(win);
            // During app exit the engine is being torn down (ShutdownWindow) and
            // its facets are gone — touching Engine.Camera here throws. The engine's
            // own teardown stops grabbing, so just drop the window.
            if (_exiting) return;
            _ = _host.Engine.Camera.StopGrabbingAsync(camera);   // release this window's lease
            vm.SetActive(false);
            vm.Dispose();
        };
        _cameraWindows.Add(win);
        win.Show();
    }

    private DisplayWindow? FindCameraWindow(string camera)
        => _cameraWindows.FirstOrDefault(w =>
            w.DataContext is CameraSectionViewModel vm &&
            string.Equals(vm.SelectedCamera, camera, System.StringComparison.OrdinalIgnoreCase));

    private bool IsCameraWindowOpen(string camera) => FindCameraWindow(camera) != null;

    /// <summary>Restores the saved main-window bounds / maximized state, if any and on-screen.</summary>
    private void RestoreMainWindowPlacement()
    {
        var p = _host.Settings.Current.MainWindow;
        if (p == null) return;

        if (p.Width > 200 && p.Height > 150 && IsOnScreen(p))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = p.Left;
            Top = p.Top;
            Width = p.Width;
            Height = p.Height;
        }
        if (p.Maximized)
            WindowState = WindowState.Maximized;
    }

    /// <summary>Captures the main window + all open camera windows into settings and persists.</summary>
    private void SaveWindowLayout()
    {
        _host.Settings.Update(_host.Settings.Current with
        {
            MainWindow = ToPlacement(this),
            CameraWindows = Application.Current.Windows.OfType<DisplayWindow>()
                .Select(w => (w, vm: w.DataContext as CameraSectionViewModel))
                .Where(t => t.vm?.SelectedCamera is not null)
                .Select(t =>
                {
                    var b = NormalBounds(t.w);
                    return new CameraWindowPlacement(t.vm!.SelectedCamera!, b.Left, b.Top, b.Width, b.Height);
                })
                .ToArray(),
        });
        _host.Settings.Save();
    }

    private static WindowPlacement ToPlacement(Window w)
    {
        var b = NormalBounds(w);
        return new WindowPlacement(b.Left, b.Top, b.Width, b.Height, w.WindowState == WindowState.Maximized);
    }

    /// <summary>The window's normal (non-maximized) bounds — uses RestoreBounds when maximized.</summary>
    private static Rect NormalBounds(Window w)
        => w.WindowState == WindowState.Normal || w.RestoreBounds.IsEmpty
            ? new Rect(w.Left, w.Top, w.Width, w.Height)
            : w.RestoreBounds;

    /// <summary>True if the placement's top-left sits within the current virtual desktop.</summary>
    private static bool IsOnScreen(WindowPlacement p) => IsOnScreen(p.Left, p.Top);

    private static bool IsOnScreen(double left, double top)
    {
        double vsLeft = SystemParameters.VirtualScreenLeft;
        double vsTop = SystemParameters.VirtualScreenTop;
        double vsRight = vsLeft + SystemParameters.VirtualScreenWidth;
        double vsBottom = vsTop + SystemParameters.VirtualScreenHeight;
        return left >= vsLeft - 10 && left <= vsRight - 60 &&
               top >= vsTop - 10 && top <= vsBottom - 30;
    }

    private void OnHomeClick(object sender, RoutedEventArgs e) => ShowHome();

    private void OnProductChanged(string? _) => UpdateIdentity();

    /// <summary>Shows the active DUT serial in the header chip while a unit run is in progress.</summary>
    private void OnActiveSerialChanged(string? serial)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => OnActiveSerialChanged(serial)); return; }

        var has = !string.IsNullOrWhiteSpace(serial);
        SerialRun.Text = serial ?? string.Empty;
        SerialChip.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        // The clear-x rides with the chip, but only while no run owns the serial.
        ClearSerialButton.Visibility = has && !_host.Process.HasActiveDutRun
            ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Clears the sticky DUT serial from the header (the tiny ✕ next to "DUT:").</summary>
    private void OnClearSerial(object sender, RoutedEventArgs e)
    {
        if (_host.Process.ClearActiveSerial())
            Log(LogLevel.Info, "Cleared current DUT serial.");
    }

    // --- DUT run lifecycle (header box; visible on the Run page) ---

    private void OnDutActivityChanged()
    {
        if (Dispatcher.CheckAccess()) RefreshDutButtons();
        else Dispatcher.BeginInvoke(RefreshDutButtons);
    }

    /// <summary>Reflects the open DUT run + alignment-busy state on the header DUT controls.</summary>
    private void RefreshDutButtons()
    {
        bool hasRun = _host.Process.HasActiveDutRun;
        bool busy = _host.Activity.AlignmentBusy;
        if (hasRun && !string.IsNullOrEmpty(_host.Process.ActiveSerial))
            SerialBox.Text = _host.Process.ActiveSerial;   // keep the box in sync with the active run
        SerialBox.IsEnabled = !hasRun;
        StartDutButton.IsEnabled = !busy && !hasRun;       // Start stays disabled while a run is open
        PassButton.IsEnabled = hasRun;
        FailButton.IsEnabled = hasRun;
        // The header clear-x is allowed only when a serial is shown and no run owns it.
        ClearSerialButton.Visibility = !hasRun && !string.IsNullOrWhiteSpace(_host.Process.ActiveSerial)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnStartDut(object sender, RoutedEventArgs e)
    {
        if (_host.Process.HasActiveDutRun) return;
        if (_host.Activity.AlignmentBusy)
        {
            Log(LogLevel.Warning, "Start DUT blocked — an alignment/scan is running. Stop it first.");
            return;
        }

        var serial = SerialBox.Text?.Trim() ?? string.Empty;
        var r = _host.Process.BeginDutRun(serial, "FAU attach");
        if (!r.IsSuccess) { Log(LogLevel.Warning, $"Start DUT failed: {r.Message}"); return; }

        _run.StartProcessTimer();
        RefreshDutButtons();
    }

    private void OnPass(object sender, RoutedEventArgs e) => EndDut(DutVerdict.Pass);

    private void OnFail(object sender, RoutedEventArgs e) => EndDut(DutVerdict.Fail);

    private void EndDut(DutVerdict verdict)
    {
        if (!_host.Process.HasActiveDutRun) return;

        var serial = SerialBox.Text?.Trim() ?? string.Empty;
        var r = _host.Process.EndDutRun(verdict);
        Log(r.IsSuccess ? LogLevel.Info : LogLevel.Warning,
            r.IsSuccess ? $"DUT '{serial}' → {verdict}." : $"End DUT failed: {r.Message}");

        _run.StopProcessTimer();
        RefreshDutButtons();
        SerialBox.SelectAll();
        SerialBox.Focus();
    }

    private void OnResults(object sender, RoutedEventArgs e)
        => new DutResultsWindow(_host) { Owner = this }.Show();

    private void Log(LogLevel level, string message)
        => _host.Log.Add(new LogEntry(level, System.DateTimeOffset.Now, "DUT", message));

    /// <summary>Refreshes the product + operator chip and the login/logout button label.</summary>
    private void UpdateIdentity()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(UpdateIdentity); return; }

        ProductRun.Text = _host.Process.ActiveProductInfo?.Label ?? "—";
        var op = _host.Session.Operator;
        OperatorRun.Text = op?.DisplayName ?? "Not logged in";
        OperatorRun.Foreground = (System.Windows.Media.Brush)FindResource(op != null ? "Text" : "Danger");
        LogoutButton.Content = _host.Session.IsLoggedIn ? "Logout" : "Login";
    }

    /// <summary>
    /// Logs the current operator out (if any) and prompts for the next login. The
    /// engine stays up throughout — this only changes who is attributed. Cancelling
    /// the dialog leaves the station logged out; the Run page gates DUT actions on
    /// a signed-in operator.
    /// </summary>
    private void OnLogout(object sender, RoutedEventArgs e)
    {
        if (_host.Session.IsLoggedIn)
            _host.Session.LogOut();

        var login = new LoginWindow(_host) { Owner = this };
        login.ShowDialog();
        UpdateIdentity();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting)
            return;   // shutdown sequence already running — let the close proceed

        // Cancel this close; we drive the exit ourselves once confirmed.
        e.Cancel = true;

        var answer = MessageBox.Show(
            this,
            "Disconnect all hardware and exit FAAM?",
            "Exit FAAM",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
            return;   // stay open

        // Capture window sizes/positions + open camera windows while everything is
        // still on screen, before teardown closes them.
        SaveWindowLayout();

        _exiting = true;
        _host.Session.Changed -= UpdateIdentity;
        _host.Process.ProductChanged -= OnProductChanged;
        _host.Process.ActiveSerialChanged -= OnActiveSerialChanged;
        _run.Deactivate();         // stop frame routing before the engine tears down
        _machineVm.Dispose();      // stop the status sampler + detach UI hooks
        JogPanelControl.Stop();    // stop the jog-panel position poll

        // Hand off to the shutdown screen — it runs the ordered teardown with
        // progress, then calls Application.Shutdown(). This window stays alive
        // but hidden so the process stays up until teardown completes.
        new ShutdownWindow(_host).Show();
        Hide();
    }
}
