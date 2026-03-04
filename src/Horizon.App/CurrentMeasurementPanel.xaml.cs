using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Horizon.Core;
using Horizon.CurrentMeasurement;
using Horizon.CurrentMeasurement.Interfaces;
using Horizon.CurrentMeasurement.Models;
using Serilog;

namespace Horizon.App;

public partial class CurrentMeasurementPanel : UserControl
{
    private readonly string _deviceName;
    private readonly CurrentMeasurementManager _manager;
    private readonly ICurrentMeasurementDevice _device;
    private readonly MeasurementHub _hub;
    private readonly List<string> _channelKeys;

    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;

    private CancellationTokenSource? _fastPollCts;
    private Task? _fastPollTask;
    private bool _fastPolling;

    private readonly TextBlock[] _readingTexts;

    public event Action? ConnectionChanged;
    public event Action<string, List<string>>? PopOutChartRequested;

    public CurrentMeasurementPanel(string deviceName, CurrentMeasurementManager manager, MeasurementHub hub)
    {
        _deviceName = deviceName;
        _manager = manager;
        _device = manager.GetDevice(deviceName)!;
        _hub = hub;

        // Build channel keys from device's enabled channels
        _channelKeys = new List<string>(_device.ChannelCount);
        foreach (var ch in _device.EnabledChannels)
            _channelKeys.Add($"{deviceName}.CH{ch}");

        InitializeComponent();

        HeaderText.Text = deviceName;
        UpdateStatusText();

        _readingTexts = new TextBlock[_device.ChannelCount];
        BuildChannelRows();

        ReadingChart.Attach(_hub, _channelKeys);
        ReadingChart.StartRendering();

        StartPolling();
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false;
        try
        {
            var ok = await Task.Run(() => _device.Connect());
            Log.Information("CMeas '{DeviceName}': Connect {Result}", _deviceName, ok ? "OK" : "FAILED");
            if (ok)
            {
                var (idResult, identity) = await Task.Run(() =>
                {
                    var result = _device.GetIdentity(out var id);
                    return (result, id);
                });
                if (idResult == CurrentMeasurementError.Success)
                {
                    IdentityText.Text = $"IDN: {identity}";
                    Log.Debug("CMeas '{DeviceName}': {Identity}", _deviceName, identity);
                }
            }
            UpdateStatusText();
            ConnectionChanged?.Invoke();
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        DisconnectButton.IsEnabled = false;
        try
        {
            var ok = await Task.Run(() => _device.Disconnect());
            Log.Information("CMeas '{DeviceName}': Disconnect {Result}", _deviceName, ok ? "OK" : "FAILED");
            IdentityText.Text = string.Empty;
            UpdateStatusText();
            ConnectionChanged?.Invoke();
        }
        finally
        {
            DisconnectButton.IsEnabled = true;
        }
    }

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        ResetButton.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => _device.Reset());
            Log.Information("CMeas '{DeviceName}': Reset {Result}", _deviceName, result == CurrentMeasurementError.Success ? "OK" : "FAILED");
        }
        finally
        {
            ResetButton.IsEnabled = true;
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (!_device.IsConnected)
        {
            Log.Warning("CMeas '{DeviceName}': Not connected", _deviceName);
            return;
        }

        RefreshButton.IsEnabled = false;
        try
        {
            var (error, readings) = await Task.Run(() =>
            {
                var err = _device.ReadAllChannels(out var currents);
                return (err, currents);
            });

            if (error == CurrentMeasurementError.Success && readings != null)
            {
                for (int i = 0; i < readings.Length && i < _readingTexts.Length; i++)
                    _readingTexts[i].Text = FormatCurrent(readings[i]);

                Log.Information("CMeas '{DeviceName}': CH readings updated — {Readings}", _deviceName,
                    string.Join(", ", readings.Select((v, i) => $"CH{(i < _device.EnabledChannels.Count ? _device.EnabledChannels[i] : i + 1)}={FormatCurrent(v)}")));
            }
            else
            {
                Log.Error("CMeas '{DeviceName}': Read failed — {Error}", _deviceName, error);
            }
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private async void SetVoltage_Click(object sender, RoutedEventArgs e)
    {
        if (!_device.IsConnected)
        {
            Log.Warning("CMeas '{DeviceName}': Not connected", _deviceName);
            return;
        }

        if (!double.TryParse(VoltageInput.Text.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var volts))
        {
            Log.Warning("CMeas '{DeviceName}': Invalid voltage value", _deviceName);
            return;
        }

        SetVoltageButton.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => _device.SetVoltage(volts));
            if (result == CurrentMeasurementError.Success)
            {
                Log.Information("CMeas '{DeviceName}': Voltage set to {Volts} V", _deviceName, volts);
                VoltageStatusText.Text = $"Set: {volts} V";
            }
            else
            {
                Log.Error("CMeas '{DeviceName}': Set voltage failed — {Error}", _deviceName, result);
            }
        }
        finally
        {
            SetVoltageButton.IsEnabled = true;
        }
    }

    private async void VSourceOn_Click(object sender, RoutedEventArgs e)
    {
        if (!_device.IsConnected)
        {
            Log.Warning("CMeas '{DeviceName}': Not connected", _deviceName);
            return;
        }

        VSourceOnButton.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => _device.SetVoltageEnabled(true));
            Log.Information("CMeas '{DeviceName}': V-Source ON {Result}", _deviceName, result == CurrentMeasurementError.Success ? "OK" : "FAILED");
            if (result == CurrentMeasurementError.Success)
                VoltageStatusText.Text = $"V-Source: ON";
        }
        finally
        {
            VSourceOnButton.IsEnabled = true;
        }
    }

    private async void VSourceOff_Click(object sender, RoutedEventArgs e)
    {
        if (!_device.IsConnected)
        {
            Log.Warning("CMeas '{DeviceName}': Not connected", _deviceName);
            return;
        }

        VSourceOffButton.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => _device.SetVoltageEnabled(false));
            Log.Information("CMeas '{DeviceName}': V-Source OFF {Result}", _deviceName, result == CurrentMeasurementError.Success ? "OK" : "FAILED");
            if (result == CurrentMeasurementError.Success)
                VoltageStatusText.Text = $"V-Source: OFF";
        }
        finally
        {
            VSourceOffButton.IsEnabled = true;
        }
    }

    private void PopOutChart_Click(object sender, RoutedEventArgs e)
    {
        PopOutChartRequested?.Invoke(_deviceName, _channelKeys);
    }

    private async void StartPoll_Click(object sender, RoutedEventArgs e)
    {
        if (!_device.IsConnected)
        {
            Log.Warning("CMeas '{DeviceName}': Not connected", _deviceName);
            return;
        }

        StartPollButton.IsEnabled = false;
        StopPollButton.IsEnabled = true;

        // Stop the passive 500ms poll while fast polling is active
        _pollCts?.Cancel();

        // Configure fast mode on background thread
        var result = await Task.Run(() => _device.ConfigureFastMode());
        if (result != CurrentMeasurementError.Success)
        {
            Log.Error("CMeas '{DeviceName}': ConfigureFastMode failed — {Error}", _deviceName, result);
            StartPollButton.IsEnabled = true;
            StopPollButton.IsEnabled = false;
            StartPolling(); // restart passive poll
            return;
        }

        Log.Information("CMeas '{DeviceName}': Fast polling started (NPLC=0.1)", _deviceName);
        _fastPolling = true;
        _fastPollCts = new CancellationTokenSource();
        _fastPollTask = FastPollLoopAsync(_fastPollCts.Token);
    }

    private async void StopPoll_Click(object sender, RoutedEventArgs e)
    {
        StopPollButton.IsEnabled = false;

        // Stop fast poll loop
        _fastPollCts?.Cancel();
        try { if (_fastPollTask != null) await _fastPollTask; } catch { }
        _fastPollCts?.Dispose();
        _fastPollCts = null;
        _fastPollTask = null;
        _fastPolling = false;

        // Restore default mode
        if (_device.IsConnected)
        {
            var result = await Task.Run(() => _device.ConfigureDefaultMode());
            Log.Information("CMeas '{DeviceName}': Fast polling stopped, default mode {Result}", _deviceName, result == CurrentMeasurementError.Success ? "restored" : "FAILED");
        }

        PollRateText.Text = string.Empty;
        StartPollButton.IsEnabled = true;

        // Restart passive polling
        StartPolling();
    }

    private async Task FastPollLoopAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        int sampleCount = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var (error, readings) = await Task.Run(() =>
                {
                    var err = _device.ReadAllChannels(out var currents);
                    return (err, currents);
                }, ct);

                if (ct.IsCancellationRequested) break;

                sampleCount++;

                if (error == CurrentMeasurementError.Success && readings != null)
                {
                    var now = DateTimeOffset.UtcNow;
                    for (int i = 0; i < readings.Length && i < _channelKeys.Count; i++)
                        _hub.Push(_channelKeys[i], readings[i], now);

                    // Calculate Hz rate every update
                    var elapsed = sw.Elapsed.TotalSeconds;
                    var hz = elapsed > 0 ? sampleCount / elapsed : 0;

                    // Reset counter every 2 seconds to get a rolling average
                    if (elapsed >= 2.0)
                    {
                        sw.Restart();
                        sampleCount = 0;
                    }

                    Dispatcher.Invoke(() =>
                    {
                        for (int i = 0; i < readings.Length && i < _readingTexts.Length; i++)
                            _readingTexts[i].Text = FormatCurrent(readings[i]);

                        PollRateText.Text = $"{hz:F1} Hz";
                    });
                }
                else
                {
                    // Brief pause on error to avoid tight spin
                    try { await Task.Delay(50, ct); } catch { break; }
                }
            }
            catch (OperationCanceledException) { break; }
            catch { try { await Task.Delay(100, ct); } catch { break; } }
        }
    }

    private void UpdateStatusText()
    {
        var channels = string.Join(", ", _device.EnabledChannels.Select(ch => $"CH{ch}"));
        StatusText.Text = $"{(_device.IsConnected ? "Connected" : "Disconnected")} — {_device.ChannelCount} channel(s) [{channels}]";
    }

    private void BuildChannelRows()
    {
        for (int i = 0; i < _device.ChannelCount; i++)
        {
            var channelNum = i < _device.EnabledChannels.Count ? _device.EnabledChannels[i] : i + 1;

            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var label = new TextBlock
            {
                Text = $"CH{channelNum}:",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("Theme.ForegroundBrush")
            };
            Grid.SetColumn(label, 0);
            grid.Children.Add(label);

            var reading = new TextBlock
            {
                Text = "---",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("Theme.ForegroundBrush")
            };
            Grid.SetColumn(reading, 1);
            grid.Children.Add(reading);

            _readingTexts[i] = reading;
            ChannelStack.Children.Add(grid);
        }
    }

    private void StartPolling()
    {
        _pollCts = new CancellationTokenSource();
        _pollTask = PollLoopAsync(_pollCts.Token);
    }

    public void StopPolling()
    {
        // Stop chart rendering
        ReadingChart.StopRendering();

        // Stop fast polling if active
        _fastPollCts?.Cancel();
        try { _fastPollTask?.Wait(2000); } catch { }
        _fastPollCts?.Dispose();
        _fastPollCts = null;
        _fastPollTask = null;
        _fastPolling = false;

        // Stop passive polling
        _pollCts?.Cancel();
        try { _pollTask?.Wait(2000); } catch { }
        _pollCts?.Dispose();
        _pollCts = null;
        _pollTask = null;
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(ct))
                    break;
            }
            catch (OperationCanceledException) { break; }

            // Update status on UI thread
            Dispatcher.Invoke(() => UpdateStatusText());

            if (!_device.IsConnected || _fastPolling)
                continue;

            // Read on background thread
            var (error, readings) = await Task.Run(() =>
            {
                var err = _device.ReadAllChannels(out var currents);
                return (err, currents);
            }, ct);

            if (error == CurrentMeasurementError.Success && readings != null)
            {
                var now = DateTimeOffset.UtcNow;
                for (int i = 0; i < readings.Length && i < _channelKeys.Count; i++)
                    _hub.Push(_channelKeys[i], readings[i], now);

                // Update UI on dispatcher thread
                Dispatcher.Invoke(() =>
                {
                    for (int i = 0; i < readings.Length && i < _readingTexts.Length; i++)
                    {
                        _readingTexts[i].Text = FormatCurrent(readings[i]);
                    }
                });
            }
        }
    }

    private static string FormatCurrent(double amps)
    {
        var absVal = Math.Abs(amps);

        if (absVal == 0)
            return "0.000 A";
        if (absVal < 1e-9)
            return $"{amps * 1e12:F3} pA";
        if (absVal < 1e-6)
            return $"{amps * 1e9:F3} nA";
        if (absVal < 1e-3)
            return $"{amps * 1e6:F3} uA";
        if (absVal < 1.0)
            return $"{amps * 1e3:F3} mA";

        return $"{amps:F6} A";
    }
}
