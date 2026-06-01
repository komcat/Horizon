using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
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
    /// <summary>Step-size presets (label, millimetres). Each jog click moves the selected step.</summary>
    private static readonly (string Label, double Mm)[] StepSizes =
    {
        ("0.1µm", 0.0001), ("0.2µm", 0.0002), ("0.5µm", 0.0005),
        ("1µm", 0.001), ("2µm", 0.002), ("5µm", 0.005),
        ("10µm", 0.01), ("25µm", 0.025), ("50µm", 0.05),
        ("100µm", 0.1), ("250µm", 0.25),
        ("1mm", 1.0), ("2mm", 2.0), ("5mm", 5.0),
    };
    private const int DefaultStepIndex = 6;   // 10µm

    private readonly JogTransformStore _transforms = new();
    private readonly ToggleButton[] _velButtons;
    private readonly double[] _velValues = new double[6];
    private readonly List<ToggleButton> _stepButtons = new();
    private readonly DispatcherTimer _poll;

    private double _stepMm = StepSizes[DefaultStepIndex].Mm;
    private EngineHost? _host;
    private string? _device;
    private JogMatrix _matrix = JogMatrix.Identity;

    /// <summary>Pale-green panel tint shown while keyboard jog is armed.</summary>
    private static readonly Brush ArmedBg = Freeze(new SolidColorBrush(Color.FromRgb(0xE3, 0xF2, 0xE3)));
    private bool _keysEnabled;

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
        _velButtons = new[] { Vel0, Vel1, Vel2, Vel3, Vel4, Vel5 };
        BuildSteps();
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

        if (!_keysEnabled) return;

        string? jog = e.Key switch
        {
            Key.A => "X-", Key.D => "X+",
            Key.W => "Y-", Key.S => "Y+",
            Key.R => "Z+", Key.F => "Z-",
            _ => null,
        };
        if (jog != null) { RequestJog(jog); e.Handled = true; return; }

        switch (e.Key)
        {
            case Key.Q: StepBy(-1); e.Handled = true; break;   // finer
            case Key.E: StepBy(+1); e.Handled = true; break;   // coarser
            case Key.Tab: CycleDevice(); e.Handled = true; break;
        }
    }

    private void OnToggleKeys(object sender, RoutedEventArgs e) => SetKeysEnabled(KeysToggle.IsChecked == true);

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

    /// <summary>Selects the step preset <paramref name="delta"/> positions away (−1 finer, +1 coarser).</summary>
    private void StepBy(int delta)
    {
        int cur = _stepButtons.FindIndex(b => b.IsChecked == true);
        if (cur < 0) cur = DefaultStepIndex;
        int next = Math.Clamp(cur + delta, 0, _stepButtons.Count - 1);
        if (next == cur) return;
        foreach (var b in _stepButtons) b.IsChecked = false;
        _stepButtons[next].IsChecked = true;
        _stepMm = (double)_stepButtons[next].Tag!;
        SetStatus($"Step {_stepButtons[next].Content}");
    }

    /// <summary>Selects the next connected motion device (wraps around).</summary>
    private void CycleDevice()
    {
        var buttons = DeviceList.Children.OfType<ToggleButton>().ToList();
        if (buttons.Count == 0) return;
        int cur = buttons.FindIndex(b => b.IsChecked == true);
        buttons[(cur + 1) % buttons.Count].IsChecked = true;   // fires OnDeviceChecked (single-select)
    }

    private void BuildSteps()
    {
        var style = (Style)FindResource("StepBtn");
        foreach (var (label, mm) in StepSizes)
        {
            var tb = new ToggleButton { Content = label, Tag = mm, Style = style };
            tb.Click += OnStep;
            _stepButtons.Add(tb);
            StepList.Children.Add(tb);
        }
        _stepButtons[DefaultStepIndex].IsChecked = true;   // 10µm default
    }

    private void OnStep(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton tb || tb.Tag is not double mm) return;
        foreach (var b in _stepButtons) b.IsChecked = false;
        tb.IsChecked = true;
        _stepMm = mm;
    }

    /// <summary>Supplies the engine host and builds the device list. Called by the shell.</summary>
    public void Attach(EngineHost host)
    {
        _host = host;
        BuildDevices();
        _poll.Start();   // live position readout (~4 Hz, reads the engine's cached state)

        // Reflect velocity-preset edits made in the Settings section.
        host.Settings.Changed += _ => { if (_device != null) LoadPresets(_device); };
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
                Margin = new Thickness(0, 0, 0, 4),
                Padding = new Thickness(6),
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

        // Default to the lowest (safest) preset so a jog never runs at an unknown speed.
        if (_velValues[0] > 0)
            _ = ApplyVelocityAsync(0);
    }

    private void LoadPresets(string device)
    {
        var presets = IsHexFamily(device)
            ? _host?.Settings.Current.HexVelocityPresets
            : _host?.Settings.Current.GantryVelocityPresets;
        presets ??= Array.Empty<double>();

        for (int i = 0; i < _velButtons.Length; i++)
        {
            double v = i < presets.Length ? presets[i] : 0;
            _velValues[i] = v;
            _velButtons[i].Content = v > 0 ? v.ToString("g") : "—";
            _velButtons[i].IsChecked = false;
            _velButtons[i].IsEnabled = v > 0;
        }
    }

    private static bool IsHexFamily(string device)
        => device.StartsWith("hex", StringComparison.OrdinalIgnoreCase);

    private void OnVelocity(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton tb)
        {
            int idx = Array.IndexOf(_velButtons, tb);
            if (idx >= 0) _ = ApplyVelocityAsync(idx);
        }
    }

    private async Task ApplyVelocityAsync(int idx)
    {
        if (_host == null || _device == null || idx < 0 || _velValues[idx] <= 0) return;

        foreach (var b in _velButtons) b.IsChecked = false;
        _velButtons[idx].IsChecked = true;   // single-select, stays selected

        var r = await _host.Engine.Motion.SetDeviceVelocityAsync(_device, _velValues[idx]);
        SetStatus(r.IsSuccess
            ? $"Velocity {_velValues[idx]:g} mm/s · {_device}"
            : $"Velocity failed: {r.Message}");
    }

    private void OnJog(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string tag)
            RequestJog(tag);
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
    private async void RequestJog(string tag)
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
        }
        else
        {
            // U/V/W (rotational) jog directly — not part of the 3×3 position transform.
            localAxis = axis.ToString();
            dir = sign;
        }
        if (dir == 0) return;

        try
        {
            var r = await _host.Engine.Motion.MoveRelativeAsync(_device, localAxis, dir * _stepMm, ct);
            // A cancelled move (superseded by a newer press) is expected — not a failure.
            if (!r.IsSuccess && !ct.IsCancellationRequested)
                SetStatus($"Jog {tag} → {localAxis}{(dir > 0 ? "+" : "-")} failed: {r.Message}");
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
}
