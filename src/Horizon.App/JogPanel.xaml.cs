using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Horizon.Camera.Models;
using Horizon.Core;
using Horizon.Pneumatic.Models;
using Serilog;

namespace Horizon.App;

public partial class JogPanel : UserControl
{
    private readonly IAppContext _context;
    private readonly DispatcherTimer _positionTimer;

    private string? _selectedDevice;
    private double _currentStepMicrons = 10.0;
    private bool _keyboardActive;
    private bool _initialized;

    // Axis+direction → button mapping for keyboard flash feedback
    private readonly Dictionary<string, Button> _jogButtons = new();

    private static readonly SolidColorBrush FlashBrush = new(Color.FromRgb(0x28, 0xA7, 0x45));
    private static readonly SolidColorBrush NormalBrush = new(Color.FromRgb(0x3A, 0x3A, 0x3A));

    private static readonly double[] StepSizes =
    {
        0.1, 0.5, 1.0, 2.0, 5.0, 10.0, 20.0, 50.0,
        100.0, 200.0, 250.0, 500.0, 1000.0, 2000.0, 5000.0
    };

    private static readonly string[] StepLabels =
    {
        "0.1µ", "0.5µ", "1µ", "2µ", "5µ", "10µ", "20µ", "50µ",
        "100µ", "200µ", "250µ", "500µ", "1mm", "2mm", "5mm"
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
            if (!_initialized)
            {
                _initialized = true;
                InterlockOverrideCheck.IsChecked = _context.InterlockService?.OverrideMode ?? false;
                try { BuildPneumaticButtons(); } catch { }
                try { BuildIOOutputButtons(); } catch { }
                try { BuildCameraSettings(); } catch { }
            }
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
            _context.MotionManager.MotionSourceContext = 0; // Jog
            bool ok = _context.MotionManager.MoveRelative(device, deviceAxis, distanceMm, blocking: false);
            if (!ok)
            {
                var reason = _context.MotionManager.LastInterlockReason;
                if (reason != null)
                {
                    Log.Warning("Jog: INTERLOCK — {Device}.{Axis}: {Reason}", device, deviceAxis, reason);
                    Dispatcher.BeginInvoke(() =>
                        MessageBox.Show(reason, "Motion Blocked", MessageBoxButton.OK, MessageBoxImage.Warning));
                }
                else
                {
                    Log.Error("Jog: MoveRelative failed — {Device}.{Axis} {Distance:+0.000;-0.000} mm",
                        device, deviceAxis, distanceMm);
                }
            }
        });
    }

    private void InterlockOverride_Click(object sender, RoutedEventArgs e)
    {
        var ils = _context.InterlockService;
        if (ils == null)
        {
            InterlockOverrideCheck.IsChecked = false;
            return;
        }
        ils.OverrideMode = InterlockOverrideCheck.IsChecked == true;
        Log.Information("Jog: Interlock override {State}", ils.OverrideMode ? "ENABLED" : "disabled");
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
            case Key.R: JogWithFlash("Z", -1); e.Handled = true; break;
            case Key.F: JogWithFlash("Z",  1); e.Handled = true; break;
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

    // ── Quick Access: Pneumatic ────────────────────────────────────

    private void BuildPneumaticButtons()
    {
        PneumaticButtonPanel.Children.Clear();
        var mgr = _context.PneumaticManager;
        foreach (var name in mgr.GetAllSlideNames())
        {
            var slideName = name;
            var panel = new StackPanel { Margin = new Thickness(2), Width = 130 };

            var label = new TextBlock
            {
                Text = slideName,
                FontFamily = new FontFamily("Consolas"), FontSize = 10,
                Foreground = (Brush)FindResource("Theme.ForegroundBrush"),
                Margin = new Thickness(0, 0, 0, 2)
            };

            var statusText = new TextBlock
            {
                FontFamily = new FontFamily("Consolas"), FontSize = 10,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 0, 0, 2)
            };

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal };

            var extBtn = new Button
            {
                Content = "Extend", Width = 60, Height = 22, Margin = new Thickness(0, 0, 2, 0),
                FontFamily = new FontFamily("Consolas"), FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x90, 0xEE, 0x90)),
                Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
                BorderBrush = (Brush)FindResource("Theme.BorderBrush"), Cursor = Cursors.Hand
            };
            extBtn.Click += (_, _) =>
            {
                Task.Run(() =>
                {
                    mgr.ExtendAndWait(slideName);
                    Dispatcher.BeginInvoke(() => UpdateSlideStatus(slideName, statusText));
                });
            };

            var retBtn = new Button
            {
                Content = "Retract", Width = 60, Height = 22,
                FontFamily = new FontFamily("Consolas"), FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x63, 0x47)),
                Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
                BorderBrush = (Brush)FindResource("Theme.BorderBrush"), Cursor = Cursors.Hand
            };
            retBtn.Click += (_, _) =>
            {
                Task.Run(() =>
                {
                    mgr.RetractAndWait(slideName);
                    Dispatcher.BeginInvoke(() => UpdateSlideStatus(slideName, statusText));
                });
            };

            btnPanel.Children.Add(extBtn);
            btnPanel.Children.Add(retBtn);
            panel.Children.Add(label);
            panel.Children.Add(statusText);
            panel.Children.Add(btnPanel);
            PneumaticButtonPanel.Children.Add(panel);

            UpdateSlideStatus(slideName, statusText);
        }

        if (mgr.GetAllSlideNames().Count == 0)
        {
            PneumaticButtonPanel.Children.Add(new TextBlock
            {
                Text = "No pneumatic slides configured",
                FontFamily = new FontFamily("Consolas"), FontSize = 11,
                Foreground = Brushes.Gray
            });
        }
    }

    private void UpdateSlideStatus(string slideName, TextBlock statusText)
    {
        var slide = _context.PneumaticManager.GetSlide(slideName);
        if (slide == null) { statusText.Text = "N/A"; return; }
        var pos = slide.GetPosition();
        statusText.Text = pos.ToString();
        statusText.Foreground = pos switch
        {
            PneumaticPosition.Extended => Brushes.LimeGreen,
            PneumaticPosition.Retracted => new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0)),
            _ => Brushes.Orange
        };
    }

    // ── Quick Access: IO Output ─────────────────────────────────────

    private void BuildIOOutputButtons()
    {
        IOOutputButtonPanel.Children.Clear();
        var ioMgr = _context.IOManager;

        foreach (var deviceName in ioMgr.GetAllDeviceNames())
        {
            var device = ioMgr.GetDevice(deviceName);
            if (device == null) continue;

            for (int pin = 0; pin < device.OutputCount; pin++)
            {
                var pinName = device.GetPinName(false, pin) ?? $"Output_{pin}";
                var devName = deviceName;
                var pinIdx = pin;

                var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2) };

                var label = new TextBlock
                {
                    Text = $"{pinName}",
                    FontFamily = new FontFamily("Consolas"), FontSize = 10,
                    Foreground = (Brush)FindResource("Theme.ForegroundBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Width = 100, Margin = new Thickness(0, 0, 4, 0)
                };

                var toggle = new ToggleButton
                {
                    Content = "OFF", Width = 50, Height = 20,
                    FontFamily = new FontFamily("Consolas"), FontSize = 10,
                    Style = (Style)FindResource("ToggleBtn")
                };

                // Read current state
                if (device.IsConnected)
                {
                    device.ReadAllOutputs(out uint outputs);
                    bool isOn = (outputs & (1u << pin)) != 0;
                    toggle.IsChecked = isOn;
                    toggle.Content = isOn ? "ON" : "OFF";
                }

                toggle.Click += (_, _) =>
                {
                    bool newState = toggle.IsChecked == true;
                    ioMgr.SetOutput(devName, pinIdx, newState);
                    toggle.Content = newState ? "ON" : "OFF";
                };

                panel.Children.Add(label);
                panel.Children.Add(toggle);
                IOOutputButtonPanel.Children.Add(panel);
            }
        }

        if (IOOutputButtonPanel.Children.Count == 0)
        {
            IOOutputButtonPanel.Children.Add(new TextBlock
            {
                Text = "No IO output pins configured",
                FontFamily = new FontFamily("Consolas"), FontSize = 11,
                Foreground = Brushes.Gray
            });
        }
    }

    // ── Quick Access: Camera Exposure ───────────────────────────────

    private void BuildCameraSettings()
    {
        CameraSettingsPanel.Children.Clear();
        var camMgr = _context.CameraManager;

        foreach (var id in camMgr.GetCameraIds())
        {
            var cameraId = id;
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };

            panel.Children.Add(new TextBlock
            {
                Text = cameraId,
                FontFamily = new FontFamily("Consolas"), FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("Theme.ForegroundBrush"),
                Margin = new Thickness(0, 0, 0, 4)
            });

            // Exposure slider
            var expRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
            expRow.Children.Add(new TextBlock
            {
                Text = "Exposure:", Width = 70,
                FontFamily = new FontFamily("Consolas"), FontSize = 10,
                Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center
            });

            ExposureSettings settings;
            try { settings = camMgr.GetExposureSettings(cameraId); }
            catch { settings = new ExposureSettings(); }

            var expLabel = new TextBlock
            {
                Text = FormatExposure(settings.ExposureTimeUs),
                FontFamily = new FontFamily("Consolas"), FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x14, 0xFF, 0xEC)),
                VerticalAlignment = VerticalAlignment.Center,
                Width = 70
            };

            var expSlider = new Slider
            {
                Minimum = 1, Maximum = 5, Width = 200,
                Value = Math.Log10(Math.Max(settings.ExposureTimeUs, 10)),
                VerticalAlignment = VerticalAlignment.Center,
                TickFrequency = 0.1, IsSnapToTickEnabled = false,
                Margin = new Thickness(4, 0, 4, 0)
            };
            expSlider.ValueChanged += (_, args) =>
            {
                var us = Math.Pow(10, args.NewValue);
                expLabel.Text = FormatExposure(us);
            };
            expSlider.PreviewMouseLeftButtonUp += (_, _) =>
            {
                var us = Math.Pow(10, expSlider.Value);
                var current = camMgr.GetExposureSettings(cameraId);
                camMgr.ApplyExposureSettings(cameraId, current with { ExposureTimeUs = us });
                Log.Information("Jog: Camera {Id} exposure set to {Us:F0} µs", cameraId, us);
            };

            expRow.Children.Add(expSlider);
            expRow.Children.Add(expLabel);
            panel.Children.Add(expRow);

            // Gain slider
            var gainRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
            gainRow.Children.Add(new TextBlock
            {
                Text = "Gain:", Width = 70,
                FontFamily = new FontFamily("Consolas"), FontSize = 10,
                Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center
            });

            var gainLabel = new TextBlock
            {
                Text = $"{settings.Gain:F1}",
                FontFamily = new FontFamily("Consolas"), FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x14, 0xFF, 0xEC)),
                VerticalAlignment = VerticalAlignment.Center,
                Width = 70
            };

            var gainSlider = new Slider
            {
                Minimum = 0, Maximum = 24, Width = 200,
                Value = settings.Gain,
                VerticalAlignment = VerticalAlignment.Center,
                TickFrequency = 0.5, IsSnapToTickEnabled = false,
                Margin = new Thickness(4, 0, 4, 0)
            };
            gainSlider.ValueChanged += (_, args) =>
            {
                gainLabel.Text = $"{args.NewValue:F1}";
            };
            gainSlider.PreviewMouseLeftButtonUp += (_, _) =>
            {
                var current = camMgr.GetExposureSettings(cameraId);
                camMgr.ApplyExposureSettings(cameraId, current with { Gain = gainSlider.Value });
                Log.Information("Jog: Camera {Id} gain set to {Gain:F1}", cameraId, gainSlider.Value);
            };

            gainRow.Children.Add(gainSlider);
            gainRow.Children.Add(gainLabel);
            panel.Children.Add(gainRow);

            CameraSettingsPanel.Children.Add(panel);
        }

        if (camMgr.GetCameraIds().Count == 0)
        {
            CameraSettingsPanel.Children.Add(new TextBlock
            {
                Text = "No cameras configured",
                FontFamily = new FontFamily("Consolas"), FontSize = 11,
                Foreground = Brushes.Gray
            });
        }
    }

    private static string FormatExposure(double us)
    {
        if (us >= 1000) return $"{us / 1000:F1} ms";
        return $"{us:F0} µs";
    }

    public void StopPolling() => _positionTimer.Stop();
}
