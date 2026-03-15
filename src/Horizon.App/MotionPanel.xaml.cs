using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Input;
using System.Windows.Threading;
using Horizon.Motion;
using Horizon.Motion.Interfaces;
using Horizon.Motion.Models;
using Serilog;

namespace Horizon.App;

public partial class MotionPanel : UserControl
{
    private readonly string _deviceName;
    private readonly UnifiedMotionManager _motionManager;
    private readonly IMotionController _controller;
    private DispatcherTimer? _pollTimer;

    private readonly List<string> _axisNames;
    private readonly TextBlock[] _positionTexts;
    private readonly TextBlock[] _statusTexts;
    private readonly TextBox[] _absMoveBoxes;

    private static readonly SolidColorBrush MovingBrush = new(Color.FromRgb(0xFF, 0xD5, 0x4F));     // amber
    private static readonly SolidColorBrush OnTargetBrush = new(Color.FromRgb(0x4E, 0xC9, 0x4E));   // green
    private static readonly SolidColorBrush IdleBrush = new(Color.FromRgb(0x88, 0x88, 0x88));       // gray
    private static readonly SolidColorBrush BlackBrush = new(Colors.Black);
    private static readonly SolidColorBrush TransparentBrush = new(Colors.Transparent);

    private Brush? _themeForeground;

    private static readonly (string Label, double Mm)[] JogSteps =
    [
        ("1 um",    0.001),
        ("5 um",    0.005),
        ("10 um",   0.01),
        ("50 um",   0.05),
        ("0.1 mm",  0.1),
        ("0.5 mm",  0.5),
        ("1 mm",    1.0),
        ("5 mm",    5.0),
        ("10 mm",  10.0),
    ];

    public event Action? ConnectionChanged;

    public MotionPanel(string deviceName, UnifiedMotionManager motionManager)
    {
        _deviceName = deviceName;
        _motionManager = motionManager;
        _controller = motionManager.GetController(deviceName)!;
        _axisNames = _controller.AxisNames;

        InitializeComponent();

        _themeForeground = (Brush)FindResource("Theme.ForegroundBrush");
        HeaderText.Text = deviceName;
        UpdateStatusText();
        JogStepCombo.ItemsSource = JogSteps.Select(s => s.Label).ToArray();

        _positionTexts = new TextBlock[_axisNames.Count];
        _statusTexts = new TextBlock[_axisNames.Count];
        _absMoveBoxes = new TextBox[_axisNames.Count];

        BuildAxisRows();
        LoadPresets();
        StartPolling();

        _motionManager.PresetsChanged += OnPresetsChanged;
    }

    private void OnPresetsChanged()
    {
        Dispatcher.Invoke(LoadPresets);
    }

    // --- Connection ---

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        var ok = _controller.Connect();
        Log.Information("Motion '{DeviceName}': Connect {Result}", _deviceName, ok ? "OK" : "FAILED");
        UpdateStatusText();
        ConnectionChanged?.Invoke();
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        var ok = _controller.Disconnect();
        Log.Information("Motion '{DeviceName}': Disconnect {Result}", _deviceName, ok ? "OK" : "FAILED");
        UpdateStatusText();
        ConnectionChanged?.Invoke();
    }

    private void StopAll_Click(object sender, RoutedEventArgs e)
    {
        var ok = _controller.StopAll();
        Log.Information("Motion '{DeviceName}': Stop All {Result}", _deviceName, ok ? "OK" : "FAILED");
    }

    // --- Status ---

    private void UpdateStatusText()
    {
        var conn = _controller.IsConnected ? "Connected" : "Disconnected";
        StatusText.Text = $"{conn} — {_controller.ControllerType} — {_axisNames.Count} axes";
    }

    // --- Axis table ---

    private void BuildAxisRows()
    {
        for (int i = 0; i < _axisNames.Count; i++)
        {
            var axisName = _axisNames[i];
            var row = BuildAxisRow(i, axisName);
            AxisStack.Children.Add(row);
        }
    }

    private UIElement BuildAxisRow(int index, string axisName)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });   // axis name
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });  // position
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });   // status
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });   // abs input
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });   // Go
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });   // Jog -
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });   // Jog +
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });   // Zero
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });   // Stop
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        int col = 0;

        // Axis name
        var nameText = new TextBlock
        {
            Text = axisName,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("Theme.ForegroundBrush")
        };
        Grid.SetColumn(nameText, col++);
        grid.Children.Add(nameText);

        // Current position
        var posText = new TextBlock
        {
            Text = "---",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("Theme.ForegroundBrush"),
            Padding = new Thickness(4, 0, 4, 0)
        };
        Grid.SetColumn(posText, col++);
        grid.Children.Add(posText);
        _positionTexts[index] = posText;

        // Status text (Moving / OnTarget / Idle)
        var statusText = new TextBlock
        {
            Text = "",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = IdleBrush,
            Padding = new Thickness(2, 0, 4, 0)
        };
        Grid.SetColumn(statusText, col++);
        grid.Children.Add(statusText);
        _statusTexts[index] = statusText;

        // Absolute move input
        var absBox = new TextBox
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Padding = new Thickness(2, 1, 2, 1),
            Margin = new Thickness(0, 0, 4, 0),
            Background = (Brush)FindResource("Theme.BackgroundBrush"),
            Foreground = (Brush)FindResource("Theme.ForegroundBrush"),
            BorderBrush = (Brush)FindResource("Theme.BorderBrush")
        };
        Grid.SetColumn(absBox, col++);
        grid.Children.Add(absBox);
        _absMoveBoxes[index] = absBox;

        // Go button
        var goBtn = new Button
        {
            Content = "Go",
            FontSize = 10,
            Padding = new Thickness(4, 1, 4, 1),
            Margin = new Thickness(0, 0, 4, 0),
            Tag = index
        };
        goBtn.Click += GoButton_Click;
        Grid.SetColumn(goBtn, col++);
        grid.Children.Add(goBtn);

        // Jog -
        var jogMinusBtn = new Button
        {
            Content = "-",
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Padding = new Thickness(2, 1, 2, 1),
            Margin = new Thickness(0, 0, 2, 0),
            Tag = index
        };
        jogMinusBtn.Click += JogMinus_Click;
        Grid.SetColumn(jogMinusBtn, col++);
        grid.Children.Add(jogMinusBtn);

        // Jog +
        var jogPlusBtn = new Button
        {
            Content = "+",
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Padding = new Thickness(2, 1, 2, 1),
            Margin = new Thickness(0, 0, 4, 0),
            Tag = index
        };
        jogPlusBtn.Click += JogPlus_Click;
        Grid.SetColumn(jogPlusBtn, col++);
        grid.Children.Add(jogPlusBtn);

        // Home
        var homeBtn = new Button
        {
            Content = "Zero",
            FontSize = 10,
            Padding = new Thickness(2, 1, 2, 1),
            Margin = new Thickness(0, 0, 4, 0),
            Tag = index
        };
        homeBtn.Click += ZeroButton_Click;
        Grid.SetColumn(homeBtn, col++);
        grid.Children.Add(homeBtn);

        // Stop
        var stopBtn = new Button
        {
            Content = "Stop",
            FontSize = 10,
            Padding = new Thickness(2, 1, 2, 1),
            Tag = index,
            Foreground = new SolidColorBrush(Colors.White),
            Background = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28))
        };
        stopBtn.Click += StopButton_Click;
        Grid.SetColumn(stopBtn, col++);
        grid.Children.Add(stopBtn);

        return grid;
    }

    // --- Axis commands ---

    private void GoButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not int idx) return;
        var axis = _axisNames[idx];
        if (!double.TryParse(_absMoveBoxes[idx].Text, out var target))
        {
            Log.Warning("Motion '{DeviceName}': Invalid position value for {Axis}", _deviceName, axis);
            return;
        }
        var ok = _motionManager.MoveToAbs(_deviceName, axis, target, blocking: false);
        Log.Information("Motion '{DeviceName}': Abs move {Axis} -> {Target:F3} {Result}", _deviceName, axis, target, ok ? "OK" : "FAILED");
    }

    private void JogMinus_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not int idx) return;
        Jog(idx, -1);
    }

    private void JogPlus_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not int idx) return;
        Jog(idx, +1);
    }

    private void Jog(int axisIndex, int direction)
    {
        var idx = JogStepCombo.SelectedIndex;
        if (idx < 0 || idx >= JogSteps.Length) return;

        var step = JogSteps[idx].Mm;
        var axis = _axisNames[axisIndex];
        var distance = step * direction;
        var ok = _motionManager.MoveRelative(_deviceName, axis, distance, blocking: false);
        Log.Information("Motion '{DeviceName}': Jog {Axis} {Direction}{Distance} {Result}",
            _deviceName, axis, direction > 0 ? "+" : "", distance, ok ? "OK" : "FAILED");
    }

    private void ZeroButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not int idx) return;
        var axis = _axisNames[idx];
        var ok = _motionManager.MoveToAbs(_deviceName, axis, 0, blocking: false);
        Log.Information("Motion '{DeviceName}': Zero {Axis} {Result}", _deviceName, axis, ok ? "OK" : "FAILED");
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not int idx) return;
        var axis = _axisNames[idx];
        var ok = _controller.StopAxis(axis);
        Log.Information("Motion '{DeviceName}': Stop {Axis} {Result}", _deviceName, axis, ok ? "OK" : "FAILED");
    }

    // --- Named positions / presets ---

    private void LoadPresets()
    {
        var names = _motionManager.GetNamedPositionNames(_deviceName);
        var rows = new List<PresetRow>();

        foreach (var name in names)
        {
            var pos = _motionManager.GetNamedPosition(_deviceName, name);
            if (pos == null) continue;

            var dist = Math.Sqrt(pos.X * pos.X + pos.Y * pos.Y + pos.Z * pos.Z);
            rows.Add(new PresetRow
            {
                Name = name,
                X = pos.X.ToString("F3"),
                Y = pos.Y.ToString("F3"),
                Z = pos.Z.ToString("F3"),
                Distance = dist.ToString("F3"),
                DistanceValue = dist
            });
        }

        rows.Sort((a, b) => a.DistanceValue.CompareTo(b.DistanceValue));
        PresetGrid.ItemsSource = rows;
        if (rows.Count > 0)
            PresetGrid.SelectedIndex = 0;
    }

    private void PresetMove_Click(object sender, RoutedEventArgs e)
    {
        if (PresetGrid.SelectedItem is not PresetRow row) return;
        var ok = _motionManager.MoveToNamedPosition(_deviceName, row.Name, blocking: false);
        Log.Information("Motion '{DeviceName}': Move to preset '{PresetName}' {Result}", _deviceName, row.Name, ok ? "OK" : "FAILED");
    }

    private void PresetGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PresetGrid.SelectedItem is not PresetRow row) return;
        var ok = _motionManager.MoveToNamedPosition(_deviceName, row.Name, blocking: false);
        Log.Information("Motion '{DeviceName}': Move to preset '{PresetName}' {Result}", _deviceName, row.Name, ok ? "OK" : "FAILED");
    }

    private class PresetRow
    {
        public string Name { get; set; } = "";
        public string X { get; set; } = "";
        public string Y { get; set; } = "";
        public string Z { get; set; } = "";
        public string Distance { get; set; } = "";
        public double DistanceValue { get; set; }
    }

    // --- Polling ---

    private void StartPolling()
    {
        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _pollTimer.Tick += PollTimer_Tick;
        _pollTimer.Start();
    }

    public void StopPolling()
    {
        _motionManager.PresetsChanged -= OnPresetsChanged;

        if (_pollTimer != null)
        {
            _pollTimer.Stop();
            _pollTimer.Tick -= PollTimer_Tick;
            _pollTimer = null;
        }
    }

    private void PollTimer_Tick(object? sender, EventArgs e)
    {
        UpdateStatusText();

        if (!_controller.IsConnected)
            return;

        // Read current position
        var pos = _motionManager.GetCurrentPosition(_deviceName);
        if (pos == null) return;

        for (int i = 0; i < _axisNames.Count; i++)
        {
            var axis = _axisNames[i];
            var value = GetAxis(pos, axis);
            _positionTexts[i].Text = value.ToString("F4");

            bool axisMoving = _controller.IsMoving(axis);
            bool onTarget = _controller.IsOnTarget(axis);

            if (axisMoving)
            {
                _statusTexts[i].Text = "Moving";
                _statusTexts[i].Foreground = MovingBrush;
                _positionTexts[i].Background = MovingBrush;
                _positionTexts[i].Foreground = BlackBrush;
            }
            else if (onTarget)
            {
                _statusTexts[i].Text = "OnTarget";
                _statusTexts[i].Foreground = OnTargetBrush;
                _positionTexts[i].Background = TransparentBrush;
                _positionTexts[i].Foreground = _themeForeground;
            }
            else
            {
                _statusTexts[i].Text = "Idle";
                _statusTexts[i].Foreground = IdleBrush;
                _positionTexts[i].Background = TransparentBrush;
                _positionTexts[i].Foreground = _themeForeground;
            }
        }
    }

    // --- Helpers ---

    private static double GetAxis(PositionStruct pos, string axis) => axis switch
    {
        "X" => pos.X, "Y" => pos.Y, "Z" => pos.Z,
        "U" => pos.U, "V" => pos.V, "W" => pos.W,
        _ => 0
    };
}
