using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Horizon.Core;
using Serilog;

namespace Horizon.App;

public partial class JogPanel : UserControl
{
    private readonly IAppContext _context;
    private readonly DispatcherTimer _positionTimer;

    private string? _selectedDevice;
    private double _currentStepMicrons = 10.0;
    private bool _keyboardActive;

    // Axis+direction → button mapping for keyboard flash feedback
    private readonly Dictionary<string, Button> _jogButtons = new();

    private static readonly SolidColorBrush FlashBrush = new(Color.FromRgb(0x28, 0xA7, 0x45));
    private static readonly SolidColorBrush NormalBrush = new(Color.FromRgb(0x3A, 0x3A, 0x3A));

    private static readonly double[] StepSizes =
    {
        0.1, 0.5, 1.0, 2.0, 5.0, 10.0, 20.0, 50.0,
        100.0, 200.0, 500.0, 1000.0, 2000.0
    };

    private static readonly string[] StepLabels =
    {
        "0.1µ", "0.5µ", "1µ", "2µ", "5µ", "10µ", "20µ", "50µ",
        "100µ", "200µ", "500µ", "1mm", "2mm"
    };

    public JogPanel(IAppContext context)
    {
        _context = context;
        InitializeComponent();

        // Build axis→button lookup for keyboard flash
        _jogButtons["X,-1"] = BtnXMinus;  _jogButtons["X,1"] = BtnXPlus;
        _jogButtons["Y,-1"] = BtnYMinus;  _jogButtons["Y,1"] = BtnYPlus;
        _jogButtons["Z,-1"] = BtnZMinus;  _jogButtons["Z,1"] = BtnZPlus;
        _jogButtons["U,-1"] = BtnUMinus;  _jogButtons["U,1"] = BtnUPlus;
        _jogButtons["V,-1"] = BtnVMinus;  _jogButtons["V,1"] = BtnVPlus;
        _jogButtons["W,-1"] = BtnWMinus;  _jogButtons["W,1"] = BtnWPlus;

        BuildDeviceButtons();
        BuildStepButtons();
        UpdateStepLabel();

        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _positionTimer.Tick += (_, _) => UpdatePositionDisplay();

        Loaded += (_, _) =>
        {
            _positionTimer.Start();
            Focus();
        };
        Unloaded += (_, _) => _positionTimer.Stop();

        GotFocus += (_, _) => SetKeyboardActive(true);
        LostFocus += (_, _) => SetKeyboardActive(false);
        IsKeyboardFocusWithinChanged += (_, e) =>
            SetKeyboardActive(e.NewValue is true);
    }

    // ── Device selector ──────────────────────────────────────────────

    private void BuildDeviceButtons()
    {
        DevicePanel.Children.Clear();
        if (!_context.IsInitialized) return;

        var names = _context.MotionManager.GetAllDeviceNames();
        bool first = true;

        foreach (var name in names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            var ctrl = _context.MotionManager.GetController(name);
            if (ctrl == null || !ctrl.IsConnected) continue;

            var btn = new ToggleButton
            {
                Content = name,
                Tag = name,
                Style = (Style)FindResource("ToggleBtn"),
                IsChecked = first
            };
            btn.Checked += DeviceButton_Checked;
            DevicePanel.Children.Add(btn);

            if (first)
            {
                _selectedDevice = name;
                first = false;
            }
        }

        RefreshUvwVisibility();
    }

    private void DeviceButton_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked) return;

        // Uncheck all others (radio-button behavior)
        foreach (var child in DevicePanel.Children.OfType<ToggleButton>())
        {
            if (child != clicked)
                child.IsChecked = false;
        }

        _selectedDevice = clicked.Tag as string;
        RefreshUvwVisibility();
        UpdatePositionDisplay();
        Log.Debug("Jog device selected: {Device}", _selectedDevice);
    }

    private void RefreshUvwVisibility()
    {
        if (_selectedDevice == null)
        {
            UvwHeader.Visibility = Visibility.Collapsed;
            UvwSection.Visibility = Visibility.Collapsed;
            return;
        }

        var ctrl = _context.MotionManager.GetController(_selectedDevice);
        if (ctrl == null)
        {
            UvwHeader.Visibility = Visibility.Collapsed;
            UvwSection.Visibility = Visibility.Collapsed;
            return;
        }

        var axes = ctrl.AxisNames;
        bool hasU = axes.Contains("U", StringComparer.OrdinalIgnoreCase);
        bool hasV = axes.Contains("V", StringComparer.OrdinalIgnoreCase);
        bool hasW = axes.Contains("W", StringComparer.OrdinalIgnoreCase);
        bool hasAny = hasU || hasV || hasW;

        UvwHeader.Visibility = hasAny ? Visibility.Visible : Visibility.Collapsed;
        UvwSection.Visibility = hasAny ? Visibility.Visible : Visibility.Collapsed;

        BtnUMinus.IsEnabled = hasU; BtnUPlus.IsEnabled = hasU;
        BtnVMinus.IsEnabled = hasV; BtnVPlus.IsEnabled = hasV;
        BtnWMinus.IsEnabled = hasW; BtnWPlus.IsEnabled = hasW;
    }

    // ── Step size selector ───────────────────────────────────────────

    private void BuildStepButtons()
    {
        for (int i = 0; i < StepSizes.Length; i++)
        {
            var btn = new ToggleButton
            {
                Content = StepLabels[i],
                Tag = StepSizes[i],
                Style = (Style)FindResource("ToggleBtn"),
                IsChecked = Math.Abs(StepSizes[i] - _currentStepMicrons) < 0.001
            };
            btn.Checked += StepButton_Checked;
            StepPanel.Children.Add(btn);
        }
    }

    private void StepButton_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked || clicked.Tag is not double size) return;

        foreach (var child in StepPanel.Children.OfType<ToggleButton>())
        {
            if (child != clicked)
                child.IsChecked = false;
        }

        _currentStepMicrons = size;
        UpdateStepLabel();
    }

    private void CycleStep(int direction)
    {
        int idx = Array.FindIndex(StepSizes, s => Math.Abs(s - _currentStepMicrons) < 0.001);
        if (idx < 0) idx = 5; // default to 10µm

        idx += direction;
        if (idx < 0) idx = StepSizes.Length - 1;
        if (idx >= StepSizes.Length) idx = 0;

        _currentStepMicrons = StepSizes[idx];

        // Update toggle state
        int i = 0;
        foreach (var child in StepPanel.Children.OfType<ToggleButton>())
        {
            child.IsChecked = i == idx;
            i++;
        }

        UpdateStepLabel();
    }

    private void UpdateStepLabel()
    {
        string text;
        if (_currentStepMicrons >= 1000.0)
            text = $"{_currentStepMicrons / 1000.0:F0} mm";
        else if (_currentStepMicrons < 1.0)
            text = $"{_currentStepMicrons:F1} µm";
        else
            text = $"{_currentStepMicrons:F0} µm";

        StepLabel.Text = $"⚙ {text}";
    }

    // ── Jog execution ────────────────────────────────────────────────

    private void Jog_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        var parts = tag.Split(',');
        if (parts.Length != 2) return;

        var axis = parts[0];
        var dir = double.Parse(parts[1]);
        ExecuteJog(axis, dir);

        // Re-focus so keyboard keeps working
        Focus();
    }

    private void ExecuteJog(string globalAxis, double direction)
    {
        if (_selectedDevice == null)
        {
            Log.Warning("Jog: No device selected");
            return;
        }

        var ctrl = _context.MotionManager.GetController(_selectedDevice);
        if (ctrl == null || !ctrl.IsConnected)
        {
            Log.Warning("Jog: Device '{Device}' not connected", _selectedDevice);
            return;
        }

        // Convert microns to mm
        double distanceMm = (_currentStepMicrons * direction) / 1000.0;

        // For now, global axis maps directly to device axis (identity transform).
        // TODO: Add transformation matrix support for rotated coordinate frames.
        var deviceAxis = globalAxis;

        if (!ctrl.AxisNames.Contains(deviceAxis, StringComparer.OrdinalIgnoreCase))
        {
            Log.Warning("Jog: Axis '{Axis}' not available on '{Device}'", deviceAxis, _selectedDevice);
            return;
        }

        // Execute on thread pool to avoid UI freeze
        var device = _selectedDevice;
        Task.Run(() =>
        {
            bool ok = _context.MotionManager.MoveRelative(device, deviceAxis, distanceMm, blocking: false);
            if (!ok)
                Log.Error("Jog: MoveRelative failed — {Device}.{Axis} {Distance:+0.000;-0.000} mm",
                    device, deviceAxis, distanceMm);
        });
    }

    private void StopAll_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDevice != null)
        {
            _context.MotionManager.StopAll(_selectedDevice);
            Log.Information("Jog: STOP ALL — {Device}", _selectedDevice);
        }
        Focus();
    }

    // ── Position display ─────────────────────────────────────────────

    private void UpdatePositionDisplay()
    {
        if (_selectedDevice == null)
        {
            PosX.Text = "X: ---"; PosY.Text = "Y: ---"; PosZ.Text = "Z: ---";
            PosU.Text = "U: ---"; PosV.Text = "V: ---"; PosW.Text = "W: ---";
            return;
        }

        var pos = _context.MotionManager.GetCurrentPosition(_selectedDevice);
        if (pos == null)
        {
            PosX.Text = "X: ---"; PosY.Text = "Y: ---"; PosZ.Text = "Z: ---";
            PosU.Text = "U: ---"; PosV.Text = "V: ---"; PosW.Text = "W: ---";
            return;
        }

        var ctrl = _context.MotionManager.GetController(_selectedDevice);
        var axes = ctrl?.AxisNames ?? new List<string>();

        PosX.Text = axes.Contains("X", StringComparer.OrdinalIgnoreCase)
            ? $"X: {pos.X:F3}" : "X: ---";
        PosY.Text = axes.Contains("Y", StringComparer.OrdinalIgnoreCase)
            ? $"Y: {pos.Y:F3}" : "Y: ---";
        PosZ.Text = axes.Contains("Z", StringComparer.OrdinalIgnoreCase)
            ? $"Z: {pos.Z:F3}" : "Z: ---";

        PosU.Text = axes.Contains("U", StringComparer.OrdinalIgnoreCase)
            ? $"U: {pos.U:F3}" : "U: ---";
        PosV.Text = axes.Contains("V", StringComparer.OrdinalIgnoreCase)
            ? $"V: {pos.V:F3}" : "V: ---";
        PosW.Text = axes.Contains("W", StringComparer.OrdinalIgnoreCase)
            ? $"W: {pos.W:F3}" : "W: ---";
    }

    // ── Keyboard bindings ────────────────────────────────────────────

    private void SetKeyboardActive(bool active)
    {
        _keyboardActive = active;
        KeyboardIndicator.Text = active
            ? "⌨ Keyboard: ACTIVE"
            : "⌨ Keyboard: INACTIVE";
        KeyboardIndicator.Foreground = active
            ? new System.Windows.Media.SolidColorBrush(
                  System.Windows.Media.Color.FromRgb(0x28, 0xA7, 0x45))
            : new System.Windows.Media.SolidColorBrush(
                  System.Windows.Media.Color.FromRgb(0x55, 0x55, 0x55));
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.IsRepeat) return;

        switch (e.Key)
        {
            case Key.W: JogWithFlash("Y", -1); e.Handled = true; break;
            case Key.S: JogWithFlash("Y",  1); e.Handled = true; break;
            case Key.A: JogWithFlash("X", -1); e.Handled = true; break;
            case Key.D: JogWithFlash("X",  1); e.Handled = true; break;
            case Key.R: JogWithFlash("Z",  1); e.Handled = true; break;
            case Key.F: JogWithFlash("Z", -1); e.Handled = true; break;
            case Key.Q: CycleStep(-1);         e.Handled = true; break;
            case Key.E: CycleStep( 1);         e.Handled = true; break;
            case Key.Escape:
                if (_selectedDevice != null)
                    _context.MotionManager.StopAll(_selectedDevice);
                e.Handled = true;
                break;
        }
    }

    private void JogWithFlash(string axis, double direction)
    {
        ExecuteJog(axis, direction);

        var key = $"{axis},{(direction > 0 ? "1" : "-1")}";
        if (_jogButtons.TryGetValue(key, out var btn))
            FlashButton(btn);
    }

    private void FlashButton(Button button)
    {
        // Find the Border named "Bd" inside the button's template
        if (button.Template.FindName("Bd", button) is not Border border)
            return;

        border.Background = FlashBrush;
        border.BorderBrush = new SolidColorBrush(Color.FromRgb(0x5C, 0xB8, 0x5C));

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) =>
        {
            border.Background = NormalBrush;
            border.BorderBrush = (Brush)FindResource("Theme.BorderBrush");
            timer.Stop();
        };
        timer.Start();
    }

    public void StopPolling() => _positionTimer.Stop();
}
