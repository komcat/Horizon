using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Horizon.Engine;

namespace FAAM.UI.Views;

/// <summary>
/// Global jog pad (lives in the shell left rail). Lists connected motion devices,
/// family-based velocity presets, and an X/Y/Z + U/V/W jog pad. The jog buttons are
/// in the GLOBAL frame; each device's <see cref="JogMatrix"/> (Layer 3 config)
/// converts a global jog into the device-local axis. Press-and-hold to jog,
/// release to stop.
/// </summary>
public partial class JogPanel : UserControl
{
    /// <summary>
    /// Fallback linear step set (mm), used before a device is selected. Once a device
    /// loads, its family's configured set (<c>HexStepPresetsMm</c> / <c>GantryStepPresetsMm</c>
    /// in AppSettings) replaces this. Both the quick-select buttons and the scroll /
    /// Q / E step cycling walk this same set.
    /// </summary>
    private static readonly double[] DefaultStepSet =
    {
        0.0005, 0.001, 0.002, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 1.0,
    };

    /// <summary>θ (U/V/W rotational) step presets — used only for the U/V/W jog buttons.</summary>
    private static readonly (string Label, double Step)[] ThetaSteps =
    {
        ("100µm", 0.1), ("200µm", 0.2), ("300µm", 0.3),
    };

    private readonly JogTransformStore _transforms = new();
    private readonly List<ToggleButton> _stepButtons = new();
    /// <summary>Velocity presets for the selected device (mm/s) — populates the velocity combo.</summary>
    private double[] _velValues = Array.Empty<double>();
    /// <summary>Suppresses the combo's apply-on-select while we set the selection programmatically.</summary>
    private bool _suppressVelApply;
    private readonly List<ToggleButton> _thetaButtons = new();
    private readonly DispatcherTimer _poll;

    /// <summary>The four Camera exposure-preset buttons + their µs values (0 = unset).</summary>
    private readonly Button[] _expPresetButtons = new Button[4];
    private readonly double[] _expPresetValues = new double[4];
    /// <summary>Last-known camera exposure — gain / frame-rate preserved across edits.</summary>
    private CameraExposure _camExposure = new(0, 0, false, false, 0);

    /// <summary>The active linear step set (mm) for the selected device's family.</summary>
    private double[] _stepSet = DefaultStepSet;
    private int _stepIndex;
    private double _stepMm = DefaultStepSet[0];
    private double _thetaStep = ThetaSteps[0].Step;   // U/V/W jog step (separate from the linear X/Y/Z step)
    private EngineHost? _host;
    private string? _device;
    private JogMatrix _matrix = JogMatrix.Identity;

    /// <summary>Pale-green panel tint shown while keyboard jog is armed.</summary>
    private static readonly Brush ArmedBg = Freeze(new SolidColorBrush(Color.FromRgb(0xE3, 0xF2, 0xE3)));
    private bool _keysEnabled;
    private bool _locked;

    // Latest-wins jog: a new press records itself as the pending jog and cancels the
    // in-flight move (the engine halts it within ~one 50 ms poll, then its await
    // returns). The pump loop then runs whatever the latest pending request is, so
    // spamming a direction abandons the current short move and restarts from the
    // current position. All these fields are touched only on the UI thread.
    private string? _pendingJog;
    private CancellationTokenSource? _jogCts;
    private bool _jogPumpRunning;

    private static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    public JogPanel()
    {
        InitializeComponent();
        SetStepSet(DefaultStepSet);   // builds the step buttons + selects a default step
        BuildThetaSteps();
        BuildExposurePresets();
        _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _poll.Tick += (_, _) => UpdatePosition();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    // Keyboard jog is captured at the window level so it works from any page while
    // the rail is visible — but only when armed (the "Keys" toggle / ` hotkey).
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } w) w.PreviewKeyDown += OnWindowPreviewKeyDown;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } w) w.PreviewKeyDown -= OnWindowPreviewKeyDown;
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Never hijack typing — bail if focus is in a text field.
        if (Keyboard.FocusedElement is TextBox or PasswordBox) return;

        // The enable/disable hotkey (` grave accent) works whether or not jog is armed.
        if (e.Key == Key.OemTilde) { ToggleKeys(); e.Handled = true; return; }

        if (!_keysEnabled || _locked) return;

        string? jog = e.Key switch
        {
            Key.A => "X-", Key.D => "X+",
            Key.W => "Y-", Key.S => "Y+",
            Key.R => "Z+", Key.F => "Z-",
            _ => null,
        };
        if (jog != null) { _ = RequestJog(jog); e.Handled = true; return; }

        switch (e.Key)
        {
            case Key.Q: StepBy(-1); e.Handled = true; break;   // finer
            case Key.E: StepBy(+1); e.Handled = true; break;   // coarser
            case Key.Tab: CycleDevice(); e.Handled = true; break;
        }
    }

    private void OnToggleKeys(object sender, RoutedEventArgs e) => SetKeysEnabled(KeysToggle.IsChecked == true);

    /// <summary>
    /// Locks the rail when nobody is signed in (see <c>AccessPolicy.Jog</c>): every
    /// control is disabled <b>except STOP</b>, which must stay live for whoever is
    /// standing at the machine. Keyboard jog is disarmed too, since a disabled
    /// ToggleButton would not otherwise stop the already-armed hotkeys.
    /// </summary>
    public void SetLocked(bool locked)
    {
        _locked = locked;
        foreach (var child in RootStack.Children.OfType<UIElement>())
            if (!ReferenceEquals(child, StopButton))
                child.IsEnabled = !locked;

        if (locked && _keysEnabled) SetKeysEnabled(false);
        if (locked) SetStatus("Locked — sign in to jog. STOP stays available.");
    }

    private void ToggleKeys()
    {
        KeysToggle.IsChecked = !_keysEnabled;
        SetKeysEnabled(KeysToggle.IsChecked == true);
    }

    private void SetKeysEnabled(bool on)
    {
        _keysEnabled = on;
        KeysToggle.IsChecked = on;
        RootBorder.Background = on ? ArmedBg : (Brush)FindResource("Panel");
        SetStatus(on
            ? "Keyboard jog ARMED — A/D=X  W/S=Y  R/F=Z  Q/E=step  Tab=device"
            : "Keyboard jog off.");
    }

    /// <summary>Moves the step selection <paramref name="delta"/> positions away (−1 finer, +1 coarser).</summary>
    private void StepBy(int delta)
    {
        if (_stepSet.Length == 0) return;
        int next = Math.Clamp(_stepIndex + delta, 0, _stepSet.Length - 1);
        if (next == _stepIndex) return;
        _stepIndex = next;
        _stepMm = _stepSet[next];
        UpdateStepDisplay();
        SetStatus($"Step {FormatStep(_stepMm)}");
    }

    /// <summary>Refreshes the large step-size readout + the preset-button highlight to the current selection.</summary>
    private void UpdateStepDisplay()
    {
        StepDisplay.Text = FormatStep(_stepMm);
        SyncStepButtons();
    }

    /// <summary>
    /// Swaps in a new linear step set (mm): rebuilds the quick-select buttons and picks a
    /// default selection (the step nearest 10µm, else the middle of the set). Called at
    /// startup with the fallback set and on each device select with the family's set.
    /// </summary>
    private void SetStepSet(double[] set)
    {
        _stepSet = set is { Length: > 0 } ? set : DefaultStepSet;

        _stepButtons.Clear();
        StepPresetList.Children.Clear();
        var style = (Style)FindResource("VelBtn");   // generic accent toggle chip
        foreach (var mm in _stepSet)
        {
            var tb = new ToggleButton { Content = FormatStep(mm), Tag = mm, Style = style, FontSize = 12 };
            tb.Click += OnStepPreset;
            _stepButtons.Add(tb);
            StepPresetList.Children.Add(tb);
        }

        // Default to the step nearest 10µm so behaviour matches the old fixed default.
        int best = 0;
        for (int i = 1; i < _stepSet.Length; i++)
            if (Math.Abs(_stepSet[i] - 0.01) < Math.Abs(_stepSet[best] - 0.01)) best = i;
        _stepIndex = best;
        _stepMm = _stepSet[best];
        UpdateStepDisplay();
    }

    private void OnStepPreset(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton tb || tb.Tag is not double mm) return;
        int idx = Array.FindIndex(_stepSet, s => Math.Abs(s - mm) < 1e-9);
        if (idx >= 0) { _stepIndex = idx; _stepMm = _stepSet[idx]; }
        UpdateStepDisplay();   // syncs the highlight
        SetStatus($"Step {FormatStep(_stepMm)}");
    }

    /// <summary>Formats a step in mm as a friendly label — µm below 1mm, mm at/above (rounded to kill fp noise).</summary>
    private static string FormatStep(double mm)
        => mm < 1.0 ? $"{Math.Round(mm * 1000.0, 4):g}µm" : $"{Math.Round(mm, 4):g}mm";

    /// <summary>Highlights the preset button matching the current step (none if the step isn't a preset).</summary>
    private void SyncStepButtons()
    {
        foreach (var b in _stepButtons)
            b.IsChecked = b.Tag is double mm && Math.Abs(mm - _stepMm) < 1e-9;
    }

    private void OnStepCoarser(object sender, MouseButtonEventArgs e) => StepBy(+1);
    private void OnStepFiner(object sender, MouseButtonEventArgs e) => StepBy(-1);
    private void OnStepWheel(object sender, MouseWheelEventArgs e) => StepBy(e.Delta > 0 ? +1 : -1);

    /// <summary>Selects the next connected motion device (wraps around).</summary>
    private void CycleDevice()
    {
        var buttons = DeviceList.Children.OfType<ToggleButton>().ToList();
        if (buttons.Count == 0) return;
        int cur = buttons.FindIndex(b => b.IsChecked == true);
        buttons[(cur + 1) % buttons.Count].IsChecked = true;   // fires OnDeviceChecked (single-select)
    }

    private void BuildThetaSteps()
    {
        var style = (Style)FindResource("ThetaStepBtn");
        foreach (var (label, step) in ThetaSteps)
        {
            var tb = new ToggleButton { Content = label, Tag = step, Style = style };
            tb.Click += OnThetaStep;
            _thetaButtons.Add(tb);
            ThetaStepList.Children.Add(tb);
        }
        _thetaButtons[0].IsChecked = true;   // 100µm default
    }

    private void OnThetaStep(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton tb || tb.Tag is not double step) return;
        foreach (var b in _thetaButtons) b.IsChecked = false;
        tb.IsChecked = true;
        _thetaStep = step;
        SetStatus($"θ step {tb.Content}");
    }

    /// <summary>Supplies the engine host and builds the device list. Called by the shell.</summary>
    public void Attach(EngineHost host)
    {
        _host = host;
        BuildDevices();
        BuildCameras();          // populates the picker → fires OnCameraChanged → LoadCameraExposure
        LoadExposurePresets();
        _poll.Start();   // live position readout (~4 Hz, reads the engine's cached state)

        // Reflect velocity / exposure-preset edits made elsewhere.
        host.Settings.Changed += _ =>
        {
            if (_device != null) LoadPresets(_device);
            LoadExposurePresets();
        };
    }

    /// <summary>Stops the live-position poll — called by the shell before engine teardown.</summary>
    public void Stop() => _poll.Stop();

    private void UpdatePosition()
    {
        if (_host == null || _device == null)
            return;

        MotionDeviceState state;
        try
        {
            state = _host.Engine.Motion.GetState(_device);   // cached, non-blocking
        }
        catch
        {
            // Engine torn down (facet accessor throws once shut down) — stop polling.
            _poll.Stop();
            return;
        }

        if (!state.Valid)
        {
            PosX.Text = PosY.Text = PosZ.Text = PosU.Text = PosV.Text = PosW.Text = "—";
            return;
        }
        var p = state.Position;
        PosX.Text = p.X.ToString("F3");
        PosY.Text = p.Y.ToString("F3");
        PosZ.Text = p.Z.ToString("F3");
        PosU.Text = p.U.ToString("F3");
        PosV.Text = p.V.ToString("F3");
        PosW.Text = p.W.ToString("F3");
    }

    private void BuildDevices()
    {
        DeviceList.Children.Clear();
        if (_host == null) return;

        foreach (var dev in _host.Engine.Motion.DeviceNames)
        {
            if (!_host.Engine.Motion.IsConnected(dev)) continue;
            var tb = new ToggleButton
            {
                Content = dev,
                Tag = dev,
                Margin = new Thickness(2),
                Padding = new Thickness(6),
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                Cursor = Cursors.Hand,
            };
            tb.Checked += OnDeviceChecked;
            DeviceList.Children.Add(tb);
        }

        if (DeviceList.Children.Count > 0 && DeviceList.Children[0] is ToggleButton first)
            first.IsChecked = true;   // selects it via OnDeviceChecked
    }

    private void OnDeviceChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton tb || tb.Tag is not string dev) return;

        foreach (var child in DeviceList.Children)
            if (child is ToggleButton other && !ReferenceEquals(other, tb))
                other.IsChecked = false;

        _device = dev;
        _matrix = _transforms.Get(dev);
        LoadPresets(dev);

        // Do NOT push a velocity to the controller on selection / startup — keep
        // whatever velocity the controller already has. Instead read and show the
        // controller's current velocity (read-only); the operator changes it by
        // clicking a preset.
        _ = ShowCurrentVelocityAsync(dev);
    }

    /// <summary>
    /// Reads the controller's current velocity (without changing it) and shows it in
    /// the status line — PI hexapods report a single system (path) velocity; ACS and
    /// others are per-axis, so we read the first axis as representative. If the read
    /// value matches a preset it highlights that preset (display-only — no apply).
    /// </summary>
    private async Task ShowCurrentVelocityAsync(string device)
    {
        if (_host == null) return;
        try
        {
            var info = _host.Engine.Motion.GetDevice(device);
            bool isPi = string.Equals(info?.ControllerType, "PI", StringComparison.OrdinalIgnoreCase);
            var r = isPi
                ? await _host.Engine.Motion.GetSystemVelocityAsync(device)
                : await _host.Engine.Motion.GetVelocityAsync(device, info is { Axes.Count: > 0 } ? info.Axes[0] : "X");

            if (_device != device) return;   // selection changed while we were reading

            if (r.IsSuccess)
            {
                SetStatus($"{device} · current velocity {r.Value:g} mm/s (unchanged)");
                SelectVelocityInCombo(r.Value);   // reflect the live velocity if it matches a preset
            }
            else
            {
                SetStatus($"{device} selected · controller velocity unchanged");
            }
        }
        catch
        {
            // Engine read failed (e.g. torn down) — leave a neutral status.
            if (_device == device) SetStatus($"{device} selected · controller velocity unchanged");
        }
    }

    private void LoadPresets(string device)
    {
        bool hex = IsHexFamily(device);
        var presets = hex
            ? _host?.Settings.Current.HexVelocityPresets
            : _host?.Settings.Current.GantryVelocityPresets;
        presets ??= Array.Empty<double>();

        _velValues = presets.Where(v => v > 0).ToArray();

        // Rebuild the combo without triggering an apply.
        _suppressVelApply = true;
        VelocityCombo.Items.Clear();
        foreach (var v in _velValues)
            VelocityCombo.Items.Add(new ComboBoxItem { Content = $"{v:g} mm/s", Tag = v });
        VelocityCombo.SelectedIndex = -1;
        VelocityCombo.IsEnabled = _velValues.Length > 0;
        _suppressVelApply = false;

        // Swap the jog step set to this device family's configured steps (mm).
        var steps = hex
            ? _host?.Settings.Current.HexStepPresetsMm
            : _host?.Settings.Current.GantryStepPresetsMm;
        SetStepSet((steps ?? Array.Empty<double>()).Where(s => s > 0).OrderBy(s => s).ToArray());
    }

    private static bool IsHexFamily(string device)
        => device.StartsWith("hex", StringComparison.OrdinalIgnoreCase);

    /// <summary>Selects the preset matching <paramref name="v"/> (or clears) without applying it.</summary>
    private void SelectVelocityInCombo(double v)
    {
        _suppressVelApply = true;
        VelocityCombo.SelectedItem = VelocityCombo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(it => it.Tag is double d && Math.Abs(d - v) < 1e-6);
        _suppressVelApply = false;
    }

    private void OnVelocityCombo(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressVelApply) return;
        if (VelocityCombo.SelectedItem is ComboBoxItem it && it.Tag is double v)
            _ = ApplyVelocityAsync(v);
    }

    /// <summary>Pushes <paramref name="v"/> mm/s to the selected device and flashes a confirmation.</summary>
    private async Task ApplyVelocityAsync(double v)
    {
        if (_host == null || _device == null || v <= 0) return;

        var r = await _host.Engine.Motion.SetDeviceVelocityAsync(_device, v);
        if (r.IsSuccess)
        {
            SetStatus($"Velocity {v:g} mm/s · {_device}");
            FlashVel($"✓ {v:g} mm/s applied");
        }
        else
        {
            SetStatus($"Velocity failed: {r.Message}");
            FlashVel("✗ failed");
        }
    }

    /// <summary>Shows a brief confirmation next to the velocity combo, then fades it out.</summary>
    private void FlashVel(string text)
    {
        VelFlash.Text = text;
        VelFlash.BeginAnimation(OpacityProperty, null);
        VelFlash.Opacity = 1;
        var fade = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromSeconds(1.4)))
        {
            BeginTime = TimeSpan.FromMilliseconds(400),
        };
        VelFlash.BeginAnimation(OpacityProperty, fade);
    }

    private void OnJog(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string tag)
            _ = RequestJog(tag);
    }

    private async void OnStop(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        // Drop any queued/in-flight jog so the pump does not re-issue a move after the halt.
        _pendingJog = null;
        _jogCts?.Cancel();
        // KILL every motion device. Report any device whose stop did not take so a
        // failed halt is never hidden behind a blanket "motion halted" message.
        var failed = new List<string>();
        foreach (var device in _host.Engine.Motion.DeviceNames)
        {
            try
            {
                var r = await _host.Engine.Motion.StopAsync(device);
                if (!r.IsSuccess) failed.Add($"{device}: {r.Message}");
            }
            catch (Exception ex) { failed.Add($"{device}: {ex.Message}"); }
        }
        SetStatus(failed.Count == 0
            ? "STOP — motion halted."
            : $"STOP — FAILED for {string.Join("; ", failed)}");
    }

    /// <summary>
    /// Records <paramref name="tag"/> as the latest jog and kicks the pump. If a move is
    /// already running, cancels it so the pump abandons it and picks up this newest request.
    /// </summary>
    private async Task RequestJog(string tag)
    {
        if (_host == null || _device == null) return;

        _pendingJog = tag;       // latest wins — overwrites any earlier pending request
        _jogCts?.Cancel();       // abandon the in-flight move; its await returns shortly

        if (_jogPumpRunning) return;
        _jogPumpRunning = true;
        try
        {
            while (_pendingJog is { } next)
            {
                _pendingJog = null;
                using var cts = new CancellationTokenSource();
                _jogCts = cts;
                await JogOnceAsync(next, cts.Token);
            }
        }
        finally
        {
            _jogPumpRunning = false;
            _jogCts = null;
        }
    }

    private async Task JogOnceAsync(string tag, CancellationToken ct)
    {
        if (_host == null || _device == null) return;

        char axis = char.ToUpperInvariant(tag[0]);
        int sign = tag[1] == '+' ? 1 : -1;

        string localAxis;
        int dir;
        double step;
        if (axis is 'X' or 'Y' or 'Z')
        {
            // Global jog → device-local via the matrix. The configured matrices are
            // permutations, so exactly one local component is non-zero.
            double gx = axis == 'X' ? sign : 0, gy = axis == 'Y' ? sign : 0, gz = axis == 'Z' ? sign : 0;
            var (lx, ly, lz) = _matrix.Apply(gx, gy, gz);
            (string ax, double val)[] comps = { ("X", lx), ("Y", ly), ("Z", lz) };
            var dom = comps.OrderByDescending(c => Math.Abs(c.val)).First();
            localAxis = dom.ax;
            dir = Math.Sign(dom.val);
            step = _stepMm;
        }
        else
        {
            // U/V/W (rotational) jog directly — not part of the 3×3 position transform;
            // driven by the separate θ step.
            localAxis = axis.ToString();
            dir = sign;
            step = _thetaStep;
        }
        if (dir == 0) return;

        try
        {
            var r = await _host.Engine.Motion.MoveRelativeAsync(_device, localAxis, dir * step, ct);
            // A cancelled move (superseded by a newer press) is expected — not a failure.
            if (!r.IsSuccess && !ct.IsCancellationRequested)
                SetStatus($"Jog {tag} → {localAxis}{(dir > 0 ? "+" : "-")} failed: {r.Message}");
            // Publish the actual executed move so the DUT log records what the operator did
            // (and, via the mirror's signal probe, how the Sagnac signal responded).
            else if (r.IsSuccess)
                _host.Activities.Publish(OperatorActivity.Jog(_device, tag, localAxis, dir, step));
        }
        catch (Exception ex)
        {
            SetStatus($"Jog error: {ex.Message}");
        }
    }

    private void SetStatus(string text)
    {
        JogStatus.Text = text;
        _host?.Log.Add(new LogEntry(LogLevel.Info, DateTimeOffset.Now, "Jog", text));
    }

    // ---------------------------------------------------------------- Camera

    /// <summary>The camera the exposure controls drive — the dropdown selection.</summary>
    private string? CameraId => CameraSelect.SelectedItem as string;

    private void BuildExposurePresets()
    {
        for (int i = 0; i < _expPresetButtons.Length; i++)
        {
            var btn = new Button
            {
                Content = "—",
                Tag = i,
                Height = 28,
                Margin = new Thickness(2),
                Cursor = Cursors.Hand,
                ToolTip = "Left-click: apply.   Right-click: set + save this preset.",
            };
            btn.Click += OnExposurePresetClick;
            btn.MouseRightButtonUp += OnExposurePresetRightClick;
            _expPresetButtons[i] = btn;
            ExposurePresetList.Children.Add(btn);
        }
    }

    /// <summary>
    /// Fills the camera picker from the connected cameras and selects a default
    /// (<c>main_camera</c> if present, else the first). Setting the selection fires
    /// <see cref="OnCameraChanged"/>, which loads that camera's exposure.
    /// </summary>
    private void BuildCameras()
    {
        CameraSelect.Items.Clear();
        var ids = _host?.Engine.Camera.CameraIds;
        if (ids != null)
            foreach (var id in ids) CameraSelect.Items.Add(id);

        bool hasCameras = CameraSelect.Items.Count > 0;
        CameraSelect.IsEnabled = hasCameras;
        if (hasCameras && ids != null)
            CameraSelect.SelectedItem = ids.Contains("main_camera") ? "main_camera" : ids[0];
        else
            LoadCameraExposure();   // no selection-change will fire — disable controls now
    }

    private void OnCameraChanged(object sender, SelectionChangedEventArgs e) => LoadCameraExposure();

    /// <summary>Pre-fills the exposure box + auto toggle from the selected camera's current settings.</summary>
    private void LoadCameraExposure()
    {
        var cam = CameraId;
        if (_host == null || cam == null)
        {
            ExposureBox.IsEnabled = ExposureApply.IsEnabled = AutoExposureToggle.IsEnabled = false;
            SetCamStatus("No camera configured.");
            return;
        }
        ExposureBox.IsEnabled = ExposureApply.IsEnabled = AutoExposureToggle.IsEnabled = true;
        _camExposure = _host.Engine.Camera.GetState(cam).Exposure;
        ExposureBox.Text = _camExposure.ExposureUs > 0 ? _camExposure.ExposureUs.ToString("g") : string.Empty;
        UpdateAutoToggle(_camExposure.AutoExposure);
    }

    private void LoadExposurePresets()
    {
        var presets = _host?.Settings.Current.ExposurePresets ?? Array.Empty<double>();
        for (int i = 0; i < _expPresetButtons.Length; i++)
        {
            double v = i < presets.Length ? presets[i] : 0;
            _expPresetValues[i] = v;
            _expPresetButtons[i].Content = v > 0 ? v.ToString("g") : "—";
        }
    }

    private void UpdateAutoToggle(bool on)
    {
        AutoExposureToggle.IsChecked = on;
        AutoExposureToggle.Content = on ? "Auto-exposure: ON" : "Auto-exposure: OFF";
    }

    private void OnExposureKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { OnApplyExposure(sender, e); e.Handled = true; }
    }

    private void OnApplyExposure(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(ExposureBox.Text, out double us) || us <= 0)
        {
            SetCamStatus("Enter a positive exposure (µs).");
            return;
        }
        _ = ApplyExposureValueAsync(us);
    }

    private void OnExposurePresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not int i) return;
        double v = _expPresetValues[i];
        if (v <= 0) { SetCamStatus("Preset empty — right-click to set a value."); return; }
        ExposureBox.Text = v.ToString("g");
        _ = ApplyExposureValueAsync(v);
    }

    /// <summary>Right-click a preset → type a µs value, store it in AppSettings and persist.</summary>
    private void OnExposurePresetRightClick(object sender, MouseButtonEventArgs e)
    {
        if (_host == null || sender is not Button b || b.Tag is not int i) return;
        e.Handled = true;

        double cur = _expPresetValues[i];
        var dlg = new SerialEntryWindow("Exposure preset", $"Exposure µs for preset {i + 1}:",
                                        cur > 0 ? cur.ToString("g") : string.Empty)
        {
            Owner = Window.GetWindow(this),
        };
        if (dlg.ShowDialog() != true) return;
        if (!double.TryParse(dlg.Serial, out double us) || us <= 0)
        {
            SetCamStatus("Preset unchanged — enter a positive number.");
            return;
        }

        // Persist into AppSettings (clone the array so the record stays immutable).
        var settings = _host.Settings.Current;
        var arr = (double[])(settings.ExposurePresets?.Clone() ?? new double[_expPresetButtons.Length]);
        if (arr.Length < _expPresetButtons.Length) Array.Resize(ref arr, _expPresetButtons.Length);
        arr[i] = us;
        _host.Settings.Update(settings with { ExposurePresets = arr });
        _host.Settings.Save();   // Changed fires → LoadExposurePresets refreshes the buttons
        SetCamStatus($"Preset {i + 1} set to {us:g} µs.");
    }

    private async void OnToggleAutoExposure(object sender, RoutedEventArgs e)
    {
        bool on = AutoExposureToggle.IsChecked == true;
        UpdateAutoToggle(on);
        var cam = CameraId;
        if (_host == null || cam == null) return;

        _camExposure = _camExposure with { AutoExposure = on };
        var r = await _host.Engine.Camera.ApplyExposureAsync(cam, _camExposure);
        SetCamStatus(r.IsSuccess
            ? (on ? "Auto-exposure ON." : "Auto-exposure OFF.")
            : $"Auto-exposure failed: {r.Message}");
    }

    /// <summary>Applies a manual exposure (µs) — turns auto-exposure off, preserves gain / frame-rate.</summary>
    private async Task ApplyExposureValueAsync(double us)
    {
        var cam = CameraId;
        if (_host == null || cam == null) return;

        _camExposure = _camExposure with { ExposureUs = us, AutoExposure = false };
        UpdateAutoToggle(false);
        var r = await _host.Engine.Camera.ApplyExposureAsync(cam, _camExposure);
        SetCamStatus(r.IsSuccess ? $"Exposure {us:g} µs applied." : $"Exposure failed: {r.Message}");
    }

    private void SetCamStatus(string text) => CamStatus.Text = text;
}
