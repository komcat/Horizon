using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Horizon.Common;
using Horizon.Engine;

namespace FAAM.UI.Views;

/// <summary>
/// A lean, reusable manual-jog control: a device picker, live X/Y/Z/U/V/W readout,
/// step-size presets, and a configurable set of axis +/- buttons. Each click moves the
/// selected device by the selected step. X/Y/Z clicks are in the GLOBAL frame and
/// mapped to the device-local axis via <see cref="JogMatrix"/> (same as
/// <see cref="JogPanel"/>); U/V/W jog directly. Self-contained — no shell coupling —
/// so it can be hosted in a window (see <c>JogPopupWindow</c>) or any page.
/// Call <see cref="Configure"/> after construction and <see cref="Stop"/> on teardown.
/// </summary>
public partial class JogPad : UserControl
{
    /// <summary>Step-size presets (label, millimetres) offered in the popup.</summary>
    private static readonly (string Label, double Mm)[] StepSizes =
    {
        ("0.1µm", 0.0001), ("0.5µm", 0.0005), ("1µm", 0.001), ("5µm", 0.005),
        ("10µm", 0.01), ("50µm", 0.05), ("100µm", 0.1), ("0.5mm", 0.5), ("1mm", 1.0),
    };

    private const string PrefsFile = "faam_jog_pad";   // { "stepMm": 0.1 } — last step the operator picked

    private readonly JogTransformStore _transforms = new();
    private readonly List<ToggleButton> _stepButtons = new();
    private readonly DispatcherTimer _poll;

    private EngineHost? _host;
    private string? _device;
    private JogMatrix _matrix = JogMatrix.Identity;
    private double _stepMm = 0.01;
    private bool _jogging;

    public JogPad()
    {
        InitializeComponent();
        _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _poll.Tick += (_, _) => UpdatePosition();
    }

    /// <summary>
    /// Wires the pad to the engine. <paramref name="devices"/> limits the picker to
    /// those motion devices (empty = every connected motion device);
    /// <paramref name="axes"/> is the subset of <c>XYZUVW</c> to expose;
    /// <paramref name="defaultStepMm"/> pre-selects the nearest step preset, unless the
    /// operator picked one in an earlier popup — that saved choice wins.
    /// </summary>
    public void Configure(EngineHost host, IReadOnlyList<string> devices, string axes, double defaultStepMm)
    {
        _host = host;
        BuildSteps(LoadSavedStep() ?? defaultStepMm);
        BuildAxes(axes);
        BuildDevices(devices);
        _poll.Start();
    }

    /// <summary>Stops the live-position poll — call before the host window closes.</summary>
    public void Stop() => _poll.Stop();

    // ---------------------------------------------------------------- build

    private void BuildDevices(IReadOnlyList<string> requested)
    {
        DeviceSelect.Items.Clear();
        if (_host == null) return;

        // Requested devices (in order) that are connected motion devices; if none were
        // requested, every connected motion device. A requested device that isn't
        // connected is silently skipped.
        var connected = _host.Engine.Motion.DeviceNames
            .Where(d => _host.Engine.Motion.IsConnected(d))
            .ToList();
        var list = requested.Count > 0
            ? requested.Where(d => connected.Contains(d, StringComparer.OrdinalIgnoreCase)).ToList()
            : connected;

        foreach (var d in list) DeviceSelect.Items.Add(d);
        if (DeviceSelect.Items.Count > 0)
            DeviceSelect.SelectedIndex = 0;   // fires OnDeviceChanged
        else
            SetStatus("No connected motion device to jog.");
    }

    private void BuildSteps(double defaultStepMm)
    {
        var style = (Style)FindResource("StepBtn");
        int best = 0;
        double bestDiff = double.MaxValue;
        for (int i = 0; i < StepSizes.Length; i++)
        {
            var (label, mm) = StepSizes[i];
            var tb = new ToggleButton { Content = label, Tag = mm, Style = style };
            tb.Click += OnStep;
            _stepButtons.Add(tb);
            StepList.Children.Add(tb);

            var diff = Math.Abs(mm - defaultStepMm);
            if (diff < bestDiff) { bestDiff = diff; best = i; }
        }
        if (_stepButtons.Count > 0)
        {
            _stepButtons[best].IsChecked = true;
            _stepMm = StepSizes[best].Mm;
        }
    }

    /// <summary>Builds one "[-] axis [+]" row per axis in <paramref name="axes"/> (XYZUVW order).</summary>
    private void BuildAxes(string axes)
    {
        AxisList.Children.Clear();
        var wanted = (axes ?? string.Empty).ToUpperInvariant();
        var jogStyle = (Style)FindResource("JogBtn");

        foreach (var axis in new[] { 'X', 'Y', 'Z', 'U', 'V', 'W' })
        {
            if (!wanted.Contains(axis)) continue;

            var row = new Grid { Margin = new Thickness(0, 2, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var minus = new Button { Content = $"{axis}−", Tag = $"{axis}-", Style = jogStyle };
            minus.Click += OnJog;
            Grid.SetColumn(minus, 0);

            var label = new TextBlock
            {
                Text = axis.ToString(),
                Foreground = (System.Windows.Media.Brush)FindResource("Subtle"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.SemiBold,
            };
            Grid.SetColumn(label, 1);

            var plus = new Button { Content = $"{axis}+", Tag = $"{axis}+", Style = jogStyle };
            plus.Click += OnJog;
            Grid.SetColumn(plus, 2);

            row.Children.Add(minus);
            row.Children.Add(label);
            row.Children.Add(plus);
            AxisList.Children.Add(row);
        }
    }

    // ---------------------------------------------------------------- interaction

    private void OnDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        _device = DeviceSelect.SelectedItem as string;
        _matrix = _device != null ? _transforms.Get(_device) : JogMatrix.Identity;
        UpdatePosition();
    }

    private void OnStep(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton tb || tb.Tag is not double mm) return;
        foreach (var b in _stepButtons) b.IsChecked = false;
        tb.IsChecked = true;
        _stepMm = mm;
        SaveStep(mm);
    }

    /// <summary>Saved step-size choice (<c>faam_jog_pad.json</c>).</summary>
    private sealed class JogPadPrefs
    {
        public double? StepMm { get; set; }
    }

    /// <summary>The step the operator last picked in any jog popup, or null if none saved.</summary>
    private static double? LoadSavedStep()
    {
        try
        {
            var prefs = ConfigLoader.LoadJson<JogPadPrefs>(ConfigLoader.GetConfigDirectory(), PrefsFile);
            return prefs?.StepMm is double mm && double.IsFinite(mm) && mm > 0 ? mm : null;
        }
        catch
        {
            return null;   // unreadable prefs → fall back to the block's default step
        }
    }

    private void SaveStep(double mm)
    {
        try
        {
            ConfigLoader.SaveJson(ConfigLoader.GetConfigDirectory(), PrefsFile, new JogPadPrefs { StepMm = mm });
        }
        catch (Exception ex)
        {
            SetStatus($"Could not save step size: {ex.Message}");
        }
    }

    private async void OnJog(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        if (_host == null || _device == null || _jogging) return;

        char axis = char.ToUpperInvariant(tag[0]);
        int sign = tag[1] == '+' ? 1 : -1;

        string localAxis;
        int dir;
        if (axis is 'X' or 'Y' or 'Z')
        {
            // Global jog → device-local via the matrix (a permutation, so one local
            // component is non-zero) — identical to JogPanel.
            double gx = axis == 'X' ? sign : 0, gy = axis == 'Y' ? sign : 0, gz = axis == 'Z' ? sign : 0;
            var (lx, ly, lz) = _matrix.Apply(gx, gy, gz);
            (string ax, double val)[] comps = { ("X", lx), ("Y", ly), ("Z", lz) };
            var dom = comps.OrderByDescending(c => Math.Abs(c.val)).First();
            localAxis = dom.ax;
            dir = Math.Sign(dom.val);
        }
        else
        {
            localAxis = axis.ToString();   // U/V/W jog directly
            dir = sign;
        }
        if (dir == 0) return;

        _jogging = true;
        try
        {
            var r = await _host.Engine.Motion.MoveRelativeAsync(_device, localAxis, dir * _stepMm);
            SetStatus(r.IsSuccess
                ? $"Jogged {_device} {localAxis}{(dir > 0 ? "+" : "-")} {_stepMm:g} mm"
                : $"Jog failed: {r.Message}");
        }
        catch (Exception ex) { SetStatus($"Jog error: {ex.Message}"); }
        finally { _jogging = false; }
    }

    private async void OnStop(object sender, RoutedEventArgs e)
    {
        if (_host == null || _device == null) return;
        try
        {
            var r = await _host.Engine.Motion.StopAsync(_device);
            SetStatus(r.IsSuccess ? $"STOP — {_device} halted." : $"STOP failed: {r.Message}");
        }
        catch (Exception ex) { SetStatus($"STOP error: {ex.Message}"); }
    }

    private void UpdatePosition()
    {
        if (_host == null || _device == null) return;

        MotionDeviceState state;
        try { state = _host.Engine.Motion.GetState(_device); }
        catch { _poll.Stop(); return; }   // engine torn down

        if (!state.Valid)
        {
            PosX.Text = PosY.Text = PosZ.Text = PosU.Text = PosV.Text = PosW.Text = "—";
            return;
        }
        var p = state.Position;
        PosX.Text = p.X.ToString("F4");
        PosY.Text = p.Y.ToString("F4");
        PosZ.Text = p.Z.ToString("F4");
        PosU.Text = p.U.ToString("F4");
        PosV.Text = p.V.ToString("F4");
        PosW.Text = p.W.ToString("F4");
    }

    private void SetStatus(string text) => PadStatus.Text = text;
}
