using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Horizon.IO;
using Horizon.IO.Adapters;
using Horizon.IO.Interfaces;
using Horizon.IO.Models;

namespace Horizon.App;

public partial class IOPanel : UserControl
{
    private readonly string _deviceName;
    private readonly EziIOManager _ioManager;
    private readonly IIODevice _device;
    private DispatcherTimer? _pollTimer;

    // Cached UI references for fast updates
    private readonly Ellipse[] _inputIndicators;
    private readonly Ellipse[] _outputIndicators;
    private readonly Button[] _outputToggleButtons;
    private uint _localOutputState; // tracks commanded output state

    private static readonly SolidColorBrush OnBrush = new(Color.FromRgb(0x4E, 0xC9, 0x4E));   // green
    private static readonly SolidColorBrush OffBrush = new(Color.FromRgb(0x55, 0x55, 0x55));   // dim gray

    public event Action? ConnectionChanged;
    public event Action<string>? LogMessage;

    public IOPanel(string deviceName, EziIOManager ioManager)
    {
        _deviceName = deviceName;
        _ioManager = ioManager;
        _device = ioManager.GetDevice(deviceName)!;

        InitializeComponent();

        HeaderText.Text = deviceName;
        UpdateStatusText();

        _inputIndicators = new Ellipse[_device.InputCount];
        _outputIndicators = new Ellipse[_device.OutputCount];
        _outputToggleButtons = new Button[_device.OutputCount];

        BuildInputRows();
        BuildOutputRows();

        StartPolling();
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        var ok = _device.Connect();
        LogMessage?.Invoke($"IO '{_deviceName}': Connect {(ok ? "OK" : "FAILED")}");
        UpdateStatusText();
        ConnectionChanged?.Invoke();
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        var ok = _device.Disconnect();
        LogMessage?.Invoke($"IO '{_deviceName}': Disconnect {(ok ? "OK" : "FAILED")}");
        UpdateStatusText();
        ConnectionChanged?.Invoke();
    }

    private void UpdateStatusText()
    {
        StatusText.Text = $"{(_device.IsConnected ? "Connected" : "Disconnected")} — {_device.InputCount}in / {_device.OutputCount}out";
    }

    private void BuildInputRows()
    {
        for (int i = 0; i < _device.InputCount; i++)
        {
            var name = _device.GetPinName(true, i) ?? $"In{i}";
            var row = BuildPinRow(i, name, isOutput: false, out var indicator, out _);
            _inputIndicators[i] = indicator;
            InputStack.Children.Add(row);
        }
    }

    private void BuildOutputRows()
    {
        for (int i = 0; i < _device.OutputCount; i++)
        {
            var name = _device.GetPinName(false, i) ?? $"Out{i}";
            var row = BuildPinRow(i, name, isOutput: true, out var indicator, out var toggleBtn);
            _outputIndicators[i] = indicator;
            _outputToggleButtons[i] = toggleBtn!;
            OutputStack.Children.Add(row);
        }
    }

    private UIElement BuildPinRow(int pin, string name, bool isOutput,
        out Ellipse indicator, out Button? toggleButton)
    {
        var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        int col = 0;
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });   // #
        int colNum = col++;
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });   // indicator
        int colIndicator = col++;
        int colToggle = -1;
        if (isOutput)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) }); // toggle btn
            colToggle = col++;
        }
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // name
        int colName = col++;

        // Pin number
        var numText = new TextBlock
        {
            Text = $"{pin}",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("Theme.ForegroundDisabledBrush")
        };
        Grid.SetColumn(numText, colNum);
        grid.Children.Add(numText);

        // Status indicator (circle)
        indicator = new Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = OffBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(indicator, colIndicator);
        grid.Children.Add(indicator);

        // Toggle button (outputs only)
        toggleButton = null;
        if (isOutput)
        {
            var btn = new Button
            {
                Content = "Toggle",
                FontSize = 10,
                Padding = new Thickness(4, 1, 4, 1),
                Margin = new Thickness(2, 0, 4, 0),
                Tag = pin
            };
            btn.Click += ToggleOutput_Click;
            Grid.SetColumn(btn, colToggle);
            grid.Children.Add(btn);
            toggleButton = btn;
        }

        // Pin name
        var nameText = new TextBlock
        {
            Text = name,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("Theme.ForegroundBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(nameText, colName);
        grid.Children.Add(nameText);

        return grid;
    }

    private void ToggleOutput_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not int pin)
            return;

        bool currentState = (_localOutputState & (1u << pin)) != 0;
        bool newState = !currentState;

        var pinName = _device.GetPinName(false, pin) ?? $"Out{pin}";
        var result = _device.SetOutput(pin, newState);
        if (result == IOError.Success)
        {
            if (newState)
                _localOutputState |= (1u << pin);
            else
                _localOutputState &= ~(1u << pin);

            _outputIndicators[pin].Fill = newState ? OnBrush : OffBrush;
            LogMessage?.Invoke($"IO '{_deviceName}': Pin {pin} ({pinName}) → {(newState ? "ON" : "OFF")}");
        }
        else
        {
            LogMessage?.Invoke($"IO '{_deviceName}': Pin {pin} ({pinName}) toggle FAILED — {result}");
        }
    }

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

        if (!_device.IsConnected)
            return;

        // Read inputs
        if (_device.ReadInputs(out uint inputs) == IOError.Success)
        {
            for (int i = 0; i < _inputIndicators.Length; i++)
            {
                bool on = (inputs & (1u << i)) != 0;
                _inputIndicators[i].Fill = on ? OnBrush : OffBrush;
            }
        }

        // Read outputs from hardware
        if (_device.ReadAllOutputs(out uint outputs) == IOError.Success)
        {
            _localOutputState = outputs;
            for (int i = 0; i < _outputIndicators.Length; i++)
            {
                bool on = (outputs & (1u << i)) != 0;
                _outputIndicators[i].Fill = on ? OnBrush : OffBrush;
            }
        }
    }
}
