using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Horizon.Station.UI.Process;
using Horizon.Station.UI.ViewModels;
using Horizon.Station.UI.ViewModels.Sections;
using Horizon.Station.UI.Views;
using Horizon.Workflow.Oee;
using Horizon.Common;
using Horizon.Engine;
using Horizon.Workflow;

namespace Horizon.Station.UI;

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
    private readonly IRunPage _run;          // the head's production page
    private readonly EngineerPageView _engineer;
    private readonly VisionAlignmentView _visionAlignment;
    private readonly ScriptEditorView _scriptEditor;
    private readonly CalibrationView _calibration;
    private readonly CameraNeedleCalibrationView _cameraNeedle;
    private readonly RepositoryView _repository;
    private readonly IStationPage _dutData;  // the head's DUT data page
    private readonly OeeView _oee;

    /// <summary>Camera pop-out windows this shell owns (auto-opened + restored).</summary>
    private readonly List<DisplayWindow> _cameraWindows = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _exiting;
    private bool _exitConfirmed;   // exit already confirmed elsewhere (software update) — skip the prompt

    public ShellWindow(EngineHost host)
    {
        _host = host;
        _machineVm = new MachinePageViewModel(host);
        _machine = new MachinePageView { DataContext = _machineVm };
        _run = host.Head.CreateRunPage(host);
        _run.ProgressChanged += OnRunProgress;
        _run.EditProgramRequested += OnEditProgramRequested;
        _engineer = new EngineerPageView(host);
        _visionAlignment = new VisionAlignmentView(host);
        _scriptEditor = new ScriptEditorView(host);
        _calibration = new CalibrationView();
        _cameraNeedle = new CameraNeedleCalibrationView(host);
        _repository = new RepositoryView();
        _repository.Attach(host);
        _dutData = host.Head.CreateDutDataPage(host);
        _oee = new OeeView();
        _oee.Attach(host);
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
        _home.VisionAlignmentRequested += ShowVisionAlignment;
        _home.ScriptRequested += ShowScript;
        _home.CalibrationRequested += ShowCalibration;
        _home.RepositoryRequested += ShowRepository;
        _home.DutDataRequested += ShowDutData;
        _home.OeeRequested += ShowOee;
        _home.UpdateExitRequested += ExitForUpdate;
        _calibration.CameraNeedleRequested += ShowCameraNeedle;
        _cameraNeedle.BackRequested += ShowCalibration;

        // Identity chip — refresh on login/logout and on product switch.
        _host.Session.Changed += UpdateIdentity;
        _host.Process.ProductChanged += OnProductChanged;
        _host.Process.ActiveSerialChanged += OnActiveSerialChanged;
        _host.Activity.Changed += OnDutActivityChanged;   // gate Start DUT while an alignment/scan holds the hexapod

        // Safety: a watchdog trip (E-Stop) raises a blocking acknowledge overlay; clearing
        // the flag (Acknowledge, or any other surface) removes it.
        _host.Engine.Safety.SafetyTripped += OnSafetyTripped;
        _host.Engine.Safety.SafetyCleared += OnSafetyCleared;
        RefreshSafetyOverlay();

        // Info bar: where this executable runs from + a 1 s wall clock.
        WorkFolderText.Text = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        WorkFolderText.ToolTip = WorkFolderText.Text;
        UpdateClock();
        _clock.Tick += (_, _) => { UpdateClock(); UpdateStatusChrome(); };
        _clock.Start();
        Closed += (_, _) => _clock.Stop();

        // OEE: the station is online from here until the shutdown sequence ends the session.
        _host.StartOeeSession();
        UpdateStatusChrome();

        UpdateIdentity();
        OnActiveSerialChanged(_host.Process.ActiveSerial);
        RefreshDutButtons();

        ShowHome();

        Closing += OnClosing;

        // Pop up the live main_camera window as soon as the app is up (the engine
        // is already connected by the time the shell is shown), if enabled.
        Loaded += OnShellLoaded;
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        ClockTime.Text = now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        ClockDate.Text = now.ToString("ddd yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Paints the machine state onto the header so it reads from across the room: the info
    /// bar takes the state colour (green running, light green standby, amber idle, red down)
    /// with a state label, and the pika follows — working (hop + zap) while anything executes
    /// (a Run-page step reports instantly via <see cref="_runProgress"/>), awake in standby,
    /// faded + asleep when idle, red with a "!" when down. Runs every second and on run progress.
    /// </summary>
    private void UpdateStatusChrome()
    {
        var status = _host.Process.Oee.Status;
        var state = _runProgress.Running && status.State is OeeState.Standby or OeeState.Idle
            ? OeeState.Running   // a Run-page step just started; the OEE bracket lands a moment later
            : status.State;

        InfoBar.Background = OeeColors.For(state);
        System.Windows.Documents.TextElement.SetForeground(InfoBar, OeeColors.TextOn(state));
        MachineStateText.Text = state switch
        {
            OeeState.Running => "UP · RUNNING",
            OeeState.Standby => "UP · STANDBY",
            _ => OeeColors.Label(state),
        };
        var since = OeeColors.Duration(DateTime.UtcNow - status.SinceUtc);
        MachineSinceText.Text = status.OpenDowntime is { } d
            ? $"by {d.DownBy} · {since}"
            : $"since {status.SinceUtc.ToLocalTime():HH:mm} · {since}";

        Mascot.Mood = state switch
        {
            OeeState.Down => MascotMood.Down,
            OeeState.Running => MascotMood.Working,
            OeeState.Idle or OeeState.Offline => MascotMood.Sleeping,
            _ => MascotMood.Awake,
        };
        Mascot.ToolTip = $"{Mascot.MascotName} — station {OeeColors.Label(state).ToLowerInvariant()}";
    }

    /// <summary>Opens %APPDATA%\Horizon (config, products, logs, history.db) in Explorer.</summary>
    private void OnOpenAppData(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Horizon");
        var error = FolderOpener.Open(dir);
        if (error is not null)
            Log(LogLevel.Warning, $"Cannot open {dir}: {error}");
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

    // --- Safety acknowledge overlay ---

    private void OnSafetyTripped(SafetyTrip trip)
        => Dispatcher.BeginInvoke(() => ShowSafetyOverlay(trip.Title, trip.Message));

    private void OnSafetyCleared()
        => Dispatcher.BeginInvoke(HideSafetyOverlay);

    private void RefreshSafetyOverlay()
    {
        if (_host.Engine.Safety.GetState().FlagRaised)
            ShowSafetyOverlay("Safety stop", "A safety flag is raised. Clear the hazard, then Acknowledge to resume.");
        else
            HideSafetyOverlay();
    }

    private void ShowSafetyOverlay(string title, string message)
    {
        SafetyTitle.Text = string.IsNullOrWhiteSpace(title) ? "⚠ SAFETY STOP" : "⚠ " + title;
        SafetyMessage.Text = message;
        SafetyHint.Text = "Clear the hazard and release the E-Stop, then Acknowledge to resume.";
        AcknowledgeButton.IsEnabled = true;
        SafetyOverlay.Visibility = Visibility.Visible;
    }

    private void HideSafetyOverlay() => SafetyOverlay.Visibility = Visibility.Collapsed;

    private async void OnAcknowledgeSafety(object sender, RoutedEventArgs e)
    {
        AcknowledgeButton.IsEnabled = false;
        SafetyHint.Text = "Acknowledging…";
        var r = await _host.Engine.Safety.AcknowledgeAsync();
        if (r.IsSuccess)
        {
            // SafetyCleared hides the overlay; if the input is still tripped the watchdog
            // re-raises and SafetyTripped shows it again.
            HideSafetyOverlay();
        }
        else
        {
            AcknowledgeButton.IsEnabled = true;
            SafetyHint.Text = $"Cannot acknowledge yet — {r.Message} Release the E-Stop and retry.";
        }
    }

    /// <summary>Stops a page's background work (camera pipeline, status sampler) when leaving it.</summary>
    private void LeaveCurrent()
    {
        ApplyHeaderProgress(RunProgressSnapshot.Idle);   // the RUN header bar belongs to the Run page only
        if (ReferenceEquals(PageHost.Content, _run))
            _run.Deactivate();
        else if (ReferenceEquals(PageHost.Content, _machine))
            _machineVm.OnDeactivated();
        else if (ReferenceEquals(PageHost.Content, _engineer))
            _engineer.Deactivate();
        else if (ReferenceEquals(PageHost.Content, _visionAlignment))
            _visionAlignment.Deactivate();
        else if (ReferenceEquals(PageHost.Content, _scriptEditor))
            _scriptEditor.Deactivate();
        else if (ReferenceEquals(PageHost.Content, _cameraNeedle))
            _cameraNeedle.Deactivate();
        else if (ReferenceEquals(PageHost.Content, _repository))
            _repository.Deactivate();
        else if (ReferenceEquals(PageHost.Content, _dutData))
            _dutData.Deactivate();
        else if (ReferenceEquals(PageHost.Content, _oee))
            _oee.Deactivate();
    }

    /// <summary>
    /// The navigation gate. Every <c>Show*</c> asks this first, so a screen can only be
    /// reached by someone signed in at its <see cref="AccessPolicy"/> level — regardless
    /// of how the navigation was triggered (a Home tile, a page's Back button, or a
    /// future caller that forgets to check). Refused navigations are logged and drop the
    /// operator on Home, which is safe at any level.
    /// </summary>
    private bool Allow(AccessLevel required, string screen)
    {
        if (_host.Session.Has(required)) return true;

        Log(LogLevel.Warning, _host.Session.IsLoggedIn
            ? $"{screen} needs {required} level — signed in as {_host.Session.Level}."
            : $"{screen} needs a sign-in.");
        ShowHome();
        return false;
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
        if (!Allow(AccessPolicy.Machine, "Machine")) return;
        LeaveCurrent();
        Header1Text.Text = "MACHINE";
        _machineVm.OnActivated();   // starts the ~5 Hz status sampler
        EngineHeader.Visibility = Visibility.Visible;
        DutHeader.Visibility = Visibility.Collapsed;
        PageHost.Content = _machine;
    }

    private void ShowRun()
    {
        if (!Allow(AccessPolicy.Run, "Run")) return;
        LeaveCurrent();
        Header1Text.Text = "RUN";
        ApplyHeaderProgress(_runProgress);   // a step may still be executing from before we left
        EngineHeader.Visibility = Visibility.Collapsed;
        DutHeader.Visibility = Visibility.Visible;   // serial-bracketed DUT run lives in the header here
        RefreshDutButtons();
        _run.Activate();   // starts grabbing + frame routing
        PageHost.Content = _run;
    }

    private RunProgressSnapshot _runProgress = RunProgressSnapshot.Idle;

    private void OnRunProgress(RunProgressSnapshot snap)
    {
        _runProgress = snap;
        UpdateStatusChrome();   // the pika works while a step runs, on any page
        if (ReferenceEquals(PageHost.Content, _run))
            ApplyHeaderProgress(snap);
    }

    /// <summary>
    /// Turns the page-title box into a big progress bar while a Run-page step executes:
    /// green fill to the current %, black "RUNNING…  42%" text. Idle restores "RUN".
    /// </summary>
    private void ApplyHeaderProgress(RunProgressSnapshot snap)
    {
        if (snap.Running)
        {
            HeaderProgress.Visibility = Visibility.Visible;
            HeaderProgress.IsIndeterminate = !snap.HasProgress;
            HeaderProgress.Value = snap.Percent;
            Header1Text.Text = snap.HasProgress ? $"RUNNING…   {snap.Short}" : "RUNNING…";
        }
        else if (HeaderProgress.Visibility == Visibility.Visible)
        {
            HeaderProgress.Visibility = Visibility.Collapsed;
            HeaderProgress.IsIndeterminate = false;
            if (ReferenceEquals(PageHost.Content, _run)) Header1Text.Text = "RUN";
        }
    }

    private void ShowEngineer()
    {
        if (!Allow(AccessPolicy.Engineer, "Engineer")) return;
        LeaveCurrent();
        Header1Text.Text = "ENGINEER";
        EngineHeader.Visibility = Visibility.Collapsed;
        DutHeader.Visibility = Visibility.Collapsed;
        _engineer.Activate();
        PageHost.Content = _engineer;
    }

    /// <summary>Run-page step "Edit" — go to the Engineer page (access-gated) and open that program.</summary>
    private void OnEditProgramRequested(string program)
    {
        ShowEngineer();
        if (ReferenceEquals(PageHost.Content, _engineer))
            _engineer.OpenProgram(program);
    }

    private void ShowVisionAlignment()
    {
        if (!Allow(AccessPolicy.VisionAlignment, "Vision Alignment")) return;
        LeaveCurrent();
        Header1Text.Text = "VISION ALIGNMENT";
        EngineHeader.Visibility = Visibility.Collapsed;
        DutHeader.Visibility = Visibility.Collapsed;
        _visionAlignment.Activate();
        PageHost.Content = _visionAlignment;
    }

    private void ShowScript()
    {
        if (!Allow(AccessPolicy.Script, "Script")) return;
        LeaveCurrent();
        Header1Text.Text = "SCRIPT";
        EngineHeader.Visibility = Visibility.Collapsed;
        DutHeader.Visibility = Visibility.Collapsed;
        _scriptEditor.Activate();
        PageHost.Content = _scriptEditor;
    }

    private void ShowCalibration()
    {
        if (!Allow(AccessPolicy.Calibration, "Calibration")) return;
        LeaveCurrent();
        Header1Text.Text = "CALIBRATION";
        EngineHeader.Visibility = Visibility.Collapsed;
        DutHeader.Visibility = Visibility.Collapsed;
        PageHost.Content = _calibration;
    }

    private void ShowRepository()
    {
        if (!Allow(AccessPolicy.Repository, "Repository")) return;
        LeaveCurrent();
        Header1Text.Text = "REPOSITORY";
        EngineHeader.Visibility = Visibility.Collapsed;
        DutHeader.Visibility = Visibility.Collapsed;
        _repository.Activate();
        PageHost.Content = _repository;
    }

    private void ShowDutData()
    {
        if (!Allow(AccessPolicy.DutData, "DUT Data")) return;
        LeaveCurrent();
        Header1Text.Text = "DUT DATA";
        EngineHeader.Visibility = Visibility.Collapsed;
        DutHeader.Visibility = Visibility.Collapsed;
        _dutData.Activate();
        PageHost.Content = _dutData;
    }

    private void ShowOee()
    {
        if (!Allow(AccessPolicy.Oee, "OEE")) return;
        LeaveCurrent();
        Header1Text.Text = "OEE";
        EngineHeader.Visibility = Visibility.Collapsed;
        DutHeader.Visibility = Visibility.Collapsed;
        _oee.Activate();
        PageHost.Content = _oee;
    }

    private void ShowCameraNeedle()
    {
        if (!Allow(AccessPolicy.Calibration, "Camera → needle calibration")) return;
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
            _host.Log.Add(new LogEntry(LogLevel.Warning, System.DateTimeOffset.Now, StationApp.Title,
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
        // Layer 2 may have ended the open DUT session (a program run entered a new serial),
        // so re-read it: Start/Pass/Fail and the clear-x all follow HasActiveDutRun.
        RefreshDutButtons();
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
        // Every DUT action is attributed to the signed-in operator, so a signed-out
        // station can neither open nor close a run.
        bool signedIn = _host.Session.Has(AccessPolicy.Run);
        if (hasRun && !string.IsNullOrEmpty(_host.Process.ActiveSerial))
            SerialBox.Text = _host.Process.ActiveSerial;   // keep the box in sync with the active run
        SerialBox.IsEnabled = signedIn && !hasRun;
        StartDutButton.IsEnabled = signedIn && !busy && !hasRun;   // Start stays disabled while a run is open
        PassButton.IsEnabled = signedIn && hasRun;
        FailButton.IsEnabled = signedIn && hasRun;
        ResultsButton.IsEnabled = signedIn;
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
        var r = _host.Process.BeginDutRun(serial, _host.Head.DutProcessName);
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

    /// <summary>
    /// Refreshes the product + operator chip, the login/logout button, and everything
    /// the sign-in gates: the jog rail and the DUT header. Runs on every
    /// <see cref="Session.Changed"/>, so a logout locks the station immediately.
    /// </summary>
    private void UpdateIdentity()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(UpdateIdentity); return; }

        ProductRun.Text = _host.Process.ActiveProductInfo?.Label ?? "—";
        var op = _host.Session.Operator;

        // The chip carries the level as well as the name — on a shared station "who is
        // signed in" is only half the answer; "as what" is the half that explains why a
        // screen is missing.
        OperatorRun.Text = op == null
            ? "Not signed in"
            : $"{op.DisplayName} · {op.Level}";
        OperatorRun.Foreground = (System.Windows.Media.Brush)FindResource(op != null ? "Text" : "Danger");
        LogoutButton.Content = _host.Session.IsLoggedIn ? "Logout" : "Sign In";

        // Manual jogging needs a sign-in; STOP never does (JogPanel.SetLocked keeps it live).
        JogPanelControl.SetLocked(!_host.Session.Has(AccessPolicy.Jog));

        // A signed-out station must not be able to open or close a DUT run — the record
        // would have nobody to attribute it to.
        RefreshDutButtons();

        // The current page may have just become off-limits (logout, or a re-login at a
        // lower level). Send the operator home rather than leaving them on it.
        EvictIfNotAllowed();
    }

    /// <summary>
    /// Drops the operator back to Home when the page they are on needs more than the
    /// level they now hold. Called after any identity change — the navigation gate only
    /// covers going <i>to</i> a page, not still being on one when the level drops.
    /// </summary>
    private void EvictIfNotAllowed()
    {
        var required =
            ReferenceEquals(PageHost.Content, _run) ? (AccessLevel?)AccessPolicy.Run :
            ReferenceEquals(PageHost.Content, _machine) ? AccessPolicy.Machine :
            ReferenceEquals(PageHost.Content, _engineer) ? AccessPolicy.Engineer :
            ReferenceEquals(PageHost.Content, _visionAlignment) ? AccessPolicy.VisionAlignment :
            ReferenceEquals(PageHost.Content, _scriptEditor) ? AccessPolicy.Script :
            ReferenceEquals(PageHost.Content, _calibration) ? AccessPolicy.Calibration :
            ReferenceEquals(PageHost.Content, _cameraNeedle) ? AccessPolicy.Calibration :
            ReferenceEquals(PageHost.Content, _repository) ? AccessPolicy.Repository :
            ReferenceEquals(PageHost.Content, _dutData) ? AccessPolicy.DutData :
            null;

        if (required != null && !_host.Session.Has(required.Value))
            ShowHome();
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

    /// <summary>
    /// A software update is downloaded and the operator already confirmed it (Home › Version):
    /// run the normal disconnect-and-exit without asking again. <see cref="ShutdownWindow"/>
    /// starts the updater once the hardware is down.
    /// </summary>
    private void ExitForUpdate()
    {
        _exitConfirmed = true;
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting)
            return;   // shutdown sequence already running — let the close proceed

        // Cancel this close; we drive the exit ourselves once confirmed.
        e.Cancel = true;

        if (!_exitConfirmed)
        {
            var answer = MessageBox.Show(
                this,
                "Disconnect all hardware and exit FAAM?",
                "Exit FAAM",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
                return;   // stay open
        }

        // Capture window sizes/positions + open camera windows while everything is
        // still on screen, before teardown closes them.
        SaveWindowLayout();

        _exiting = true;
        _host.Session.Changed -= UpdateIdentity;
        _host.Process.ProductChanged -= OnProductChanged;
        _host.Process.ActiveSerialChanged -= OnActiveSerialChanged;
        _host.Engine.Safety.SafetyTripped -= OnSafetyTripped;
        _host.Engine.Safety.SafetyCleared -= OnSafetyCleared;
        // Stop every page-owned poller before the engine tears down: whichever page is
        // showing (LeaveCurrent), plus the Run + Vision Alignment pages explicitly (idempotent) —
        // their overlay / refresh timers would otherwise keep touching the facets on the UI
        // thread after they are nulled and throw "Engine is not initialized" mid-shutdown.
        LeaveCurrent();
        _run.Deactivate();
        _visionAlignment.Deactivate();
        _machineVm.Dispose();      // stop the status sampler + detach UI hooks
        JogPanelControl.Stop();    // stop the jog-panel position poll

        // Close the camera pop-outs *before* teardown starts. They are separate top
        // levels, so hiding this window leaves them up — and their tool-overlay /
        // detection timers keep polling the engine on the UI thread while the facets
        // are being nulled, which throws mid-shutdown and stalls the exit.
        foreach (var win in Application.Current.Windows.OfType<DisplayWindow>().ToList())
            win.Close();

        // Hand off to the shutdown screen — it runs the ordered teardown with
        // progress, then calls Application.Shutdown(). This window stays alive
        // but hidden so the process stays up until teardown completes.
        new ShutdownWindow(_host).Show();
        Hide();
    }
}
