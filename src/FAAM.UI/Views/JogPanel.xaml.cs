using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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

    public JogPanel()
    {
        InitializeComponent();
        _velButtons = new[] { Vel0, Vel1, Vel2, Vel3, Vel4, Vel5 };
        BuildSteps();
        _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _poll.Tick += (_, _) => UpdatePosition();
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
            _ = JogAsync(tag);
    }

    private async void OnStop(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        // Halt every motion device (StopAsync bypasses the per-device lock).
        foreach (var device in _host.Engine.Motion.DeviceNames)
        {
            try { await _host.Engine.Motion.StopAsync(device); }
            catch (Exception ex) { SetStatus($"Stop {device} failed: {ex.Message}"); }
        }
        SetStatus("STOP — motion halted.");
    }

    private async Task JogAsync(string tag)
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
            var r = await _host.Engine.Motion.MoveRelativeAsync(_device, localAxis, dir * _stepMm);
            if (!r.IsSuccess)
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
